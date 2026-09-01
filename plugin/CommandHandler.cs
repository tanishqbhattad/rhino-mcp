// RhinoAIBridge v4.5 - CommandHandler.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace RhinoAIBridge
{
    /// <summary>
    /// Phase 1 changes vs v3:
    ///   1. No Doc.Views.Redraw() inside individual ops. RedrawScope.Mark() instead;
    ///      the outer scope (opened in AIBridgeServer per command) flushes one redraw.
    ///      Batches now redraw exactly ONCE no matter how many sub-ops.
    ///   2. AreaMassProperties / VolumeMassProperties are opt-in via params.measure=true.
    ///      Default response shape returns ids + bbox only - what the next tool call actually needs.
    ///      For the common architect flow (extrude, transform, section, repeat) this saves
    ///      a Brep integration on every single create.
    ///   3. capture_viewport now uses MemoryStream + JPEG default + bitmap downscale
    ///      instead of disk round-trip + Rhino re-render at smaller sizes.
    /// 
    /// The dispatch table, schema, and tool semantics are unchanged. v3 callers still work.
    /// </summary>
    public class CommandHandler
    {
        private readonly Dictionary<string, Func<JObject, JObject>> _commands;

        // Phase 3: when > 0, the U decorator suppresses its per-command undo record so that
        // a single batch-level undo record contains every sub-op. Required for atomic rollback.
        private int _atomicBatchDepth = 0;

        // Auto-thumbnail: tracks batch nesting (atomic + non-atomic) so we capture ONE thumbnail
        // at the end of a batch rather than one per sub-op. Set to true inside DispatchBatch.
        private int _batchDepth = 0;

        public enum BridgeMode { Safe, Standard, Developer }
        public static BridgeMode Mode = BridgeMode.Safe;
        public static bool SafeMode => Mode == BridgeMode.Safe;

        private static readonly HashSet<string> CodeExecCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "execute_script", "run_python", "execute_python3", "start_script_server", "run_command" };

        private static readonly HashSet<string> DestructiveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "delete_objects", "boolean_operation",
            // v4.10: Safe mode consistency - these delete or replace geometry too.
            "delete_layer", "remove_section", "clear_trace_layers",
            "delete_checkpoint", "restore_checkpoint",
        };

        private static readonly HashSet<string> AutoCheckpointUndoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Boolean", "Delete", "DelLayer", "Restore", "RhinoCmd", "Script", "TrimPlanes" };

        // v4.8 (protocol 5): commands that never mutate the document. The server uses
        // this to decide which requests need idempotency registration + WAL bracketing.
        public static readonly HashSet<string> ReadOnlyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ping", "hello", "cancel", "query_scene", "get_objects", "list_objects", "get_context",
            "get_scene_summary", "get_object_details", "get_object_info", "list_layers",
            "get_selection", "measure_object", "measure_distance", "check_intersection",
            "validate_objects", "get_log", "get_log_stats", "get_rhino_commands",
            "get_scene_diff", "get_change_log", "get_tracker_version", "get_state",
            "list_sections", "list_display_modes", "list_materials", "get_material",
            "list_checkpoints", "get_design_brief", "get_design_rules", "get_provenance",
            "search_memory", "get_related_objects", "get_group", "get_all_groups", "get_groups",
            "get_trace_layers", "get_section_profile", "get_silhouette",
            "capture_viewport", "get_viewport_image", "capture_inspection_view", "thumbnail",
            "batch_preview", "get_camera_target", "suggest_tools", "lint_script",
            "validate_architecture", "get_recovery_log", "get_building_systems",
            "get_level_summary", "detect_design_patterns", "find_unassigned_geometry",
            "analyze_architecture", "capture_illustration",
            "detect_clashes", "list_commands",
            // Intent-validation reads. section_preview adds a clipping plane but always
            // removes it in a finally block, so it leaves no trace.
            "assert_geometry", "find_unsupported", "section_preview",
            "get_operation_result", "list_operations",
        };

        public CommandHandler()
        {
            _commands = new Dictionary<string, Func<JObject, JObject>>
            {
                // Context & Scene
                ["get_context"] = W(GetContext), ["get_selection"] = W(GetSelection),
                ["list_commands"] = W(ListCommands),
                // v4.14: retrieve the result of a call the client gave up waiting for.
                ["get_operation_result"] = W(GetOperationResultCmd),
                ["list_operations"] = W(ListOperationsCmd),
                ["get_scene_summary"] = W(GetSceneSummary), ["get_objects"] = W(GetObjects), ["list_objects"] = W(GetObjects),
                ["get_object_details"] = W(GetObjectDetails), ["get_object_info"] = W(GetObjectDetails),
                // Architecture
                ["create_wall"] = U("Wall", CreateWall), ["create_slab"] = U("Slab", CreateSlab),
                ["create_column"] = U("Column", CreateColumn), ["create_opening"] = U("Opening", CreateOpening),
                ["create_roof"] = U("Roof", CreateRoof),
                // Phase 5 - Architect intelligence layer
                ["query_scene"] = W(QueryScene),
                ["create_massing"] = U("Massing", CreateMassing),
                ["derive_floors_from_mass"] = U("FloorsFromMass", DeriveFloorsFromMass),
                ["create_core"] = U("Core", CreateCore),
                ["place_openings_on_facade"] = U("FacadeOpenings", PlaceOpeningsOnFacade),
                ["align_to_grid"] = U("AlignGrid", AlignToGrid),
                ["report_areas"] = W(ReportAreas),
                // Universal create + modify + transform (Phase 6 universal transform)
                ["create_object"] = U("Create", CreateObject), ["modify_object"] = U("Modify", ModifyObject),
                ["transform_objects"] = U("Transform", TransformObjects),
                // Primitives
                ["create_box"] = U("Box", CreateBox), ["create_cylinder"] = U("Cyl", CreateCylinder),
                ["create_sphere"] = U("Sphere", CreateSphere), ["create_line"] = U("Line", CreateLine),
                ["create_polyline"] = U("Polyline", CreatePolyline),
                // Advanced
                ["loft"] = U("Loft", Loft), ["loft_surface"] = U("Loft", Loft),
                ["sweep1"] = U("Sweep", Sweep1), ["sweep2"] = U("Sweep2", Sweep2),
                ["pipe"] = U("Pipe", Pipe), ["pipe_curve"] = U("Pipe", Pipe),
                ["extrude_curve"] = U("Extrude", ExtrudeCurve),
                ["network_surface"] = U("NetworkSurface", NetworkSurface),
                ["sphere_patch"] = U("SpherePatch", SpherePatch),
                ["trim_with_planes"] = U("TrimPlanes", TrimWithPlanes),
                // Smart ops
                ["fillet_edges"] = U("Fillet", FilletEdges), ["offset_curve"] = U("Offset", OffsetCurve),
                ["extrude_curves"] = U("Extrude", ExtrudeCurves), ["join_curves"] = U("Join", JoinCurves),
                ["offset_and_extrude"] = U("OffExtr", OffsetAndExtrude),
                // Transforms
                ["move_objects"] = U("Move", MoveObjects), ["rotate_objects"] = U("Rotate", RotateObjects),
                ["scale_objects"] = U("Scale", ScaleObjects), ["mirror_objects"] = U("Mirror", MirrorObjects),
                ["array_objects"] = U("Array", ArrayObjects), ["delete_objects"] = U("Delete", DeleteObjects),
                ["boolean_operation"] = U("Boolean", BooleanOp),
                // Layers
                ["list_layers"] = W(ListLayers), ["create_layer"] = W(CreateLayer),
                ["create_or_set_layer"] = W(CreateLayer), ["set_active_layer"] = W(SetActiveLayer),
                ["delete_layer"] = U("DelLayer", DeleteLayer), ["set_object_layer"] = U("SetLayer", SetObjectLayer),
                ["batch_layer_visibility"] = W(BatchLayerVis), ["setup_arch_layers"] = W(SetupArchLayers),
                // Analysis
                ["measure_object"] = W(MeasureObject), ["measure_distance"] = W(MeasureDistance),
                ["check_intersection"] = W(CheckIntersection), ["validate_objects"] = W(ValidateObjects),
                ["detect_clashes"] = W(DetectClashes),
                // Viewport
                ["set_view"] = W(SetView), ["set_display_mode"] = W(SetDisplayMode),
                ["capture_viewport"] = W(CaptureViewport), ["get_viewport_image"] = W(CaptureViewport),
                ["capture_inspection_view"] = W(CaptureInspectionView),
                ["select_objects"] = W(SelectObjects), ["set_selection"] = W(SelectObjects),
                ["select_by_semantic"] = W(SelectBySemanticCmd),
                ["set_camera"] = W(SetCamera), ["get_rhino_commands"] = W(GetRhinoCommands),
                // Materials & Commands
                ["set_layer_material"] = W(SetLayerMaterial),
                ["run_command"] = U("RhinoCmd", RunCommand),
                // Workflow (Tier 2)
                ["get_cross_section"] = U("Section", GetCrossSection),
                ["get_section_profile"] = W(GetSectionProfile), ["get_silhouette"] = W(GetSilhouette),
                ["create_floor_stack"] = U("Floors", CreateFloorStack),
                ["group_objects"] = U("Group", GroupObjects), ["ungroup_objects"] = U("Ungroup", UngroupObjects),
                ["get_groups"] = W(GetGroups), ["hollow_solid"] = U("Hollow", HollowSolid),
                ["create_objects_batch"] = U("Batch", BatchCreate),
                // Intelligence (Tier 3)
                ["validate_architecture"] = W(ValidateArch), ["suggest_tools"] = W(SuggestTools),
                ["lint_script"] = W(LintScript), ["get_camera_target"] = W(GetCameraTarget),
                // Script & Undo & Logs
                ["execute_script"] = W(ExecuteScript), ["run_python"] = W(ExecuteScript),
                ["start_script_server"] = W(StartScriptServer),
                ["undo"] = W(DoUndo), ["redo"] = W(DoRedo),
                ["get_log"] = W(GetLog), ["get_log_stats"] = W(GetLogStats),
                // v4.7: Sections, Elevations, Plans
                ["create_section"] = W(CreateSectionCmd), ["create_elevation"] = W(CreateElevationCmd),
                ["cut_section"] = W(CutSectionCmd), ["align_view_to_section"] = W(AlignViewToSectionCmd),
                ["create_plan"] = W(CreatePlanCmd), ["create_all_plans"] = W(CreateAllPlansCmd),
                ["list_sections"] = W(ListSectionsCmd), ["update_section"] = W(UpdateSectionCmd),
                ["remove_section"] = W(RemoveSectionCmd),
                // v4.7: Illustration & Display Modes
                ["create_display_mode"] = W(CreateDisplayModeCmd), ["apply_display_mode"] = W(ApplyDisplayModeCmd),
                ["list_display_modes"] = W(ListDisplayModesCmd), ["adjust_display_mode"] = W(AdjustDisplayModeCmd),
                ["delete_display_mode"] = W(DeleteDisplayModeCmd), ["capture_illustration"] = W(CaptureIllustrationCmd),
                // v4.7: Material Intelligence
                ["apply_downloaded_material"] = W(ApplyDownloadedMaterialCmd), ["edit_material"] = W(EditMaterialCmd),
                ["list_materials"] = W(ListMaterialsCmd), ["get_material"] = W(GetMaterialCmd),
                // v4.7: File Tracing
                ["import_dwg"] = W(ImportDwgCmd), ["calibrate_scale"] = W(CalibrateScaleCmd),
                ["apply_traced_elements"] = W(ApplyTracedElementsCmd),
                ["get_trace_layers"] = W(GetTraceLayersCmd), ["clear_trace_layers"] = W(ClearTraceLayersCmd),
                // v4.7.4: Tier 1 — accuracy + speed boosters
                ["set_state"] = W(SetState), ["get_state"] = W(GetState), ["clear_state"] = W(ClearState),
                ["set_pbr_material"] = W(SetPbrMaterial),
                ["revolve_profile"] = U("Revolve", RevolveProfile),
                ["create_layer_tree"] = W(CreateLayerTree),
                ["thumbnail"] = W(Thumbnail),
                // v4.7.4: Tier 2 — workflow features
                ["export_objects"] = W(ExportObjects),
                ["save_checkpoint"] = W(SaveCheckpoint), ["restore_checkpoint"] = U("Restore", RestoreCheckpoint),
                ["list_checkpoints"] = W(ListCheckpoints), ["delete_checkpoint"] = W(DeleteCheckpoint),
                ["get_recovery_log"] = W(GetRecoveryLog),
                // v4.7.5: Semantic intelligence (was missing from dispatch)
                ["analyze_architecture"] = W(AnalyzeArchitectureCmd),
                ["get_building_systems"] = W(GetBuildingSystemsCmd),
                ["get_level_summary"] = W(GetLevelSummaryCmd),
                ["detect_design_patterns"] = W(DetectDesignPatternsCmd),
                ["find_unassigned_geometry"] = W(FindUnassignedCmd),
                ["batch_preview"] = W(BatchPreviewCmd),
                // v4.10.1: intent validation (field report §4)
                ["assert_geometry"] = W(AssertGeometry),
                ["find_unsupported"] = W(FindUnsupported),
                ["section_preview"] = W(SectionPreview),
                // v4.7.5: Design Memory (was missing from dispatch)
                ["set_design_brief"] = W(SetDesignBrief),
                ["get_design_brief"] = W(GetDesignBrief),
                ["tag_object"] = W(TagObjectCmd),
                ["get_provenance"] = W(GetProvenance),
                ["search_memory"] = W(SearchMemory),
                ["get_related_objects"] = W(GetRelatedObjects),
                ["name_group"] = W(NameGroupCmd),
                ["get_group"] = W(GetGroupCmd),
                ["get_all_groups"] = W(GetAllGroupsCmd),
                ["add_design_rule"] = W(AddDesignRule),
                ["get_design_rules"] = W(GetDesignRules),
                ["log_session"] = W(LogSessionCmd),
                // v4.7.5: Incremental Scene Sync (was missing from dispatch)
                ["get_scene_diff"] = W(GetSceneDiff),
                ["get_change_log"] = W(GetChangeLogCmd),
                ["get_tracker_version"] = W(GetTrackerVersion),
            };
        }

        public JObject Dispatch(JObject cmd)
        {
            string type = cmd["type"]?.ToString() ?? "";

            if (CodeExecCommands.Contains(type) && Mode != BridgeMode.Developer)
                return Err($"Command '{type}' requires Developer mode.", "MODE_BLOCKED");
            if (DestructiveCommands.Contains(type) && Mode == BridgeMode.Safe)
                return Err($"Command '{type}' is blocked in Safe mode.", "MODE_BLOCKED");

            var p = cmd["params"] as JObject ?? new JObject();
            if (type == "batch")
            {
                return DispatchBatch(cmd);
            }
            return _commands.TryGetValue(type, out var h) ? h(p) : Err($"Unknown command: {type}");
        }

        // --- Phase 3: atomic batches + reference resolution --------------------------------------------------
        // 
        // A batch is { type: "batch", commands: [...], atomic: bool, stop_on_error: bool }.
        // 
        // - atomic=true  -> wrap whole batch in one Rhino undo record. On any failure, roll back
        //                  via Doc.Undo() and return error with all results so Claude sees what
        //                  happened. The U decorator's per-op undo records are suppressed via
        //                  _atomicBatchDepth so the single outer record holds everything.
        // 
        // - References: any string starting with "$N" inside a sub-op's params resolves to the
        //               Nth (1-indexed) prior result, with optional dot/bracket path. So you can
        //               feed "$1.object_ids[0]" into op 2 to chain ops without an extra round-trip.
        //               This is the architect's superpower: build -> derive -> punch in one batch.
        JObject DispatchBatch(JObject cmd)
        {
            var commands = cmd["commands"] as JArray ?? new JArray();

            // Dry run: validate without executing
            if (cmd["dry_run"]?.ToObject<bool>() == true)
                return BatchPlanner.Preview(commands, _commands);

            bool atomic = cmd["atomic"]?.ToObject<bool>() ?? false;
            // For atomic batches stop_on_error defaults to true (rollback semantics need it).
            bool stopOnError = cmd["stop_on_error"]?.ToObject<bool>() ?? atomic;
            var results = new JArray();
            var prior = new List<JObject>();
            var failed = new JArray();
            uint undo = 0;
            bool endedUndo = false;

            // Suppress per-sub-op thumbnails; we add one for the whole batch at the end.
            _batchDepth++;

            try
            {
                using (RedrawScope.Defer())
                {
                    if (atomic)
                    {
                        undo = Doc.BeginUndoRecord("AI: Atomic Batch");
                        _atomicBatchDepth++;
                    }

                    for (int i = 0; i < commands.Count; i++)
                    {
                        // v4.8: cooperative cancellation - a cancel frame (or a client
                        // timeout) stops the batch at the next op boundary; atomic
                        // batches then roll back through the normal failure path.
                        if (OperationRegistry.CancelRequested)
                        {
                            var cc = Err("Batch cancelled by client", "CANCELLED");
                            cc["index"] = i;
                            cc["op_index"] = i + 1;
                            results.Add(cc);
                            failed.Add(i);
                            break;
                        }
                        var raw = commands[i] as JObject ?? new JObject();
                        JObject sub;
                        try
                        {
                            // DeepClone so reference resolution doesn't mutate the caller's input.
                            sub = ResolveReferences((JObject)raw.DeepClone(), prior);
                        }
                        catch (Exception e)
                        {
                            var rr = Err($"Reference resolution failed at batch op {i + 1}: {e.Message}");
                            rr["index"] = i;
                            rr["op_index"] = i + 1;
                            results.Add(rr);
                            failed.Add(i);
                            if (stopOnError) break;
                            prior.Add(rr);
                            continue;
                        }

                        var r = Dispatch(sub);
                        r["index"] = i;
                        r["op_index"] = i + 1;
                        results.Add(r);
                        prior.Add(r);

                        if (r["status"]?.ToString() != "ok")
                        {
                            failed.Add(i);
                            if (stopOnError) break;
                        }
                    }
                }
            }
            finally
            {
                _batchDepth = Math.Max(0, _batchDepth - 1);
                if (atomic) _atomicBatchDepth = Math.Max(0, _atomicBatchDepth - 1);
                if (undo > 0)
                {
                    Doc.EndUndoRecord(undo);
                    endedUndo = true;
                }
            }

            if (failed.Count > 0)
            {
                int firstFailed = failed[0].ToObject<int>();
                if (atomic && endedUndo)
                {
                    try
                    {
                        // Single Doc.Undo() pops the whole batch-level record because we
                        // suppressed nested undo records via _atomicBatchDepth.
                        Doc.Undo();
                        RedrawScope.Mark();
                    }
                    catch (Exception e)
                    {
                        AIBridgeLogger.Log(LogLevel.ERROR, "Batch", "Atomic rollback failed", error: e.ToString());
                        return Err("Atomic batch failed and rollback failed", "BATCH_ROLLED_BACK", new JObject
                        {
                            ["rollback_error"] = e.Message,
                            ["failed_index"] = firstFailed,
                            ["results"] = results
                        });
                    }
                }
                return new JObject
                {
                    ["status"] = "error",
                    ["error_code"] = "BATCH_ROLLED_BACK",
                    ["message"] = atomic ? $"Atomic batch failed at op {firstFailed + 1}; changes rolled back" : $"Batch failed at op {firstFailed + 1}",
                    ["failed_index"] = firstFailed,
                    ["failed_indices"] = failed,
                    ["atomic"] = atomic,
                    ["rolled_back"] = atomic,
                    ["results"] = results
                };
            }

            var batchOk = new JObject
            {
                ["status"] = "ok",
                ["atomic"] = atomic,
                ["count"] = results.Count,
                ["results"] = results
            };
            // One thumbnail for the entire batch - Claude sees the final state without an
            // extra capture_viewport round-trip.
            var batchThumb = TryCaptureThumbnail();
            if (batchThumb != null) batchOk["thumbnail_base64"] = batchThumb;
            // v4.8: big batches also get a plan + front strip so the model can verify
            // massing and elevation without a follow-up capture round-trip.
            if (results.Count >= 8)
            {
                var planThumb = TryCaptureViewThumbnail("top");
                if (planThumb != null) batchOk["thumbnail_plan_base64"] = planThumb;
                var frontThumb = TryCaptureViewThumbnail("front");
                if (frontThumb != null) batchOk["thumbnail_front_base64"] = frontThumb;
            }
            return batchOk;
        }

        JObject ResolveReferences(JObject obj, List<JObject> prior)
        {
            return (JObject)ResolveToken(obj, prior);
        }

        JToken ResolveToken(JToken token, List<JObject> prior)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String)
            {
                var s = token.ToString();
                if (TryResolveReference(s, prior, out var resolved)) return resolved.DeepClone();
                return token;
            }
            if (token is JObject o)
            {
                foreach (var prop in o.Properties().ToList()) prop.Value = ResolveToken(prop.Value, prior);
                return o;
            }
            if (token is JArray a)
            {
                for (int i = 0; i < a.Count; i++) a[i] = ResolveToken(a[i], prior);
                return a;
            }
            return token;
        }

        // Matches "$N" or "$N.path" where path can have dots and [N] indexes.
        static readonly Regex RefRegex = new Regex(@"^\$(\d+)(?:\.(.+))?$", RegexOptions.Compiled);

        bool TryResolveReference(string value, List<JObject> prior, out JToken resolved)
        {
            resolved = null;
            var m = RefRegex.Match(value ?? "");
            if (!m.Success) return false;

            int op = int.Parse(m.Groups[1].Value);
            if (op < 1 || op > prior.Count) throw new InvalidOperationException($"${op} has no prior result");
            resolved = prior[op - 1];

            var path = m.Groups[2].Success ? m.Groups[2].Value : "";
            if (!string.IsNullOrWhiteSpace(path))
            {
                resolved = ResolvePath(resolved, path);
                if (resolved == null) throw new InvalidOperationException($"Reference ${op}.{path} resolved to null");
            }
            return true;
        }

        JToken ResolvePath(JToken root, string path)
        {
            var cur = root;
            int pos = 0;
            foreach (Match part in Regex.Matches(path, @"([^\.\[\]]+)|(\[(\d+)\])"))
            {
                var gap = path.Substring(pos, part.Index - pos);
                if (gap.Trim('.').Length != 0)
                    throw new InvalidOperationException($"Malformed reference path near '{path.Substring(pos)}'");
                pos = part.Index + part.Length;

                if (part.Groups[1].Success)
                {
                    cur = cur?[part.Groups[1].Value];
                }
                else if (part.Groups[3].Success)
                {
                    var arr = cur as JArray;
                    int idx = int.Parse(part.Groups[3].Value);
                    cur = arr != null && idx >= 0 && idx < arr.Count ? arr[idx] : null;
                }
                if (cur == null) return null;
            }
            if (path.Substring(pos).Trim('.').Length != 0)
                throw new InvalidOperationException($"Malformed reference path: '{path}'");
            return cur;
        }

        // --- Decorators --------------------------------------------------
        // U = mutating: open a Rhino undo record + a deferred-redraw scope.
        // Inside an atomic batch, suppress per-op undo records so the batch-level record
        // holds the full atomic unit (allowing single-Doc.Undo() rollback).
        // Auto-thumbnail: after the RedrawScope exits (so viewport is updated), captures a small
        // JPEG and embeds it in the response - only at the top level, not inside any batch.
        Func<JObject, JObject> U(string name, Func<JObject, JObject> fn) => (p) =>
        {
            uint uid = 0;
            if (_atomicBatchDepth == 0) uid = Doc.BeginUndoRecord($"AI: {name}");
            JObject result;
            JObject autoCheckpoint = null;
            try
            {
                if (_batchDepth == 0 && _atomicBatchDepth == 0
                    && AutoCheckpointUndoNames.Contains(name))
                {
                    // v4.10.1: per-call policy. `checkpoint` wins; legacy
                    // auto_checkpoint:false still means "off".
                    var policy = p?["checkpoint"]?.ToString();
                    if (string.IsNullOrWhiteSpace(policy))
                        policy = p?["auto_checkpoint"]?.ToObject<bool>() == false ? "off" : "auto";
                    autoCheckpoint = SaveAutoCheckpoint(name, policy);
                    // Keep responses lean: only report a checkpoint that was actually
                    // written, or a skip that leaves a real gap (throttled).
                    if (autoCheckpoint?["skipped"]?.ToObject<bool>() == true &&
                        autoCheckpoint["reason"]?.ToString()?.StartsWith("throttled") != true)
                    {
                        autoCheckpoint = null;
                    }
                }
                using (RedrawScope.Defer())
                {
                    result = fn(p);
                }
                if (autoCheckpoint != null)
                    result["auto_checkpoint"] = autoCheckpoint;
                // RedrawScope has exited - exactly one Redraw() has fired. Capture thumbnail
                // only at the top level (not inside a batch - batch adds its own at the end).
                if (_batchDepth == 0 && result?["status"]?.ToString() == "ok")
                {
                    var thumb = TryCaptureThumbnail();
                    if (thumb != null) result["thumbnail_base64"] = thumb;
                }
            }
            catch (Exception e)
            {
                AIBridgeLogger.Log(LogLevel.ERROR, "Cmd", e.Message, name, error: e.ToString());
                result = ErrFromException(e, name);
                if (autoCheckpoint != null)
                    result["auto_checkpoint"] = autoCheckpoint;
            }
            finally { if (uid > 0) Doc.EndUndoRecord(uid); }
            return result;
        };

        // W = read-only / no-undo: only the deferred-redraw scope (cheap, no-op if nothing mutates)
        Func<JObject, JObject> W(Func<JObject, JObject> fn) => (p) =>
        {
            try { return fn(p); }
            catch (Exception e)
            {
                AIBridgeLogger.Log(LogLevel.ERROR, "Cmd", e.Message, error: e.ToString());
                return ErrFromException(e);
            }
        };

        JObject GetOperationResultCmd(JObject p)
            => OperationRegistry.Lookup(p["request_id"]?.ToString());

        JObject ListOperationsCmd(JObject p)
        {
            var running = OperationRegistry.InFlightIds();
            return Ok(("running", running), ("running_count", running.Count));
        }

        // Machine-readable dispatch table: lets the MCP server generate its
        // capabilities resource from the live registry instead of a hand-
        // maintained list that drifts (v4.10).
        JObject ListCommands(JObject p)
        {
            var names = _commands.Keys.Concat(new[] { "batch" }).OrderBy(k => k);
            return new JObject
            {
                ["status"] = "ok",
                ["commands"] = new JArray(names),
                ["count"] = _commands.Count + 1,
                ["protocol_version"] = AIBridgeServer.PROTOCOL_VERSION,
                ["note"] = "Every command is callable directly over TCP or as a batch sub-op ({type, params})."
            };
        }

        // --- Helpers --------------------------------------------------
        static RhinoDoc Doc => RhinoDoc.ActiveDoc;
        static double Tol => Doc.ModelAbsoluteTolerance;

        static JObject Ok(params (string k, JToken v)[] ps)
        {
            var j = new JObject { ["status"] = "ok" };
            foreach (var (k, v) in ps) j[k] = v;
            return j;
        }

        static JObject Err(string m, string code = "COMMAND_FAILED", JObject diag = null)
        {
            var j = new JObject { ["status"] = "error", ["error_code"] = code, ["message"] = m };
            j["recoverable"] = code is not ("AUTH_FAILED" or "MODE_BLOCKED" or "INVALID_REQUEST");
            j["retry_hint"] = code switch
            {
                "INVALID_REQUEST" => "A parameter was the wrong shape or type. Re-read the tool schema - "
                                   + "selectors accept a plain string or an array of strings.",
                "SCRIPT_ERROR" => "This is a Python error in your script, not a geometry problem. "
                                + "Read the message and fix the code. Remember execute_script runs "
                                + "IronPython 2 (no f-strings/type hints), and call rab.help() to see "
                                + "the helper API instead of guessing names.",
                "LAYER_NOT_FOUND" => "Use list_layers or pass the full Parent::Child layer path.",
                "OBJECT_NOT_FOUND" => "Call query_scene/list_objects again and use the current object id.",
                "NOT_A_CURVE" => "Pass a curve id, or create/extract a curve first.",
                "NOT_A_BREP" => "Pass a Brep or polysurface id for this operation.",
                "CAPTURE_FAILED" => "Try smaller dimensions, a lighter display mode, or wireframe=true.",
                "MODE_BLOCKED" => "Switch AIBridge to Standard or Developer mode in Rhino, then retry.",
                "COMMAND_FAILED" => "Check diagnostics and simplify the inputs; this is usually a geometry validity issue.",
                _ => null
            };
            if (diag != null) j["diagnostics"] = diag;
            return j;
        }

        static JObject ErrFromException(Exception e, string commandName = null)
        {
            var diag = new JObject { ["exception_type"] = e.GetType().Name };
            if (!string.IsNullOrEmpty(commandName)) diag["command"] = commandName;
            return Err(e.Message, ClassifyErrorCode(e), diag);
        }

        // A Python-level mistake in a script is NOT a geometry problem. Telling the
        // caller "this is usually a geometry validity issue" for a NameError sent
        // real sessions off debugging the wrong thing - a misleading hint is worse
        // than no hint. (field report v4.11)
        static readonly string[] PythonErrorMarkers =
        {
            "is not defined", "invalid syntax", "has no attribute", "unexpected indent",
            "not callable", "takes exactly", "takes no arguments", "cannot import",
            "unsupported operand", "object is not", "unexpected token", "IndentationError",
        };

        static readonly HashSet<string> PythonExceptionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UnboundNameException", "SyntaxErrorException", "MissingMemberException",
            "ArgumentTypeException", "ImportException", "IndentationException",
        };

        static bool LooksLikeScriptError(Exception e)
        {
            if (e == null) return false;
            if (PythonExceptionTypes.Contains(e.GetType().Name)) return true;
            var m = e.Message ?? "";
            foreach (var marker in PythonErrorMarkers)
                if (m.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static string ClassifyErrorCode(Exception e)
        {
            var m = e.Message ?? "";
            if (LooksLikeScriptError(e)) return "SCRIPT_ERROR";
            // A JSON conversion failure is a caller/parameter problem, never geometry.
            if (e is Newtonsoft.Json.JsonException) return "INVALID_REQUEST";
            if (e is FormatException || e is ArgumentException) return "INVALID_REQUEST";
            if (m.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) return "OBJECT_NOT_FOUND";
            if (m.IndexOf("not a curve", StringComparison.OrdinalIgnoreCase) >= 0) return "NOT_A_CURVE";
            if (m.IndexOf("not a brep", StringComparison.OrdinalIgnoreCase) >= 0) return "NOT_A_BREP";
            if (m.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0 &&
                m.IndexOf("not", StringComparison.OrdinalIgnoreCase) >= 0) return "LAYER_NOT_FOUND";
            if (m.IndexOf("capture", StringComparison.OrdinalIgnoreCase) >= 0) return "CAPTURE_FAILED";
            return "COMMAND_FAILED";
        }

        // --- Checkpoint economics (v4.10.1, field report §5) -----------------
        // A full .3dm snapshot per mutating call cost ~2.5 GB in one 8,000-object
        // session, including calls that created nothing. Three guards:
        //   1. scene unchanged since the last checkpoint  -> skip (the existing file
        //      is still an accurate rollback point)
        //   2. large documents                            -> throttle by interval
        //   3. explicit per-call policy                   -> off | auto | force
        private static long _lastCpSceneVersion = -1;
        private static long _lastCpSizeKb;
        private static DateTime _lastCpTimeUtc = DateTime.MinValue;
        private const long CP_THROTTLE_ABOVE_KB = 20_000;
        private static readonly TimeSpan CP_MIN_INTERVAL = TimeSpan.FromMinutes(2);

        private static long CurrentSceneVersion()
        {
            try { return SceneSnapshotRegistry.Active?.SceneVersion ?? -1; }
            catch { return -1; }
        }

        private static void NoteCheckpointTaken(long sceneVersion, long sizeKb)
        {
            _lastCpSceneVersion = sceneVersion;
            _lastCpSizeKb = sizeKb;
            _lastCpTimeUtc = DateTime.UtcNow;
        }

        private static JObject CheckpointSkipped(string reason, string operation) => new JObject
        {
            ["skipped"] = true,
            ["reason"] = reason,
            ["operation"] = operation,
            ["note"] = "Pass checkpoint='force' to snapshot anyway, or 'off' to never snapshot this call.",
        };

        JObject SaveAutoCheckpoint(string operation, string policy = "auto")
        {
            policy = (policy ?? "auto").ToLowerInvariant();
            if (policy == "off") return CheckpointSkipped("checkpoint policy 'off'", operation);

            long sv = CurrentSceneVersion();
            if (policy != "force")
            {
                // 1. Nothing has changed since the last snapshot - it still restores
                //    the current state, so writing an identical copy is pure waste.
                if (sv >= 0 && sv == _lastCpSceneVersion)
                {
                    // Word this as a statement about the PRE-RUN snapshot. "scene unchanged"
                    // was read as "nothing happened" on a script that went on to create 260
                    // objects (field report A8).
                    return CheckpointSkipped(
                        $"pre-run snapshot identical to the existing checkpoint (scene_version {sv}); "
                        + "this says nothing about what this command is about to do", operation);
                }
                // 2. Big documents: don't pay a full write on every call.
                if (_lastCpSizeKb > CP_THROTTLE_ABOVE_KB &&
                    DateTime.UtcNow - _lastCpTimeUtc < CP_MIN_INTERVAL)
                {
                    return CheckpointSkipped(
                        $"throttled: last checkpoint was {_lastCpSizeKb / 1024} MB, less than " +
                        $"{CP_MIN_INTERVAL.TotalMinutes:0} min ago", operation);
                }
            }

            try
            {
                var name = $"auto_{operation}_{DateTime.Now:yyyyMMdd_HHmmss}";
                var cp = SaveCheckpoint(new JObject { ["name"] = name });
                if (cp?["status"]?.ToString() == "ok")
                {
                    NoteCheckpointTaken(sv, cp["size_kb"]?.ToObject<long>() ?? 0);
                    return new JObject
                    {
                        ["checkpoint"] = cp["checkpoint"],
                        ["path"] = cp["path"],
                        ["size_kb"] = cp["size_kb"],
                        ["operation"] = operation
                    };
                }
                return new JObject
                {
                    ["status"] = "warning",
                    ["message"] = cp?["message"]?.ToString() ?? "Auto-checkpoint failed"
                };
            }
            catch (Exception e)
            {
                return new JObject
                {
                    ["status"] = "warning",
                    ["message"] = $"Auto-checkpoint failed: {e.Message}"
                };
            }
        }

        static Point3d Pt(JToken t)
        {
            if (t == null) return Point3d.Origin;
            var a = t.ToObject<double[]>();
            return new Point3d(a[0], a[1], a.Length > 2 ? a[2] : 0);
        }

        static Vector3d Vec(JToken t)
        {
            var a = t.ToObject<double[]>();
            return new Vector3d(a[0], a[1], a.Length > 2 ? a[2] : 0);
        }

        static JArray PA(Point3d p) => new JArray { Math.Round(p.X, 2), Math.Round(p.Y, 2), Math.Round(p.Z, 2) };

        static JObject BB(BoundingBox b) => new JObject
        {
            ["min"] = PA(b.Min),
            ["max"] = PA(b.Max),
            ["size"] = new JObject
            {
                ["x"] = Math.Round(b.Max.X - b.Min.X, 2),
                ["y"] = Math.Round(b.Max.Y - b.Min.Y, 2),
                ["z"] = Math.Round(b.Max.Z - b.Min.Z, 2)
            }
        };

        static JArray CA(Color c) => new JArray { c.R, c.G, c.B };

        static JArray PointsJson(IEnumerable<Point3d> pts)
        {
            var a = new JArray();
            foreach (var pt in pts) a.Add(PA(pt));
            return a;
        }

        static List<Point3d> SampleCurvePoints(Curve c, int samples = 80)
        {
            var pts = new List<Point3d>();
            if (c == null) return pts;
            var div = c.DivideByCount(Math.Max(4, samples), true);
            if (div != null && div.Length > 0)
            {
                pts.AddRange(div.Select(t => c.PointAt(t)));
            }
            else
            {
                pts.Add(c.PointAtStart);
                pts.Add(c.PointAtEnd);
            }
            return pts;
        }

        static JObject PolylinesPayload(IEnumerable<List<Point3d>> polylines, string projection = "xy")
        {
            var lines = polylines.Where(l => l.Count > 0).ToList();
            var arr = new JArray();
            foreach (var line in lines) arr.Add(PointsJson(line));

            var all = lines.SelectMany(l => l).ToList();
            var bbox = all.Count > 0 ? new BoundingBox(all) : BoundingBox.Empty;
            return new JObject
            {
                ["polylines"] = arr,
                ["polyline_count"] = lines.Count,
                ["bbox"] = bbox.IsValid ? BB(bbox) : null,
                ["svg"] = BuildSvg(lines, projection)
            };
        }

        static string BuildSvg(IEnumerable<List<Point3d>> polylines, string projection = "xy")
        {
            var lines = polylines.Where(l => l.Count > 0).ToList();
            if (lines.Count == 0) return "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\" />";
            Func<Point3d, (double x, double y)> map = projection.ToLowerInvariant() switch
            {
                "xz" => p => (p.X, p.Z),
                "yz" => p => (p.Y, p.Z),
                _ => p => (p.X, p.Y)
            };
            var flat = lines.SelectMany(l => l).Select(map).ToList();
            double minX = flat.Min(p => p.x), maxX = flat.Max(p => p.x);
            double minY = flat.Min(p => p.y), maxY = flat.Max(p => p.y);
            double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{minX:F3} {-maxY:F3} {w:F3} {h:F3}\">");
            sb.Append("<g fill=\"none\" stroke=\"black\" stroke-width=\"1\" vector-effect=\"non-scaling-stroke\">");
            foreach (var line in lines)
            {
                var d = new StringBuilder();
                for (int i = 0; i < line.Count; i++)
                {
                    var (x, y) = map(line[i]);
                    d.Append(i == 0 ? "M " : " L ");
                    d.Append($"{x:F3} {-y:F3}");
                }
                sb.Append($"<path d=\"{d}\" />");
            }
            sb.Append("</g></svg>");
            return sb.ToString();
        }

        static int EnsureLayer(string name, int[] color = null)
        {
            int idx = Doc.Layers.FindByFullPath(name, -1);
            if (idx < 0)
            {
                var l = new Layer { Name = name };
                if (color != null) l.Color = Color.FromArgb(color[0], color[1], color[2]);
                idx = Doc.Layers.Add(l);
            }
            return idx;
        }

        static ObjectAttributes MkAttr(JObject p)
        {
            var a = new ObjectAttributes();
            var ln = p["layer"]?.ToString();
            if (!string.IsNullOrEmpty(ln)) a.LayerIndex = EnsureLayer(ln);
            var nm = p["name"]?.ToString();
            if (!string.IsNullOrEmpty(nm)) a.Name = nm;
            var c = p["color"]?.ToObject<int[]>();
            if (c != null && c.Length >= 3)
            {
                a.ObjectColor = Color.FromArgb(c[0], c[1], c[2]);
                a.ColorSource = ObjectColorSource.ColorFromObject;
            }
            return a;
        }

        static List<RhinoObject> AllObjs()
        {
            var s = new ObjectEnumeratorSettings { DeletedObjects = false, HiddenObjects = true, LockedObjects = true };
            return Doc.Objects.GetObjectList(s).ToList();
        }

        // Snapshot accessor - null safe.
        static List<string> CaptureAddedIds(Action action)
        {
            var added = new List<string>();
            EventHandler<Rhino.DocObjects.RhinoObjectEventArgs> handler = (s, e) =>
            {
                try { if (e.TheObject != null && ReferenceEquals(e.TheObject.Document, Doc)) added.Add(e.ObjectId.ToString()); }
                catch { }
            };
            RhinoDoc.AddRhinoObject += handler;
            try { action(); }
            finally { RhinoDoc.AddRhinoObject -= handler; }
            return added
                .Where(id => Guid.TryParse(id, out var gid) && Doc.Objects.FindId(gid) != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string baseName = Path.GetFileName(raw.Trim());
            if (string.IsNullOrEmpty(baseName) || baseName == "." || baseName == "..") return null;
            foreach (var c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
            baseName = baseName.Replace("/", "_").Replace("\\", "_").Trim();
            return string.IsNullOrEmpty(baseName) ? null : baseName;
        }

        static SceneSnapshot Snap => SceneSnapshotRegistry.Get(Doc);

        static List<string> ResIds(JToken t)
        {
            if (t == null) return new List<string>();
            var ids = t.ToObject<List<string>>();
            if (ids == null || ids.Count == 0) return new List<string>();
            var f = ids[0];

            // selected/last_created don't have snapshot indexes (selection state is noisy and
            // last_created depends on Rhino's internal pointer, not our cache).
            if (f == "selected") return Doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id.ToString()).ToList();
            if (f == "last_created") { var o = Doc.Objects.MostRecentObject(); return o != null ? new List<string> { o.Id.ToString() } : new(); }

            // Phase 2: prefer the snapshot for id-only lookups. O(M) where M = result size.
            var snap = Snap;
            if (snap != null)
            {
                if (f == "all") return snap.All().Select(m => m.Id.ToString()).ToList();
                // by_layer: includes descendants (the common intent in nested trees).
                // by_layer_exact: restores the old single-layer behaviour.
                if (f.StartsWith("by_layer_exact:"))
                    return snap.ByLayerName(f[15..], includeDescendants: false).Select(m => m.Id.ToString()).ToList();
                if (f.StartsWith("by_layer:"))
                    return snap.ByLayerName(f[9..]).Select(m => m.Id.ToString()).ToList();
                if (f.StartsWith("by_name:"))
                    return snap.ByNameSubstring(f[8..]).Select(m => m.Id.ToString()).ToList();
            }
            else
            {
                // Snapshot unavailable (shouldn't happen in v4, but degrade gracefully).
                if (f == "all") return AllObjs().Select(o => o.Id.ToString()).ToList();
                if (f.StartsWith("by_layer:")) { int i = Doc.Layers.FindByFullPath(f[9..], -1); return AllObjs().Where(o => o.Attributes.LayerIndex == i).Select(o => o.Id.ToString()).ToList(); }
                if (f.StartsWith("by_name:")) { var p = f[8..].Replace("*", "").ToLower(); return AllObjs().Where(o => (o.Attributes.Name ?? "").ToLower().Contains(p)).Select(o => o.Id.ToString()).ToList(); }
            }
            return ids;
        }

        static Brep GetBrep(RhinoObject o) => o?.Geometry is Brep b ? b : o?.Geometry is Extrusion e ? e.ToBrep() : null;

        /// <summary>
        /// Build a creation result. By default returns just ids + bbox - what the next tool call needs.
        /// Pass measure:true in params (or set asBatch:false in the caller's caller) to include area/volume.
        /// 
        /// In v3, AreaMassProperties.Compute and VolumeMassProperties.Compute ran on every single create -
        /// for an architect doing "create_floor_stack levels=30" that's 30 unwanted Brep integrations.
        /// In v4 this is opt-in. Callers that need it (measure_object, validate_architecture) ask explicitly.
        /// </summary>
        static JObject CrResult(Guid gid, string layer = null, bool measure = false)
        {
            if (gid == Guid.Empty) { var m = Doc.Objects.MostRecentObject(); if (m != null) gid = m.Id; else return Err("Creation failed"); }
            var obj = Doc.Objects.FindId(gid);
            var r = Ok(("object_ids", new JArray { gid.ToString() }));
            if (obj?.Geometry != null)
            {
                r["bounding_box"] = BB(obj.Geometry.GetBoundingBox(true));
                // v4.8: geometry post-conditions - the model detects bad geometry
                // (open/invalid breps) without a follow-up validate call.
                if (obj.Geometry is Brep pbr)
                {
                    r["is_valid"] = pbr.IsValid;
                    r["is_solid"] = pbr.IsSolid;
                    r["face_count"] = pbr.Faces.Count;
                }
                else if (obj.Geometry is Extrusion pex)
                {
                    r["is_valid"] = pex.IsValid;
                    r["is_solid"] = pex.IsSolid;
                }
                if (measure && obj.Geometry is Brep br)
                {
                    var am = AreaMassProperties.Compute(br);
                    var vm = VolumeMassProperties.Compute(br);
                    r["measurements"] = new JObject
                    {
                        ["area"] = am != null ? Math.Round(am.Area, 2) : 0,
                        ["volume"] = vm != null ? Math.Round(vm.Volume, 2) : 0
                    };
                }
            }
            if (layer != null) r["layer"] = layer;
            return r;
        }

        // Convenience - pulls measure flag from params, defaulting false.
        static bool WantMeasure(JObject p) => p["measure"]?.ToObject<bool>() ?? false;

        /// <summary>
        /// v4.8 unit-aware default: explicit values pass through untouched; defaults
        /// (historically assumed to be mm) are scaled into the document's unit system,
        /// so a meters document gets a 3m default wall, not a 3000m one.
        /// </summary>
        static double MmDef(JObject p, string key, double mmDefault)
        {
            var v = p?[key]?.ToObject<double?>();
            if (v.HasValue) return v.Value;
            double scale = 1.0;
            try { scale = RhinoMath.UnitScale(UnitSystem.Millimeters, Doc.ModelUnitSystem); } catch { }
            return mmDefault * scale;
        }

        /// <summary>
        /// v4.8: derive a wall's local frame from its geometry instead of assuming an
        /// axis-aligned bounding box - origin at the wall start (base Z), axis along its
        /// longest horizontal edge, length/thickness measured along/across that axis.
        /// Makes opening placement correct for diagonal walls. Falls back to bbox axes.
        /// </summary>
        static bool WallFrame(Brep wb, out Point3d origin, out Vector3d axis, out double length, out double thickness)
        {
            var bb = wb.GetBoundingBox(true);
            origin = bb.Min; axis = Vector3d.XAxis; length = 0; thickness = 0;
            if (!bb.IsValid) return false;

            Vector3d best = Vector3d.Zero; double bestLen = -1;
            foreach (var e in wb.Edges)
            {
                var d = e.PointAtEnd - e.PointAtStart;
                double horiz = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (horiz < 1e-9) continue;
                if (Math.Abs(d.Z) > horiz * 0.05) continue;   // not (near-)horizontal
                if (horiz > bestLen) { bestLen = horiz; best = new Vector3d(d.X, d.Y, 0); }
            }
            if (bestLen <= 0)
            {
                var szf = bb.Max - bb.Min;
                best = szf.X >= szf.Y ? Vector3d.XAxis : Vector3d.YAxis;
            }
            best.Unitize();
            axis = best;
            var normal = new Vector3d(-axis.Y, axis.X, 0);

            double minA = double.MaxValue, maxA = double.MinValue;
            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (var v in wb.Vertices)
            {
                var rel = v.Location - bb.Min;
                double a = rel.X * axis.X + rel.Y * axis.Y;
                double nn = rel.X * normal.X + rel.Y * normal.Y;
                if (a < minA) minA = a; if (a > maxA) maxA = a;
                if (nn < minN) minN = nn; if (nn > maxN) maxN = nn;
            }
            if (minA > maxA) return false;
            length = maxA - minA;
            thickness = Math.Max(0, maxN - minN);
            origin = new Point3d(
                bb.Min.X + axis.X * minA + normal.X * (minN + maxN) / 2.0,
                bb.Min.Y + axis.Y * minA + normal.Y * (minN + maxN) / 2.0,
                bb.Min.Z);
            return length > 1e-9;
        }

        static JObject OI(RhinoObject o)
        {
            if (o == null) return new JObject();
            return new JObject
            {
                ["id"] = o.Id.ToString(),
                ["name"] = o.Attributes.Name ?? "",
                ["type"] = o.Geometry?.ObjectType.ToString() ?? "?",
                ["layer"] = Doc.Layers[o.Attributes.LayerIndex]?.Name ?? "",
                ["bounding_box"] = o.Geometry != null ? BB(o.Geometry.GetBoundingBox(true)) : null
            };
        }

        static Brep ExtrudeCC(Curve crv, Vector3d dir)
        {
            var srf = Surface.CreateExtrusion(crv, dir); if (srf == null) return null;
            var b = srf.ToBrep();
            if (b != null) { var c = b.CapPlanarHoles(Tol); if (c != null && c.IsValid) return c; if (b.IsValid) return b; }
            return null;
        }

        // --- CONTEXT & SCENE --------------------------------------------------
        // Phase 2: these read tools now resolve through SceneSnapshotRegistry,
        // turning O(N) walks into O(1)/O(M). The snapshot is populated lazily on
        // first read after server start, then maintained by Rhino doc events.
        // OI() still constructs "lite" object views; it doesn't pay the geometry/bbox cost
        // when the snapshot already cached it.

        JObject GetContext(JObject p)
        {
            var snap = Snap;
            // Selection state is intentionally NOT cached - it's noisy and not central to architect workflow.
            // Pull selected directly; everything else from the snapshot.
            var sel = Doc.Objects.GetSelectedObjects(false, false).Take(20).Select(OI);
            var layers = Doc.Layers.Where(l => !l.IsDeleted).Select(l => new JObject
            {
                ["name"] = l.Name,
                ["visible"] = l.IsVisible,
                ["current"] = l.Index == Doc.Layers.CurrentLayerIndex
            });
            return Ok(
                ("document_name", Doc.Name ?? "Untitled"),
                ("unit_system", Doc.ModelUnitSystem.ToString()),
                ("active_layer", Doc.Layers[Doc.Layers.CurrentLayerIndex].Name),
                ("total_objects", snap?.Count ?? 0),
                ("selected_objects", new JArray(sel)),
                ("layers", new JArray(layers)));
        }

        JObject GetSelection(JObject p)
        {
            // Selection lives outside the snapshot (see GetContext comment).
            var s = Doc.Objects.GetSelectedObjects(false, false).Take(50).Select(OI).ToList();
            return Ok(("count", s.Count), ("objects", new JArray(s)));
        }

        JObject GetSceneSummary(JObject p)
        {
            var snap = Snap;
            if (snap == null)
            {
                // Cold path - should be unreachable in v4 but keep correctness.
                return GetSceneSummaryFallback();
            }

            var byType = snap.CountsByType();
            var byLayer = snap.CountsByLayerName();

            // Build the layers array from the live LayerTable so visibility/locked are fresh.
            // The counts come from the snapshot index - no per-object loop.
            var layers = new JArray();
            foreach (var l in Doc.Layers.Where(x => !x.IsDeleted))
            {
                byLayer.TryGetValue(l.Name, out int cnt);
                layers.Add(new JObject
                {
                    ["name"] = l.Name,
                    ["visible"] = l.IsVisible,
                    ["object_count"] = cnt
                });
            }

            // True bbox, cached. Recomputed only on geometry change.
            var bb = snap.SceneBoundingBox();

            return Ok(
                ("document_name", Doc.Name ?? "Untitled"),
                ("unit_system", Doc.ModelUnitSystem.ToString()),
                ("total_objects", snap.Count),
                ("scene_version", snap.SceneVersion),
                ("objects_by_type", JObject.FromObject(byType)),
                ("objects_by_layer", JObject.FromObject(byLayer)),
                ("layers", layers),
                ("bounding_box", bb.IsValid ? BB(bb) : null));
        }

        JObject GetSceneSummaryFallback()
        {
            // Only used when the snapshot is somehow null. Mirrors v4 Phase 1 behavior.
            var objs = AllObjs();
            var byType = objs.GroupBy(o => o.Geometry?.ObjectType.ToString() ?? "?").ToDictionary(g => g.Key, g => g.Count());
            var byLayer = objs.GroupBy(o => Doc.Layers[o.Attributes.LayerIndex]?.Name ?? "?").ToDictionary(g => g.Key, g => g.Count());
            var bb = BoundingBox.Empty;
            foreach (var o in objs) if (o.Geometry != null) bb.Union(o.Geometry.GetBoundingBox(true));
            return Ok(
                ("document_name", Doc.Name ?? "Untitled"),
                ("unit_system", Doc.ModelUnitSystem.ToString()),
                ("total_objects", objs.Count),
                ("objects_by_type", JObject.FromObject(byType)),
                ("objects_by_layer", JObject.FromObject(byLayer)),
                ("bounding_box", bb.IsValid ? BB(bb) : null));
        }

        JObject GetObjects(JObject p)
        {
            var snap = Snap;
            if (snap == null) return GetObjectsFallback(p);

            // Pick the most selective index up front; intersect from there.
            // Architects most commonly filter by layer ("show me all walls"),
            // so we bias the index pick toward layer when present.
            IEnumerable<SceneSnapshot.ObjectMeta> seed;
            var ln = p["layer"]?.ToString();
            var ot = p["object_type"]?.ToString();
            var pat = p["name_pattern"]?.ToString();

            if (!string.IsNullOrEmpty(ln)) seed = snap.ByLayerName(ln);
            else if (!string.IsNullOrEmpty(ot)) seed = snap.ByType(ot);
            else if (!string.IsNullOrEmpty(pat)) seed = snap.ByNameSubstring(pat);
            else seed = snap.All();

            // Apply the remaining filters as a stream.
            if (!string.IsNullOrEmpty(ot) && ln != null)
            {
                var needle = ot.ToLowerInvariant();
                seed = seed.Where(m => m.Type.ToString().ToLowerInvariant().Contains(needle));
            }
            if (!string.IsNullOrEmpty(pat) && (ln != null || ot != null))
            {
                var needle = pat.ToLowerInvariant().Replace("*", "");
                seed = seed.Where(m => m.Name.ToLowerInvariant().Contains(needle));
            }

            int limit = p["limit"]?.ToObject<int>() ?? 50;
            var res = new JArray();
            int total = 0;
            foreach (var m in seed)
            {
                total++;
                if (res.Count < limit) res.Add(MetaToOI(m, snap));
            }
            return Ok(("objects", res), ("count", res.Count), ("matched", total));
        }

        JObject GetObjectsFallback(JObject p)
        {
            var objs = AllObjs().AsEnumerable();
            var ln = p["layer"]?.ToString();
            if (!string.IsNullOrEmpty(ln)) { int i = Doc.Layers.FindByFullPath(ln, -1); objs = objs.Where(o => o.Attributes.LayerIndex == i); }
            var ot = p["object_type"]?.ToString()?.ToLower();
            if (!string.IsNullOrEmpty(ot)) objs = objs.Where(o => o.Geometry != null && o.Geometry.ObjectType.ToString().ToLower().Contains(ot));
            var pat = p["name_pattern"]?.ToString()?.Replace("*", "")?.ToLower();
            if (!string.IsNullOrEmpty(pat)) objs = objs.Where(o => (o.Attributes.Name ?? "").ToLower().Contains(pat));
            var res = objs.Take(p["limit"]?.ToObject<int>() ?? 50).Select(OI).ToList();
            return Ok(("objects", new JArray(res)), ("count", res.Count));
        }

        // Build the lite-object view from cached snapshot metadata - avoids re-fetching geometry.
        static JObject MetaToOI(SceneSnapshot.ObjectMeta m, SceneSnapshot snap)
        {
            return new JObject
            {
                ["id"] = m.Id.ToString(),
                ["name"] = m.Name ?? "",
                ["type"] = m.Type.ToString(),
                ["layer"] = snap.LayerNameOf(m),
                ["bounding_box"] = m.Bbox.IsValid ? BB(m.Bbox) : null
            };
        }

        JObject GetObjectDetails(JObject p)
        {
            var obj = Doc.Objects.FindId(new Guid((p["object_id"] ?? p["id"]).ToString()));
            if (obj == null) return Err("Not found");
            var r = OI(obj); r["status"] = "ok";
            if (obj.Geometry is Brep b) { r["face_count"] = b.Faces.Count; r["edge_count"] = b.Edges.Count; r["is_solid"] = b.IsSolid; }
            else if (obj.Geometry is Curve c) { r["is_closed"] = c.IsClosed; r["length"] = Math.Round(c.GetLength(), 2); }
            return r;
        }

        // --- ARCHITECTURE --------------------------------------------------
        JObject CreateWall(JObject p)
        {
            var sp = Pt(p["start_point"]); var ep = Pt(p["end_point"]);
            double h = MmDef(p, "height", 3000), t = MmDef(p, "thickness", 200);
            var horiz = new Vector3d(ep.X - sp.X, ep.Y - sp.Y, 0);
            if (horiz.Length < 1e-9)
                return Err("Vertical wall: start_point and end_point need distinct X/Y coordinates.", "INVALID_GEOMETRY");
            horiz.Unitize(); var n = new Vector3d(-horiz.Y, horiz.X, 0); n.Unitize(); var off = n * (t / 2);
            var crv = new Polyline(new[] { sp + off, ep + off, ep - off, sp - off, sp + off }).ToNurbsCurve();
            var b = ExtrudeCC(crv, new Vector3d(0, 0, h));
            if (b == null) return Err("Wall failed");
            var gid = Doc.Objects.AddBrep(b, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString() ?? "Wall", WantMeasure(p));
        }

        JObject CreateSlab(JObject p)
        {
            var pts = p["boundary_points"].Select(t => Pt(t)).ToList();
            double th = MmDef(p, "thickness", 200), z = p["z_level"]?.ToObject<double>() ?? 0;
            pts = pts.Select(pt => new Point3d(pt.X, pt.Y, z)).ToList();
            if (pts.First().DistanceTo(pts.Last()) > 0.01) pts.Add(pts[0]);
            var b = ExtrudeCC(new Polyline(pts).ToNurbsCurve(), new Vector3d(0, 0, -th));
            if (b == null) return Err("Slab failed", "INVALID_GEOMETRY");
            var gid = Doc.Objects.AddBrep(b, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString() ?? "Slab", WantMeasure(p));
        }

        JObject CreateColumn(JObject p)
        {
            var c = Pt(p["base_point"]);
            double w = MmDef(p, "width", 400), d = MmDef(p, "depth", 400), h = MmDef(p, "height", 3000);
            var b = Brep.CreateFromBox(new BoundingBox(new Point3d(c.X - w / 2, c.Y - d / 2, c.Z), new Point3d(c.X + w / 2, c.Y + d / 2, c.Z + h)));
            var gid = Doc.Objects.AddBrep(b, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString() ?? "Column", WantMeasure(p));
        }

        JObject CreateOpening(JObject p)
        {
            var wo = Doc.Objects.FindId(new Guid(p["wall_id"].ToString()));
            if (wo == null) return Err("Wall not found", "OBJECT_NOT_FOUND");
            var wb = GetBrep(wo); if (wb == null) return Err("Not solid", "INVALID_GEOMETRY");
            double pos = p["position"].ToObject<double>();
            double w = MmDef(p, "width", 900), h = MmDef(p, "height", 2100);
            double sill = p["sill_height"]?.ToObject<double>() ?? 0;

            // v4.8: oriented placement - the cutting box follows the wall's own axis
            // (longest horizontal edge), so diagonal walls get correct openings instead
            // of axis-aligned approximations.
            if (!WallFrame(wb, out var origin, out var wd, out var wallLen, out var wt))
                return Err("Could not derive wall orientation", "INVALID_GEOMETRY");
            if (pos < 0 || pos > wallLen)
                return Err($"position {pos:F0} is outside the wall (length {wallLen:F0})", "INVALID_REQUEST");

            var oc = origin + wd * pos + new Vector3d(0, 0, sill);
            var plane = new Plane(oc, wd, Vector3d.ZAxis);   // x = along wall, y = up, normal = across
            var box = new Box(plane,
                new Interval(-w / 2, w / 2),
                new Interval(0, h),
                new Interval(-wt * 0.6, wt * 0.6));
            var ob = box.ToBrep();
            if (ob == null) return Err("Opening volume failed", "INVALID_GEOMETRY");
            var res = Brep.CreateBooleanDifference(wb, ob, Tol);
            if (res == null || res.Length == 0) return Err("Boolean failed", "INVALID_GEOMETRY");
            // Add results before deleting the input wall - if AddBrep throws, the wall survives.
            var ids = new JArray();
            var solids = new JArray();
            foreach (var r in res) { ids.Add(Doc.Objects.AddBrep(r, wo.Attributes).ToString()); solids.Add(r.IsSolid); }
            Doc.Objects.Delete(wo, true);
            RedrawScope.Mark();
            return Ok(("object_ids", ids), ("results_solid", solids), ("wall_length", Math.Round(wallLen, 2)));
        }

        JObject CreateRoof(JObject p)
        {
            var pts = p["boundary_points"].Select(t => Pt(t)).ToList();
            double z = MmDef(p, "z_level", 3000), th = MmDef(p, "thickness", 200);
            pts = pts.Select(pt => new Point3d(pt.X, pt.Y, z)).ToList();
            if (pts.First().DistanceTo(pts.Last()) > 0.01) pts.Add(pts[0]);
            var b = ExtrudeCC(new Polyline(pts).ToNurbsCurve(), new Vector3d(0, 0, th));
            if (b == null) return Err("Roof failed", "INVALID_GEOMETRY");
            var gid = Doc.Objects.AddBrep(b, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString() ?? "Roof", WantMeasure(p));
        }

        // --- PHASE 5 - ARCHITECT INTELLIGENCE LAYER --------------------------------------------------
        //
        // These tools match how architects actually think: massing first, then floors,
        // then core, then facade rhythm, then alignment, then schedules. Each is shaped
        // to be the canonical "next move" in that workflow rather than a generic primitive.

        // query_scene - replaces 5 separate getters (get_scene_summary, get_objects,
        // list_layers, get_object_details by-layer/type/name) with one parameterized tool.
        // Served from the snapshot (Phase 2), so all branches are O(1) or O(M).
        JObject QueryScene(JObject p)
        {
            var snap = Snap;
            string scope = (p["scope"]?.ToString() ?? "objects").ToLowerInvariant();
            string detail = (p["mode"]?.ToString() ?? p["detail"]?.ToString() ?? "summary").ToLowerInvariant();
            var f = p["filter"] as JObject ?? new JObject();
            int limit = p["limit"]?.ToObject<int>() ?? (detail == "full" ? 200 : 80);

            // scope=summary -> full scene summary (the GetSceneSummary payload)
            if (scope == "summary" || scope == "scene")
            {
                var r = GetSceneSummary(p);
                r["status"] = "ok";
                r["cache"] = snap != null ? "scene_snapshot" : "live_walk";
                return r;
            }

            // scope=layers -> layer list with counts
            if (scope == "layers")
            {
                var r = ListLayers(p);
                if (snap != null) r["scene_version"] = snap.SceneVersion;
                return r;
            }

            // scope=objects (default) - apply filter and detail level
            var lookupParams = new JObject();
            if (f["layer"] != null) lookupParams["layer"] = f["layer"];
            if (f["object_type"] != null || f["type"] != null) lookupParams["object_type"] = f["object_type"] ?? f["type"];
            if (f["name"] != null || f["name_pattern"] != null) lookupParams["name_pattern"] = f["name_pattern"] ?? f["name"];
            lookupParams["limit"] = limit;
            var got = GetObjects(lookupParams);

            if (got["status"]?.ToString() != "ok") return got;

            var objs = got["objects"] as JArray ?? new JArray();
            if (detail == "ids")
            {
                var idArr = new JArray();
                foreach (var o in objs.OfType<JObject>())
                {
                    var id = o["id"];
                    if (id != null) idArr.Add(id.DeepClone());
                }
                objs = idArr;
            }
            else if (detail == "summary")
            {
                // Already lite; pass through.
            }
            // detail="full" returns whatever GetObjects gave us (currently lite - future: add geometry stats)

            // v4.8: columnar format - parallel arrays instead of repeated per-object
            // JSON keys. Typically 40-60% fewer tokens on large listings.
            string fmt = (p["format"]?.ToString() ?? "rows").ToLowerInvariant();
            if (fmt == "columnar" && detail != "ids")
            {
                var colIds = new JArray(); var colNames = new JArray(); var colLayers = new JArray();
                var colTypes = new JArray(); var colBboxes = new JArray();
                foreach (var o in objs.OfType<JObject>())
                {
                    colIds.Add(o["id"]?.ToString());
                    colNames.Add(o["name"]?.ToString() ?? "");
                    colLayers.Add(o["layer"]?.ToString() ?? "");
                    colTypes.Add(o["type"]?.ToString() ?? "");
                    var bbx = o["bounding_box"] as JObject;
                    if (bbx?["min"] is JArray mn2 && bbx["max"] is JArray mx2 && mn2.Count >= 3 && mx2.Count >= 3)
                        colBboxes.Add(new JArray((double)mn2[0], (double)mn2[1], (double)mn2[2], (double)mx2[0], (double)mx2[1], (double)mx2[2]));
                    else colBboxes.Add(null);
                }
                var colResult = new JObject
                {
                    ["status"] = "ok",
                    ["format"] = "columnar",
                    ["columns"] = new JObject
                    {
                        ["ids"] = colIds, ["names"] = colNames, ["layers"] = colLayers,
                        ["types"] = colTypes, ["bboxes"] = colBboxes,
                    },
                    ["bbox_format"] = "[min_x, min_y, min_z, max_x, max_y, max_z]",
                    ["count"] = colIds.Count,
                    ["matched"] = got["matched"] ?? got["count"],
                };
                if (snap != null) colResult["scene_version"] = snap.SceneVersion;
                return colResult;
            }

            var result = new JObject
            {
                ["status"] = "ok",
                ["objects"] = objs,
                ["count"] = objs.Count,
                ["matched"] = got["matched"] ?? got["count"],
            };
            if (snap != null) result["scene_version"] = snap.SceneVersion;
            return result;
        }

        // create_massing - site footprint -> solid mass. The canonical first move.
        // Returns a `mass_id` key explicitly so the next tool (derive_floors_from_mass)
        // can consume it via reference: derive_floors_from_mass mass_id=$1.mass_id.
        JObject CreateMassing(JObject p)
        {
            var pts = p["footprint"].Select(t => Pt(t)).ToList();
            if (pts.Count < 3) return Err("Footprint needs at least 3 points");
            if (pts.First().DistanceTo(pts.Last()) > 0.01) pts.Add(pts[0]);
            int levels = p["levels"]?.ToObject<int>() ?? 1;
            double levelHeight = MmDef(p, "level_height", 3000);
            // v4.10.1: accept level_heights[] (same shape derive_floors_from_mass uses)
            // so a variable floor stack defines the massing height. Previously this
            // param was silently ignored and the mass came out too short - found by
            // the eval harness (massing_floors task).
            var lvlHeights = p["level_heights"]?.ToObject<List<double>>();
            double height;
            if (lvlHeights != null && lvlHeights.Count > 0)
            {
                levels = lvlHeights.Count;
                height = p["height"]?.ToObject<double>() ?? lvlHeights.Sum();
            }
            else
            {
                height = p["height"]?.ToObject<double>() ?? Math.Max(1, levels) * levelHeight;
            }
            string layer = p["layer"]?.ToString() ?? "Massing";
            string name = p["name"]?.ToString() ?? $"Massing_{levels}L";
            var crv = new Polyline(pts).ToNurbsCurve();
            var b = ExtrudeCC(crv, new Vector3d(0, 0, height));
            if (b == null) return Err("Massing extrusion failed; check that the footprint is planar and closed");
            var a = new ObjectAttributes { Name = name, LayerIndex = EnsureLayer(layer, new[] { 120, 120, 120 }) };
            var gid = Doc.Objects.AddBrep(b, a);
            RedrawScope.Mark();
            var r = CrResult(gid, layer, WantMeasure(p));
            r["mass_id"] = gid.ToString();
            r["levels"] = levels;
            r["level_height"] = levelHeight;
            r["height"] = height;
            return r;
        }

        // derive_floors_from_mass - section a solid at floor heights, extrude each section
        // downward into a slab. Variable level_heights[] supports non-uniform floor heights
        // (ground floor taller, mezzanines, etc) - the architect-realistic case.
        JObject DeriveFloorsFromMass(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["mass_id"].ToString()));
            if (o == null) return Err("Mass not found");
            var b = GetBrep(o); if (b == null) return Err("Mass is not a Brep/solid");
            var heights = p["level_heights"]?.ToObject<List<double>>() ?? new List<double>();
            int levels = p["levels"]?.ToObject<int>() ?? Math.Max(1, heights.Count);
            double defaultH = MmDef(p, "level_height", 3000);
            double slabT = MmDef(p, "slab_thickness", 250);
            double z = p["start_z"]?.ToObject<double>() ?? b.GetBoundingBox(true).Min.Z;
            string layer = p["layer"]?.ToString() ?? "Slab";
            var ids = new JArray();
            var zLevels = new JArray();
            using (RedrawScope.Defer())
            {
                for (int i = 0; i < levels; i++)
                {
                    if (OperationRegistry.CancelRequested)
                        return Ok(("object_ids", ids), ("floors_created", ids.Count), ("z_levels", zLevels), ("cancelled", true), ("source_mass_id", p["mass_id"]));
                    if (i > 0) z += i - 1 < heights.Count ? heights[i - 1] : defaultH;
                    if (!Intersection.BrepPlane(b, new Plane(new Point3d(0, 0, z), Vector3d.ZAxis), Tol, out var curves, out _) || curves.Length == 0) continue;
                    foreach (var c in curves.Where(c => c.IsClosed))
                    {
                        var slab = ExtrudeCC(c, new Vector3d(0, 0, -slabT));
                        if (slab == null) continue;
                        var a = new ObjectAttributes { Name = $"Floor_{i + 1:D2}", LayerIndex = EnsureLayer(layer) };
                        ids.Add(Doc.Objects.AddBrep(slab, a).ToString());
                    }
                    zLevels.Add(Math.Round(z, 2));
                }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ids), ("floors_created", ids.Count), ("z_levels", zLevels), ("source_mass_id", p["mass_id"]));
        }

        // create_core - core walls + lift/stair/shaft modules + optional punch-through
        // of those modules into target massing solids. One call instead of dozens.
        JObject CreateCore(JObject p)
        {
            var boundary = p["boundary"].Select(t => Pt(t)).ToList();
            if (boundary.Count < 3) return Err("Core boundary needs at least 3 points");
            if (boundary.First().DistanceTo(boundary.Last()) > 0.01) boundary.Add(boundary[0]);
            double height = MmDef(p, "height", 3000);
            double th = MmDef(p, "wall_thickness", 200);
            double z0 = p["z_level"]?.ToObject<double>() ?? boundary.Min(pt => pt.Z);
            string wallLayer = p["wall_layer"]?.ToString() ?? "Core::Walls";
            string shaftLayer = p["shaft_layer"]?.ToString() ?? "Core::Shafts";
            var ids = new JArray();
            var coreBreps = new List<Brep>();

            using (RedrawScope.Defer())
            {
                // Walls: explicit list takes precedence; otherwise generate from boundary edges.
                var walls = p["walls"] as JArray;
                if (walls != null && walls.Count > 0)
                {
                    foreach (var wt in walls.OfType<JObject>())
                    {
                        var wp = new JObject
                        {
                            ["start_point"] = wt["start"] ?? wt["start_point"],
                            ["end_point"] = wt["end"] ?? wt["end_point"],
                            ["height"] = wt["height"] != null ? wt["height"] : JToken.FromObject(height),
                            ["thickness"] = wt["thickness"] != null ? wt["thickness"] : JToken.FromObject(th),
                            ["layer"] = wt["layer"]?.ToString() ?? wallLayer,
                            ["name"] = wt["name"]?.ToString() ?? "Core_Wall"
                        };
                        var r = CreateWall(wp);
                        foreach (var id in r["object_ids"] ?? new JArray())
                        {
                            ids.Add(id);
                            var ro = Doc.Objects.FindId(new Guid(id.ToString()));
                            var rb = GetBrep(ro); if (rb != null) coreBreps.Add(rb);
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < boundary.Count - 1; i++)
                    {
                        var wp = new JObject
                        {
                            ["start_point"] = PA(new Point3d(boundary[i].X, boundary[i].Y, z0)),
                            ["end_point"] = PA(new Point3d(boundary[i + 1].X, boundary[i + 1].Y, z0)),
                            ["height"] = height,
                            ["thickness"] = th,
                            ["layer"] = wallLayer,
                            ["name"] = $"Core_Wall_{i + 1:D2}"
                        };
                        var r = CreateWall(wp);
                        foreach (var id in r["object_ids"] ?? new JArray())
                        {
                            ids.Add(id);
                            var ro = Doc.Objects.FindId(new Guid(id.ToString()));
                            var rb = GetBrep(ro); if (rb != null) coreBreps.Add(rb);
                        }
                    }
                }

                // Modules: lifts, stairs, MEP shafts. If none specified, generate sensible defaults
                // based on the boundary bbox proportions. The architect can override freely.
                var bb = new BoundingBox(boundary);
                double w = bb.Max.X - bb.Min.X, d = bb.Max.Y - bb.Min.Y;
                var modules = p["modules"] as JArray;
                if (modules == null || modules.Count == 0)
                {
                    modules = new JArray
                    {
                        new JObject { ["type"] = "lift", ["name"] = "Lift_01", ["origin"] = new JArray(bb.Min.X + w * 0.15, bb.Min.Y + d * 0.15, z0), ["size"] = new JArray(w * 0.22, d * 0.28) },
                        new JObject { ["type"] = "lift", ["name"] = "Lift_02", ["origin"] = new JArray(bb.Min.X + w * 0.40, bb.Min.Y + d * 0.15, z0), ["size"] = new JArray(w * 0.22, d * 0.28) },
                        new JObject { ["type"] = "stair", ["name"] = "Stair_01", ["origin"] = new JArray(bb.Min.X + w * 0.15, bb.Min.Y + d * 0.55, z0), ["size"] = new JArray(w * 0.47, d * 0.30) },
                        new JObject { ["type"] = "shaft", ["name"] = "MEP_Shaft", ["origin"] = new JArray(bb.Min.X + w * 0.70, bb.Min.Y + d * 0.15, z0), ["size"] = new JArray(w * 0.15, d * 0.25) }
                    };
                }

                foreach (var mt in modules.OfType<JObject>())
                {
                    var origin = Pt(mt["origin"]);
                    var size = mt["size"]?.ToObject<double[]>() ?? new[] { 1200.0, 1200.0 };
                    double mh = mt["height"]?.ToObject<double>() ?? height;
                    var box = Brep.CreateFromBox(new BoundingBox(origin, new Point3d(origin.X + size[0], origin.Y + size[1], origin.Z + mh)));
                    var a = new ObjectAttributes { Name = mt["name"]?.ToString() ?? mt["type"]?.ToString() ?? "Core_Module", LayerIndex = EnsureLayer(mt["layer"]?.ToString() ?? shaftLayer) };
                    var gid = Doc.Objects.AddBrep(box, a);
                    ids.Add(gid.ToString());
                    coreBreps.Add(box);
                }
            }

            // Punch-through: subtract core modules from listed massing solids. This is the
            // architect-felt magic - the core actually carves voids in the floor stack.
            var punched = new JArray();
            var punchIds = p["punch_through"]?.ToObject<List<string>>() ?? new List<string>();
            if (punchIds.Count > 0 && coreBreps.Count > 0)
            {
                foreach (var sid in punchIds)
                {
                    var mo = Doc.Objects.FindId(new Guid(sid));
                    var mb = GetBrep(mo); if (mo == null || mb == null) continue;
                    var diff = Brep.CreateBooleanDifference(new[] { mb }, coreBreps, Tol);
                    if (diff == null || diff.Length == 0) continue;
                    Doc.Objects.Delete(mo, true);
                    foreach (var db in diff) punched.Add(Doc.Objects.AddBrep(db, mo.Attributes).ToString());
                }
            }

            RedrawScope.Mark();
            return Ok(("object_ids", ids), ("core_object_ids", ids), ("punched_mass_ids", punched), ("count", ids.Count));
        }

        // place_openings_on_facade - distribute windows at a constant rhythm along walls.
        // The whole facade in one call instead of N CreateOpening calls.
        JObject PlaceOpeningsOnFacade(JObject p)
        {
            var wallIds = ResIds(p["wall_ids"] ?? p["object_ids"]);
            double sill = MmDef(p, "sill", 900);
            double head = MmDef(p, "head", 2400);
            double width = MmDef(p, "width", 1200);
            double height = p["height"]?.ToObject<double>() ?? Math.Max(MmDef(p, "min_opening_height", 300), head - sill);
            double rhythm = MmDef(p, "rhythm", 3000);
            double margin = p["margin"]?.ToObject<double>() ?? rhythm * 0.5;
            string layer = p["layer"]?.ToString() ?? "Opening";
            var ids = new JArray();
            var errors = new JArray();
            foreach (var wid in wallIds)
            {
                var wo = Doc.Objects.FindId(new Guid(wid));
                var wb = GetBrep(wo); if (wo == null || wb == null) { errors.Add($"Wall not found/not solid: {wid}"); continue; }
                // v4.8: measure along the wall's own axis (diagonal walls supported).
                if (!WallFrame(wb, out _, out _, out var len, out _)) { errors.Add($"Wall orientation failed: {wid}"); continue; }
                if (len <= width + margin * 2) continue;
                int count = Math.Max(1, (int)Math.Floor((len - margin * 2) / rhythm) + 1);
                for (int i = 0; i < count; i++)
                {
                    if (OperationRegistry.CancelRequested)
                        return Ok(("object_ids", ids), ("openings_created", ids.Count), ("errors", errors), ("cancelled", true));
                    double pos = margin + i * rhythm;
                    if (pos + width / 2 > len) break;
                    var op = new JObject
                    {
                        ["wall_id"] = wid,
                        ["position"] = pos,
                        ["opening_type"] = "window",
                        ["width"] = width,
                        ["height"] = height,
                        ["sill_height"] = sill,
                        ["layer"] = layer
                    };
                    var r = CreateOpening(op);
                    if (r["status"]?.ToString() == "ok") foreach (var id in r["object_ids"] ?? new JArray()) ids.Add(id);
                    else errors.Add(new JObject { ["wall_id"] = wid, ["position"] = pos, ["message"] = r["message"] });
                }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ids), ("openings_created", ids.Count), ("errors", errors));
        }

        // align_to_grid - snap object centers to grid spacing. Architect grid alignment
        // for column/wall regularization. snap_z controls whether vertical alignment also snaps.
        JObject AlignToGrid(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            double g = MmDef(p, "grid_spacing", 1000);
            if (g <= 0) return Err("grid_spacing must be > 0");
            var moved = new JArray();
            foreach (var sid in ids)
            {
                var o = Doc.Objects.FindId(new Guid(sid)); if (o?.Geometry == null) continue;
                var bb = o.Geometry.GetBoundingBox(true); if (!bb.IsValid) continue;
                var c = bb.Center;
                double tx = Math.Round(c.X / g) * g - c.X;
                double ty = Math.Round(c.Y / g) * g - c.Y;
                double tz = p["snap_z"]?.ToObject<bool>() == true ? Math.Round(c.Z / g) * g - c.Z : 0;
                var newGuid = Doc.Objects.Transform(o.Id, Transform.Translation(tx, ty, tz), true);
                var newId = newGuid != Guid.Empty ? newGuid.ToString() : sid;
                moved.Add(new JObject { ["id"] = newId, ["old_id"] = sid, ["translation"] = new JArray(Math.Round(tx, 2), Math.Round(ty, 2), Math.Round(tz, 2)) });
            }
            RedrawScope.Mark();
            return Ok(("aligned", moved), ("count", moved.Count), ("grid_spacing", g));
        }

        // report_areas - GFA / NFA / by-floor schedule. The thing every architect asks for.
        // Plan-area estimation: for solid Breps with a known volume and bbox height,
        // plan_area ~ volume / height. Falls back to top-face area, then to bbox footprint.
        JObject ReportAreas(JObject p)
        {
            string by = (p["by"]?.ToString() ?? "layer").ToLowerInvariant();
            double levelHeight = MmDef(p, "level_height", 3000);
            // v4.14 (field report A3): this ran VolumeMassProperties.Compute on EVERY Brep,
            // including hundreds of OPEN vault webs where volume is meaningless, on the UI
            // thread under a 60s budget - a 900-object scene simply timed out.
            //   mode "fast" (default): exact volume only for closed solids, and only until
            //     the budget is spent; open/one-off shapes use the bbox footprint.
            //   mode "exact": always integrate (the old behaviour), for final schedules.
            //   scope: restrict to a selector instead of the whole document.
            string mode = (p["mode"]?.ToString() ?? "fast").ToLowerInvariant();
            bool exact = mode == "exact";
            int volumeBudget = p["max_volume_computations"]?.ToObject<int?>() ?? (exact ? int.MaxValue : 1500);

            List<RhinoObject> targets;
            if (p["scope"] != null) targets = ResolveSelector(p["scope"]);
            else targets = AllObjs();

            var rows = new Dictionary<string, Tuple<int, double, double>>(StringComparer.OrdinalIgnoreCase);
            int volumesComputed = 0, openSkipped = 0, budgetSkipped = 0, cancelled = 0;

            foreach (var o in targets)
            {
                if (OperationRegistry.CancelRequested) { cancelled = 1; break; }
                if (o?.Geometry == null) continue;
                var bb = o.Geometry.GetBoundingBox(true); if (!bb.IsValid) continue;
                string key;
                if (by == "name") key = string.IsNullOrWhiteSpace(o.Attributes.Name) ? "(unnamed)" : o.Attributes.Name;
                else if (by == "level") key = $"Level_{Math.Max(0, (int)Math.Floor((bb.Min.Z + 1e-6) / Math.Max(1, levelHeight))) + 1:D2}";
                else key = Doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? Doc.Layers[o.Attributes.LayerIndex]?.Name ?? "?";
                double area = 0;
                double vol = 0;
                if (o.Geometry is Brep br)
                {
                    bool wantVolume = exact || br.IsSolid;
                    if (!br.IsSolid && !exact) openSkipped++;
                    if (wantVolume && volumesComputed >= volumeBudget) { wantVolume = false; budgetSkipped++; }
                    if (wantVolume)
                    {
                        var vmp = VolumeMassProperties.Compute(br);
                        vol = vmp?.Volume ?? 0;
                        volumesComputed++;
                        area = EstimatePlanArea(br, bb, vol);
                    }
                }
                else if (o.Geometry is Curve crv && crv.IsClosed)
                {
                    var amp = AreaMassProperties.Compute(crv);
                    area = amp?.Area ?? 0;
                }
                if (area <= 0) area = Math.Max(0, (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y));
                if (!rows.TryGetValue(key, out var row)) row = Tuple.Create(0, 0.0, 0.0);
                rows[key] = Tuple.Create(row.Item1 + 1, row.Item2 + area, row.Item3 + vol);
            }
            var arr = new JArray(rows.OrderBy(kv => kv.Key).Select(kv => new JObject
            {
                ["group"] = kv.Key,
                ["count"] = kv.Value.Item1,
                ["area"] = Math.Round(kv.Value.Item2, 2),
                ["volume"] = Math.Round(kv.Value.Item3, 2)
            }));
            var res = Ok(("by", by), ("rows", arr),
                         ("total_area", Math.Round(rows.Values.Sum(r => r.Item2), 2)),
                         ("unit_system", Doc.ModelUnitSystem.ToString()));
            res["objects"] = targets.Count;
            res["mode"] = exact ? "exact" : "fast";
            res["volumes_computed"] = volumesComputed;
            if (openSkipped > 0)
            {
                res["open_breps_skipped"] = openSkipped;
                res["note"] = $"{openSkipped} open Brep(s) got a bounding-box footprint instead of an "
                            + "integrated volume - volume is undefined for an open shell. Pass mode='exact' to force it.";
            }
            if (budgetSkipped > 0)
            {
                res["budget_skipped"] = budgetSkipped;
                res["hint"] = "Volume budget reached. Narrow with scope=, or raise max_volume_computations, "
                            + "or use mode='exact' with a larger timeout_seconds.";
            }
            if (cancelled == 1) { res["cancelled"] = true; res["partial"] = true; }
            return res;
        }

        static double EstimatePlanArea(Brep br, BoundingBox bb, double volume)
        {
            double h = Math.Max(Tol, bb.Max.Z - bb.Min.Z);
            // For prismatic solids (extrusions), volume / height gives exact plan area.
            if (Math.Abs(volume) > Tol) return Math.Abs(volume) / h;
            // Fallback: largest horizontal face area.
            double best = 0;
            foreach (var f in br.Faces)
            {
                var n = f.NormalAt(f.Domain(0).Mid, f.Domain(1).Mid);
                if (Math.Abs(n.Z) < 0.75) continue;
                var amp = AreaMassProperties.Compute(f);
                if (amp != null) best = Math.Max(best, amp.Area);
            }
            return best;
        }

        // transform_objects - Phase 6 universal transform. Replaces move/rotate/scale/mirror/array
        // as separate tools. Accepts either flat shorthand fields or a sequenced operations[] array.
        // Sequenced ops are useful in batches: each op's output object_ids feed into the next.
        JObject TransformObjects(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            if (ids.Count == 0) return Err("No object_ids resolved");
            bool copy = p["copy"]?.ToObject<bool>() ?? false;
            var current = new List<string>(ids);
            var operations = p["operations"] as JArray;

            // Shorthand: if no operations[] array, build one from flat fields.
            if (operations == null || operations.Count == 0)
            {
                operations = new JArray();
                if (p["translation"] != null) operations.Add(new JObject { ["type"] = "move", ["translation"] = p["translation"].DeepClone() });
                if (p["rotation"] != null || p["angle_degrees"] != null)
                {
                    var op = new JObject { ["type"] = "rotate" };
                    if (p["rotation"] != null) op["rotation"] = p["rotation"].DeepClone();
                    if (p["angle_degrees"] != null) op["angle_degrees"] = p["angle_degrees"].DeepClone();
                    op["center"] = p["center"]?.DeepClone() ?? new JArray(0, 0, 0);
                    op["axis"] = p["axis"]?.DeepClone() ?? new JArray(0, 0, 1);
                    operations.Add(op);
                }
                if (p["scale_factor"] != null || p["scale"] != null) operations.Add(new JObject { ["type"] = "scale", ["scale_factor"] = p["scale_factor"]?.DeepClone() ?? p["scale"]?.DeepClone(), ["base_point"] = p["base_point"]?.DeepClone() ?? new JArray(0, 0, 0) });
                if (p["mirror_plane_start"] != null && p["mirror_plane_end"] != null) operations.Add(new JObject { ["type"] = "mirror", ["mirror_plane_start"] = p["mirror_plane_start"].DeepClone(), ["mirror_plane_end"] = p["mirror_plane_end"].DeepClone() });
                if (p["count_x"] != null || p["count_y"] != null) operations.Add(new JObject { ["type"] = "array", ["count_x"] = p["count_x"]?.DeepClone() ?? JToken.FromObject(1), ["count_y"] = p["count_y"]?.DeepClone() ?? JToken.FromObject(1), ["spacing_x"] = p["spacing_x"]?.DeepClone() ?? JToken.FromObject(0), ["spacing_y"] = p["spacing_y"]?.DeepClone() ?? JToken.FromObject(0) });
            }

            var opResults = new JArray();
            foreach (var tok in operations.OfType<JObject>())
            {
                string kind = (tok["type"]?.ToString() ?? tok["op"]?.ToString() ?? "move").ToLowerInvariant();
                var pp = new JObject();
                foreach (var prop in tok.Properties()) pp[prop.Name] = prop.Value.DeepClone();
                pp["object_ids"] = new JArray(current);
                pp["copy"] = tok["copy"]?.DeepClone() ?? JToken.FromObject(copy);

                // rotate shorthand - accept rotation:[rx,ry,rz] degrees as alternative to angle_degrees
                if ((kind == "rotate") && pp["angle_degrees"] == null && pp["rotation"] != null)
                {
                    var rv = pp["rotation"].ToObject<double[]>();
                    pp["angle_degrees"] = rv.Length > 2 ? rv[2] : (rv.Length > 0 ? rv[0] : 0);
                }

                JObject r;
                switch (kind)
                {
                    case "move": case "translate": r = MoveObjects(pp); break;
                    case "rotate": r = RotateObjects(pp); break;
                    case "scale": r = ScaleObjects(pp); break;
                    case "mirror": r = MirrorObjects(pp); break;
                    case "array": r = ArrayObjects(pp); break;
                    case "align_to_grid": case "align_grid": r = AlignToGrid(pp); break;
                    default: return Err($"Unknown transform operation: {kind}");
                }
                opResults.Add(r);
                if (r["status"]?.ToString() != "ok") return Err($"Transform operation failed: {kind}", "COMMAND_FAILED", new JObject { ["operation"] = kind, ["result"] = r });
                current = (r["object_ids"] as JArray)?.Select(x => x.ToString()).ToList() ?? current;
                copy = false; // copy only applies to the first op in a chain
            }

            RedrawScope.Mark();
            return Ok(("object_ids", new JArray(current)), ("operations", opResults), ("count", current.Count));
        }

        // --- UNIVERSAL CREATE --------------------------------------------------
        JObject CreateObject(JObject p)
        {
            string type = (p["type"]?.ToString() ?? "BOX").ToUpper();
            var gp = p["params"] as JObject ?? new JObject();

            // Phase 6: this is the universal creation entry point. The MCP surface uses
            // create_object for primitives AND architect-level objects (massing, core, wall...).
            // The legacy dedicated commands are still callable directly (e.g. inside batches),
            // but most callers go through here.
            JObject MergeParams()
            {
                // Merge top-level fields (layer/name/color/measure) into params for the
                // dedicated handler. Top-level wins only when params doesn't already define it.
                var merged = gp.DeepClone() as JObject ?? new JObject();
                foreach (var prop in p.Properties())
                {
                    if (prop.Name == "params" || prop.Name == "type") continue;
                    if (merged[prop.Name] == null) merged[prop.Name] = prop.Value.DeepClone();
                }
                return merged;
            }

            switch (type)
            {
                case "WALL": return CreateWall(MergeParams());
                case "SLAB": case "FLOOR": return CreateSlab(MergeParams());
                case "COLUMN": return CreateColumn(MergeParams());
                case "OPENING": case "WINDOW": case "DOOR": return CreateOpening(MergeParams());
                case "ROOF": return CreateRoof(MergeParams());
                case "MASS": case "MASSING": case "BUILDING_MASS": return CreateMassing(MergeParams());
                case "CORE": return CreateCore(MergeParams());
            }

            var a = MkAttr(p); Guid gid = Guid.Empty;
            switch (type)
            {
                case "POINT": gid = Doc.Objects.AddPoint(new Point3d(gp["x"]?.ToObject<double>() ?? 0, gp["y"]?.ToObject<double>() ?? 0, gp["z"]?.ToObject<double>() ?? 0), a); break;
                case "LINE": gid = Doc.Objects.AddLine(Pt(gp["start"]), Pt(gp["end"]), a); break;
                case "POLYLINE": gid = Doc.Objects.AddPolyline(gp["points"].Select(t => Pt(t)).ToList(), a); break;
                case "CIRCLE": gid = Doc.Objects.AddCircle(new Circle(Pt(gp["center"]), gp["radius"].ToObject<double>()), a); break;
                case "ARC": gid = Doc.Objects.AddArc(new Arc(new Plane(Pt(gp["center"]), Vector3d.ZAxis), gp["radius"].ToObject<double>(), gp["angle"].ToObject<double>() * Math.PI / 180), a); break;
                case "ELLIPSE": gid = Doc.Objects.AddCurve(NurbsCurve.CreateFromEllipse(new Ellipse(new Plane(Pt(gp["center"]), Vector3d.ZAxis), gp["radius_x"].ToObject<double>(), gp["radius_y"].ToObject<double>())), a); break;
                case "CURVE": var cp = gp["points"].Select(t => Pt(t)).ToList(); var cv = Curve.CreateControlPointCurve(cp, gp["degree"]?.ToObject<int>() ?? 3); if (cv != null) gid = Doc.Objects.AddCurve(cv, a); break;
                case "BOX":
                    {
                        double bw = gp["width"]?.ToObject<double>() ?? gp["size_x"]?.ToObject<double>() ?? 1000;
                        double bl = gp["length"]?.ToObject<double>() ?? gp["size_y"]?.ToObject<double>() ?? 1000;
                        double bh = gp["height"]?.ToObject<double>() ?? gp["size_z"]?.ToObject<double>() ?? 1000;
                        var o = gp["origin"] != null ? Pt(gp["origin"]) : new Point3d(-bw / 2, -bl / 2, 0);
                        var br = Brep.CreateFromBox(new BoundingBox(o, new Point3d(o.X + bw, o.Y + bl, o.Z + bh)));
                        if (br != null) gid = Doc.Objects.AddBrep(br, a);
                        break;
                    }
                case "SPHERE": gid = Doc.Objects.AddBrep(new Sphere(Pt(gp["center"]), gp["radius"].ToObject<double>()).ToBrep(), a); break;
                case "CONE":
                    {
                        var cn = new Cone(Plane.WorldXY, gp["height"].ToObject<double>(), gp["radius"].ToObject<double>());
                        var br = Brep.CreateFromCone(cn, gp["cap"]?.ToObject<bool>() ?? true);
                        if (br != null) gid = Doc.Objects.AddBrep(br, a);
                        break;
                    }
                case "CYLINDER":
                    {
                        var ct = gp["center"] != null ? Pt(gp["center"]) : Point3d.Origin;
                        var cy = new Cylinder(new Circle(new Plane(ct, Vector3d.ZAxis), gp["radius"].ToObject<double>()), gp["height"].ToObject<double>());
                        bool cap = gp["cap"]?.ToObject<bool>() ?? true;
                        gid = Doc.Objects.AddBrep(cy.ToBrep(cap, cap), a);
                        break;
                    }
                case "SURFACE":
                    {
                        var sc = gp["count"].ToObject<int[]>(); var sp = gp["points"].Select(t => Pt(t)).ToList();
                        var sd = gp["degree"]?.ToObject<int[]>() ?? new[] { 3, 3 };
                        var scl = gp["closed"]?.ToObject<bool[]>() ?? new[] { false, false };
                        var sf = NurbsSurface.CreateThroughPoints(sp, sc[0], sc[1], sd[0], sd[1], scl[0], scl[1]);
                        if (sf != null) gid = Doc.Objects.AddSurface(sf, a);
                        break;
                    }
                default: return Err($"Unknown type: {type}");
            }
            if (gid == Guid.Empty) return Err($"Failed to create {type}");

            // Apply post-creation transforms
            Transform xf = Transform.Identity; bool hx = false;
            if (p["translation"] != null) { xf *= Transform.Translation(Vec(p["translation"])); hx = true; }
            if (p["rotation"] != null)
            {
                var r = p["rotation"].ToObject<double[]>();
                var ctr = Doc.Objects.FindId(gid).Geometry.GetBoundingBox(true).Center;
                if (r[0] != 0) xf *= Transform.Rotation(r[0], Vector3d.XAxis, ctr);
                if (r[1] != 0) xf *= Transform.Rotation(r[1], Vector3d.YAxis, ctr);
                if (r.Length > 2 && r[2] != 0) xf *= Transform.Rotation(r[2], Vector3d.ZAxis, ctr);
                hx = true;
            }
            if (p["scale"] != null)
            {
                xf *= Transform.Scale(Doc.Objects.FindId(gid).Geometry.GetBoundingBox(true).Center, p["scale"].ToObject<double>());
                hx = true;
            }
            if (hx) { var newGuid = Doc.Objects.Transform(gid, xf, true); if (newGuid != Guid.Empty) gid = newGuid; }

            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        JObject ModifyObject(JObject p)
        {
            var idStr = (p["id"] ?? p["object_id"])?.ToString();
            RhinoObject obj = null;
            if (!string.IsNullOrEmpty(idStr)) obj = Doc.Objects.FindId(new Guid(idStr));
            else if (p["name"] != null)
            {
                var nm = p["name"].ToString();
                obj = AllObjs().FirstOrDefault(o => o.Attributes.Name == nm);
            }
            if (obj == null) return Err("Object not found");
            var attrs = obj.Attributes.Duplicate();
            bool attrChanged = false;
            if (p["new_name"] != null) { attrs.Name = p["new_name"].ToString(); attrChanged = true; }
            if (p["new_layer"] != null) { attrs.LayerIndex = EnsureLayer(p["new_layer"].ToString()); attrChanged = true; }
            if (p["new_color"] != null)
            {
                var c = p["new_color"].ToObject<int[]>();
                attrs.ObjectColor = Color.FromArgb(c[0], c[1], c[2]);
                attrs.ColorSource = ObjectColorSource.ColorFromObject;
                attrChanged = true;
            }
            if (p["visible"] != null)
            {
                if (p["visible"].ToObject<bool>()) Doc.Objects.Show(obj.Id, true);
                else Doc.Objects.Hide(obj.Id, true);
            }
            if (attrChanged) Doc.Objects.ModifyAttributes(obj, attrs, true);

            Transform xf = Transform.Identity; bool hx = false;
            if (p["translation"] != null) { xf *= Transform.Translation(Vec(p["translation"])); hx = true; }
            if (p["rotation"] != null)
            {
                var r = p["rotation"].ToObject<double[]>();
                var ctr = obj.Geometry.GetBoundingBox(true).Center;
                if (r[0] != 0) xf *= Transform.Rotation(r[0], Vector3d.XAxis, ctr);
                if (r[1] != 0) xf *= Transform.Rotation(r[1], Vector3d.YAxis, ctr);
                if (r.Length > 2 && r[2] != 0) xf *= Transform.Rotation(r[2], Vector3d.ZAxis, ctr);
                hx = true;
            }
            if (p["scale"] != null)
            {
                xf *= Transform.Scale(obj.Geometry.GetBoundingBox(true).Center, p["scale"].ToObject<double>());
                hx = true;
            }
            if (hx) { var newGuid = Doc.Objects.Transform(obj.Id, xf, true); if (newGuid != Guid.Empty) obj = Doc.Objects.FindId(newGuid); }

            RedrawScope.Mark();
            var ri = OI(Doc.Objects.FindId(obj.Id)); ri["status"] = "ok"; return ri;
        }

        // --- PRIMITIVES --------------------------------------------------
        JObject CreateBox(JObject p)
        {
            var o = Pt(p["origin"]);
            var b = Brep.CreateFromBox(new BoundingBox(o, new Point3d(o.X + p["size_x"].ToObject<double>(), o.Y + p["size_y"].ToObject<double>(), o.Z + p["size_z"].ToObject<double>())));
            var gid = Doc.Objects.AddBrep(b, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        JObject CreateCylinder(JObject p)
        {
            var c = Pt(p["base_center"]);
            var cy = new Cylinder(new Circle(new Plane(c, Vector3d.ZAxis), p["radius"].ToObject<double>()), p["height"].ToObject<double>());
            var gid = Doc.Objects.AddBrep(cy.ToBrep(true, true), MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        JObject CreateSphere(JObject p)
        {
            var gid = Doc.Objects.AddBrep(new Sphere(Pt(p["center"]), p["radius"].ToObject<double>()).ToBrep(), MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        JObject CreateLine(JObject p)
        {
            var gid = Doc.Objects.AddLine(Pt(p["start"]), Pt(p["end"]), MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), false);
        }

        JObject CreatePolyline(JObject p)
        {
            var pts = p["points"].Select(t => Pt(t)).ToList();
            if (p["closed"]?.ToObject<bool>() == true && pts.First().DistanceTo(pts.Last()) > 0.01) pts.Add(pts[0]);
            bool isClosed = pts.Count > 2 && pts.First().DistanceTo(pts.Last()) <= 0.01;
            var gid = Doc.Objects.AddPolyline(new Polyline(pts), MkAttr(p));
            RedrawScope.Mark();
            var result = CrResult(gid, p["layer"]?.ToString(), false);
            result["closed"] = isClosed;
            return result;
        }

        // --- ADVANCED GEOMETRY --------------------------------------------------
        JObject Loft(JObject p)
        {
            var ids = p["curve_ids"].ToObject<List<string>>();
            if (ids.Count < 2) return Err("Need 2+ curves");
            var curves = new List<Curve>();
            foreach (var id in ids) { var o = Doc.Objects.FindId(new Guid(id)); if (o?.Geometry is Curve c) curves.Add(c); else return Err($"{id} not a curve"); }
            var lt = (p["loft_type"]?.ToObject<int>() ?? 0) switch { 1 => LoftType.Loose, 2 => LoftType.Tight, 3 => LoftType.Straight, _ => LoftType.Normal };
            var breps = Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset, lt, p["closed"]?.ToObject<bool>() ?? false);
            if (breps == null || breps.Length == 0) return Err("Loft failed");
            var a = MkAttr(p); var ni = new JArray();
            foreach (var b in breps) ni.Add(Doc.Objects.AddBrep(b, a).ToString());
            RedrawScope.Mark();
            return Ok(("object_ids", ni), ("count", breps.Length));
        }

        JObject Sweep1(JObject p)
        {
            var ro = Doc.Objects.FindId(new Guid(p["rail_id"].ToString()));
            if (ro?.Geometry is not Curve rail) return Err("Rail not a curve");
            var profs = new List<Curve>();
            foreach (var id in p["profile_ids"].ToObject<List<string>>())
            {
                var o = Doc.Objects.FindId(new Guid(id));
                if (o?.Geometry is Curve c) profs.Add(c);
            }
            var sw = new SweepOneRail(); sw.SetToRoadlikeTop();
            var breps = sw.PerformSweep(rail, profs);
            if (breps == null || breps.Length == 0) return Err("Sweep failed");
            var a = MkAttr(p); var ni = new JArray();
            foreach (var b in breps) ni.Add(Doc.Objects.AddBrep(b, a).ToString());
            RedrawScope.Mark();
            return Ok(("object_ids", ni), ("count", breps.Length));
        }

        JObject Pipe(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["curve_id"].ToString()));
            if (o?.Geometry is not Curve crv) return Err("Curve not found");
            var breps = Brep.CreatePipe(crv, p["radius"].ToObject<double>(), false,
                p["cap"]?.ToObject<bool>() ?? true ? PipeCapMode.Flat : PipeCapMode.None,
                false, Tol, Doc.ModelAngleToleranceRadians);
            if (breps == null || breps.Length == 0) return Err("Pipe failed");
            var a = MkAttr(p); var ni = new JArray();
            foreach (var b in breps) ni.Add(Doc.Objects.AddBrep(b, a).ToString());
            RedrawScope.Mark();
            return Ok(("object_ids", ni), ("count", breps.Length));
        }

        JObject ExtrudeCurve(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["curve_id"].ToString()));
            if (o?.Geometry is not Curve crv) return Err("Curve not found");
            var srf = Surface.CreateExtrusion(crv, Vec(p["direction"]));
            if (srf == null) return Err("Extrude failed");
            var b = srf.ToBrep();
            if (p["cap"]?.ToObject<bool>() != false && crv.IsClosed && b != null) { var c = b.CapPlanarHoles(Tol); if (c != null) b = c; }
            var gid = Doc.Objects.AddBrep(b, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        // --- SMART OPS --------------------------------------------------
        JObject Sweep2(JObject p)
        {
            var r1 = Doc.Objects.FindId(new Guid(p["rail1_id"].ToString()));
            var r2 = Doc.Objects.FindId(new Guid(p["rail2_id"].ToString()));
            if (r1?.Geometry is not Curve rail1) return Err("rail1_id is not a curve", "NOT_A_CURVE");
            if (r2?.Geometry is not Curve rail2) return Err("rail2_id is not a curve", "NOT_A_CURVE");

            var profiles = new List<Curve>();
            foreach (var id in p["profile_ids"]?.ToObject<List<string>>() ?? new List<string>())
            {
                var o = Doc.Objects.FindId(new Guid(id));
                if (o?.Geometry is Curve c) profiles.Add(c);
            }
            if (profiles.Count == 0) return Err("profile_ids must contain at least one curve", "INVALID_REQUEST");

            var sw = new SweepTwoRail { SweepTolerance = Tol, AngleToleranceRadians = Doc.ModelAngleToleranceRadians };
            var breps = sw.PerformSweep(rail1, rail2, profiles);
            if (breps == null || breps.Length == 0) return Err("Sweep2 failed");

            var a = MkAttr(p);
            var ni = new JArray();
            foreach (var b in breps) ni.Add(Doc.Objects.AddBrep(b, a).ToString());
            RedrawScope.Mark();
            return Ok(("object_ids", ni), ("count", breps.Length));
        }

        JObject NetworkSurface(JObject p)
        {
            var curves = new List<Curve>();
            foreach (var id in p["curve_ids"]?.ToObject<List<string>>() ?? new List<string>())
            {
                var o = Doc.Objects.FindId(new Guid(id));
                if (o?.Geometry is Curve c) curves.Add(c);
            }
            if (curves.Count < 3) return Err("network_surface needs at least 3 boundary/section curves", "INVALID_REQUEST");

            var brep = Brep.CreateEdgeSurface(curves);
            if (brep == null) return Err("Network/edge surface failed");
            var gid = Doc.Objects.AddBrep(brep, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        JObject SpherePatch(JObject p)
        {
            var center = Pt(p["center"]);
            double radius = p["radius"]?.ToObject<double>() ?? 1000.0;
            double u0 = RhinoMath.ToRadians(p["u_start_deg"]?.ToObject<double>() ?? -45.0);
            double u1 = RhinoMath.ToRadians(p["u_end_deg"]?.ToObject<double>() ?? 45.0);
            double v0 = RhinoMath.ToRadians(p["v_start_deg"]?.ToObject<double>() ?? -20.0);
            double v1 = RhinoMath.ToRadians(p["v_end_deg"]?.ToObject<double>() ?? 45.0);
            int uCount = Math.Clamp(p["u_count"]?.ToObject<int>() ?? 12, 4, 64);
            int vCount = Math.Clamp(p["v_count"]?.ToObject<int>() ?? 8, 4, 64);

            var pts = new List<Point3d>(uCount * vCount);
            for (int v = 0; v < vCount; v++)
            {
                double vv = v0 + (v1 - v0) * v / Math.Max(1, vCount - 1);
                for (int u = 0; u < uCount; u++)
                {
                    double uu = u0 + (u1 - u0) * u / Math.Max(1, uCount - 1);
                    double cv = Math.Cos(vv);
                    pts.Add(new Point3d(
                        center.X + radius * cv * Math.Cos(uu),
                        center.Y + radius * cv * Math.Sin(uu),
                        center.Z + radius * Math.Sin(vv)));
                }
            }

            var srf = NurbsSurface.CreateThroughPoints(pts, uCount, vCount, 3, 3, false, false);
            if (srf == null) return Err("Sphere patch surface failed");
            var gid = Doc.Objects.AddSurface(srf, MkAttr(p));
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }

        JObject TrimWithPlanes(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["object_id"].ToString()));
            var source = GetBrep(o);
            if (source == null) return Err("object_id is not a Brep", "NOT_A_BREP");

            var pieces = new List<Brep> { source.DuplicateBrep() };
            foreach (var planeToken in p["planes"] ?? new JArray())
            {
                Plane plane;
                if (planeToken["origin"] != null && planeToken["normal"] != null)
                    plane = new Plane(Pt(planeToken["origin"]), Vec(planeToken["normal"]));
                else
                {
                    var coeff = planeToken.ToObject<double[]>();
                    if (coeff == null || coeff.Length < 4) return Err("Each plane must be {origin, normal} or [a,b,c,d]", "INVALID_REQUEST");
                    var normal = new Vector3d(coeff[0], coeff[1], coeff[2]);
                    if (!normal.Unitize()) return Err("Plane normal cannot be zero", "INVALID_REQUEST");
                    plane = new Plane(new Point3d(normal.X * -coeff[3], normal.Y * -coeff[3], normal.Z * -coeff[3]), normal);
                }

                var next = new List<Brep>();
                foreach (var brep in pieces)
                {
                    var trimmed = brep.Trim(plane, Tol);
                    if (trimmed != null) next.AddRange(trimmed);
                }
                pieces = next;
                if (pieces.Count == 0) break;
            }
            if (pieces.Count == 0) return Err("All geometry was trimmed away");

            var a = MkAttr(p);
            var ni = new JArray();
            foreach (var b in pieces) ni.Add(Doc.Objects.AddBrep(b, a).ToString());
            if (p["delete_input"]?.ToObject<bool>() != false) Doc.Objects.Delete(o, true);
            RedrawScope.Mark();
            return Ok(("object_ids", ni), ("count", pieces.Count));
        }

        JObject FilletEdges(JObject p)
        {
            var ids = ResIds(p["object_ids"]); double r = p["radius"].ToObject<double>(); var ni = new JArray();
            foreach (var sid in ids)
            {
                var o = Doc.Objects.FindId(new Guid(sid));
                var b = GetBrep(o); if (b == null) continue;
                var ei = Enumerable.Range(0, b.Edges.Count).ToArray();
                var rd = ei.Select(_ => r).ToArray();
                var bl = ei.Select(_ => BlendType.Fillet).ToArray();
                var fb = Brep.CreateFilletEdges(b, ei, rd, rd, BlendType.Fillet, RailType.RollingBall, true, 0.01, Tol);
                if (fb != null) { foreach (var f in fb) ni.Add(Doc.Objects.AddBrep(f, o.Attributes).ToString()); Doc.Objects.Delete(o, true); }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }

        Plane CurveOffsetPlane(Curve curve)
        {
            if (curve != null && curve.TryGetPlane(out var plane, Tol)) return plane;
            return Plane.WorldXY;
        }

        JObject OffsetCurve(JObject p)
        {
            var oid = p["object_id"]?.ToString();
            if (oid == "selected") oid = ResIds(new JArray("selected")).FirstOrDefault();
            var o = Doc.Objects.FindId(new Guid(oid));
            if (o?.Geometry is not Curve crv) return Err("Curve not found");
            double d = p["distance"].ToObject<double>(); var ni = new JArray();
            var plane = CurveOffsetPlane(crv);
            var o1 = crv.Offset(plane, d, Tol, CurveOffsetCornerStyle.Sharp);
            if (o1 != null) foreach (var c in o1) ni.Add(Doc.Objects.AddCurve(c, o.Attributes).ToString());
            if (p["both_sides"]?.ToObject<bool>() == true)
            {
                var o2 = crv.Offset(plane, -d, Tol, CurveOffsetCornerStyle.Sharp);
                if (o2 != null) foreach (var c in o2) ni.Add(Doc.Objects.AddCurve(c, o.Attributes).ToString());
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }

        JObject ExtrudeCurves(JObject p)
        {
            var ids = ResIds(p["object_ids"]); double h = p["height"].ToObject<double>(); bool cap = p["cap"]?.ToObject<bool>() ?? true; var ni = new JArray();
            foreach (var sid in ids)
            {
                var o = Doc.Objects.FindId(new Guid(sid));
                if (o?.Geometry is not Curve crv) continue;
                var srf = Surface.CreateExtrusion(crv, new Vector3d(0, 0, h));
                if (srf != null)
                {
                    var b = srf.ToBrep();
                    if (cap && b != null) { var c = b.CapPlanarHoles(Tol); if (c != null) b = c; }
                    if (b != null) ni.Add(Doc.Objects.AddBrep(b, MkAttr(p)).ToString());
                }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }

        JObject JoinCurves(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            var curves = ids.Select(id => Doc.Objects.FindId(new Guid(id)))
                .Where(o => o?.Geometry is Curve).Select(o => (Curve)o.Geometry).ToList();
            var joined = Curve.JoinCurves(curves, Tol);
            if (joined == null) return Err("Join failed");
            var ni = new JArray();
            foreach (var jc in joined) ni.Add(Doc.Objects.AddCurve(jc).ToString());
            if (p["delete_input"]?.ToObject<bool>() != false)
                foreach (var sid in ids) Doc.Objects.Delete(new Guid(sid), true);
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }

        JObject OffsetAndExtrude(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            double th = p["thickness"]?.ToObject<double>() ?? 200, h = p["height"]?.ToObject<double>() ?? 3000;
            var ni = new JArray();
            foreach (var sid in ids)
            {
                var o = Doc.Objects.FindId(new Guid(sid));
                if (o?.Geometry is not Curve crv) continue;
                var plane = CurveOffsetPlane(crv);
                var o1 = crv.Offset(plane, th / 2, Tol, CurveOffsetCornerStyle.Sharp);
                var o2 = crv.Offset(plane, -th / 2, Tol, CurveOffsetCornerStyle.Sharp);
                if (o1 != null && o2 != null)
                {
                    var all = o1.Concat(o2).Concat(new[] {
                        new LineCurve(o1[0].PointAtStart, o2[0].PointAtStart),
                        new LineCurve(o1[0].PointAtEnd, o2[0].PointAtEnd) }).ToArray();
                    var joined = Curve.JoinCurves(all, Tol * 10);
                    if (joined != null)
                        foreach (var jc in joined)
                        {
                            var b = ExtrudeCC(jc, new Vector3d(0, 0, h));
                            if (b != null) ni.Add(Doc.Objects.AddBrep(b, MkAttr(p)).ToString());
                        }
                }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }

        // --- TRANSFORMS --------------------------------------------------
        JObject TfObjs(JObject p, Transform xf)
        {
            var ids = ResIds(p["object_ids"]); bool cp = p["copy"]?.ToObject<bool>() ?? false;
            var ni = new JArray();
            foreach (var sid in ids)
            {
                var gid = new Guid(sid);
                var o = Doc.Objects.FindId(gid); if (o == null) continue;
                if (cp) { var g = o.Geometry.Duplicate(); g.Transform(xf); ni.Add(Doc.Objects.Add(g, o.Attributes).ToString()); }
                else { var newGuid = Doc.Objects.Transform(gid, xf, true); ni.Add((newGuid != Guid.Empty ? newGuid : gid).ToString()); }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }
        JObject MoveObjects(JObject p) => TfObjs(p, Transform.Translation(Vec(p["translation"])));
        JObject RotateObjects(JObject p) => TfObjs(p, Transform.Rotation(p["angle_degrees"].ToObject<double>() * Math.PI / 180, p["axis"] != null ? Vec(p["axis"]) : Vector3d.ZAxis, Pt(p["center"])));
        JObject ScaleObjects(JObject p) => TfObjs(p, Transform.Scale(Pt(p["base_point"]), p["scale_factor"].ToObject<double>()));
        JObject MirrorObjects(JObject p)
        {
            var s = Pt(p["mirror_plane_start"]); var e = Pt(p["mirror_plane_end"]);
            var d = e - s; d.Unitize();
            p["copy"] = p["copy"] ?? true;
            return TfObjs(p, Transform.Mirror(new Point3d((s.X + e.X) / 2, (s.Y + e.Y) / 2, (s.Z + e.Z) / 2), new Vector3d(-d.Y, d.X, 0)));
        }
        JObject ArrayObjects(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            int cx = p["count_x"]?.ToObject<int>() ?? 1, cy = p["count_y"]?.ToObject<int>() ?? 1;
            double sx = p["spacing_x"]?.ToObject<double>() ?? 0, sy = p["spacing_y"]?.ToObject<double>() ?? 0;
            var ni = new JArray();
            foreach (var sid in ids)
            {
                var o = Doc.Objects.FindId(new Guid(sid)); if (o == null) continue;
                for (int ix = 0; ix < cx; ix++)
                    for (int iy = 0; iy < cy; iy++)
                    {
                        if (ix == 0 && iy == 0) continue;
                        var g = o.Geometry.Duplicate();
                        g.Transform(Transform.Translation(ix * sx, iy * sy, 0));
                        ni.Add(Doc.Objects.Add(g, o.Attributes).ToString());
                    }
            }
            RedrawScope.Mark();
            return Ok(("object_ids", ni));
        }
        JObject DeleteObjects(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            if (p["dry_run"]?.ToObject<bool>() == true)
            {
                var prev = new JArray();
                foreach (var sid in ids) { if (Guid.TryParse(sid, out var g)) { var o = Doc?.Objects.FindId(g); if (o != null) prev.Add(new JObject { ["id"] = sid, ["type"] = o.ObjectType.ToString(), ["layer"] = Doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "" }); } }
                return new JObject { ["status"] = "ok", ["dry_run"] = true, ["would_delete"] = prev, ["count"] = prev.Count };
            }
            int c = ids.Count(sid => Doc.Objects.Delete(new Guid(sid), true));
            RedrawScope.Mark();
            return Ok(("deleted_count", c));
        }
        JObject BooleanOp(JObject p)
        {
            string op = p["operation"].ToString().ToLower();
            var oA = Doc.Objects.FindId(new Guid(p["object_id_a"].ToString()));
            var oB = Doc.Objects.FindId(new Guid(p["object_id_b"].ToString()));
            if (oA == null || oB == null) return Err("Objects not found");
            var bA = GetBrep(oA); var bB = GetBrep(oB);
            if (bA == null || bB == null) return Err("Not solids");
            Brep[] res = op switch
            {
                "union" => Brep.CreateBooleanUnion(new[] { bA, bB }, Tol),
                "difference" => Brep.CreateBooleanDifference(bA, bB, Tol),
                "intersection" => Brep.CreateBooleanIntersection(bA, bB, Tol),
                _ => null
            };
            if (res == null || res.Length == 0)
                return Err($"Boolean {op} failed", "INVALID_GEOMETRY", new JObject
                {
                    ["a_bbox"] = BB(bA.GetBoundingBox(true)),
                    ["b_bbox"] = BB(bB.GetBoundingBox(true)),
                    ["a_solid"] = bA.IsSolid,
                    ["b_solid"] = bB.IsSolid,
                    ["suggestion"] = !bA.IsSolid || !bB.IsSolid ? "Objects must be closed solids" : "May not overlap"
                });
            // Add results BEFORE deleting inputs - if AddBrep throws, the inputs survive.
            var ni = new JArray();
            var solidFlags = new JArray();
            foreach (var r in res) { ni.Add(Doc.Objects.AddBrep(r, oA.Attributes).ToString()); solidFlags.Add(r.IsValid && r.IsSolid); }
            if (p["delete_input"]?.ToObject<bool>() != false) { Doc.Objects.Delete(oA, true); Doc.Objects.Delete(oB, true); }
            RedrawScope.Mark();
            // v4.8: post-conditions - per-result solidity so the model spots bad booleans immediately.
            return Ok(("object_ids", ni), ("results_solid", solidFlags), ("result_count", res.Length));
        }

        // --- LAYERS --------------------------------------------------
        static readonly Dictionary<string, int[]> LC = new()
        {
            ["Wall"] = new[] { 180, 60, 60 },
            ["Slab"] = new[] { 100, 100, 180 },
            ["Column"] = new[] { 60, 150, 60 },
            ["Beam"] = new[] { 180, 140, 60 },
            ["Opening"] = new[] { 200, 200, 80 },
            ["Roof"] = new[] { 140, 80, 140 },
            ["Stair"] = new[] { 80, 160, 160 },
            ["Furniture"] = new[] { 160, 120, 80 },
            ["Site"] = new[] { 80, 140, 80 },
            ["Grid"] = new[] { 150, 150, 150 },
            ["Annotation"] = new[] { 50, 50, 50 }
        };
        JObject ListLayers(JObject p)
        {
            // Counts come from the snapshot's per-layer index. O(L) instead of O(N*L).
            // v4.14: keyed by INDEX - keying by name reported 0 for every nested layer
            // and collided on duplicate leaf names (field report A1).
            var snap = Snap;
            var counts = snap?.CountsByLayerIndex() ?? new Dictionary<int, int>();
            string prefix = p["prefix"]?.ToString();
            bool countsIncludeChildren = p["include_descendant_counts"]?.ToObject<bool>() ?? true;

            var layers = Doc.Layers.Where(l => !l.IsDeleted).ToList();
            var rows = new JArray();
            foreach (var l in layers)
            {
                var full = l.FullPath ?? l.Name;
                if (!string.IsNullOrEmpty(prefix) &&
                    !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                int own = counts.TryGetValue(l.Index, out var c) ? c : 0;
                int subtree = own;
                if (countsIncludeChildren)
                {
                    string sub = full + "::";
                    foreach (var other in layers)
                    {
                        if (other.Index == l.Index) continue;
                        var of = other.FullPath ?? other.Name;
                        if (of.StartsWith(sub, StringComparison.OrdinalIgnoreCase))
                            subtree += counts.TryGetValue(other.Index, out var c2) ? c2 : 0;
                    }
                }
                int depth = full.Split(new[] { "::" }, StringSplitOptions.None).Length - 1;
                string parent = depth > 0 ? full.Substring(0, full.LastIndexOf("::", StringComparison.Ordinal)) : null;

                rows.Add(new JObject
                {
                    ["name"] = l.Name,
                    ["full_path"] = full,
                    ["parent"] = parent,
                    ["depth"] = depth,
                    ["visible"] = l.IsVisible,
                    ["locked"] = l.IsLocked,
                    ["color"] = new JArray(l.Color.R, l.Color.G, l.Color.B),
                    ["object_count"] = own,
                    ["subtree_count"] = subtree,
                });
            }
            var r = Ok(("layers", rows), ("count", rows.Count));
            r["note"] = "object_count is objects on THIS layer; subtree_count includes descendants. "
                      + "Use full_path with by_layer: selectors - by_layer matches a layer and its descendants.";
            if (!string.IsNullOrEmpty(prefix)) r["prefix"] = prefix;
            return r;
        }
        JObject CreateLayer(JObject p)
        {
            int i = EnsureLayer(p["name"].ToString(), p["color"]?.ToObject<int[]>());
            return Ok(("name", p["name"].ToString()), ("index", i));
        }
        JObject SetActiveLayer(JObject p)
        {
            Doc.Layers.SetCurrentLayerIndex(EnsureLayer(p["name"].ToString()), true);
            return Ok(("active_layer", p["name"].ToString()));
        }
        JObject DeleteLayer(JObject p)
        {
            int i = Doc.Layers.FindByFullPath(p["name"].ToString(), -1);
            if (i < 0) return Err("Not found");
            if (p["delete_objects"]?.ToObject<bool>() == true)
                foreach (var o in AllObjs().Where(o => o.Attributes.LayerIndex == i)) Doc.Objects.Delete(o, true);
            Doc.Layers.Delete(i, true);
            RedrawScope.Mark();
            return Ok(("deleted", p["name"].ToString()));
        }
        JObject SetObjectLayer(JObject p)
        {
            var ids = ResIds(p["object_ids"]); int i = EnsureLayer(p["layer"].ToString());
            foreach (var sid in ids)
            {
                var o = Doc.Objects.FindId(new Guid(sid));
                if (o != null) { o.Attributes.LayerIndex = i; Doc.Objects.ModifyAttributes(o, o.Attributes, true); }
            }
            RedrawScope.Mark();
            return Ok(("moved_count", ids.Count));
        }
        JObject BatchLayerVis(JObject p)
        {
            if (p["isolate"] != null)
            {
                var t = p["isolate"].ToString();
                foreach (var l in Doc.Layers.Where(l => !l.IsDeleted)) l.IsVisible = l.Name == t;
            }
            foreach (var n in p["show"]?.ToObject<List<string>>() ?? new()) { int i = Doc.Layers.FindByFullPath(n, -1); if (i >= 0) Doc.Layers[i].IsVisible = true; }
            foreach (var n in p["hide"]?.ToObject<List<string>>() ?? new()) { int i = Doc.Layers.FindByFullPath(n, -1); if (i >= 0) Doc.Layers[i].IsVisible = false; }
            RedrawScope.Mark();
            return Ok();
        }
        JObject SetupArchLayers(JObject p)
        {
            string pfx = p["prefix"]?.ToString() ?? "";
            var cr = new JArray();
            foreach (var (n, c) in LC) { EnsureLayer(pfx + n, c); cr.Add(pfx + n); }
            return Ok(("layers", cr));
        }

        // --- ANALYSIS --------------------------------------------------
        JObject MeasureObject(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["object_id"].ToString()));
            if (o == null) return Err("Not found");
            var r = Ok(("type", o.Geometry.ObjectType.ToString()), ("bounding_box", BB(o.Geometry.GetBoundingBox(true))));
            if (o.Geometry is Brep b)
            {
                var am = AreaMassProperties.Compute(b); if (am != null) r["area"] = Math.Round(am.Area, 2);
                var vm = VolumeMassProperties.Compute(b); if (vm != null) r["volume"] = Math.Round(vm.Volume, 2);
            }
            else if (o.Geometry is Curve c) r["length"] = Math.Round(c.GetLength(), 2);
            return r;
        }
        JObject MeasureDistance(JObject p)
        {
            var a = Pt(p["point_a"]); var b = Pt(p["point_b"]);
            return Ok(("distance", Math.Round(a.DistanceTo(b), 2)),
                ("dx", Math.Round(Math.Abs(b.X - a.X), 2)),
                ("dy", Math.Round(Math.Abs(b.Y - a.Y), 2)),
                ("dz", Math.Round(Math.Abs(b.Z - a.Z), 2)));
        }
        JObject CheckIntersection(JObject p)
        {
            var oA = Doc.Objects.FindId(new Guid(p["object_id_a"].ToString()));
            var oB = Doc.Objects.FindId(new Guid(p["object_id_b"].ToString()));
            if (oA == null || oB == null) return Err("Not found");
            var a = oA.Geometry.GetBoundingBox(true); var b = oB.Geometry.GetBoundingBox(true);
            return Ok(("bounding_boxes_intersect",
                a.Max.X >= b.Min.X && b.Max.X >= a.Min.X &&
                a.Max.Y >= b.Min.Y && b.Max.Y >= a.Min.Y &&
                a.Max.Z >= b.Min.Z && b.Max.Z >= a.Min.Z));
        }
        // v4.9: real clash detection. Broad phase = RTree over bounding boxes;
        // narrow phase = true Brep-Brep intersection with tolerance (not bbox-only).
        JObject DetectClashes(JObject p)
        {
            double tol = p["tolerance"]?.ToObject<double>() ?? Tol;
            if (tol <= 0) tol = Tol;
            int maxChecks = Math.Clamp(p["max_checks"]?.ToObject<int>() ?? 1500, 1, 20000);
            bool includeTouch = p["include_touching"]?.ToObject<bool>() ?? true;
            bool solidOverlap = p["solid_overlap"]?.ToObject<bool>() ?? true;

            List<RhinoObject> objs;
            var idsTok = p["object_ids"];
            if (idsTok is JArray ja && ja.Count > 0)
                objs = ResIds(idsTok).Select(s => Doc.Objects.FindId(Guid.TryParse(s, out var g) ? g : Guid.Empty))
                                     .Where(o => o != null).ToList();
            else
                objs = AllObjs();
            string layer = p["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layer))
            {
                int li = Doc.Layers.FindByFullPath(layer, -1);
                if (li >= 0) objs = objs.Where(o => o.Attributes.LayerIndex == li).ToList();
            }
            var items = objs.Where(o => o.Geometry is Brep || o.Geometry is Extrusion).ToList();
            int n = items.Count;
            if (n < 2)
                return Ok(("clash_count", 0), ("clashes", new JArray()), ("checked_objects", n),
                          ("message", "Need at least 2 solid objects (Brep/Extrusion) in scope."));

            var bboxes = new BoundingBox[n];
            for (int i = 0; i < n; i++) bboxes[i] = items[i].Geometry.GetBoundingBox(false);

            var tree = new RTree();
            for (int i = 0; i < n; i++) tree.Insert(bboxes[i], i);
            var pairs = new HashSet<long>();
            for (int i = 0; i < n; i++)
            {
                int ic = i;
                var sb = bboxes[i]; sb.Inflate(tol);
                tree.Search(sb, (s, e) => { if (e.Id > ic) pairs.Add((long)ic * n + e.Id); });
            }

            var clashes = new JArray();
            int checks = 0, boolChecks = 0, hard = 0; bool truncated = false;
            foreach (var key in pairs)
            {
                if (checks >= maxChecks) { truncated = true; break; }
                int i = (int)(key / n), j = (int)(key % n);
                var ba = GetBrep(items[i]); var bb2 = GetBrep(items[j]);
                if (ba == null || bb2 == null) continue;
                checks++;
                if (!Intersection.BrepBrep(ba, bb2, tol, out var crvs, out var pts)) continue;
                bool has = (crvs != null && crvs.Length > 0) || (pts != null && pts.Length > 0);
                if (!has) continue;
                double len = crvs != null ? crvs.Sum(c => c.GetLength()) : 0;
                Point3d rep = (crvs != null && crvs.Length > 0) ? crvs[0].PointAtNormalizedLength(0.5)
                             : (pts != null && pts.Length > 0) ? pts[0] : Point3d.Origin;
                string kind = "touch";
                if (solidOverlap && boolChecks < 300 && ba.IsSolid && bb2.IsSolid)
                {
                    boolChecks++;
                    var inter = Brep.CreateBooleanIntersection(new[] { ba }, new[] { bb2 }, tol);
                    if (inter != null && inter.Length > 0)
                    {
                        double vol = 0;
                        foreach (var bi in inter) { var vm = VolumeMassProperties.Compute(bi); if (vm != null) vol += Math.Abs(vm.Volume); }
                        if (vol > tol * tol * tol) { kind = "overlap"; hard++; }
                    }
                }
                else if (solidOverlap) kind = "intersect";
                if (kind == "touch" && !includeTouch) continue;
                clashes.Add(new JObject
                {
                    ["a"] = items[i].Id.ToString(),
                    ["b"] = items[j].Id.ToString(),
                    ["kind"] = kind,
                    ["point"] = new JArray { Math.Round(rep.X, 1), Math.Round(rep.Y, 1), Math.Round(rep.Z, 1) },
                    ["intersection_length"] = Math.Round(len, 1),
                    ["a_layer"] = Doc.Layers[items[i].Attributes.LayerIndex]?.Name,
                    ["b_layer"] = Doc.Layers[items[j].Attributes.LayerIndex]?.Name
                });
            }
            var res = Ok(("clash_count", clashes.Count), ("hard_overlaps", hard),
                         ("checked_objects", n), ("candidate_pairs", pairs.Count),
                         ("narrow_checks", checks), ("tolerance", tol), ("clashes", clashes));
            if (truncated)
            {
                res["truncated"] = true;
                res["hint"] = "Hit max_checks; narrow the scope (object_ids/layer) or raise max_checks.";
            }
            return res;
        }

        // v4.9: semantic selection -- type + level + facing orientation.
        JObject SelectBySemanticCmd(JObject p)
        {
            string type = p["type"]?.ToString();
            int? level = (p["level"] != null && p["level"].Type == JTokenType.Integer) ? (int?)p["level"].ToObject<int>() : null;
            string orient = p["orientation"]?.ToString();
            bool select = p["select"]?.ToObject<bool>() ?? true;
            bool clear = p["clear_selection"]?.ToObject<bool>() ?? true;
            var matches = SemanticClassifier.Query(Doc, type, level, orient);
            int selected = 0;
            if (select)
            {
                if (clear) Doc.Objects.UnselectAll();
                var guids = matches.Select(m => Guid.TryParse(m.Id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
                selected = Doc.Objects.Select(guids, true);
                RedrawScope.Mark();
            }
            var arr = new JArray(matches.Select(m => new JObject
            {
                ["id"] = m.Id,
                ["type"] = m.Type.ToString().ToLower(),
                ["level"] = m.LevelIndex,
                ["orientation"] = m.Orientation,
                ["layer"] = m.Layer
            }));
            return Ok(("count", matches.Count), ("selected_count", selected),
                      ("filter", new JObject { ["type"] = type, ["level"] = level, ["orientation"] = orient }),
                      ("objects", arr));
        }

        // v4.10.1 (field report B3/B4): validity and openness are DIFFERENT facts.
        // A single-surface vault severy or roof plane is an intentional open shell, not
        // corruption - reporting them together made the tool unusable on a scene where
        // 743 of 6,500 objects were deliberately open. Now: `invalid` = real corruption,
        // `open` = topology fact with naked-edge length so a hairline gap (tiny total
        // length) is distinguishable from a deliberately open surface (long boundary).
        // Also accepts layer / name_pattern / since_version filters so an agent can
        // validate exactly what it just made instead of 13 blind whole-scene calls.
        JObject ValidateObjectsFiltered(JObject p)
        {
            bool expectShells = p["expect_shells"]?.ToObject<bool>() ?? false;
            int maxChecks = Math.Max(1, p["max_checks"]?.ToObject<int>() ?? 500);
            string layerFilter = p["layer"]?.ToString();
            string namePattern = p["name_pattern"]?.ToString();
            int sinceVersion = p["since_version"]?.ToObject<int>() ?? -1;

            var targets = new List<RhinoObject>();
            string scope;

            if (sinceVersion >= 0)
            {
                var (added, _, modified, toVer, truncated) = ChangeTracker.GetDiff(sinceVersion);
                var changed = added.Concat(modified)
                    .Select(t => t["id"]?.ToString())
                    .Where(s => !string.IsNullOrEmpty(s));
                foreach (var s in changed)
                    if (Guid.TryParse(s, out var g))
                    {
                        var o = Doc.Objects.FindId(g);
                        if (o != null) targets.Add(o);
                    }
                scope = $"changed since tracker version {sinceVersion} (now {toVer})";
                if (truncated)
                    scope += " [WARNING: change log truncated - validate the full scene]";
            }
            else if (!string.IsNullOrWhiteSpace(layerFilter))
            {
                int li = Doc.Layers.FindByFullPath(layerFilter, -1);
                if (li < 0) return Err($"Layer not found: {layerFilter}", "LAYER_NOT_FOUND");
                targets.AddRange(AllObjs().Where(o => o.Attributes.LayerIndex == li));
                scope = $"layer '{layerFilter}'";
            }
            else
            {
                targets.AddRange(AllObjs());
                scope = "all objects";
            }

            if (!string.IsNullOrWhiteSpace(namePattern))
            {
                var pat = namePattern.Trim();
                var starts = pat.EndsWith("*");
                var core = pat.TrimEnd('*');
                targets = targets.Where(o =>
                {
                    var n = o.Attributes.Name ?? "";
                    return starts
                        ? n.StartsWith(core, StringComparison.OrdinalIgnoreCase)
                        : n.Equals(core, StringComparison.OrdinalIgnoreCase);
                }).ToList();
                scope += $", name '{namePattern}'";
            }

            var invalid = new JArray();
            var open = new JArray();
            int checkedCount = 0, solids = 0, notBrep = 0;

            foreach (var o in targets)
            {
                if (checkedCount >= maxChecks) break;
                var b = GetBrep(o);
                if (b == null) { notBrep++; continue; }
                checkedCount++;
                if (!b.IsValid)
                {
                    b.IsValidWithLog(out string log);
                    invalid.Add(new JObject
                    {
                        ["id"] = o.Id.ToString(),
                        ["issue"] = "Invalid Brep",
                        ["layer"] = Doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "",
                        ["name"] = o.Attributes.Name ?? "",
                        ["detail"] = string.IsNullOrWhiteSpace(log) ? null : log.Split('\n')[0].Trim(),
                    });
                    continue;
                }
                if (b.IsSolid) { solids++; continue; }

                // Open but valid: quantify the boundary so hairline gaps stand out.
                double nakedLen = 0; int nakedCount = 0;
                try
                {
                    var edges = b.DuplicateNakedEdgeCurves(true, true);
                    if (edges != null)
                    {
                        nakedCount = edges.Length;
                        foreach (var c in edges) { nakedLen += c.GetLength(); c.Dispose(); }
                    }
                }
                catch { }
                open.Add(new JObject
                {
                    ["id"] = o.Id.ToString(),
                    ["layer"] = Doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "",
                    ["name"] = o.Attributes.Name ?? "",
                    ["naked_edge_count"] = nakedCount,
                    ["naked_edge_length"] = Math.Round(nakedLen, 3),
                    ["face_count"] = b.Faces.Count,
                });
            }

            var result = Ok(
                ("scope", scope),
                ("checked", checkedCount),
                ("solids", solids),
                ("invalid", invalid),
                ("invalid_count", invalid.Count),
                ("open", open),
                ("open_count", open.Count));
            result["skipped_non_brep"] = notBrep;
            if (targets.Count > checkedCount + notBrep)
            {
                result["remaining_unchecked"] = targets.Count - checkedCount - notBrep;
                result["hint"] = "Raise max_checks, or narrow the scope with layer / name_pattern / since_version.";
            }
            // Back-compat: `issues` is what older callers read. Open shells are only
            // counted as issues when the caller has NOT declared them expected.
            var issues = new JArray(invalid.Select(t => t.DeepClone()));
            if (!expectShells)
                foreach (var t in open)
                    issues.Add(new JObject { ["id"] = t["id"], ["issue"] = "Open Brep",
                                             ["naked_edge_length"] = t["naked_edge_length"] });
            result["issues"] = issues;
            result["interpretation"] =
                "invalid = real geometry corruption, always fix. open = not closed; legitimate for "
                + "single-surface roofs/vault webs/glazing. A SHORT naked_edge_length on a shape that "
                + "should be solid usually means a hairline gap or a missing face.";
            return result;
        }

        // =====================================================================
        // INTENT VALIDATION (v4.10.1, field report §4)
        // Brep validity is the wrong invariant for generated architecture. An agent
        // producing thousands of parametric objects makes ARITHMETIC and WIRING
        // errors - doubled base heights, swapped arguments, cutters added instead of
        // subtracted - all of which yield perfectly valid, closed, non-degenerate
        // breps. These commands check intent instead of topology.
        // =====================================================================

        private List<RhinoObject> ResolveSelector(JToken sel)
        {
            // Accept a bare string ("all", "by_layer:Foo", a guid) as well as an array.
            // Requiring the array form is a pointless trap for callers.
            if (sel != null && sel.Type == JTokenType.String)
                sel = new JArray(sel.ToString());
            var ids = ResIds(sel ?? JToken.FromObject(new[] { "all" }));
            var list = new List<RhinoObject>();
            foreach (var s in ids)
                if (Guid.TryParse(s, out var g))
                {
                    var o = Doc.Objects.FindId(g);
                    if (o != null) list.Add(o);
                }
            return list;
        }

        private static BoundingBox UnionBox(IEnumerable<RhinoObject> objs)
        {
            var bb = BoundingBox.Empty;
            foreach (var o in objs)
            {
                var g = o?.Geometry;
                if (g == null) continue;
                var b = g.GetBoundingBox(true);
                if (b.IsValid) bb.Union(b);
            }
            return bb;
        }

        /// <summary>Post-condition contracts: assert what the geometry MEANS, not just that it parses.</summary>
        JObject AssertGeometry(JObject p)
        {
            var asserts = p["assertions"] as JArray;
            if (asserts == null || asserts.Count == 0)
                return Err("assertions[] required", "INVALID_INPUT");

            var results = new JArray();
            int passed = 0;

            foreach (var aTok in asserts)
            {
                var a = aTok as JObject ?? new JObject();
                string kind = (a["kind"]?.ToString() ?? "").ToLowerInvariant();
                double tol = a["tol"]?.ToObject<double>() ?? Math.Max(Tol, 1e-6);
                var entry = new JObject { ["kind"] = kind };
                if (a["selector"] != null) entry["selector"] = a["selector"];
                bool ok = false;
                var offenders = new JArray();
                string detail = "";

                try
                {
                    switch (kind)
                    {
                        case "bbox":
                        {
                            var objs = ResolveSelector(a["selector"]);
                            if (objs.Count == 0) { detail = "selector matched no objects"; break; }
                            var bb = UnionBox(objs);
                            ok = true;
                            var checks = new (string key, double actual)[]
                            {
                                ("x_min", bb.Min.X), ("y_min", bb.Min.Y), ("z_min", bb.Min.Z),
                                ("x_max", bb.Max.X), ("y_max", bb.Max.Y), ("z_max", bb.Max.Z),
                            };
                            var parts = new List<string>();
                            foreach (var (key, actual) in checks)
                            {
                                var want = a[key]?.ToObject<double>();
                                if (want == null) continue;
                                double diff = Math.Abs(actual - want.Value);
                                bool good = diff <= tol;
                                if (!good) ok = false;
                                parts.Add($"{key} actual={actual:0.###} expected={want.Value:0.###} diff={diff:0.###}{(good ? "" : " FAIL")}");
                            }
                            detail = parts.Count > 0 ? string.Join("; ", parts) : "no bounds given to check";
                            if (parts.Count == 0) ok = false;
                            entry["objects"] = objs.Count;
                            break;
                        }
                        case "envelope":
                        {
                            var objs = ResolveSelector(a["selector"]);
                            var box = a["box"];
                            if (box == null) { detail = "box [[minx,miny,minz],[maxx,maxy,maxz]] required"; break; }
                            var lo = Pt(box[0]); var hi = Pt(box[1]);
                            var env = new BoundingBox(
                                new Point3d(Math.Min(lo.X, hi.X), Math.Min(lo.Y, hi.Y), Math.Min(lo.Z, hi.Z)),
                                new Point3d(Math.Max(lo.X, hi.X), Math.Max(lo.Y, hi.Y), Math.Max(lo.Z, hi.Z)));
                            foreach (var o in objs)
                            {
                                var b = o.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                                if (!b.IsValid) continue;
                                if (b.Min.X < env.Min.X - tol || b.Min.Y < env.Min.Y - tol || b.Min.Z < env.Min.Z - tol ||
                                    b.Max.X > env.Max.X + tol || b.Max.Y > env.Max.Y + tol || b.Max.Z > env.Max.Z + tol)
                                {
                                    if (offenders.Count < 50)
                                        offenders.Add(new JObject
                                        {
                                            ["id"] = o.Id.ToString(),
                                            ["name"] = o.Attributes.Name ?? "",
                                            ["bbox_min"] = PA(b.Min),
                                            ["bbox_max"] = PA(b.Max),
                                        });
                                }
                            }
                            ok = offenders.Count == 0;
                            entry["objects"] = objs.Count;
                            detail = ok ? $"all {objs.Count} objects inside the envelope"
                                        : $"{offenders.Count} object(s) outside the envelope";
                            break;
                        }
                        case "count":
                        {
                            var objs = ResolveSelector(a["selector"]);
                            int n = objs.Count;
                            var expect = a["expect"]?.ToObject<int>();
                            var min = a["min"]?.ToObject<int>();
                            var max = a["max"]?.ToObject<int>();
                            ok = expect.HasValue ? n == expect.Value
                                 : (!min.HasValue || n >= min.Value) && (!max.HasValue || n <= max.Value);
                            detail = $"count={n}" + (expect.HasValue ? $" expected={expect.Value}" : $" allowed=[{min},{max}]");
                            break;
                        }
                        case "count_delta":
                        {
                            int since = a["since_version"]?.ToObject<int>() ?? -1;
                            if (since < 0) { detail = "since_version required (get it from get_tracker_version)"; break; }
                            var (added, deleted, _, toVer, truncated) = ChangeTracker.GetDiff(since);
                            int delta = added.Count - deleted.Count;
                            var expect = a["expect"]?.ToObject<int>();
                            ok = !expect.HasValue || delta == expect.Value;
                            detail = $"added={added.Count} deleted={deleted.Count} delta={delta}"
                                     + (expect.HasValue ? $" expected={expect.Value}" : "")
                                     + $" (tracker now {toVer})" + (truncated ? " [log truncated]" : "");
                            break;
                        }
                        case "watertight":
                        {
                            var objs = ResolveSelector(a["selector"]);
                            int solid = 0, checkedN = 0;
                            foreach (var o in objs)
                            {
                                var b = GetBrep(o);
                                if (b == null) continue;
                                checkedN++;
                                if (b.IsSolid && b.IsValid) solid++;
                                else if (offenders.Count < 50)
                                    offenders.Add(new JObject
                                    {
                                        ["id"] = o.Id.ToString(),
                                        ["name"] = o.Attributes.Name ?? "",
                                        ["issue"] = b.IsValid ? "open" : "invalid",
                                    });
                            }
                            ok = offenders.Count == 0 && checkedN > 0;
                            entry["objects"] = checkedN;
                            detail = $"{solid}/{checkedN} closed valid solids";
                            break;
                        }
                        case "supported":
                        {
                            var objs = ResolveSelector(a["selector"]);
                            double maxGap = a["max_gap"]?.ToObject<double>() ?? Math.Max(Tol * 10, 0.05);
                            var unsupported = FindUnsupportedIn(objs, maxGap, out int checkedN);
                            foreach (var u in unsupported.Take(50)) offenders.Add(u);
                            ok = unsupported.Count == 0 && checkedN > 0;
                            entry["objects"] = checkedN;
                            detail = ok ? $"all {checkedN} solids rest on something within {maxGap:0.###}"
                                        : $"{unsupported.Count} floating object(s)";
                            break;
                        }
                        default:
                            detail = $"unknown assertion kind '{kind}'. Supported: bbox, envelope, count, count_delta, watertight, supported.";
                            break;
                    }
                }
                catch (Exception e) { detail = $"assertion error: {e.Message}"; ok = false; }

                entry["pass"] = ok;
                entry["detail"] = detail;
                if (offenders.Count > 0) entry["offenders"] = offenders;
                results.Add(entry);
                if (ok) passed++;
            }

            var r = Ok(("assertions", results), ("passed", passed), ("failed", asserts.Count - passed));
            r["status"] = passed == asserts.Count ? "ok" : "error";
            if (passed != asserts.Count)
            {
                r["error_code"] = "ASSERTION_FAILED";
                r["message"] = $"{asserts.Count - passed} of {asserts.Count} assertions failed.";
                r["retry_hint"] = "Inspect `offenders` for the specific object ids, fix the generator inputs, and re-assert.";
            }
            return r;
        }

        /// <summary>
        /// Bbox-shadow support test: an object is "supported" if the ground or another
        /// object's top surface lies within max_gap directly beneath its footprint.
        /// Catches floating spires, pinnacles and statuary - the class of defect that
        /// otherwise survives until a human looks at a render.
        /// </summary>
        private List<JObject> FindUnsupportedIn(List<RhinoObject> objs, double maxGap, out int checkedCount)
        {
            checkedCount = 0;
            var result = new List<JObject>();

            // Candidate supporters: every solid/surface in the document.
            var all = AllObjs().Where(o => o.Geometry != null).ToList();
            var boxes = new List<(RhinoObject obj, BoundingBox bb)>();
            foreach (var o in all)
            {
                var b = o.Geometry.GetBoundingBox(true);
                if (b.IsValid) boxes.Add((o, b));
            }
            if (boxes.Count == 0) return result;

            var tree = new RTree();
            for (int i = 0; i < boxes.Count; i++) tree.Insert(boxes[i].bb, i);

            double groundZ = boxes.Min(t => t.bb.Min.Z);

            foreach (var o in objs)
            {
                var g = o.Geometry;
                if (g == null) continue;
                var bb = g.GetBoundingBox(true);
                if (!bb.IsValid) continue;
                checkedCount++;

                // Resting on the lowest plane in the scene counts as grounded.
                if (bb.Min.Z <= groundZ + maxGap) continue;

                // Search the column directly below this object's footprint.
                var probe = new BoundingBox(
                    new Point3d(bb.Min.X + Tol, bb.Min.Y + Tol, bb.Min.Z - maxGap),
                    new Point3d(bb.Max.X - Tol, bb.Max.Y - Tol, bb.Min.Z + maxGap));
                if (!probe.IsValid)
                    probe = new BoundingBox(
                        new Point3d(bb.Min.X, bb.Min.Y, bb.Min.Z - maxGap),
                        new Point3d(bb.Max.X, bb.Max.Y, bb.Min.Z + maxGap));

                bool supported = false;
                double bestTop = double.NegativeInfinity;
                tree.Search(probe, (sender, args) =>
                {
                    var cand = boxes[args.Id];
                    if (cand.obj.Id == o.Id) return;
                    // Must actually overlap in plan and reach up to this object's base.
                    bool xy = cand.bb.Max.X > bb.Min.X && cand.bb.Min.X < bb.Max.X
                           && cand.bb.Max.Y > bb.Min.Y && cand.bb.Min.Y < bb.Max.Y;
                    if (!xy) return;
                    // A supporter must genuinely START below this object. Without that,
                    // peers sitting at the same level (e.g. the four webs of one vault)
                    // vouch for each other and everything looks supported.
                    bool startsBelow = cand.bb.Min.Z < bb.Min.Z - Tol;
                    if (!startsBelow) return;
                    if (cand.bb.Max.Z > bestTop) bestTop = cand.bb.Max.Z;
                    if (cand.bb.Max.Z >= bb.Min.Z - maxGap) supported = true;
                });

                if (!supported)
                {
                    double gap = double.IsNegativeInfinity(bestTop) ? bb.Min.Z - groundZ : bb.Min.Z - bestTop;
                    result.Add(new JObject
                    {
                        ["id"] = o.Id.ToString(),
                        ["name"] = o.Attributes.Name ?? "",
                        ["layer"] = Doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "",
                        ["base_z"] = Math.Round(bb.Min.Z, 3),
                        ["gap_below"] = Math.Round(gap, 3),
                        ["nearest_top_below"] = double.IsNegativeInfinity(bestTop) ? null : (JToken)Math.Round(bestTop, 3),
                    });
                }
            }
            return result;
        }

        JObject FindUnsupported(JObject p)
        {
            double maxGap = p["max_gap"]?.ToObject<double>() ?? Math.Max(Tol * 10, 0.05);
            var objs = ResolveSelector(p["selector"] ?? p["object_ids"]);
            // Only solids/surfaces are meaningful here.
            objs = objs.Where(o => o.ObjectType == ObjectType.Brep || o.ObjectType == ObjectType.Extrusion
                                || o.ObjectType == ObjectType.Surface || o.ObjectType == ObjectType.Mesh).ToList();
            var floating = FindUnsupportedIn(objs, maxGap, out int checkedCount);
            var arr = new JArray();
            foreach (var f in floating.OrderByDescending(t => t["gap_below"]?.ToObject<double>() ?? 0).Take(200))
                arr.Add(f);
            var r = Ok(("checked", checkedCount), ("unsupported_count", floating.Count), ("unsupported", arr));
            r["max_gap"] = maxGap;
            r["interpretation"] =
                "An object is supported when the scene's lowest plane, or another object's top, lies within "
                + "max_gap directly under its footprint. Large gap_below on a spire, pinnacle or statue means "
                + "it is floating. Test is bbox-based: interlocking or cantilevered geometry can report false positives.";
            return r;
        }

        /// <summary>Fast interior inspection: clip at a station, look perpendicular, capture, restore.</summary>
        JObject SectionPreview(JObject p)
        {
            string axis = (p["axis"]?.ToString() ?? "x").Trim().ToLowerInvariant();
            if (axis != "x" && axis != "y" && axis != "z")
                return Err("axis must be 'x', 'y' or 'z'", "INVALID_INPUT");

            var sceneBox = UnionBox(AllObjs());
            if (!sceneBox.IsValid) return Err("Scene is empty", "EMPTY_SCENE");
            var c = sceneBox.Center;
            var diag = sceneBox.Diagonal.Length;
            double station = p["station"]?.ToObject<double>()
                             ?? (axis == "x" ? c.X : axis == "y" ? c.Y : c.Z);

            Vector3d normal = axis == "x" ? Vector3d.XAxis : axis == "y" ? Vector3d.YAxis : Vector3d.ZAxis;
            Point3d origin = axis == "x" ? new Point3d(station, c.Y, c.Z)
                           : axis == "y" ? new Point3d(c.X, station, c.Z)
                                         : new Point3d(c.X, c.Y, station);

            var view = Doc.Views.ActiveView;
            var vp = view?.ActiveViewport;
            if (vp == null) return Err("No active viewport");

            var savedLoc = vp.CameraLocation;
            var savedTgt = vp.CameraTarget;
            var savedUp = vp.CameraUp;
            var savedParallel = vp.IsParallelProjection;
            var savedMode = vp.DisplayMode;
            Guid clipId = Guid.Empty;

            try
            {
                // Clipping plane: keeps the half-space BEHIND the normal visible.
                var plane = new Plane(origin, normal);
                double size = diag > 0 ? diag * 1.5 : 1000;
                clipId = Doc.Objects.AddClippingPlane(plane, size, size, new[] { vp.Id });

                // Camera on the normal side, looking back at the cut face.
                vp.ChangeToParallelProjection(true);
                vp.SetCameraDirection(-normal, true);
                vp.CameraUp = axis == "z" ? Vector3d.YAxis : Vector3d.ZAxis;

                string modeName = p["display_mode"]?.ToString();
                if (!string.IsNullOrWhiteSpace(modeName))
                {
                    var dm = Rhino.Display.DisplayModeDescription.FindByName(modeName);
                    if (dm != null) vp.DisplayMode = dm;
                }
                // Frame the scene WITHOUT losing the direction we just set
                // (ZoomExtents re-derives the camera and undoes it).
                var framed = sceneBox;
                framed.Inflate(diag * 0.02);
                if (!vp.ZoomBoundingBox(framed)) vp.ZoomExtents();
                SettleDisplay(vp, vp.DisplayMode?.EnglishName ?? modeName);

                var cap = new JObject
                {
                    ["width"] = p["width"]?.ToObject<int>() ?? 900,
                    ["height"] = p["height"]?.ToObject<int>() ?? 700,
                    ["restore_state"] = false,
                    ["format"] = p["format"]?.ToString() ?? "auto",
                    ["quality"] = p["quality"]?.ToObject<int>() ?? 80,
                };
                var img = CaptureViewport(cap);
                if (img != null && img["status"]?.ToString() == "ok")
                {
                    img["axis"] = axis;
                    img["station"] = station;
                    img["note"] = "Clipping plane and camera were restored after capture.";
                }
                return img;
            }
            catch (Exception e) { return ErrFromException(e, "SectionPreview"); }
            finally
            {
                // Always clean up: a stray clipping plane would silently alter every later view.
                try { if (clipId != Guid.Empty) Doc.Objects.Delete(clipId, true); } catch { }
                try
                {
                    vp.DisplayMode = savedMode;
                    vp.ChangeToParallelProjection(savedParallel);
                    vp.SetCameraLocations(savedTgt, savedLoc);
                    vp.CameraUp = savedUp;
                    RedrawScope.Mark();
                }
                catch { }
            }
        }

        JObject ValidateObjects(JObject p)
        {
            // Any filter/policy argument routes to the v4.10.1 implementation.
            if (p["layer"] != null || p["name_pattern"] != null || p["since_version"] != null
                || p["expect_shells"] != null || p["max_checks"] != null)
                return ValidateObjectsFiltered(p);

            var ids = p["object_ids"]?.ToObject<List<string>>();
            // Phase 2: when no IDs given, use the snapshot to pre-filter to Brep/Extrusion candidates
            // so we don't fetch geometry for every curve, point, and annotation.
            List<RhinoObject> objs;
            if (ids != null && ids.Count > 0)
            {
                objs = new List<RhinoObject>();
                foreach (var id in ids)
                {
                    if (id.StartsWith("by_layer:", StringComparison.OrdinalIgnoreCase))
                    {
                        var layerName = id.Substring(9);
                        int li = Doc.Layers.FindByFullPath(layerName, -1);
                        if (li >= 0) objs.AddRange(AllObjs().Where(o => o.Attributes.LayerIndex == li));
                    }
                    else if (Guid.TryParse(id, out var g))
                    {
                        var o = Doc.Objects.FindId(g);
                        if (o != null) objs.Add(o);
                    }
                }
            }
            else
            {
                // v4.8: whole-scene validation through the snapshot validity cache.
                // Cached flags answer instantly; only uncached objects pay for a geometry
                // fetch, budgeted per call so 5000-object scenes stay responsive. Results
                // accumulate: call again to validate the remainder.
                var snap = Snap;
                if (snap != null)
                {
                    const int FRESH_BUDGET = 500;
                    var issues2 = new JArray();
                    int fromCache = 0, computed = 0, remaining = 0;
                    foreach (var m in snap.All())
                    {
                        if (m.Type != ObjectType.Brep && m.Type != ObjectType.Extrusion) continue;
                        bool isValid, isSolid;
                        if (m.IsValidGeo.HasValue && m.IsSolid.HasValue)
                        {
                            isValid = m.IsValidGeo.Value; isSolid = m.IsSolid.Value; fromCache++;
                        }
                        else if (computed < FRESH_BUDGET)
                        {
                            var ro2 = Doc.Objects.FindId(m.Id);
                            var b2 = GetBrep(ro2);
                            if (b2 == null) continue;
                            isValid = b2.IsValid; isSolid = b2.IsSolid;
                            snap.SetValidity(m.Id, isValid, isSolid);
                            computed++;
                        }
                        else { remaining++; continue; }
                        if (!isValid) issues2.Add(new JObject { ["id"] = m.Id.ToString(), ["issue"] = "Invalid Brep" });
                        if (!isSolid) issues2.Add(new JObject { ["id"] = m.Id.ToString(), ["issue"] = "Open Brep" });
                    }
                    var vr = Ok(("checked", fromCache + computed), ("issues", issues2),
                                ("from_cache", fromCache), ("computed", computed));
                    if (remaining > 0)
                    {
                        vr["remaining_unchecked"] = remaining;
                        vr["hint"] = "Call validate_objects again - validation accumulates in the cache until the whole scene is covered.";
                    }
                    return vr;
                }
                objs = AllObjs();
            }
            var issues = new JArray();
            foreach (var o in objs.Take(100))
            {
                if (o.Geometry is Brep b)
                {
                    if (!b.IsValid) issues.Add(new JObject { ["id"] = o.Id.ToString(), ["issue"] = "Invalid Brep" });
                    if (!b.IsSolid) issues.Add(new JObject { ["id"] = o.Id.ToString(), ["issue"] = "Open Brep" });
                }
            }
            return Ok(("checked", objs.Count), ("issues", issues));
        }

        // --- VIEWPORT --------------------------------------------------
        JObject SetView(JObject p)
        {
            var n = p["view_name"].ToString().ToLower();
            var proj = n switch
            {
                "top" => Rhino.Display.DefinedViewportProjection.Top,
                "front" => Rhino.Display.DefinedViewportProjection.Front,
                "right" => Rhino.Display.DefinedViewportProjection.Right,
                "left" => Rhino.Display.DefinedViewportProjection.Left,
                "back" => Rhino.Display.DefinedViewportProjection.Back,
                _ => Rhino.Display.DefinedViewportProjection.Perspective
            };
            Doc.Views.ActiveView.ActiveViewport.SetProjection(proj, n, true);
            Doc.Views.ActiveView.ActiveViewport.ZoomExtents();
            RedrawScope.Mark();
            return Ok(("view", n));
        }
        JObject SetDisplayMode(JObject p)
        {
            string requested = p["mode"]?.ToString();
            if (string.IsNullOrWhiteSpace(requested)) return Err("mode required", "INVALID_INPUT");

            var m = Rhino.Display.DisplayModeDescription.FindByName(requested);
            if (m == null)
            {
                var modes = Rhino.Display.DisplayModeDescription.GetDisplayModes()
                    .Select(dm => dm.EnglishName ?? dm.LocalName ?? dm.Id.ToString())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n)
                    .ToList();
                var close = modes
                    .Where(n => n.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0
                             || requested.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(8)
                    .ToArray();
                var detail = new JObject { ["available_modes"] = new JArray(modes.Take(40)) };
                if (close.Length > 0) detail["suggestions"] = new JArray(close);
                return Err($"Display mode not found: {requested}", "DISPLAY_MODE_NOT_FOUND", detail);
            }

            var view = Doc.Views.ActiveView;
            if (view == null) return Err("No active viewport", "VIEWPORT_EMPTY");
            view.ActiveViewport.DisplayMode = m;
            view.Redraw();
            RedrawScope.Mark();
            return Ok(("mode", m.EnglishName ?? m.LocalName ?? requested));
        }

        JArray CaptureAnnotations(JObject p)
        {
            string scope = (p["annotation_scope"]?.ToString() ?? "selected").ToLowerInvariant();
            int max = Math.Max(0, Math.Min(200, p["max_annotations"]?.ToObject<int>() ?? 20));
            if (max == 0) return new JArray();

            IEnumerable<RhinoObject> objs = scope == "visible"
                ? Doc.Objects.Where(o => !o.IsDeleted && o.Visible)
                : Doc.Objects.GetSelectedObjects(false, false);

            var arr = new JArray();
            foreach (var o in objs.Take(max))
            {
                if (o?.Geometry == null) continue;
                var layer = o.Attributes.LayerIndex >= 0 ? Doc.Layers[o.Attributes.LayerIndex] : null;
                var bb = o.Geometry.GetBoundingBox(true);
                arr.Add(new JObject
                {
                    ["id"] = o.Id.ToString(),
                    ["short_id"] = o.Id.ToString("N").Substring(0, 8),
                    ["name"] = string.IsNullOrWhiteSpace(o.Name) ? null : o.Name,
                    ["object_type"] = o.ObjectType.ToString(),
                    ["selected"] = o.IsSelected(false) != 0,
                    ["layer"] = layer?.FullPath ?? layer?.Name ?? "",
                    ["layer_color"] = layer != null ? CA(layer.Color) : null,
                    ["bbox"] = bb.IsValid ? BB(bb) : null
                });
            }
            return arr;
        }

        /// <summary>
        /// Phase 1 capture_viewport rewrite:
        ///   - MemoryStream, no disk I/O
        ///   - JPEG default for shaded/rendered (5-10x smaller than PNG, imperceptible quality loss for AI vision)
        ///   - Bitmap.Resize for downscale instead of re-rendering Rhino at lower resolution
        ///   - Quality-stepped fallback to fit max_bytes (4 attempts max instead of 5 re-renders)
        /// Phase 6 (McNeel parity):
        ///   - restore_state: save + restore viewport camera/display-mode so AI inspection never
        ///     disrupts the user's current view. Default true.
        ///   - view / display_mode: optional overrides applied before capture, restored after.
        /// </summary>
        // v4.10.1 (field report B2): display modes with an ambient-occlusion / accumulation
        // pass (Arctic, Rendered, Raytraced) need the pipeline to resolve before capture.
        // Assigning vp.DisplayMode and capturing immediately produced washed-out,
        // semi-transparent frames showing interior geometry through opaque walls.
        private static readonly HashSet<string> AccumulatingDisplayModes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Arctic", "Rendered", "Raytraced", "Ray Traced", "Ambient Occlusion", "Rendered Studio" };

        private static bool IsAccumulatingMode(string modeName) =>
            !string.IsNullOrEmpty(modeName) && AccumulatingDisplayModes.Contains(modeName);

        /// <summary>Force the display pipeline to finish drawing before a capture.</summary>
        private static void SettleDisplay(Rhino.Display.RhinoViewport vp, string modeName)
        {
            try
            {
                var view = vp?.ParentView ?? Doc?.Views?.ActiveView;
                if (view == null) return;
                bool heavy = IsAccumulatingMode(modeName);
                int cycles = heavy ? 6 : 1;
                for (int i = 0; i < cycles; i++)
                {
                    view.Redraw();
                    RhinoApp.Wait();          // pump messages so the pipeline advances
                    if (heavy) System.Threading.Thread.Sleep(40);
                }
            }
            catch { /* settling is best-effort; never fail a capture over it */ }
        }

        /// <summary>
        /// Frame a selection to the OUTPUT image aspect (field report: the highest-value
        /// capture addition). width/height change the render resolution, and because the
        /// live viewport's frustum aspect is unrelated to the requested size, Rhino widens
        /// the FOV or crops instead of reframing - so every capture became a guess.
        ///
        /// This computes the camera EXACTLY: for each bbox corner, in camera space,
        ///     D >= |v.x| / tan(halfFovH) + v.z    and    D >= |v.y| / tan(halfFovV) + v.z
        /// Taking the maximum over all corners gives the closest distance at which the
        /// whole selection is inside the frame. Being absolute (derived from the bbox,
        /// never from the current camera) it is also idempotent - repeating a capture
        /// cannot drift.
        /// </summary>
        private bool ApplyFitFraming(Rhino.Display.RhinoViewport vp, JToken fitToken,
                                     int width, int height, out JObject note)
        {
            note = null;
            if (fitToken == null || vp == null) return false;

            JToken selector = fitToken;
            double margin = 0.04;
            if (fitToken is JObject fo)
            {
                selector = fo["selector"] ?? fo["layers"] ?? fo["ids"] ?? fo["objects"];
                var mg = fo["margin"]?.ToObject<double?>();
                if (mg.HasValue) margin = Math.Max(0.0, Math.Min(0.5, mg.Value));
            }
            if (selector == null) return false;

            // "by_layer:Foo" / "all" / guid list all resolve through the usual selector path.
            var objs = ResolveSelector(selector);
            if (objs.Count == 0)
            {
                note = new JObject { ["fit"] = "no objects matched - framing left unchanged" };
                return false;
            }
            var box = UnionBox(objs);
            if (!box.IsValid) return false;

            double aspect = (double)width / Math.Max(1, height);
            var vi = new Rhino.DocObjects.ViewportInfo(vp);
            vi.SetScreenPort(0, width, 0, height, 0, 1);
            vi.FrustumAspect = aspect;

            if (!vi.GetFrustum(out double fl, out double fr, out double fb, out double ft,
                               out double fn, out double ff))
                return false;

            var z = vi.CameraZ; z.Unitize();          // points from target BACK to camera
            var x = vi.CameraX; x.Unitize();
            var y = vi.CameraY; y.Unitize();
            var dir = -z;
            var c = box.Center;
            var corners = box.GetCorners();

            if (vi.IsParallelProjection)
            {
                double hw = 0, hh = 0, hd = 0;
                foreach (var pt in corners)
                {
                    var v = pt - c;
                    hw = Math.Max(hw, Math.Abs(v * x));
                    hh = Math.Max(hh, Math.Abs(v * y));
                    hd = Math.Max(hd, Math.Abs(v * z));
                }
                double halfH = Math.Max(hh, hw / aspect) * (1.0 + margin);
                if (halfH <= 0) return false;
                double halfW = halfH * aspect;
                double dist = hd * 3.0 + 1.0;
                vi.SetCameraLocation(c - dir * dist);
                vi.SetFrustum(-halfW, halfW, -halfH, halfH, 0.001, dist + hd * 3.0 + 1.0);
            }
            else
            {
                // IDEMPOTENCE: the vertical half-angle is read from the frustum and the
                // frustum is then rewritten, so folding the margin into the angle makes
                // each call widen the lens a little more - two identical captures drifted
                // (lens 33.7 -> 32.4). Keep the angles EXACTLY as they are and express the
                // margin purely as extra camera distance, which cannot feed back.
                double tanV = ft / fn;
                if (tanV <= 0) return false;
                double tanH = tanV * aspect;
                double d = 0;
                foreach (var pt in corners)
                {
                    var v = pt - c;
                    double vx = Math.Abs(v * x), vy = Math.Abs(v * y), vz = v * z;
                    d = Math.Max(d, Math.Max(vx / tanH, vy / tanV) + vz);
                }
                d *= (1.0 + margin);
                if (d <= 0) d = box.Diagonal.Length;
                double near = Math.Max(d * 0.01, 0.01);
                double far = d + box.Diagonal.Length * 2.0;
                vi.SetCameraLocation(c - dir * d);
                vi.SetFrustum(-tanH * near, tanH * near, -tanV * near, tanV * near, near, far);
            }

            vi.TargetPoint = c;
            bool applied = vp.SetViewProjection(vi, true);
            if (applied)
            {
                note = new JObject
                {
                    ["fit"] = "framed to the requested output aspect",
                    ["objects"] = objs.Count,
                    ["aspect"] = Math.Round(aspect, 4),
                    ["margin"] = margin,
                };
            }
            return applied;
        }

        JObject CaptureViewport(JObject p)
        {
            int w = p["width"]?.ToObject<int>() ?? 800;
            int h = p["height"]?.ToObject<int>() ?? 600;
            int max = p["max_bytes"]?.ToObject<int>() ?? 800_000;
            string format = (p["format"]?.ToString() ?? "auto").ToLower();
            int quality = p["quality"]?.ToObject<int>() ?? 80;
            bool restore = p["restore_state"]?.ToObject<bool>() ?? true;
            string viewOverride = p["view"]?.ToString();
            string modeOverride = p["display_mode"]?.ToString();
            bool annotate = p["annotate"]?.ToObject<bool>() ?? false;

            var vp = Doc.Views.ActiveView?.ActiveViewport;
            if (vp == null) return Err("No active viewport");

            // -- Save state --------------------------------------------------
            var savedTarget   = vp.CameraTarget;
            var savedLocation = vp.CameraLocation;
            var savedUp       = vp.CameraUp;
            var savedMode     = vp.DisplayMode;
            bool savedParallel = vp.IsParallelProjection;
            double savedLens = vp.Camera35mmLensLength;

            try
            {
                // -- Apply requested view/mode overrides --------------------------------------------------
                if (!string.IsNullOrEmpty(viewOverride))
                {
                    var proj = viewOverride.ToLower() switch
                    {
                        "top"    => Rhino.Display.DefinedViewportProjection.Top,
                        "front"  => Rhino.Display.DefinedViewportProjection.Front,
                        "right"  => Rhino.Display.DefinedViewportProjection.Right,
                        "left"   => Rhino.Display.DefinedViewportProjection.Left,
                        "back"   => Rhino.Display.DefinedViewportProjection.Back,
                        _        => Rhino.Display.DefinedViewportProjection.Perspective,
                    };
                    vp.SetProjection(proj, viewOverride, true);
                    vp.ZoomExtents();
                }
                if (!string.IsNullOrEmpty(modeOverride))
                {
                    var dm = Rhino.Display.DisplayModeDescription.FindByName(modeOverride);
                    if (dm == null)
                        return Err($"Display mode not found: {modeOverride}", "DISPLAY_MODE_NOT_FOUND");
                    vp.DisplayMode = dm;
                }
                // Optional lens override, applied BEFORE fit so framing accounts for it.
                var lens = p["lens_length"]?.ToObject<double?>();
                if (lens.HasValue && lens.Value > 1 && !vp.IsParallelProjection)
                    vp.Camera35mmLensLength = lens.Value;

                // fit: frame a selection to the requested OUTPUT aspect.
                JObject fitNote = null;
                if (p["fit"] != null)
                    ApplyFitFraming(vp, p["fit"], w, h, out fitNote);

                // Let camera and/or display-mode changes fully resolve before capturing.
                SettleDisplay(vp, vp.DisplayMode?.EnglishName ?? modeOverride);

                // -- Capture --------------------------------------------------
                Bitmap source;
                try
                {
                    // v4.8: render through ViewCapture - draws offscreen at the exact
                    // requested resolution. No viewport resize, no pre-capture Redraw.
                    source = null;
                    try
                    {
                        var vcap = new Rhino.Display.ViewCapture
                        {
                            Width = w,
                            Height = h,
                            ScaleScreenItems = false,
                            DrawAxes = false,
                            DrawGrid = vp.ConstructionGridVisible,
                            DrawGridAxes = vp.ConstructionAxesVisible,
                            TransparentBackground = false,
                            RealtimeRenderPasses = 0,
                        };
                        source = vcap.CaptureToBitmap(Doc.Views.ActiveView);
                    }
                    catch { source = null; }
                    if (source == null)
                    {
                        // Fallback: legacy live-viewport capture.
                        Doc.Views.ActiveView.Redraw();
                        source = Doc.Views.ActiveView.CaptureToBitmap(new Size(w, h));
                    }
                }
                catch (Exception e) { return Err($"Capture failed: {e.Message}"); }
                if (source == null) return Err("Capture returned null");

                // Pick format: explicit > display-mode-derived
                bool usePng;
                if (format == "png") usePng = true;
                else if (format == "jpeg" || format == "jpg") usePng = false;
                else
                {
                    var dm = vp.DisplayMode?.EnglishName ?? "";
                    var pngModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "Wireframe", "Ghosted", "Hidden", "Technical", "Artistic", "Pen" };
                    usePng = pngModes.Contains(dm);
                }

                byte[] bytes = null;
                int outW = w, outH = h;
                string actualFormat = usePng ? "png" : "jpeg";

                using (source)
                {
                    bytes = Encode(source, usePng, quality);
                    if (bytes.Length > max)
                    {
                        foreach (double sc in new[] { 0.75, 0.5, 0.35, 0.25 })
                        {
                            outW = (int)(w * sc); outH = (int)(h * sc);
                            using var scaled = new Bitmap(outW, outH);
                            using (var g = Graphics.FromImage(scaled))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.DrawImage(source, 0, 0, outW, outH);
                            }
                            bytes = Encode(scaled, usePng, quality);
                            if (bytes.Length <= max || sc <= 0.25) break;
                        }
                    }
                }

                var r = Ok(
                    ("image_base64", Convert.ToBase64String(bytes)),
                    ("format", actualFormat),
                    ("width", outW),
                    ("height", outH),
                    ("bytes", bytes.Length));
                if (outW != w) r["note"] = $"Scaled to {outW}x{outH}";
                // Viewport metadata -- camera context for every capture
                try {
                    var snap2 = SceneSnapshotRegistry.Active;
                    r["camera"] = new JObject {
                        ["location"]     = PA(vp.CameraLocation),
                        ["target"]       = PA(vp.CameraTarget),
                        ["projection"]   = vp.IsParallelProjection ? "parallel" : "perspective",
                        ["display_mode"] = vp.DisplayMode?.EnglishName ?? "Unknown",
                        ["lens_mm"]      = vp.IsParallelProjection ? 0.0 : Math.Round(vp.Camera35mmLensLength, 1)
                    };
                    r["scene"] = new JObject {
                        ["visible_objects"] = Doc.Objects.Count(o => !o.IsDeleted && o.Visible),
                        ["total_objects"]   = snap2?.Count ?? 0
                    };
                    if (fitNote != null) r["framing"] = fitNote;
                    if (annotate)
                    {
                        r["annotations"] = CaptureAnnotations(p);
                        r["annotation_scope"] = p["annotation_scope"]?.ToString() ?? "selected";
                    }
                } catch { /* metadata best-effort */ }
                return r;
            }
            finally
            {
                // -- Restore state --------------------------------------------------
                if (restore)
                {
                    try
                    {
                        if (savedParallel) vp.ChangeToParallelProjection(true);
                        else              vp.ChangeToPerspectiveProjection(true, savedLens);
                        vp.SetCameraLocations(savedTarget, savedLocation);
                        vp.CameraUp = savedUp;
                        vp.DisplayMode = savedMode;
                        Doc.Views.ActiveView.Redraw();
                    }
                    catch { /* best-effort restore */ }
                }
            }
        }

        /// <summary>
        /// set_camera - two modes:
        ///   1. Explicit location+target: positions camera directly.
        ///   2. Bounding-box framing (box_min + box_max): computes camera distance to fit bbox.
        ///      Mirrors McNeel's boxMin/boxMax parameter.
        /// </summary>
        JObject CaptureInspectionView(JObject p)
        {
            var vp = Doc.Views.ActiveView?.ActiveViewport;
            if (vp == null) return Err("No active viewport");

            var savedTarget = vp.CameraTarget;
            var savedLocation = vp.CameraLocation;
            var savedUp = vp.CameraUp;
            var savedMode = vp.DisplayMode;
            bool savedParallel = vp.IsParallelProjection;
            double savedLens = vp.Camera35mmLensLength;   // preserve the user's lens, don't reset to 50mm

            try
            {
                if (p["projection"]?.ToString()?.ToLowerInvariant() == "parallel")
                    vp.ChangeToParallelProjection(true);
                else if (p["projection"]?.ToString()?.ToLowerInvariant() == "perspective")
                    vp.ChangeToPerspectiveProjection(true, 50);

                if (p["display_mode"] != null)
                {
                    var dm = Rhino.Display.DisplayModeDescription.FindByName(p["display_mode"].ToString());
                    if (dm != null) vp.DisplayMode = dm;
                }

                if ((p["location"] != null || p["camera_location"] != null) &&
                    (p["target"] != null || p["camera_target"] != null))
                {
                    var loc = Pt(p["location"] ?? p["camera_location"]);
                    var tgt = Pt(p["target"] ?? p["camera_target"]);
                    vp.SetCameraLocations(tgt, loc);
                }
                else if (p["direction"] != null && p["target"] != null)
                {
                    var target = Pt(p["target"]);
                    var dir = Vec(p["direction"]);
                    if (!dir.Unitize()) return Err("direction cannot be zero", "INVALID_REQUEST");
                    double distance = p["distance"]?.ToObject<double>() ?? 20000.0;
                    vp.SetCameraLocations(target, target - dir * distance);
                }
                else if (p["box_min"] != null && p["box_max"] != null)
                {
                    var bbox = new BoundingBox(Pt(p["box_min"]), Pt(p["box_max"]));
                    vp.ZoomBoundingBox(bbox);
                }

                var captureParams = new JObject
                {
                    ["width"] = p["width"] ?? 900,
                    ["height"] = p["height"] ?? 650,
                    ["max_bytes"] = p["max_bytes"] ?? 900000,
                    ["format"] = p["format"] ?? "auto",
                    ["quality"] = p["quality"] ?? 80,
                    ["restore_state"] = false
                };
                var r = CaptureViewport(captureParams);
                r["inspection"] = true;
                r["offscreen"] = false;
                r["viewport_restored"] = true;
                r["note"] = "Captured from an inspection camera and restored the active viewport.";
                return r;
            }
            finally
            {
                try
                {
                    if (savedParallel) vp.ChangeToParallelProjection(true);
                    else vp.ChangeToPerspectiveProjection(true, savedLens);
                    vp.SetCameraLocations(savedTarget, savedLocation);
                    vp.CameraUp = savedUp;
                    vp.DisplayMode = savedMode;
                    Doc.Views.ActiveView?.Redraw();
                }
                catch { }
            }
        }

        JObject SetCamera(JObject p)
        {
            var vp = Doc.Views.ActiveView?.ActiveViewport;
            if (vp == null) return Err("No active viewport");

            // Projection override
            string proj = p["projection"]?.ToString()?.ToLower();
            if (proj == "parallel") vp.ChangeToParallelProjection(true);
            else if (proj == "perspective") vp.ChangeToPerspectiveProjection(true, 50);

            // Lens length
            if (p["lens_length"] != null)
            {
                double ll = p["lens_length"].ToObject<double>();
                vp.Camera35mmLensLength = ll;
            }

            bool hasBbox = p["box_min"] != null && p["box_max"] != null;
            if (hasBbox)
            {
                // Bounding-box framing mode
                var mn = p["box_min"].ToObject<double[]>();
                var mx = p["box_max"].ToObject<double[]>();
                var bbox = new BoundingBox(
                    new Point3d(mn[0], mn[1], mn[2]),
                    new Point3d(mx[0], mx[1], mx[2]));
                vp.ZoomBoundingBox(bbox);
            }
            else if (p["location"] != null && p["target"] != null)
            {
                var loc = p["location"].ToObject<double[]>();
                var tgt = p["target"].ToObject<double[]>();
                var locPt = new Point3d(loc[0], loc[1], loc[2]);
                var tgtPt = new Point3d(tgt[0], tgt[1], tgt[2]);
                vp.SetCameraLocations(tgtPt, locPt);
            }
            else
            {
                return Err("Provide either location+target or box_min+box_max");
            }

            // Phase 7: Don’t use RedrawScope.Mark() for camera-only changes.
            // Mark() triggers a full Doc.Views.Redraw() at scope exit, which
            // re-renders all 3D objects in rendered mode — 4+ minute hang on large scenes.
            // Instead, invalidate only the active view for a lightweight refresh.
            try { Doc.Views.ActiveView?.Redraw(); }
            catch { /* best-effort */ }
            return Ok(("camera_set", true));
        }

        /// <summary>
        /// get_rhino_commands - live command discoverability via Command.GetCommandNames.
        /// Mirrors McNeel's get_commands tool. Capped at 200 results.
        /// </summary>
        JObject GetRhinoCommands(JObject p)
        {
            string filter = p["filter"]?.ToString() ?? "";
            var names = Rhino.Commands.Command.GetCommandNames(true, false)
                .Where(n => string.IsNullOrEmpty(filter)
                         || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n)
                .Take(200)
                .ToArray();
            var r = Ok(("count", names.Length), ("filter", filter));
            r["commands"] = new Newtonsoft.Json.Linq.JArray(names);
            return r;
        }

        // --- AUTO-THUMBNAIL --------------------------------------------------
        /// <summary>
        /// Captures a small JPEG thumbnail of the active viewport.
        /// Called automatically after every top-level mutation and after every batch.
        /// Claude can see what it built without issuing a separate capture_viewport call.
        /// Returns null on any error (thumbnail is best-effort, never fails the command).
        /// </summary>
        private static string TryCaptureThumbnail(int w = 240, int h = 180, int quality = 55)
        {
            try
            {
                var view = Doc?.Views?.ActiveView;
                if (view == null) return null;
                using var bmp = view.CaptureToBitmap(new Size(w, h));
                if (bmp == null) return null;
                var enc = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.MimeType == "image/jpeg");
                if (enc == null) return null;
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
                using var ms = new MemoryStream();
                bmp.Save(ms, enc, ep);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch { return null; }
        }

        /// <summary>
        /// v4.8: capture a small thumbnail from a named projection (top/front/...),
        /// restoring the user's camera afterwards. Used for big-batch review strips.
        /// </summary>
        private static string TryCaptureViewThumbnail(string viewName, int w = 240, int h = 180, int quality = 55)
        {
            try
            {
                var view = Doc?.Views?.ActiveView;
                var vp = view?.ActiveViewport;
                if (vp == null) return null;
                var savedTarget = vp.CameraTarget;
                var savedLocation = vp.CameraLocation;
                var savedUp = vp.CameraUp;
                bool savedParallel = vp.IsParallelProjection;
                double savedLens = vp.Camera35mmLensLength;
                try
                {
                    var proj = viewName.ToLowerInvariant() switch
                    {
                        "top" => Rhino.Display.DefinedViewportProjection.Top,
                        "front" => Rhino.Display.DefinedViewportProjection.Front,
                        "right" => Rhino.Display.DefinedViewportProjection.Right,
                        "left" => Rhino.Display.DefinedViewportProjection.Left,
                        "back" => Rhino.Display.DefinedViewportProjection.Back,
                        _ => Rhino.Display.DefinedViewportProjection.Perspective,
                    };
                    vp.SetProjection(proj, null, true);
                    vp.ZoomExtents();
                    return TryCaptureThumbnail(w, h, quality);
                }
                finally
                {
                    if (savedParallel) vp.ChangeToParallelProjection(true);
                    else vp.ChangeToPerspectiveProjection(true, savedLens);
                    vp.SetCameraLocations(savedTarget, savedLocation);
                    vp.CameraUp = savedUp;
                }
            }
            catch { return null; }
        }

        // --- MATERIALS --------------------------------------------------
        /// <summary>
        /// set_layer_material - set PBR material properties on a layer.
        /// Parity with McNeel's set_layer_material tool.
        /// color: [R, G, B] or [R, G, B, A] 0-255 ints.
        /// roughness / metallic / opacity: 0.0-1.0 floats.
        /// emission: [R, G, B] emissive color 0-255.
        /// </summary>
        JObject SetLayerMaterial(JObject p)
        {
            string layerName = p["layer"]?.ToString();
            if (string.IsNullOrEmpty(layerName)) return Err("layer required");

            // Find layer by full path, then short name
            int li = Doc.Layers.FindByFullPath(layerName, -1);
            if (li < 0)
            {
                var found = Doc.Layers
                    .Where(l => !l.IsDeleted &&
                           string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (found != null) li = found.Index;
            }
            if (li < 0) return Err($"Layer not found: {layerName}");

            var layer = Doc.Layers[li];

            // Resolve or create material
            int matIdx = layer.RenderMaterialIndex;
            Rhino.DocObjects.Material mat;
            if (matIdx >= 0 && matIdx < Doc.Materials.Count)
            {
                // Doc.Materials[i] returns a value copy in RhinoCommon — assign directly.
                mat = Doc.Materials[matIdx];
            }
            else
            {
                mat = new Rhino.DocObjects.Material { Name = $"AI_{layerName}" };
                matIdx = -1;
            }

            // Apply color to both layer display color and material diffuse
            if (p["color"] != null)
            {
                var c = p["color"].ToObject<int[]>();
                var col = c.Length > 3
                    ? System.Drawing.Color.FromArgb(c[3], c[0], c[1], c[2])
                    : System.Drawing.Color.FromArgb(255, c[0], c[1], c[2]);
                layer.Color = col;
                mat.DiffuseColor = col;
            }

            // PBR-style properties mapped to Rhino material
            if (p["roughness"] != null)
            {
                double r = Math.Clamp(p["roughness"].ToObject<double>(), 0.0, 1.0);
                mat.ReflectionGlossiness = 1.0 - r;   // Rhino: glossiness = 1 - roughness
            }
            if (p["metallic"] != null)
            {
                mat.Reflectivity = Math.Clamp(p["metallic"].ToObject<double>(), 0.0, 1.0);
            }
            if (p["opacity"] != null)
            {
                double op = Math.Clamp(p["opacity"].ToObject<double>(), 0.0, 1.0);
                mat.Transparency = 1.0 - op;
            }
            if (p["emission"] != null)
            {
                var e = p["emission"].ToObject<int[]>();
                mat.EmissionColor = System.Drawing.Color.FromArgb(255, e[0], e[1], e[2]);
            }

            // Commit material
            if (matIdx < 0)
            {
                matIdx = Doc.Materials.Add(mat);
                layer.RenderMaterialIndex = matIdx;
            }
            else
            {
                Doc.Materials.Modify(mat, matIdx, true);
            }
            Doc.Layers.Modify(layer, li, true);
            RedrawScope.Mark();
            return Ok(("layer", layerName), ("material_index", matIdx), ("applied", true));
        }

        // --- RUN COMMAND --------------------------------------------------
        /// <summary>
        /// run_command - execute any Rhino command string via RhinoApp.RunScript.
        /// First-class MCP tool mirroring McNeel's approach. Tracks newly created objects.
        /// echo=false suppresses Rhino's command-line echo (default, keeps UI clean).
        /// </summary>
        JObject RunCommand(JObject p)
        {
            string cmd = p["command"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(cmd)) return Err("command required");
            if (Doc == null) return Err("No active document", "RHINO_NOT_RUNNING");
            bool echo = p["echo"]?.ToObject<bool>() ?? false;

            bool ok = false;
            var newIds = CaptureAddedIds(() => ok = RhinoApp.RunScript(cmd, echo));
            RedrawScope.Mark();

            var r = Ok(("command", cmd), ("success", ok));
            if (newIds.Count > 0) r["new_object_ids"] = new JArray(newIds.Take(20).ToArray<object>());
            if (!ok)
            {
                r["status"] = "error";
                r["error_code"] = "COMMAND_FAILED";
                r["message"] = "Command returned false. Use execute_script for Python scripts with print() output.";
            }
            return r;
        }

        private static byte[] Encode(Bitmap bmp, bool png, int quality)
        {
            using var ms = new MemoryStream();
            if (png)
            {
                bmp.Save(ms, ImageFormat.Png);
            }
            else
            {
                var enc = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
                bmp.Save(ms, enc, ep);
            }
            return ms.ToArray();
        }

        JObject SelectObjects(JObject p)
        {
            if (p["clear_selection"]?.ToObject<bool>() != false) Doc.Objects.UnselectAll();
            var guids = p["object_ids"].ToObject<List<string>>()
                .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty).ToList();
            int c = Doc.Objects.Select(guids, true);
            RedrawScope.Mark();
            return Ok(("selected_count", c));
        }

        // --- WORKFLOW (Tier 2) --------------------------------------------------
        JObject GetCrossSection(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["object_id"].ToString()));
            if (o == null) return Err("Not found");
            var b = GetBrep(o); if (b == null) return Err("Not a Brep");
            double z = p["z_height"].ToObject<double>();
            if (!Intersection.BrepPlane(b, new Plane(new Point3d(0, 0, z), Vector3d.ZAxis), Tol, out var curves, out _) || curves.Length == 0)
            {
                var bb = b.GetBoundingBox(true);
                return Err($"No intersection at z={z}. Range: {bb.Min.Z:F0}-{bb.Max.Z:F0}");
            }
            var a = MkAttr(p); var ni = new JArray();
            foreach (var c in curves) ni.Add(Doc.Objects.AddCurve(c, a).ToString());
            RedrawScope.Mark();
            return Ok(("object_ids", ni), ("z_height", z), ("curve_count", curves.Length));
        }

        JObject GetSectionProfile(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["object_id"].ToString()));
            if (o == null) return Err("Not found", "OBJECT_NOT_FOUND");
            var b = GetBrep(o);
            if (b == null) return Err("Not a Brep", "NOT_A_BREP");

            double z = p["z_height"]?.ToObject<double>() ?? 0.0;
            int samples = Math.Clamp(p["samples"]?.ToObject<int>() ?? 80, 8, 300);
            if (!Intersection.BrepPlane(b, new Plane(new Point3d(0, 0, z), Vector3d.ZAxis), Tol, out var curves, out _) || curves.Length == 0)
            {
                var bb = b.GetBoundingBox(true);
                return Err($"No intersection at z={z}. Range: {bb.Min.Z:F0}-{bb.Max.Z:F0}", "NO_INTERSECTION",
                    new JObject { ["z_min"] = bb.Min.Z, ["z_max"] = bb.Max.Z });
            }

            var payload = PolylinesPayload(curves.Select(c => SampleCurvePoints(c, samples)), "xy");
            payload["status"] = "ok";
            payload["z_height"] = z;
            payload["curve_count"] = curves.Length;
            return payload;
        }

        JObject GetSilhouette(JObject p)
        {
            var ids = p["object_ids"] != null
                ? ResIds(p["object_ids"]).ToList()
                : Doc.Objects.Where(o => !o.IsDeleted && o.Visible).Select(o => o.Id.ToString()).ToList();
            string view = p["view"]?.ToString()?.ToLowerInvariant() ?? "front";
            string projection = view switch
            {
                "top" => "xy",
                "right" or "left" => "yz",
                _ => "xz"
            };

            var polylines = new List<List<Point3d>>();
            foreach (var id in ids.Take(250))
            {
                var ro = Doc.Objects.FindId(new Guid(id));
                var bb = ro?.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                if (!bb.IsValid) continue;

                var min = bb.Min;
                var max = bb.Max;
                if (projection == "xy")
                    polylines.Add(new List<Point3d> { new(min.X, min.Y, 0), new(max.X, min.Y, 0), new(max.X, max.Y, 0), new(min.X, max.Y, 0), new(min.X, min.Y, 0) });
                else if (projection == "yz")
                    polylines.Add(new List<Point3d> { new(0, min.Y, min.Z), new(0, max.Y, min.Z), new(0, max.Y, max.Z), new(0, min.Y, max.Z), new(0, min.Y, min.Z) });
                else
                    polylines.Add(new List<Point3d> { new(min.X, 0, min.Z), new(max.X, 0, min.Z), new(max.X, 0, max.Z), new(min.X, 0, max.Z), new(min.X, 0, min.Z) });
            }

            var payload = PolylinesPayload(polylines, projection);
            payload["status"] = "ok";
            payload["view"] = view;
            payload["projection"] = projection;
            payload["note"] = "Cheap silhouette from object bounding boxes; use capture_viewport for final visual QA.";
            return payload;
        }

        JObject CreateFloorStack(JObject p)
        {
            int levels = p["levels"]?.ToObject<int>() ?? 10;
            double fh = MmDef(p, "floor_height", 3000);
            double st = MmDef(p, "slab_thickness", 300);
            double sz = p["start_z"]?.ToObject<double>() ?? 0;
            string layer = p["layer"]?.ToString() ?? "Slab";
            var ni = new JArray();

            if (p["footprint_id"] != null)
            {
                var o = Doc.Objects.FindId(new Guid(p["footprint_id"].ToString()));
                if (o == null) return Err("Not found");
                if (o.Geometry is Brep br)
                {
                    var bb = br.GetBoundingBox(true);
                    for (int i = 0; i < levels; i++)
                    {
                        double z = sz + i * fh;
                        if (z < bb.Min.Z || z > bb.Max.Z) continue;
                        if (Intersection.BrepPlane(br, new Plane(new Point3d(0, 0, z), Vector3d.ZAxis), Tol, out var curves, out _) && curves.Length > 0)
                            foreach (var c in curves.Where(c => c.IsClosed))
                            {
                                var slab = ExtrudeCC(c, new Vector3d(0, 0, -st));
                                if (slab != null)
                                {
                                    var a = new ObjectAttributes { Name = $"Floor_{i:D2}", LayerIndex = EnsureLayer(layer) };
                                    ni.Add(Doc.Objects.AddBrep(slab, a).ToString());
                                }
                            }
                    }
                }
                else if (o.Geometry is Curve crv && crv.IsClosed)
                {
                    for (int i = 0; i < levels; i++)
                    {
                        double z = sz + i * fh;
                        var m = crv.DuplicateCurve();
                        m.Translate(new Vector3d(0, 0, z - crv.PointAtStart.Z));
                        var slab = ExtrudeCC(m, new Vector3d(0, 0, -st));
                        if (slab != null)
                        {
                            var a = new ObjectAttributes { Name = $"Floor_{i:D2}", LayerIndex = EnsureLayer(layer) };
                            ni.Add(Doc.Objects.AddBrep(slab, a).ToString());
                        }
                    }
                }
            }
            else if (p["boundary_points"] != null)
            {
                var pts = p["boundary_points"].Select(t => Pt(t)).ToList();
                if (pts.First().DistanceTo(pts.Last()) > 0.01) pts.Add(pts[0]);
                var bc = new Polyline(pts).ToNurbsCurve();
                for (int i = 0; i < levels; i++)
                {
                    double z = sz + i * fh;
                    var m = bc.DuplicateCurve();
                    m.Translate(new Vector3d(0, 0, z - m.PointAtStart.Z));
                    var slab = ExtrudeCC(m, new Vector3d(0, 0, -st));
                    if (slab != null)
                    {
                        var a = new ObjectAttributes { Name = $"Floor_{i:D2}", LayerIndex = EnsureLayer(layer) };
                        ni.Add(Doc.Objects.AddBrep(slab, a).ToString());
                    }
                }
            }

            return Ok(("object_ids", ni), ("count", ni.Count));
        }

        JObject GroupObjects(JObject p)
        {
            var ids = ResIds(p["object_ids"]);
            string name = p["name"]?.ToString() ?? "Group";
            int gi = Doc.Groups.Add(name);
            foreach (var sid in ids) Doc.Groups.AddToGroup(gi, new Guid(sid));
            return Ok(("group_name", name), ("group_index", gi), ("member_count", ids.Count));
        }
        JObject UngroupObjects(JObject p)
        {
            int i = p["name"] != null ? Doc.Groups.Find(p["name"].ToString()) : p["group_index"]?.ToObject<int>() ?? -1;
            if (i < 0) return Err("Not found");
            Doc.Groups.Delete(i);
            return Ok(("deleted_group", i));
        }
        JObject GetGroups(JObject p)
        {
            var g = new JArray();
            for (int i = 0; i < Doc.Groups.Count; i++)
            {
                var gr = Doc.Groups.FindIndex(i);
                if (gr != null) g.Add(new JObject { ["name"] = gr.Name ?? $"Group_{gr.Index}", ["index"] = gr.Index });
            }
            return Ok(("groups", g));
        }
        JObject HollowSolid(JObject p)
        {
            var o = Doc.Objects.FindId(new Guid(p["object_id"].ToString()));
            if (o == null) return Err("Not found");
            var b = GetBrep(o);
            if (b == null || !b.IsSolid) return Err("Must be solid");
            var off = Brep.CreateOffsetBrep(b, -(p["thickness"]?.ToObject<double>() ?? 200), true, true, Tol, out _, out _);
            if (off == null || off.Length == 0) return Err("Offset failed");
            var res = Brep.CreateBooleanDifference(b, off[0], Tol);
            if (res == null || res.Length == 0) return Err("Shell failed");
            var gid = Doc.Objects.AddBrep(res[0], MkAttr(p));
            if (p["delete_original"]?.ToObject<bool>() != false) Doc.Objects.Delete(o, true);
            RedrawScope.Mark();
            return CrResult(gid, p["layer"]?.ToString(), WantMeasure(p));
        }
        JObject BatchCreate(JObject p)
        {
            var items = p["objects"] as JArray ?? new JArray();
            var ni = new JArray();
            foreach (JObject item in items)
            {
                var r = CreateObject(item);
                if (r["object_ids"] is JArray ids) foreach (var id in ids) ni.Add(id);
            }
            // Mark already called by each CreateObject; no need to mark again. Outer scope coalesces.
            return Ok(("total_created", ni.Count), ("object_ids", ni));
        }

        // --- INTELLIGENCE (Tier 3) --------------------------------------------------
        JObject ValidateArch(JObject p)
        {
            // Phase 2: counts + layer filtering via snapshot. Brep validity still needs live geometry.
            var snap = Snap;
            int total = snap?.Count ?? AllObjs().Count;
            int defaultLayerCount = 0;
            int unnamedCount = 0;
            var brepCandidates = new List<Guid>();

            if (snap != null)
            {
                foreach (var m in snap.All())
                {
                    if (snap.LayerNameOf(m) == "Default") defaultLayerCount++;
                    if (string.IsNullOrEmpty(m.Name)) unnamedCount++;
                    if (m.Type == ObjectType.Brep || m.Type == ObjectType.Extrusion)
                        brepCandidates.Add(m.Id);
                }
            }
            else
            {
                var objs = AllObjs();
                defaultLayerCount = objs.Count(o => Doc.Layers[o.Attributes.LayerIndex].Name == "Default");
                unnamedCount = objs.Count(o => string.IsNullOrEmpty(o.Attributes.Name));
                brepCandidates = objs.Where(o => o.Geometry is Brep || o.Geometry is Extrusion).Select(o => o.Id).ToList();
            }

            var issues = new JArray();
            int solidCount = 0;
            int checkedBreps = 0;
            // Cap at 100 so a 5000-object scene doesn't pay for thousands of validity checks.
            // Phase 5 will cache per-object validity flags; this is a conscious deferral.
            foreach (var id in brepCandidates.Take(100))
            {
                var ro = Doc.Objects.FindId(id);
                if (ro == null) continue;
                var b = GetBrep(ro); if (b == null) continue;
                checkedBreps++;
                if (b.IsSolid) solidCount++;
                if (!b.IsValid) issues.Add(new JObject { ["id"] = id.ToString(), ["issue"] = "Invalid Brep", ["severity"] = "error" });
                if (!b.IsSolid) issues.Add(new JObject { ["id"] = id.ToString(), ["issue"] = "Open Brep", ["severity"] = "warning" });
                var bb = b.GetBoundingBox(true); var sz = bb.Max - bb.Min;
                if (Math.Min(sz.X, Math.Min(sz.Y, sz.Z)) < 1)
                    issues.Add(new JObject { ["id"] = id.ToString(), ["issue"] = $"Very thin: {Math.Min(sz.X, Math.Min(sz.Y, sz.Z)):F1}mm", ["severity"] = "info" });
            }
            if (defaultLayerCount > 10) issues.Add(new JObject { ["issue"] = $"{defaultLayerCount} objects on Default layer", ["severity"] = "suggestion" });
            if (unnamedCount > 20) issues.Add(new JObject { ["issue"] = $"{unnamedCount} unnamed objects", ["severity"] = "suggestion" });
            return Ok(
                ("stats", new JObject { ["total"] = total, ["solids"] = solidCount, ["breps_checked"] = checkedBreps }),
                ("issues", issues));
        }

        JObject SuggestTools(JObject p)
        {
            var snap = Snap;
            var s = new JArray();

            if (snap == null)
            {
                var objs = AllObjs();
                var byLayerFb = objs.GroupBy(o => Doc.Layers[o.Attributes.LayerIndex]?.Name ?? "Default").ToDictionary(g => g.Key, g => g.Count());
                if (objs.Any(o => { var bb = o.Geometry?.GetBoundingBox(true); return bb.HasValue && bb.Value.Max.Z - bb.Value.Min.Z > 5000; }) && !byLayerFb.ContainsKey("Slab"))
                    s.Add("Tall massing but no Slab layer - use create_floor_stack");
                if (byLayerFb.GetValueOrDefault("Default", 0) > 10) s.Add("Organize - use setup_arch_layers then set_object_layer");
                if (Doc.Groups.Count == 0 && objs.Count > 20) s.Add($"{objs.Count} ungrouped objects - use group_objects");
                return Ok(("suggestions", s), ("scene_summary", JObject.FromObject(byLayerFb)));
            }

            var byLayer = snap.CountsByLayerName();
            // "Tall massing" check - uses cached bboxes, no geometry refetch.
            bool tallMassing = snap.All().Any(m => m.Bbox.IsValid && (m.Bbox.Max.Z - m.Bbox.Min.Z) > 5000);
            if (tallMassing && !byLayer.ContainsKey("Slab")) s.Add("Tall massing but no Slab layer - use create_floor_stack");
            if (byLayer.GetValueOrDefault("Default", 0) > 10) s.Add("Organize - use setup_arch_layers then set_object_layer");
            if (Doc.Groups.Count == 0 && snap.Count > 20) s.Add($"{snap.Count} ungrouped objects - use group_objects");
            return Ok(("suggestions", s), ("scene_summary", JObject.FromObject(byLayer)));
        }

        JObject LintScript(JObject p)
        {
            string code = p["code"]?.ToString() ?? "";
            var s = new JArray();
            var pats = new Dictionary<string, string>
            {
                ["AddBox"] = "create_box",
                ["AddCylinder"] = "create_cylinder",
                ["AddSphere"] = "create_sphere",
                ["CreateBooleanUnion"] = "boolean_operation",
                ["CreateFromLoft"] = "loft",
                ["CreatePipe"] = "pipe",
                ["for i in range"] = "create_floor_stack or batch"
            };
            foreach (var (k, v) in pats)
                if (code.Contains(k)) s.Add(new JObject { ["pattern"] = k, ["suggestion"] = $"Use structured tool: {v}" });
            if (Doc.ModelUnitSystem == UnitSystem.Millimeters && new[] { "= 10\n", "= 20\n", "= 100\n", "= 120\n" }.Any(n => code.Contains(n)))
                s.Add(new JObject { ["pattern"] = "Small numbers in mm doc", ["suggestion"] = "Multiply by 1000?" });
            return Ok(("suggestions", s), ("tool_alternatives", s.Count));
        }

        JObject GetCameraTarget(JObject p)
        {
            var vp = Doc.Views.ActiveView.ActiveViewport;
            var r = Ok(
                ("camera_location", PA(vp.CameraLocation)),
                ("camera_target", PA(vp.CameraTarget)));
            var cam = vp.CameraLocation;
            var dir = vp.CameraTarget - cam;
            r["ground_point"] = Math.Abs(dir.Z) > 0.001
                ? PA(new Point3d(cam.X - dir.X * cam.Z / dir.Z, cam.Y - dir.Y * cam.Z / dir.Z, 0))
                : PA(vp.CameraTarget);
            return r;
        }

        // --- SCRIPT & UNDO --------------------------------------------------
        JObject StartScriptServer(JObject p)
        {
            try
            {
                bool ok = RhinoApp.RunScript("_-StartScriptServer", false);
                if (!ok) ok = RhinoApp.RunScript("_StartScriptServer", false);
                return Ok(
                    ("started", ok),
                    ("message", ok
                        ? "RhinoCode script server start command issued."
                        : "StartScriptServer command returned false. Rhino 8.11+ is required for rhinocode CLI execution."),
                    ("requires", "Rhino 8.11+ with the RhinoCode script server"));
            }
            catch (Exception e)
            {
                return Err($"Failed to start RhinoCode script server: {e.Message}", "SCRIPT_SERVER_FAILED");
            }
        }

        JObject ExecuteScript(JObject p)
        {
            string code = p["code"]?.ToString();
            JObject autoCheckpoint = p?["auto_checkpoint"]?.ToObject<bool>() == false
                ? null
                : SaveAutoCheckpoint("Script");
            uint uid = Doc.BeginUndoRecord(p["undo_name"]?.ToString() ?? "AI: Script");
            try
            {
                var py = Rhino.Runtime.PythonScript.Create();
                if (py == null) return Err("Python engine unavailable");
                var output = new List<string>();
                try { py.Output += s => output.Add(s?.ToString() ?? ""); } catch { }
                // Preamble injected before every AI-generated script. System is needed for
                // System.Drawing.Color, System.Guid, etc. - scripts don't need to import it.
                // Double-importing is a no-op, so user code may also import these safely.
                // Phase 7: Auto-import Rhino.Geometry (every script uses it) and
                // inject a boolean-failure tracking wrapper so silent failures
                // get surfaced as warnings in the response.
                const string preamble =
                    "import rhinoscriptsyntax as rs\n" +
                    "import scriptcontext as sc\n" +
                    "import Rhino\n" +
                    "from Rhino.Geometry import *\n" +
                    "import System\n" +
                    "sc.doc = Rhino.RhinoDoc.ActiveDoc\n" +
                    "# Boolean-failure tracker\n" +
                    "_rab_bool_fails = [0]\n" +
                    "def _rab_check_bool(result, op_name=\'boolean\'):\n" +
                    "    if result is None or (hasattr(result, \'Count\') and result.Count == 0) or (isinstance(result, (list, tuple)) and len(result) == 0):\n" +
                    "        _rab_bool_fails[0] += 1\n" +
                    "        print(\'[BOOLEAN_FAIL] %s returned empty/None (total: %d)\' % (op_name, _rab_bool_fails[0]))\n" +
                    "        return None\n" +
                    "    return result\n";
                bool ok = false;
                var newIds = CaptureAddedIds(() => ok = py.ExecuteScript(preamble + code));
                bool rollbackOnError = p["rollback_on_error"]?.ToObject<bool>() ?? false;
                var rolledBackIds = new List<string>();
                if (!ok && rollbackOnError && newIds.Count > 0)
                {
                    foreach (var nid in newIds.ToList())
                    {
                        if (Guid.TryParse(nid, out var gid) && Doc.Objects.Delete(gid, true))
                            rolledBackIds.Add(nid);
                    }
                    newIds = new List<string>();
                }

                RedrawScope.Mark();   // outer scope flushes; no double redraw

                var warns = new JArray();
                foreach (var nid in newIds.Take(10))
                {
                    var o = Doc.Objects.FindId(new Guid(nid));
                    if (o?.Geometry != null)
                    {
                        var bb = o.Geometry.GetBoundingBox(true);
                        double mx = Math.Max(bb.Max.X - bb.Min.X, Math.Max(bb.Max.Y - bb.Min.Y, bb.Max.Z - bb.Min.Z));
                        if (Doc.ModelUnitSystem == UnitSystem.Millimeters && mx < 10)
                            warns.Add($"{nid[..8]} is {mx:F1}mm - meters?");
                    }
                }
                if (p["default_layer"] != null)
                {
                    int li = EnsureLayer(p["default_layer"].ToString());
                    foreach (var nid in newIds)
                    {
                        var o = Doc.Objects.FindId(new Guid(nid));
                        if (o != null) { o.Attributes.LayerIndex = li; Doc.Objects.ModifyAttributes(o, o.Attributes, true); }
                    }
                }
                var r = new JObject
                {
                    ["status"] = ok ? "ok" : "error",
                    ["output"] = output.Count > 0 ? string.Join("\n", output) : "(no output)",
                    ["objects_created"] = newIds.Count,
                    ["new_object_ids"] = new JArray(newIds.Take(20))
                };
                if (rolledBackIds.Count > 0)
                {
                    r["rolled_back"] = true;
                    r["rolled_back_object_ids"] = new JArray(rolledBackIds.Take(20));
                }
                // Phase 7: Detect boolean failures from the _rab_check_bool tracker
                int boolFails = 0;
                foreach (var line in output)
                {
                    if (line != null && line.Contains("[BOOLEAN_FAIL]"))
                        boolFails++;
                }
                if (boolFails > 0)
                {
                    warns.Add($"\u26a0\ufe0f {boolFails} boolean operation(s) returned empty/None. Check geometry validity (IsSolid, overlap).");
                    r["boolean_failures"] = boolFails;
                }
                if (warns.Count > 0) r["warnings"] = warns;
                if (!ok) r["message"] = "Script failed";
                if (autoCheckpoint != null) r["auto_checkpoint"] = autoCheckpoint;
                return r;
            }
            catch (Exception e)
            {
                var r = ErrFromException(e, "Script");
                if (autoCheckpoint != null) r["auto_checkpoint"] = autoCheckpoint;
                return r;
            }
            finally { if (uid > 0) Doc.EndUndoRecord(uid); }
        }

        JObject DoUndo(JObject p)
        {
            int c = p["count"]?.ToObject<int>() ?? 1;
            int done = 0;
            for (int i = 0; i < c; i++) { if (!Doc.Undo()) break; done++; }
            RedrawScope.Mark();
            return Ok(("undone", done), ("requested", c));
        }
        JObject DoRedo(JObject p)
        {
            int c = p["count"]?.ToObject<int>() ?? 1;
            int done = 0;
            for (int i = 0; i < c; i++) { if (!Doc.Redo()) break; done++; }
            RedrawScope.Mark();
            return Ok(("redone", done), ("requested", c));
        }


        // ═══ SESSION SCRATCHPAD (Tier 1) ═══════════════════════════════════
        // Persistent key-value store for the current session. Lets the AI cache
        // derived geometry data (face centers, grid points, etc.) across calls
        // instead of re-deriving them every time.
        private static readonly Dictionary<string, JToken> _sessionState = new Dictionary<string, JToken>();
        // v4.8: get_state is served from TCP worker threads (protocol 5 inline reads),
        // so the dictionary needs a gate.
        private static readonly object _sessionStateGate = new object();

        JObject SetState(JObject p)
        {
            string key = p["key"]?.ToString();
            if (string.IsNullOrEmpty(key)) return Err("key is required");
            int total;
            lock (_sessionStateGate)
            {
                _sessionState[key] = p["value"] ?? JValue.CreateNull();
                total = _sessionState.Count;
            }
            return Ok(("key", key), ("stored", true), ("total_keys", total));
        }

        JObject GetState(JObject p)
        {
            string key = p["key"]?.ToString();
            lock (_sessionStateGate)
            {
                if (string.IsNullOrEmpty(key))
                {
                    // Return all keys with types and preview
                    var listing = new JArray();
                    foreach (var kv in _sessionState)
                    {
                        var entry = new JObject { ["key"] = kv.Key, ["type"] = kv.Value?.Type.ToString() ?? "Null" };
                        var s = kv.Value?.ToString(Formatting.None) ?? "null";
                        entry["preview"] = s.Length > 120 ? s.Substring(0, 120) + "..." : s;
                        listing.Add(entry);
                    }
                    return Ok(("keys", listing), ("count", _sessionState.Count));
                }
                if (_sessionState.TryGetValue(key, out var val))
                    return Ok(("key", key), ("value", val.DeepClone()));
            }
            return Err($"Key '{key}' not found. Use get_state() with no key to list all.", "KEY_NOT_FOUND");
        }

        JObject ClearState(JObject p)
        {
            string key = p["key"]?.ToString();
            lock (_sessionStateGate)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    bool removed = _sessionState.Remove(key);
                    return Ok(("key", key), ("removed", removed));
                }
                int count = _sessionState.Count;
                _sessionState.Clear();
                return Ok(("cleared", count));
            }
        }

        // ═══ SET PBR MATERIAL (Tier 1) ═════════════════════════════════════
        // One-call PBR material creation + layer assignment. Replaces ~12 lines
        // of execute_script boilerplate per material.
        JObject SetPbrMaterial(JObject p)
        {
            string layer = p["layer"]?.ToString();
            if (string.IsNullOrEmpty(layer)) return Err("layer is required");

            int li = EnsureLayer(layer);
            var layerObj = Doc.Layers[li];

            // Parse color: accept [r,g,b] array or "#rrggbb" hex string
            var baseColor = System.Drawing.Color.FromArgb(200, 200, 200); // default light gray
            if (p["base_color"] != null)
            {
                if (p["base_color"].Type == JTokenType.Array)
                {
                    var c = p["base_color"].ToObject<int[]>();
                    baseColor = System.Drawing.Color.FromArgb(c.Length > 3 ? c[3] : 255, c[0], c[1], c[2]);
                }
                else
                {
                    string hex = p["base_color"].ToString().TrimStart('#');
                    if (hex.Length == 6)
                        baseColor = System.Drawing.Color.FromArgb(
                            Convert.ToInt32(hex.Substring(0, 2), 16),
                            Convert.ToInt32(hex.Substring(2, 2), 16),
                            Convert.ToInt32(hex.Substring(4, 2), 16));
                }
            }

            double roughness = p["roughness"]?.ToObject<double>() ?? 0.5;
            double metallic  = p["metallic"]?.ToObject<double>() ?? 0.0;
            double opacity   = p["opacity"]?.ToObject<double>() ?? 1.0;
            string name      = p["name"]?.ToString() ?? $"PBR_{layer}";
            double uvRepeat  = p["uv_repeat"]?.ToObject<double>() ?? 1.0;

            roughness = Math.Max(0, Math.Min(1, roughness));
            metallic  = Math.Max(0, Math.Min(1, metallic));
            opacity   = Math.Max(0, Math.Min(1, opacity));

            // Create and configure material
            var mat = new Rhino.DocObjects.Material { Name = name, DiffuseColor = baseColor };
            mat.ReflectionGlossiness = 1.0 - roughness;  // Rhino uses glossiness (inverse of roughness)
            mat.Reflectivity = metallic;
            mat.Transparency = 1.0 - opacity;

            // Apply texture maps if provided
            var mapsApplied = new List<string>();
            var textureMaps = p["texture_maps"] as JObject;
            if (textureMaps != null)
            {
                var slotMap = new (string key, TextureType type)[]
                {
                    ("albedo",       TextureType.Bitmap),
                    ("roughness",    TextureType.PBR_Roughness),
                    ("normal",       TextureType.Bump),
                    ("metallic",     TextureType.PBR_Metallic),
                    ("ao",           TextureType.PBR_AmbientOcclusion),
                    ("displacement", TextureType.PBR_Displacement),
                };
                foreach (var (key, texType) in slotMap)
                {
                    string path = textureMaps[key]?.ToString();
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        var tex = MaterialManager.BuildTexture(path, uvRepeat);
                        mat.SetTexture(tex, texType);
                        mapsApplied.Add(key);
                    }
                }
            }

            int matIdx = Doc.Materials.Add(mat);
            layerObj.RenderMaterialIndex = matIdx;
            Doc.Layers.Modify(layerObj, li, true);

            var result = Ok(
                ("material_name", name),
                ("material_index", matIdx),
                ("layer", layer),
                ("base_color", new JArray(baseColor.R, baseColor.G, baseColor.B)),
                ("roughness", roughness),
                ("metallic", metallic),
                ("opacity", opacity));
            if (mapsApplied.Count > 0)
                result["maps_applied"] = new JArray(mapsApplied.Cast<object>().ToArray());
            return result;
        }

        // ═══ REVOLVE PROFILE (Tier 1) ══════════════════════════════════════
        // Revolves a 2D point profile around an axis. Covers domes, minarets,
        // finials, chhatri domes, lathe-turned elements — the #1 most common
        // execute_script pattern in both smoke tests.
        JObject RevolveProfile(JObject p)
        {
            var ptsRaw = p["points"]?.ToObject<double[][]>();
            if (ptsRaw == null || ptsRaw.Length < 2) return Err("Need at least 2 profile points");
            var axisStart = Pt(p["axis_start"]);
            var axisEnd   = Pt(p["axis_end"]);
            if (axisStart.DistanceTo(axisEnd) < Tol) return Err("Axis start and end are the same point");

            double angleDeg = p["angle_degrees"]?.ToObject<double>() ?? 360.0;
            bool cap        = p["cap"]?.ToObject<bool>() ?? true;
            string layer    = p["layer"]?.ToString();
            int degree      = p["curve_degree"]?.ToObject<int>() ?? 3;

            // Build profile curve
            var pts = ptsRaw.Select(a => new Point3d(a[0], a[1], a.Length > 2 ? a[2] : 0)).ToList();
            NurbsCurve curve;
            if (degree == 1)
                curve = new Polyline(pts).ToNurbsCurve();
            else
                curve = NurbsCurve.Create(false, Math.Min(degree, pts.Count - 1), pts);
            if (curve == null) return Err("Could not create profile curve from points");

            var axis = new Line(axisStart, axisEnd);
            double startRad = 0;
            double endRad = angleDeg * Math.PI / 180.0;

            var revSrf = RevSurface.Create(curve, axis, startRad, endRad);
            if (revSrf == null) return Err("RevSurface.Create failed — check that profile is not on the axis");

            var brep = Brep.CreateFromRevSurface(revSrf, cap, cap);
            if (brep == null) return Err("Could not create brep from revolve surface");

            var attr = new ObjectAttributes();
            if (!string.IsNullOrEmpty(layer)) attr.LayerIndex = EnsureLayer(layer);
            var id = Doc.Objects.AddBrep(brep, attr);
            RedrawScope.Mark();

            var bb = brep.GetBoundingBox(true);
            var r = Ok(
                ("object_ids", new JArray { id.ToString() }),
                ("is_solid", brep.IsSolid),
                ("bbox", BB(bb)));
            return r;
        }

        // ═══ CREATE LAYER TREE (Tier 1) ════════════════════════════════════
        // Batch-creates a hierarchy of layers in one call.
        // Path format: "Parent::Child::Grandchild" (Rhino native convention).
        JObject CreateLayerTree(JObject p)
        {
            var layers = p["layers"] as JArray;
            if (layers == null || layers.Count == 0) return Err("layers array is required");

            var created = new JArray();
            var existed = new JArray();

            foreach (var entry in layers)
            {
                string path = entry["path"]?.ToString() ?? entry.ToString();
                if (string.IsNullOrEmpty(path)) continue;

                var parts = path.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                int parentIdx = -1;

                for (int i = 0; i < parts.Length; i++)
                {
                    string partName = parts[i].Trim();
                    // Check if this layer already exists under the current parent
                    int existingIdx = -1;
                    foreach (var l in Doc.Layers)
                    {
                        if (l.IsDeleted) continue;
                        bool nameMatch = string.Equals(l.Name, partName, StringComparison.OrdinalIgnoreCase);
                        bool parentMatch = parentIdx < 0
                            ? l.ParentLayerId == Guid.Empty
                            : l.ParentLayerId == Doc.Layers[parentIdx].Id;
                        if (nameMatch && parentMatch) { existingIdx = l.Index; break; }
                    }

                    if (existingIdx >= 0)
                    {
                        parentIdx = existingIdx;
                        if (i == parts.Length - 1) existed.Add(path);
                        continue;
                    }

                    var newLayer = new Layer { Name = partName };
                    if (parentIdx >= 0) newLayer.ParentLayerId = Doc.Layers[parentIdx].Id;

                    // Apply color/material only to the leaf layer
                    if (i == parts.Length - 1)
                    {
                        if (entry["color"] != null)
                        {
                            var c = entry["color"].ToObject<int[]>();
                            newLayer.Color = System.Drawing.Color.FromArgb(c[0], c[1], c.Length > 2 ? c[2] : 0);
                        }
                        if (entry["visible"] != null)
                            newLayer.IsVisible = entry["visible"].ToObject<bool>();
                    }

                    parentIdx = Doc.Layers.Add(newLayer);
                    if (parentIdx >= 0 && i == parts.Length - 1) created.Add(path);
                }

                // Apply material to leaf layer if specified
                if (parentIdx >= 0 && entry["material"] != null)
                {
                    var matEntry = entry["material"] as JObject;
                    if (matEntry != null)
                    {
                        var matP = new JObject { ["layer"] = path };
                        foreach (var kv in matEntry) matP[kv.Key] = kv.Value;
                        SetPbrMaterial(matP);
                    }
                }
            }

            return Ok(("created", created), ("existed", existed),
                       ("created_count", created.Count), ("existed_count", existed.Count));
        }

        // ═══ THUMBNAIL (Tier 1) ════════════════════════════════════════════
        // Fast, cheap wireframe capture. Always uses wireframe display mode
        // so it returns in <1s regardless of scene complexity.
        JObject Thumbnail(JObject p)
        {
            int w = p["width"]?.ToObject<int>() ?? 240;
            int h = p["height"]?.ToObject<int>() ?? 180;
            int quality = p["quality"]?.ToObject<int>() ?? 60;
            // Default matches the MCP tool contract: Shaded (wireframe=false).
            bool forceWireframe = p["wireframe"]?.ToObject<bool>() ?? false;

            var view = Doc.Views.ActiveView;
            if (view == null) return Err("No active viewport");
            var vp = view.ActiveViewport;
            if (vp == null) return Err("No active viewport");

            var savedMode = vp.DisplayMode;
            try
            {
                if (forceWireframe)
                {
                    var wf = Rhino.Display.DisplayModeDescription.FindByName("Wireframe");
                    if (wf != null) vp.DisplayMode = wf;
                }

                view.Redraw();
                using var bmp = view.CaptureToBitmap(new Size(w, h));

                if (bmp == null) return Err("Capture returned null");

                var enc = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.MimeType == "image/jpeg");
                if (enc == null) return Err("JPEG encoder unavailable");

                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));

                using var ms = new MemoryStream();
                bmp.Save(ms, enc, ep);
                var bytes = ms.ToArray();

                var r = Ok(
                    ("image_base64", Convert.ToBase64String(bytes)),
                    ("format", "jpeg"),
                    ("width", w),
                    ("height", h),
                    ("bytes", bytes.Length));

                // Add camera context
                r["camera"] = new JObject
                {
                    ["location"] = PA(vp.CameraLocation),
                    ["target"]   = PA(vp.CameraTarget),
                    ["projection"] = vp.IsParallelProjection ? "parallel" : "perspective"
                };
                r["scene"] = new JObject
                {
                    ["visible_objects"] = Doc.Objects.Count(o => !o.IsDeleted && o.Visible)
                };

                return r;
            }
            catch (Exception e) { return Err($"Thumbnail failed: {e.Message}"); }
            finally
            {
                if (forceWireframe) vp.DisplayMode = savedMode;
            }
        }

        // ═══ EXPORT OBJECTS (Tier 2) ════════════════════════════════════════
        // Programmatic export without dialogs. Supports STL, OBJ, 3DM, STEP.
        JObject ExportObjects(JObject p)
        {
            string format = (p["format"]?.ToString() ?? "stl").ToLower();
            string path = p["path"]?.ToString();
            var ids = p["object_ids"]?.ToObject<string[]>();
            bool allObjects = ids == null || ids.Length == 0;

            // Build export path
            if (string.IsNullOrEmpty(path))
            {
                string ext = format switch { "obj" => ".obj", "step" => ".stp", "iges" => ".igs", "3dm" => ".3dm", _ => ".stl" };
                path = Path.Combine(Path.GetTempPath(), $"aibridge_export_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            }
            else
            {
                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                {
                    string safeName = SanitizeFileName(path);
                    if (safeName == null) return Err("Invalid export file name.", "INVALID_NAME");
                    path = Path.Combine(Path.GetTempPath(), safeName);
                }
                else
                {
                    try { path = Path.GetFullPath(path); }
                    catch { return Err("Invalid export path.", "INVALID_PATH"); }
                    string parent = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                        return Err("Export directory does not exist.", "INVALID_PATH");
                    if (string.IsNullOrEmpty(Path.GetFileName(path)))
                        return Err("Export path must include a file name.", "INVALID_PATH");
                }
            }

            // -_Export operates on the current selection. For "all objects" the old code
            // selected NOTHING, so the scripted command stalled at the object prompt and
            // the export silently produced no file. Select explicitly in both paths.
            int selectedCount;
            Doc.Objects.UnselectAll();
            if (allObjects)
            {
                var allGuids = Doc.Objects
                    .Where(o => !o.IsDeleted && o.Visible)
                    .Select(o => o.Id).ToList();
                if (allGuids.Count == 0) return Err("Nothing to export: no visible objects.", "NOTHING_TO_EXPORT");
                selectedCount = Doc.Objects.Select(allGuids, true);
            }
            else
            {
                var guids = ids.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                               .Where(guid => guid != Guid.Empty).ToList();
                if (guids.Count == 0) return Err("No valid GUIDs in object_ids.", "INVALID_REQUEST");
                selectedCount = Doc.Objects.Select(guids, true);
                if (selectedCount == 0) return Err("None of the requested objects could be selected.", "OBJECT_NOT_FOUND");
            }

            // v4.8: native FileIO path for 3dm - no command-line scripting fragility,
            // real error reporting, no reliance on Rhino's interactive Export pipeline.
            if (format == "3dm")
            {
                var opts3dm = new Rhino.FileIO.FileWriteOptions
                {
                    WriteSelectedObjectsOnly = true,
                    IncludeRenderMeshes = false,
                };
                bool ok3dm = Doc.WriteFile(path, opts3dm);
                Doc.Objects.UnselectAll();
                if (!ok3dm || !File.Exists(path))
                    return Err($"3dm export failed - Rhino did not write '{path}'.", "EXPORT_FAILED",
                               new JObject { ["path"] = path });
                return Ok(("exported", true), ("path", path), ("format", "3dm"),
                          ("object_count", selectedCount), ("via", "FileIO"));
            }

            string cmd = $"-_Export \"{path}\" _Enter _Enter";
            bool ok = RhinoApp.RunScript(cmd, false);
            Doc.Objects.UnselectAll();
            bool fileWritten = ok && File.Exists(path);

            if (!fileWritten)
                return Err($"Export failed — Rhino did not write '{path}'. Check the format/extension and that the directory is writable.",
                           "EXPORT_FAILED", new JObject { ["path"] = path, ["format"] = format, ["command_ok"] = ok });

            return Ok(
                ("exported", true),
                ("path", path),
                ("format", format),
                ("object_count", selectedCount));
        }

        // ═══ DESIGN CHECKPOINTS (Tier 2) ═══════════════════════════════════
        // Named save states for design exploration. The AI can branch, experiment,
        // and roll back without losing work.
        private static readonly Dictionary<string, string> _checkpoints = new Dictionary<string, string>();
        private static bool _checkpointsLoaded;
        private const int MAX_AUTO_CHECKPOINTS = 10;

        static string CheckpointDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "aibridge_checkpoints");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string CheckpointRegistryPath() => Path.Combine(CheckpointDir(), "registry.json");

        // v4.8: the registry persists to a JSON sidecar, so checkpoints saved before a
        // Rhino restart (or crash) remain restorable by name in the next session.
        static void LoadCheckpointRegistry()
        {
            if (_checkpointsLoaded) return;
            _checkpointsLoaded = true;
            try
            {
                var path = CheckpointRegistryPath();
                if (!File.Exists(path)) return;
                var data = JObject.Parse(File.ReadAllText(path));
                foreach (var prop in data.Properties())
                {
                    var fp = prop.Value?.ToString();
                    if (!string.IsNullOrEmpty(fp) && File.Exists(fp) && !_checkpoints.ContainsKey(prop.Name))
                        _checkpoints[prop.Name] = fp;
                }
            }
            catch { }
        }

        static void PersistCheckpointRegistry()
        {
            try
            {
                var data = new JObject();
                foreach (var kv in _checkpoints) data[kv.Key] = kv.Value;
                File.WriteAllText(CheckpointRegistryPath(), data.ToString(Formatting.None));
            }
            catch { }
        }

        // Auto-checkpoints accumulate fast (one per risky op). Keep only the newest few.
        static void PruneAutoCheckpoints()
        {
            try
            {
                var stale = _checkpoints
                    .Where(kv => kv.Key.StartsWith("auto_", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => new { kv.Key, kv.Value, Time = File.Exists(kv.Value) ? File.GetLastWriteTimeUtc(kv.Value) : DateTime.MinValue })
                    .OrderByDescending(x => x.Time)
                    .Skip(MAX_AUTO_CHECKPOINTS)
                    .ToList();
                foreach (var a in stale)
                {
                    _checkpoints.Remove(a.Key);
                    try { if (File.Exists(a.Value)) File.Delete(a.Value); } catch { }
                }
            }
            catch { }
        }

        JObject SaveCheckpoint(JObject p)
        {
            string rawName = p["name"]?.ToString();
            if (string.IsNullOrEmpty(rawName)) return Err("name is required");
            string name = SanitizeFileName(rawName);
            if (name == null) return Err("Invalid checkpoint name.", "INVALID_NAME");

            LoadCheckpointRegistry();
            string dir = CheckpointDir();
            string filePath = Path.Combine(dir, $"{name}.3dm");

            // Clean old checkpoint with same name
            if (File.Exists(filePath))
                File.Delete(filePath);

            var opts = new Rhino.FileIO.FileWriteOptions
            {
                WriteGeometryOnly = false,
                IncludeRenderMeshes = false,  // smaller file, faster save
            };
            bool ok = Doc.WriteFile(filePath, opts);
            if (!ok) return Err("Failed to save checkpoint file");

            _checkpoints[name] = filePath;
            PruneAutoCheckpoints();
            PersistCheckpointRegistry();
            var fi = new FileInfo(filePath);
            // An explicit checkpoint is also a fresh rollback point - reset the
            // auto-checkpoint economics so the next mutating call doesn't re-write it.
            NoteCheckpointTaken(CurrentSceneVersion(), fi.Length / 1024);

            return Ok(
                ("checkpoint", name),
                ("path", filePath),
                ("size_kb", (int)(fi.Length / 1024)),
                ("object_count", Doc.Objects.Count(o => !o.IsDeleted)),
                ("total_checkpoints", _checkpoints.Count));
        }

        JObject RestoreCheckpoint(JObject p)
        {
            string rawName = p["name"]?.ToString();
            if (string.IsNullOrEmpty(rawName)) return Err("name is required");
            string name = SanitizeFileName(rawName) ?? rawName;

            LoadCheckpointRegistry();
            if (!_checkpoints.TryGetValue(name, out var filePath) || !File.Exists(filePath))
                return Err($"Checkpoint '{name}' not found");

            int beforeCount = Doc.Objects.Count(o => !o.IsDeleted);

            // IMPORT FIRST, DELETE AFTER. The old order (wipe doc, then import) left the
            // user with an EMPTY document if the import failed. Imported objects get fresh
            // GUIDs, so we can snapshot the pre-import ids, import, verify, then remove
            // only the old objects.
            var oldIds = Doc.Objects.Where(o => !o.IsDeleted).Select(o => o.Id).ToHashSet();

            // v4.8: native File3dm read - real error reporting, no scripted -_Import.
            int importedCount = 0;
            try
            {
                using var f3dm = Rhino.FileIO.File3dm.Read(filePath);
                if (f3dm == null)
                    return Err("Checkpoint file could not be read - current geometry left untouched.",
                               "RESTORE_FAILED", new JObject { ["checkpoint"] = name, ["path"] = filePath });
                var layerMap = new Dictionary<int, int>();
                foreach (var fl in f3dm.AllLayers)
                    layerMap[fl.Index] = EnsureLayer(fl.Name);
                foreach (var fo in f3dm.Objects)
                {
                    var geo = fo.Geometry;
                    if (geo == null) continue;
                    var attr = fo.Attributes?.Duplicate() ?? new ObjectAttributes();
                    attr.LayerIndex = layerMap.TryGetValue(attr.LayerIndex, out var li2) ? li2 : 0;
                    var nid = Doc.Objects.Add(geo, attr);
                    if (nid != Guid.Empty) importedCount++;
                }
            }
            catch (Exception e)
            {
                return Err($"Checkpoint import failed ({e.Message}) - current geometry left untouched.",
                           "RESTORE_FAILED", new JObject { ["checkpoint"] = name, ["path"] = filePath });
            }
            if (importedCount == 0)
            {
                return Err("Checkpoint contained no importable geometry - current geometry left untouched.",
                           "RESTORE_FAILED", new JObject { ["checkpoint"] = name, ["path"] = filePath });
            }

            foreach (var id in oldIds) Doc.Objects.Delete(id, true);

            RedrawScope.Mark();
            int afterCount = Doc.Objects.Count(o => !o.IsDeleted);

            return Ok(
                ("restored", name),
                ("objects_before", beforeCount),
                ("objects_after", afterCount));
        }

        JObject ListCheckpoints(JObject p)
        {
            LoadCheckpointRegistry();
            var list = new JArray();
            foreach (var kv in _checkpoints)
            {
                var entry = new JObject { ["name"] = kv.Key, ["path"] = kv.Value };
                if (File.Exists(kv.Value))
                {
                    var fi = new FileInfo(kv.Value);
                    entry["size_kb"] = (int)(fi.Length / 1024);
                    entry["saved_at"] = fi.LastWriteTime.ToString("HH:mm:ss");
                    entry["exists"] = true;
                }
                else
                {
                    entry["exists"] = false;
                }
                list.Add(entry);
            }
            return Ok(("checkpoints", list), ("count", list.Count));
        }

        JObject DeleteCheckpoint(JObject p)
        {
            string rawName = p["name"]?.ToString();
            if (string.IsNullOrEmpty(rawName)) return Err("name is required");
            string name = SanitizeFileName(rawName) ?? rawName;
            LoadCheckpointRegistry();
            if (!_checkpoints.TryGetValue(name, out var filePath))
                return Err($"Checkpoint '{name}' not found", "OBJECT_NOT_FOUND");
            _checkpoints.Remove(name);
            bool fileDeleted = false;
            try { if (File.Exists(filePath)) { File.Delete(filePath); fileDeleted = true; } } catch { }
            PersistCheckpointRegistry();
            return Ok(("deleted", name), ("file_deleted", fileDeleted), ("remaining", _checkpoints.Count));
        }

        JObject GetRecoveryLog(JObject p)
        {
            int limit = Math.Min(Math.Max(1, p["limit"]?.ToObject<int>() ?? 50), 500);
            return Ok(("entries", WriteAheadLog.GetRecent(limit)),
                      ("note", "Write-ahead log of mutating commands (begin/end pairs). After a Rhino crash, compare the tail against query_scene to recover working context."));
        }


        // --- LOGGING --------------------------------------------------
        JObject GetLog(JObject p)
        {
            int c = p["count"]?.ToObject<int>() ?? 50;
            bool eo = p["errors_only"]?.ToObject<bool>() ?? false;
            var entries = AIBridgeLogger.GetRecentEntries(c, eo ? LogLevel.ERROR : null);
            var arr = new JArray();
            foreach (var e in entries)
                arr.Add(new JObject
                {
                    ["time"] = e.Timestamp.ToString("HH:mm:ss"),
                    ["level"] = e.Level.ToString(),
                    ["category"] = e.Category,
                    ["cmd"] = e.CommandType,
                    ["ms"] = e.ElapsedMs,
                    ["message"] = e.Message,
                    ["error"] = e.Error,
                });
            return Ok(("entries", arr), ("count", (int)arr.Count));
        }

        JObject GetLogStats(JObject p)
        {
            var stats = AIBridgeLogger.GetStats();
            var j = new JObject { ["status"] = "ok" };
            foreach (var kv in stats) j[kv.Key] = JToken.FromObject(kv.Value);
            return j;
        }

        // ===================================================================
        // DESIGN MEMORY
        // ===================================================================

        JObject SetDesignBrief(JObject p)
        {
            var brief = p["brief"]?.ToString() ?? "";
            bool append = p["append"]?.ToObject<bool>() ?? false;
            bool clear = p["clear"]?.ToObject<bool>() ?? false;
            if (!clear && string.IsNullOrWhiteSpace(brief)) return Err("brief required");
            DesignMemory.SetBrief(brief, append: append, allowEmpty: clear);
            return new JObject {
                ["status"] = "ok",
                ["brief"] = DesignMemory.GetBrief(),
                ["mode"] = clear ? "clear" : (append ? "append" : "replace")
            };
        }

        JObject GetDesignBrief(JObject p) =>
            new JObject { ["status"] = "ok", ["brief"] = DesignMemory.GetBrief(), ["rules"] = DesignMemory.GetRules() };

        JObject TagObjectCmd(JObject p)
        {
            var ids  = p["ids"] as JArray ?? (p["id"] != null ? new JArray(p["id"]) : new JArray());
            var tags = p["tags"] as JObject ?? new JObject();
            if (ids.Count == 0) return Err("ids required");
            int tagged = 0;
            foreach (var idTok in ids)
            {
                if (!Guid.TryParse(idTok.ToString(), out var g)) continue;
                var obj = Doc?.Objects.FindId(g);
                if (obj == null) continue;
                var dict = tags.Properties().ToDictionary(x => x.Name, x => x.Value.ToString());
                DesignMemory.TagObject(obj, dict);
                tagged++;
            }
            return new JObject { ["status"] = "ok", ["tagged"] = tagged };
        }

        JObject GetProvenance(JObject p)
        {
            var id = p["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) return Err("id required");
            if (!Guid.TryParse(id, out var g)) return Err("invalid GUID");
            var obj = Doc?.Objects.FindId(g);
            if (obj == null) return Err($"Object {id} not found", "OBJECT_NOT_FOUND");
            return new JObject { ["status"] = "ok", ["id"] = id,
                ["type"] = obj.ObjectType.ToString(),
                ["layer"] = Doc.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "",
                ["provenance"] = DesignMemory.GetObjectTags(obj) };
        }

        JObject SearchMemory(JObject p)
        {
            var query = p["query"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(query)) return Err("query required");
            return new JObject { ["status"] = "ok", ["query"] = query,
                ["results"] = DesignMemory.SearchMemory(query, Doc) };
        }

        JObject GetRelatedObjects(JObject p)
        {
            var id = p["id"]?.ToString();
            if (string.IsNullOrEmpty(id) || !Guid.TryParse(id, out var g)) return Err("id required (GUID)");
            var obj = Doc?.Objects.FindId(g);
            if (obj == null) return Err($"Object {id} not found", "OBJECT_NOT_FOUND");
            return new JObject { ["status"] = "ok", ["id"] = id,
                ["related"] = DesignMemory.GetRelatedObjects(obj, p["relation"]?.ToString() ?? "", Doc) };
        }

        JObject NameGroupCmd(JObject p)
        {
            var name = p["name"]?.ToString() ?? p["group"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return Err("name required");
            var ids = (p["ids"] as JArray)?.Select(x => x.ToString()) ?? Array.Empty<string>();
            DesignMemory.NameGroup(name, ids);
            return new JObject { ["status"] = "ok", ["group"] = name };
        }

        JObject GetGroupCmd(JObject p)
        {
            var name = p["name"]?.ToString() ?? p["group"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) return Err("name required");
            return new JObject { ["status"] = "ok", ["group"] = name, ["ids"] = DesignMemory.GetGroup(name) };
        }

        JObject GetAllGroupsCmd(JObject p) =>
            new JObject { ["status"] = "ok", ["groups"] = DesignMemory.GetAllGroups() };

        JObject AddDesignRule(JObject p)
        {
            var rule = p["rule"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(rule)) return Err("rule required");
            DesignMemory.AddRule(rule);
            return new JObject { ["status"] = "ok", ["rule"] = rule };
        }

        JObject GetDesignRules(JObject p) =>
            new JObject { ["status"] = "ok", ["rules"] = DesignMemory.GetRules() };

        JObject LogSessionCmd(JObject p)
        {
            var summary = p["summary"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(summary)) return Err("summary required");
            DesignMemory.AddSession(summary);
            return new JObject { ["status"] = "ok" };
        }

        // ===================================================================
        // INCREMENTAL SCENE SYNC
        // ===================================================================

        JObject GetSceneDiff(JObject p)
        {
            int fromVersion = p["from_version"]?.ToObject<int>() ?? 0;
            var (added, deleted, modified, toVersion, truncated) = ChangeTracker.GetDiff(fromVersion);
            var r = new JObject { ["status"] = "ok",
                ["from_version"] = fromVersion, ["to_version"] = toVersion,
                ["added"] = added, ["deleted"] = deleted, ["modified"] = modified,
                ["has_changes"] = added.Count + deleted.Count + modified.Count > 0 };
            if (truncated)
            {
                r["log_truncated"] = true;
                r["warning"] = "Change log was truncated since from_version — this diff is INCOMPLETE. "
                             + "Use query_scene(scope='summary') for an authoritative state.";
            }
            return r;
        }

        JObject GetChangeLogCmd(JObject p)
        {
            int limit = Math.Min(p["limit"]?.ToObject<int>() ?? 50, 200);
            int since = p["since_version"]?.ToObject<int>() ?? 0;
            return new JObject { ["status"] = "ok",
                ["current_version"] = ChangeTracker.CurrentVersion,
                ["events"] = ChangeTracker.GetLog(limit, since) };
        }

        JObject GetTrackerVersion(JObject p) =>
            new JObject { ["status"] = "ok", ["version"] = ChangeTracker.CurrentVersion };

        // ===================================================================
        // SEMANTIC SCENE INTELLIGENCE
        // ===================================================================

        JObject AnalyzeArchitectureCmd(JObject p) => SemanticClassifier.AnalyzeArchitecture(Doc);

        JObject GetBuildingSystemsCmd(JObject p) =>
            SemanticClassifier.GetBuildingSystems(Doc, p["system"]?.ToString() ?? "all");

        JObject GetLevelSummaryCmd(JObject p)
        {
            int? level = p["level"] != null ? (int?)p["level"].ToObject<int>() : null;
            return SemanticClassifier.GetLevelSummary(Doc, level);
        }

        JObject DetectDesignPatternsCmd(JObject p) => SemanticClassifier.DetectDesignPatterns(Doc);

        JObject FindUnassignedCmd(JObject p)
        {
            double minVol = p["min_volume"]?.ToObject<double>() ?? 0;
            return SemanticClassifier.FindUnassigned(Doc, minVol);
        }

        // ===================================================================
        // SMART BATCHING -- PREVIEW
        // ===================================================================

        JObject BatchPreviewCmd(JObject p)
        {
            var commands = p["commands"] as JArray;
            if (commands == null || commands.Count == 0) return Err("commands array required");
            return BatchPlanner.Preview(commands, _commands);
        }

        // ── v4.7 Sections, Elevations, Plans ────────────────────────────
        JObject CreateSectionCmd(JObject p) => SectionManager.CreateSection(p, Doc);
        JObject CreateElevationCmd(JObject p) => SectionManager.CreateElevation(p, Doc);
        JObject CutSectionCmd(JObject p) => SectionManager.CutSection(p, Doc);
        JObject AlignViewToSectionCmd(JObject p) => SectionManager.AlignViewToSection(p, Doc);
        JObject CreatePlanCmd(JObject p) => SectionManager.CreatePlan(p, Doc);
        JObject CreateAllPlansCmd(JObject p) => SectionManager.CreateAllPlans(p, Doc);
        JObject ListSectionsCmd(JObject p) => SectionManager.ListSections(p, Doc);
        JObject UpdateSectionCmd(JObject p) => SectionManager.UpdateSection(p, Doc);
        JObject RemoveSectionCmd(JObject p) => SectionManager.RemoveSection(p, Doc);

        // ── v4.7 Illustration & Display Modes ───────────────────────────
        JObject CreateDisplayModeCmd(JObject p) => DisplayModeManager.CreateDisplayMode(p, Doc);
        JObject ApplyDisplayModeCmd(JObject p) => DisplayModeManager.ApplyDisplayMode(p, Doc);
        JObject ListDisplayModesCmd(JObject p) => DisplayModeManager.ListDisplayModes(p, Doc);
        JObject AdjustDisplayModeCmd(JObject p) => DisplayModeManager.AdjustDisplayMode(p, Doc);
        JObject DeleteDisplayModeCmd(JObject p) => DisplayModeManager.DeleteDisplayMode(p, Doc);
        JObject CaptureIllustrationCmd(JObject p) => DisplayModeManager.CaptureIllustration(p, Doc);

        // ── v4.7 Material Intelligence ───────────────────────────────────
        JObject ApplyDownloadedMaterialCmd(JObject p) => MaterialManager.ApplyDownloadedMaterial(p, Doc);
        JObject EditMaterialCmd(JObject p) => MaterialManager.EditMaterial(p, Doc);
        JObject ListMaterialsCmd(JObject p) => MaterialManager.ListMaterials(p, Doc);
        JObject GetMaterialCmd(JObject p) => MaterialManager.GetMaterial(p, Doc);

        // ── v4.7 File Tracing ────────────────────────────────────────────
        JObject ApplyTracedElementsCmd(JObject p) => TracingManager.ApplyTracedElements(p, Doc);
        JObject GetTraceLayersCmd(JObject p) => TracingManager.GetTraceLayers(p, Doc);
        JObject ClearTraceLayersCmd(JObject p) => TracingManager.ClearTraceLayers(p, Doc);

        JObject ImportDwgCmd(JObject p)
        {
            // Import DWG/DXF natively via Rhino command, then post-process
            var filePath = p["file_path"]?.ToString();
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return Err("file_path required and must exist");
            var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".dwg" && ext != ".dxf")
                return Err("Only .dwg and .dxf files supported by import_dwg");
            // Count objects before import
            int before = Doc.Objects.Count;
            // Run Rhino import command
            var script = $"_-Import \"{filePath}\" _Enter";
            RhinoApp.RunScript(script, false);
            int after = Doc.Objects.Count;
            int imported = after - before;
            Doc.Views.Redraw();
            return new JObject
            {
                ["status"] = "ok",
                ["file"] = System.IO.Path.GetFileName(filePath),
                ["objects_imported"] = imported,
                ["message"] = $"Imported {imported} objects from {System.IO.Path.GetFileName(filePath)}. Use query_scene to inspect the result."
            };
        }

        JObject CalibrateScaleCmd(JObject p)
        {
            // User provides two points and the known real-world distance between them
            // The tool scales all geometry to match
            var pt1 = p["point1"] as JObject;
            var pt2 = p["point2"] as JObject;
            double knownDistance = p["known_distance"]?.ToObject<double>() ?? 0;
            string unit = p["unit"]?.ToString() ?? "mm";
            if (pt1 == null || pt2 == null || knownDistance <= 0)
                return Err("point1, point2 (x/y/z) and known_distance required");
            var p1 = new Rhino.Geometry.Point3d(pt1["x"]?.ToObject<double>() ?? 0, pt1["y"]?.ToObject<double>() ?? 0, pt1["z"]?.ToObject<double>() ?? 0);
            var p2 = new Rhino.Geometry.Point3d(pt2["x"]?.ToObject<double>() ?? 0, pt2["y"]?.ToObject<double>() ?? 0, pt2["z"]?.ToObject<double>() ?? 0);
            double measuredDistance = p1.DistanceTo(p2);
            if (measuredDistance < 1e-10) return Err("Points are too close together");
            // Convert known distance to model units
            double knownInModelUnits = knownDistance;
            if (unit == "mm") knownInModelUnits = RhinoMath.UnitScale(UnitSystem.Millimeters, Doc.ModelUnitSystem) * knownDistance;
            else if (unit == "m") knownInModelUnits = RhinoMath.UnitScale(UnitSystem.Meters, Doc.ModelUnitSystem) * knownDistance;
            else if (unit == "cm") knownInModelUnits = RhinoMath.UnitScale(UnitSystem.Centimeters, Doc.ModelUnitSystem) * knownDistance;
            else if (unit == "ft") knownInModelUnits = RhinoMath.UnitScale(UnitSystem.Feet, Doc.ModelUnitSystem) * knownDistance;
            else if (unit == "in") knownInModelUnits = RhinoMath.UnitScale(UnitSystem.Inches, Doc.ModelUnitSystem) * knownDistance;
            double scaleFactor = knownInModelUnits / measuredDistance;
            var xform = Rhino.Geometry.Transform.Scale(Rhino.Geometry.Point3d.Origin, scaleFactor);
            int scaled = 0;
            var settings = new Rhino.DocObjects.ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                DeletedObjects = false,
            };
            // Materialize the list BEFORE transforming: Transform(..., true) replaces each
            // object (new GUID), and mutating the table while enumerating it live risked
            // re-visiting replacement objects and double-scaling them.
            var toScale = Doc.Objects.GetObjectList(settings)
                .Where(o => o.ObjectType != Rhino.DocObjects.ObjectType.ClipPlane
                         && o.ObjectType != Rhino.DocObjects.ObjectType.Light)
                .Select(o => o.Id)
                .ToList();
            foreach (var id in toScale)
            {
                if (Doc.Objects.Transform(id, xform, true) != Guid.Empty) scaled++;
            }
            Doc.Views.Redraw();
            return new JObject
            {
                ["status"] = "ok",
                ["scale_factor"] = Math.Round(scaleFactor, 6),
                ["measured_distance"] = Math.Round(measuredDistance, 4),
                ["known_distance"] = knownDistance,
                ["unit"] = unit,
                ["objects_scaled"] = scaled
            };
        }
    }
}
