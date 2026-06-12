using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Display;
using Newtonsoft.Json.Linq;

namespace RhinoAIBridge
{
    public static class SectionManager
    {
        // Auto-incrementing section label counter
        private static int _sectionCounter = 0;

        // ─────────────────────────────────────────────────────────────
        // PUBLIC METHODS
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a section line with arrowheads and labels on a dedicated layer.
        /// </summary>
        public static JObject CreateSection(JObject p, RhinoDoc doc)
        {
            try
            {
                string label = p["label"]?.ToString() ?? GetSectionLabel();
                string viewSide = p["view_side"]?.ToString() ?? "left";

                BoundingBox bbox = GetModelBBox(doc);
                if (!bbox.IsValid)
                    return new JObject { ["status"] = "error", ["message"] = "No visible geometry found in document." };

                Point3d bboxCenter = bbox.Center;
                double bboxDiag = bbox.Diagonal.Length;

                // Parse start/end points
                Point3d start, end;
                if (p["start_point"] != null)
                    start = ParsePoint(p["start_point"]);
                else
                    start = new Point3d(bbox.Min.X, bboxCenter.Y, bbox.Min.Z);

                if (p["end_point"] != null)
                    end = ParsePoint(p["end_point"]);
                else
                    end = new Point3d(bbox.Max.X, bboxCenter.Y, bbox.Min.Z);

                // Create layer
                string layerName = $"Section-{label}";
                int layerIdx = EnsureLayer(doc, layerName, Color.Red);

                // Line direction and perpendicular view direction
                Vector3d lineDir = end - start;
                lineDir.Unitize();

                // View direction: left or right perpendicular in XY plane
                Vector3d viewDir;
                if (viewSide.ToLower() == "right")
                    viewDir = new Vector3d(lineDir.Y, -lineDir.X, 0);
                else
                    viewDir = new Vector3d(-lineDir.Y, lineDir.X, 0);
                viewDir.Unitize();

                // Draw section line
                var line = new Line(start, end);
                var lineAttr = new ObjectAttributes { LayerIndex = layerIdx };
                lineAttr.ColorSource = ObjectColorSource.ColorFromLayer;
                doc.Objects.AddLine(line, lineAttr);

                // Arrowhead and tick size
                double arrowSize = bboxDiag * 0.02;
                double textHeight = bboxDiag * 0.015;
                double tickLen = arrowSize * 1.5;

                // Arrowheads at both ends (pointing in view direction)
                AddArrowhead(doc, start, viewDir, arrowSize, layerIdx);
                AddArrowhead(doc, end, viewDir, arrowSize, layerIdx);

                // Tick lines at both ends
                var tick1Start = start;
                var tick1End = start + viewDir * tickLen;
                var tick2Start = end;
                var tick2End = end + viewDir * tickLen;
                doc.Objects.AddLine(new Line(tick1Start, tick1End), lineAttr);
                doc.Objects.AddLine(new Line(tick2Start, tick2End), lineAttr);

                // Labels at both ends
                AddSectionLabel(doc, start + viewDir * (tickLen + textHeight * 0.5), label, textHeight, layerIdx);
                AddSectionLabel(doc, end + viewDir * (tickLen + textHeight * 0.5), label, textHeight, layerIdx);

                // Store view direction in layer user dictionary
                var layer = doc.Layers[layerIdx];
                layer.UserDictionary.Set("view_dir_x", viewDir.X);
                layer.UserDictionary.Set("view_dir_y", viewDir.Y);
                layer.UserDictionary.Set("view_dir_z", viewDir.Z);
                layer.UserDictionary.Set("start_x", start.X);
                layer.UserDictionary.Set("start_y", start.Y);
                layer.UserDictionary.Set("start_z", start.Z);
                layer.UserDictionary.Set("end_x", end.X);
                layer.UserDictionary.Set("end_y", end.Y);
                layer.UserDictionary.Set("end_z", end.Z);
                doc.Layers.Modify(layer, layerIdx, false);

                doc.Views.Redraw();

                return new JObject
                {
                    ["status"] = "ok",
                    ["label"] = label,
                    ["layer"] = layerName,
                    ["start_point"] = PointToJObject(start),
                    ["end_point"] = PointToJObject(end),
                    ["view_direction"] = VectorToJObject(viewDir),
                    ["message"] = $"Section {label} created. Reposition the line as needed, then say \"cut section {label}\" to generate the clipping plane and aligned view."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Creates an elevation marker for a given building face direction.
        /// </summary>
        public static JObject CreateElevation(JObject p, RhinoDoc doc)
        {
            try
            {
                string label = p["label"]?.ToString() ?? GetSectionLabel();
                string directionStr = p["direction"]?.ToString() ?? "north";

                BoundingBox bbox = GetModelBBox(doc);
                if (!bbox.IsValid)
                    return new JObject { ["status"] = "error", ["message"] = "No visible geometry found in document." };

                double bboxDiag = bbox.Diagonal.Length;
                double defaultOffset = bboxDiag * 0.10;
                double offset = p["offset"] != null ? (double)p["offset"] : defaultOffset;

                // Resolve direction vector and building face point
                Vector3d viewDir;
                Point3d facePoint;

                switch (directionStr.ToLower())
                {
                    case "north":
                        viewDir = new Vector3d(0, -1, 0); // looking south toward building
                        facePoint = new Point3d(bbox.Center.X, bbox.Max.Y + offset, bbox.Center.Z);
                        break;
                    case "south":
                        viewDir = new Vector3d(0, 1, 0);
                        facePoint = new Point3d(bbox.Center.X, bbox.Min.Y - offset, bbox.Center.Z);
                        break;
                    case "east":
                        viewDir = new Vector3d(-1, 0, 0);
                        facePoint = new Point3d(bbox.Max.X + offset, bbox.Center.Y, bbox.Center.Z);
                        break;
                    case "west":
                        viewDir = new Vector3d(1, 0, 0);
                        facePoint = new Point3d(bbox.Min.X - offset, bbox.Center.Y, bbox.Center.Z);
                        break;
                    default:
                        // Try parse as custom vector
                        if (p["direction"] is JObject dv)
                        {
                            viewDir = new Vector3d(
                                dv["x"] != null ? (double)dv["x"] : 0,
                                dv["y"] != null ? (double)dv["y"] : 0,
                                dv["z"] != null ? (double)dv["z"] : 0);
                            viewDir.Unitize();
                            facePoint = bbox.Center + (-viewDir) * (bboxDiag * 0.5 + offset);
                        }
                        else
                        {
                            viewDir = new Vector3d(0, -1, 0);
                            facePoint = new Point3d(bbox.Center.X, bbox.Max.Y + offset, bbox.Center.Z);
                        }
                        break;
                }
                viewDir.Unitize();

                string layerName = $"Elevation-{label}";
                int layerIdx = EnsureLayer(doc, layerName, Color.Blue);

                double arrowSize = bboxDiag * 0.02;
                double textHeight = bboxDiag * 0.015;
                double markerLen = arrowSize * 4;

                // Elevation marker line: horizontal line at the face point, perpendicular to view direction
                Vector3d perp = Vector3d.CrossProduct(viewDir, Vector3d.ZAxis);
                perp.Unitize();
                Point3d markerStart = facePoint - perp * markerLen * 0.5;
                Point3d markerEnd = facePoint + perp * markerLen * 0.5;

                var lineAttr = new ObjectAttributes { LayerIndex = layerIdx };
                lineAttr.ColorSource = ObjectColorSource.ColorFromLayer;
                doc.Objects.AddLine(new Line(markerStart, markerEnd), lineAttr);

                // Arrow pointing toward the building (in -viewDir direction)
                Vector3d towardBuilding = -viewDir;
                towardBuilding.Unitize();
                AddArrowhead(doc, facePoint, towardBuilding, arrowSize, layerIdx);

                // Label
                AddSectionLabel(doc, facePoint + viewDir * (arrowSize + textHeight), label, textHeight, layerIdx);

                // Store metadata in layer
                var layer = doc.Layers[layerIdx];
                layer.UserDictionary.Set("view_dir_x", viewDir.X);
                layer.UserDictionary.Set("view_dir_y", viewDir.Y);
                layer.UserDictionary.Set("view_dir_z", viewDir.Z);
                layer.UserDictionary.Set("marker_x", facePoint.X);
                layer.UserDictionary.Set("marker_y", facePoint.Y);
                layer.UserDictionary.Set("marker_z", facePoint.Z);
                doc.Layers.Modify(layer, layerIdx, false);

                doc.Views.Redraw();

                return new JObject
                {
                    ["status"] = "ok",
                    ["label"] = label,
                    ["layer"] = layerName,
                    ["direction"] = directionStr,
                    ["direction_vector"] = VectorToJObject(viewDir),
                    ["marker_position"] = PointToJObject(facePoint),
                    ["message"] = $"Elevation {label} marker created facing {directionStr}. Say \"cut elevation {label}\" to generate the aligned view."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Cuts a section by creating a clipping plane and aligning the view.
        /// </summary>
        public static JObject CutSection(JObject p, RhinoDoc doc)
        {
            try
            {
                string label = p["label"]?.ToString();
                if (string.IsNullOrEmpty(label))
                    return new JObject { ["status"] = "error", ["message"] = "label parameter is required." };

                bool capture = p["capture"] == null || (bool)p["capture"];
                bool restoreView = p["restore_view"]?.ToObject<bool>() ?? capture;

                string layerName = $"Section-{label}";
                int layerIdx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
                if (layerIdx < 0)
                    return new JObject { ["status"] = "error", ["message"] = $"Layer '{layerName}' not found. Create section first." };

                var layer = doc.Layers[layerIdx];

                // Retrieve view direction from layer user dictionary
                Vector3d viewDir;
                if (layer.UserDictionary.ContainsKey("view_dir_x"))
                {
                    viewDir = new Vector3d(
                        layer.UserDictionary.TryGetDouble("view_dir_x", out double vdx) ? vdx : 0.0,
                        layer.UserDictionary.TryGetDouble("view_dir_y", out double vdy) ? vdy : 0.0,
                        layer.UserDictionary.TryGetDouble("view_dir_z", out double vdz) ? vdz : 0.0);
                }
                else
                {
                    // Recompute from section line
                    var objs = GetLayerObjects(doc, layerIdx);
                    Line sectionLine = FindLongestLine(doc, objs);
                    if (sectionLine.Length < RhinoMath.ZeroTolerance)
                        return new JObject { ["status"] = "error", ["message"] = "Cannot find section line on layer." };

                    Vector3d lineDir = sectionLine.Direction;
                    lineDir.Unitize();
                    viewDir = new Vector3d(-lineDir.Y, lineDir.X, 0);
                    viewDir.Unitize();
                }

                // Find section line for plane origin
                var layerObjs = GetLayerObjects(doc, layerIdx);
                Line secLine = FindLongestLine(doc, layerObjs);

                Point3d planeOrigin;
                if (layer.UserDictionary.ContainsKey("start_x"))
                {
                    Point3d start = new Point3d(
                        layer.UserDictionary.TryGetDouble("start_x", out double sx) ? sx : 0.0,
                        layer.UserDictionary.TryGetDouble("start_y", out double sy) ? sy : 0.0,
                        layer.UserDictionary.TryGetDouble("start_z", out double sz) ? sz : 0.0);
                    Point3d end = new Point3d(
                        layer.UserDictionary.TryGetDouble("end_x", out double ex) ? ex : 0.0,
                        layer.UserDictionary.TryGetDouble("end_y", out double ey) ? ey : 0.0,
                        layer.UserDictionary.TryGetDouble("end_z", out double ez) ? ez : 0.0);
                    planeOrigin = (start + end) / 2.0;
                }
                else if (secLine.Length > RhinoMath.ZeroTolerance)
                {
                    planeOrigin = secLine.PointAt(0.5);
                }
                else
                {
                    planeOrigin = GetModelBBox(doc).Center;
                }

                // Section plane: normal = viewDir (the camera looks opposite to normal in RhinoCommon clipping)
                // Clipping plane normal should point TOWARD the camera (into the view)
                var sectionPlane = new Plane(planeOrigin, viewDir);

                BoundingBox bbox = GetModelBBox(doc);
                double bboxDiag = bbox.Diagonal.Length;
                double magnitude = bboxDiag * 2.0;

                // Remove existing clipping plane if any
                string cpKey = $"ai:section:{label}:clipping_plane_id";
                RemoveExistingClippingPlane(doc, cpKey);

                // Create new clipping plane
                var activeView = doc.Views.ActiveView;
                if (activeView == null) return new JObject { ["status"] = "error", ["message"] = "No active viewport." };
                var viewport = activeView.ActiveViewport;
                Guid viewportId = viewport.Id;
                var savedView = restoreView ? SaveViewState(viewport) : null;
                Guid cpId = doc.Objects.AddClippingPlane(sectionPlane, magnitude, magnitude, new Guid[] { viewportId });

                if (cpId == Guid.Empty)
                    return new JObject { ["status"] = "error", ["message"] = "Failed to create clipping plane." };

                doc.Strings.SetString(cpKey, cpId.ToString());

                try
                {
                    // Align view
                    var alignParams = new JObject
                    {
                        ["label"] = label
                    };
                    AlignViewToSection(alignParams, doc);

                    doc.Views.Redraw();

                    var result = new JObject
                    {
                        ["status"] = "ok",
                        ["label"] = label,
                        ["clipping_plane_id"] = cpId.ToString(),
                        ["view_restored"] = restoreView,
                        ["section_plane"] = new JObject
                        {
                            ["origin"] = PointToJObject(planeOrigin),
                            ["normal"] = VectorToJObject(viewDir)
                        },
                        ["view_direction"] = VectorToJObject(viewDir)
                    };

                    if (capture)
                    {
                        string b64 = CaptureViewport(doc, 1600, 1200);
                        if (b64 != null)
                            result["image_base64"] = b64;
                    }

                    return result;
                }
                finally
                {
                    if (savedView != null) RestoreViewState(doc, savedView);
                }
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Aligns the active viewport to look at a section plane.
        /// </summary>
        public static JObject AlignViewToSection(JObject p, RhinoDoc doc)
        {
            try
            {
                Vector3d viewDir = Vector3d.YAxis; // default
                Point3d planeOrigin = Point3d.Origin;

                string label = p["label"]?.ToString();
                if (!string.IsNullOrEmpty(label))
                {
                    // Load from layer user dictionary
                    string layerName = $"Section-{label}";
                    int layerIdx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
                    if (layerIdx >= 0)
                    {
                        var layer = doc.Layers[layerIdx];
                        if (layer.UserDictionary.ContainsKey("view_dir_x"))
                        {
                            viewDir = new Vector3d(
                                layer.UserDictionary.TryGetDouble("view_dir_x", out double vdx2) ? vdx2 : 0.0,
                                layer.UserDictionary.TryGetDouble("view_dir_y", out double vdy2) ? vdy2 : 0.0,
                                layer.UserDictionary.TryGetDouble("view_dir_z", out double vdz2) ? vdz2 : 0.0);
                        }
                        if (layer.UserDictionary.ContainsKey("start_x"))
                        {
                            Point3d s = new Point3d(
                                layer.UserDictionary.TryGetDouble("start_x", out double sx2) ? sx2 : 0.0,
                                layer.UserDictionary.TryGetDouble("start_y", out double sy2) ? sy2 : 0.0,
                                layer.UserDictionary.TryGetDouble("start_z", out double sz2) ? sz2 : 0.0);
                            Point3d e = new Point3d(
                                layer.UserDictionary.TryGetDouble("end_x", out double ex2) ? ex2 : 0.0,
                                layer.UserDictionary.TryGetDouble("end_y", out double ey2) ? ey2 : 0.0,
                                layer.UserDictionary.TryGetDouble("end_z", out double ez2) ? ez2 : 0.0);
                            planeOrigin = (s + e) / 2.0;
                        }
                    }
                }
                else if (p["plane_normal"] != null && p["plane_origin"] != null)
                {
                    viewDir = ParseVector(p["plane_normal"]);
                    planeOrigin = ParsePoint(p["plane_origin"]);
                }

                viewDir.Unitize();

                BoundingBox bbox = GetModelBBox(doc);
                double bboxDiag = bbox.Diagonal.Length;
                Point3d target = bbox.IsValid ? bbox.Center : planeOrigin;

                // Camera positioned looking in -viewDir toward the target
                // (camera location is behind the section, looking through the cut)
                Point3d camLocation = target + (-viewDir) * (bboxDiag * 2.0);

                var activeView = doc.Views.ActiveView;
                if (activeView == null) return new JObject { ["status"] = "error", ["message"] = "No active viewport." };
                var vp = activeView.ActiveViewport;
                vp.ChangeToParallelProjection(true);
                vp.CameraUp = Vector3d.ZAxis;
                vp.SetCameraLocations(target, camLocation);
                vp.ZoomBoundingBox(bbox);

                doc.Views.Redraw();

                return new JObject
                {
                    ["status"] = "ok",
                    ["camera_location"] = PointToJObject(camLocation),
                    ["camera_target"] = PointToJObject(target),
                    ["view_direction"] = VectorToJObject(viewDir),
                    ["message"] = "View aligned to section."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Creates a floor plan clipping plane at a specified floor level.
        /// </summary>
        public static JObject CreatePlan(JObject p, RhinoDoc doc)
        {
            try
            {
                bool capture = p["capture"] == null || (bool)p["capture"];
                bool restoreView = p["restore_view"]?.ToObject<bool>() ?? capture;

                // Get level data from SemanticClassifier
                JObject levelSummary = null;
                try { levelSummary = SemanticClassifier.GetLevelSummary(doc, null); } catch { }

                var levels = new List<double>();
                if (levelSummary?["levels"] is JArray levArr)
                {
                    foreach (var lev in levArr)
                    {
                        var zVal = lev["z"] ?? lev["elevation"] ?? lev["height"];
                        if (zVal != null)
                            levels.Add((double)zVal);
                    }
                }

                // Fall back: use bbox to estimate floors
                BoundingBox bbox = GetModelBBox(doc);
                if (!bbox.IsValid)
                    return new JObject { ["status"] = "error", ["message"] = "No visible geometry found." };

                if (levels.Count == 0)
                {
                    // Estimate floors every 3m (or model-unit equivalent)
                    double floorHeight = ConvertToModelUnits(doc, 3000);
                    double bboxH = bbox.Max.Z - bbox.Min.Z;
                    int numFloors = Math.Max(1, (int)Math.Round(bboxH / floorHeight));
                    for (int i = 0; i < numFloors; i++)
                        levels.Add(bbox.Min.Z + i * floorHeight);
                }

                levels.Sort();

                // Parse floor reference
                int floorIndex = ParseFloorRef(p["floor"]?.ToString() ?? "0", levels);
                if (floorIndex < 0) floorIndex = 0;
                if (floorIndex >= levels.Count) floorIndex = levels.Count - 1;

                double floorZ = levels[floorIndex];

                // Cut height: default 1200mm above floor in model units
                double cutHeightModel = ConvertToModelUnits(doc, 1200);
                if (p["cut_height_mm"] != null)
                    cutHeightModel = ConvertToModelUnits(doc, p["cut_height_mm"].ToObject<double>());
                else if (p["cut_height"] != null)
                    cutHeightModel = p["cut_height"].ToObject<double>(); // already in model units

                double cutZ = floorZ + cutHeightModel;

                string layerName = $"Plan-Floor-{floorIndex:D2}";
                int layerIdx = EnsureLayer(doc, layerName, Color.Green);

                // Horizontal clipping plane at cutZ, normal pointing UP (+Z) so everything above is clipped
                var planePlane = new Plane(new Point3d(bbox.Center.X, bbox.Center.Y, cutZ), Vector3d.ZAxis);
                double bboxDiag = bbox.Diagonal.Length;
                double magnitude = bboxDiag * 2.0;

                string cpKey = $"ai:plan:floor_{floorIndex:D2}:clipping_plane_id";
                RemoveExistingClippingPlane(doc, cpKey);

                var planActiveView = doc.Views.ActiveView;
                if (planActiveView == null) return new JObject { ["status"] = "error", ["message"] = "No active viewport." };
                var viewport = planActiveView.ActiveViewport;
                Guid viewportId = viewport.Id;
                var savedView = restoreView ? SaveViewState(viewport) : null;
                Guid cpId = doc.Objects.AddClippingPlane(planePlane, magnitude, magnitude, new Guid[] { viewportId });

                if (cpId == Guid.Empty)
                    return new JObject { ["status"] = "error", ["message"] = "Failed to create clipping plane." };

                doc.Strings.SetString(cpKey, cpId.ToString());
                doc.Strings.SetString($"ai:plan:floor_{floorIndex:D2}:layer", layerName);

                try
                {
                    // Set Top view
                    viewport.SetProjection(DefinedViewportProjection.Top, null, true);

                    // Frame to floor footprint XY bbox at that level
                    BoundingBox planeBbox = new BoundingBox(
                        new Point3d(bbox.Min.X, bbox.Min.Y, floorZ),
                        new Point3d(bbox.Max.X, bbox.Max.Y, cutZ));
                    viewport.ZoomBoundingBox(planeBbox);

                    doc.Views.Redraw();

                    var result = new JObject
                    {
                        ["status"] = "ok",
                        ["floor_index"] = floorIndex,
                        ["floor_elevation"] = floorZ,
                        ["cut_elevation"] = cutZ,
                        ["layer"] = layerName,
                        ["clipping_plane_id"] = cpId.ToString(),
                        ["view_restored"] = restoreView
                    };

                    if (capture)
                    {
                        string b64 = CaptureViewport(doc, 1600, 1200);
                        if (b64 != null)
                            result["image_base64"] = b64;
                    }

                    return result;
                }
                finally
                {
                    if (savedView != null) RestoreViewState(doc, savedView);
                }
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Creates floor plan clipping planes for all detected floors.
        /// </summary>
        public static JObject CreateAllPlans(JObject p, RhinoDoc doc)
        {
            try
            {
                JObject levelSummary = null;
                try { levelSummary = SemanticClassifier.GetLevelSummary(doc, null); } catch { }

                var levels = new List<double>();
                if (levelSummary?["levels"] is JArray levArr)
                {
                    foreach (var lev in levArr)
                    {
                        var zVal = lev["z"] ?? lev["elevation"] ?? lev["height"];
                        if (zVal != null)
                            levels.Add((double)zVal);
                    }
                }

                BoundingBox bbox = GetModelBBox(doc);
                if (!bbox.IsValid)
                    return new JObject { ["status"] = "error", ["message"] = "No visible geometry found." };

                if (levels.Count == 0)
                {
                    double floorHeight = ConvertToModelUnits(doc, 3000);
                    double bboxH = bbox.Max.Z - bbox.Min.Z;
                    int numFloors = Math.Max(1, (int)Math.Round(bboxH / floorHeight));
                    for (int i = 0; i < numFloors; i++)
                        levels.Add(bbox.Min.Z + i * floorHeight);
                }

                levels.Sort();

                var results = new JArray();
                for (int i = 0; i < levels.Count; i++)
                {
                    var floorP = new JObject
                    {
                        ["floor"] = i.ToString(),
                        ["capture"] = false
                    };
                    var r = CreatePlan(floorP, doc);
                    results.Add(r);
                }

                return new JObject
                {
                    ["status"] = "ok",
                    ["floor_count"] = levels.Count,
                    ["plans"] = results,
                    ["message"] = $"Created {levels.Count} floor plan clipping planes."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Lists all section, elevation, and plan layers with their clipping plane status.
        /// </summary>
        public static JObject ListSections(JObject p, RhinoDoc doc)
        {
            try
            {
                var items = new JArray();

                foreach (var layer in doc.Layers)
                {
                    string name = layer.FullPath;
                    string type = null;

                    if (name.StartsWith("Section-", StringComparison.OrdinalIgnoreCase))
                        type = "section";
                    else if (name.StartsWith("Elevation-", StringComparison.OrdinalIgnoreCase))
                        type = "elevation";
                    else if (name.StartsWith("Plan-Floor-", StringComparison.OrdinalIgnoreCase))
                        type = "plan";

                    if (type == null) continue;

                    // Determine clipping plane key
                    string cpKey = null;
                    if (type == "section")
                    {
                        string lbl = name.Substring("Section-".Length);
                        cpKey = $"ai:section:{lbl}:clipping_plane_id";
                    }
                    else if (type == "elevation")
                    {
                        string lbl = name.Substring("Elevation-".Length);
                        cpKey = $"ai:elevation:{lbl}:clipping_plane_id";
                    }
                    else if (type == "plan")
                    {
                        string floorPart = name.Substring("Plan-Floor-".Length);
                        cpKey = $"ai:plan:floor_{floorPart}:clipping_plane_id";
                    }

                    string cpIdStr = cpKey != null ? doc.Strings.GetValue(cpKey) : null;
                    bool hasCp = !string.IsNullOrEmpty(cpIdStr) && Guid.TryParse(cpIdStr, out _);

                    var item = new JObject
                    {
                        ["name"] = name,
                        ["type"] = type,
                        ["visible"] = layer.IsVisible,
                        ["has_clipping_plane"] = hasCp,
                    };
                    if (hasCp)
                        item["clipping_plane_id"] = cpIdStr;

                    items.Add(item);
                }

                return new JObject
                {
                    ["status"] = "ok",
                    ["count"] = items.Count,
                    ["items"] = items
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Updates an existing section with new start/end points.
        /// </summary>
        public static JObject UpdateSection(JObject p, RhinoDoc doc)
        {
            try
            {
                string label = p["label"]?.ToString();
                if (string.IsNullOrEmpty(label))
                    return new JObject { ["status"] = "error", ["message"] = "label parameter is required." };

                string layerName = $"Section-{label}";
                int layerIdx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
                if (layerIdx < 0)
                    return new JObject { ["status"] = "error", ["message"] = $"Layer '{layerName}' not found." };

                var layer = doc.Layers[layerIdx];

                // Retrieve old points from user dictionary
                Point3d oldStart = layer.UserDictionary.ContainsKey("start_x")
                    ? new Point3d(
                        layer.UserDictionary.TryGetDouble("start_x", out double osx) ? osx : 0.0,
                        layer.UserDictionary.TryGetDouble("start_y", out double osy) ? osy : 0.0,
                        layer.UserDictionary.TryGetDouble("start_z", out double osz) ? osz : 0.0)
                    : Point3d.Origin;

                Point3d oldEnd = layer.UserDictionary.ContainsKey("end_x")
                    ? new Point3d(
                        layer.UserDictionary.TryGetDouble("end_x", out double oex) ? oex : 0.0,
                        layer.UserDictionary.TryGetDouble("end_y", out double oey) ? oey : 0.0,
                        layer.UserDictionary.TryGetDouble("end_z", out double oez) ? oez : 0.0)
                    : Point3d.Origin;

                string viewSide = "left";
                if (layer.UserDictionary.ContainsKey("view_dir_x"))
                {
                    var vd = new Vector3d(
                        layer.UserDictionary.TryGetDouble("view_dir_x", out double uvdx) ? uvdx : 0.0,
                        layer.UserDictionary.TryGetDouble("view_dir_y", out double uvdy) ? uvdy : 0.0,
                        0);
                    // determine side: if vd is left-perpendicular, side=left
                    // We just preserve the existing direction by passing it through
                }

                // Apply new start/end if provided
                Point3d newStart = p["start_point"] != null ? ParsePoint(p["start_point"]) : oldStart;
                Point3d newEnd = p["end_point"] != null ? ParsePoint(p["end_point"]) : oldEnd;

                // Apply offset if provided
                if (p["offset"] is JObject offsetVec)
                {
                    Vector3d off = ParseVector(offsetVec);
                    newStart += off;
                    newEnd += off;
                }

                // Delete all objects on the layer
                DeleteLayerObjects(doc, layerIdx);

                // Remove existing clipping plane
                string cpKey = $"ai:section:{label}:clipping_plane_id";
                RemoveExistingClippingPlane(doc, cpKey);

                // Recreate with new params
                var createParams = new JObject
                {
                    ["label"] = label,
                    ["start_point"] = PointToJObject(newStart),
                    ["end_point"] = PointToJObject(newEnd),
                    ["view_side"] = viewSide
                };

                // Delete the old layer so CreateSection can recreate it fresh
                // (CreateSection calls EnsureLayer which will just return the existing layer)
                var result = CreateSection(createParams, doc);

                doc.Views.Redraw();

                if (result["status"]?.ToString() == "ok")
                    result["message"] = $"Section {label} updated. Say \"cut section {label}\" to re-apply the clipping plane.";

                return result;
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        /// <summary>
        /// Removes a section layer and its associated clipping plane.
        /// </summary>
        public static JObject RemoveSection(JObject p, RhinoDoc doc)
        {
            try
            {
                string label = p["label"]?.ToString();
                if (string.IsNullOrEmpty(label))
                    return new JObject { ["status"] = "error", ["message"] = "label parameter is required." };

                // Try section, elevation, or plan
                string[] prefixes = { "Section-", "Elevation-", "Plan-Floor-" };
                string[] cpKeyPatterns = { $"ai:section:{label}:clipping_plane_id", $"ai:elevation:{label}:clipping_plane_id", null };

                bool found = false;
                foreach (var prefix in prefixes)
                {
                    string layerName = prefix + label;
                    int layerIdx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
                    if (layerIdx < 0) continue;

                    found = true;

                    // Remove clipping plane
                    string cpKey;
                    if (prefix == "Section-")
                    {
                        cpKey = $"ai:section:{label}:clipping_plane_id";
                    }
                    else if (prefix == "Elevation-")
                    {
                        cpKey = $"ai:elevation:{label}:clipping_plane_id";
                    }
                    else
                    {
                        // Plan layers are stored with D2 format (e.g., "Plan-Floor-00").
                        // Scan doc.Strings to find the matching clipping plane key.
                        cpKey = null;
                        for (int ki = 0; ki < 200; ki++)
                        {
                            string candidate = $"ai:plan:floor_{ki:D2}:clipping_plane_id";
                            if (!string.IsNullOrEmpty(doc.Strings.GetValue(candidate)))
                            {
                                string layerCandidate = $"Plan-Floor-{ki:D2}";
                                if (layerCandidate == layerName || ki.ToString() == label ||
                                    (ki == 0 && (label == "ground" || label == "G")))
                                {
                                    cpKey = candidate;
                                    break;
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(cpKey))
                    {
                        RemoveExistingClippingPlane(doc, cpKey);
                        doc.Strings.Delete(cpKey);
                    }

                    // Delete objects then layer
                    DeleteLayerObjects(doc, layerIdx);
                    doc.Layers.Delete(layerIdx, true);
                }

                if (!found)
                    return new JObject { ["status"] = "error", ["message"] = $"No section/elevation/plan with label '{label}' found." };

                doc.Views.Redraw();

                return new JObject
                {
                    ["status"] = "ok",
                    ["label"] = label,
                    ["message"] = $"Section/elevation/plan '{label}' removed."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["status"] = "error", ["message"] = ex.Message };
            }
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE HELPER METHODS
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures a layer exists with the given name and color, returning its index.
        /// </summary>
        private static int EnsureLayer(RhinoDoc doc, string name, Color color)
        {
            int idx = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
            if (idx >= 0) return idx;

            var layer = new Layer
            {
                Name = name,
                Color = color,
                IsVisible = true,
                IsLocked = false
            };
            return doc.Layers.Add(layer);
        }

        /// <summary>
        /// Returns the bounding box of all visible, non-deleted geometry.
        /// </summary>
        private static BoundingBox GetModelBBox(RhinoDoc doc)
        {
            var bbox = BoundingBox.Empty;
            var settings = new ObjectEnumeratorSettings
            {
                IncludeLights = false,
                IncludeGrips = false,
                DeletedObjects = false,
                ActiveObjects = true,
                VisibleFilter = true
            };
            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                var ob = obj.Geometry?.GetBoundingBox(true);
                if (ob.HasValue && ob.Value.IsValid)
                    bbox.Union(ob.Value);
            }
            return bbox;
        }

        /// <summary>
        /// Parses a floor reference string to an index in the levels list.
        /// Accepts: integer strings, "ground", "G", "B1", "basement", "1st", "2nd", etc.
        /// </summary>
        private static int ParseFloorRef(string floorRef, List<double> levels)
        {
            if (string.IsNullOrEmpty(floorRef)) return 0;

            string f = floorRef.Trim().ToLower();

            // Direct integer
            if (int.TryParse(f, out int n))
                return Math.Max(0, Math.Min(n, levels.Count - 1));

            // Named
            if (f == "ground" || f == "g" || f == "gf" || f == "ground floor" || f == "0")
                return 0;

            if (f.StartsWith("b") || f == "basement" || f == "lower ground")
            {
                // B1 = index -1 from ground (if levels have negative z)
                if (int.TryParse(f.Replace("b", ""), out int bn))
                    return Math.Max(0, -bn);
                return 0;
            }

            // "8th", "8th floor", "floor 8"
            string stripped = f.Replace("st", "").Replace("nd", "").Replace("rd", "").Replace("th", "")
                               .Replace("floor", "").Replace(" ", "");
            if (int.TryParse(stripped, out int fn))
                return Math.Max(0, Math.Min(fn, levels.Count - 1));

            return 0;
        }

        /// <summary>
        /// Adds a filled mesh triangle arrowhead at tip, pointing in direction.
        /// </summary>
        private static void AddArrowhead(RhinoDoc doc, Point3d tip, Vector3d direction, double size, int layerIdx)
        {
            direction.Unitize();

            // Perpendicular in XY plane
            Vector3d perp = Vector3d.CrossProduct(direction, Vector3d.ZAxis);
            if (perp.IsTiny()) perp = Vector3d.XAxis;
            perp.Unitize();

            // Triangle: tip at front, base at -direction * size
            Point3d basePt = tip - direction * size;
            Point3d left = basePt + perp * (size * 0.4);
            Point3d right = basePt - perp * (size * 0.4);

            var mesh = new Mesh();
            mesh.Vertices.Add(tip);    // 0
            mesh.Vertices.Add(left);   // 1
            mesh.Vertices.Add(right);  // 2
            mesh.Faces.AddFace(0, 1, 2);
            mesh.Normals.ComputeNormals();
            mesh.Compact();

            var attr = new ObjectAttributes { LayerIndex = layerIdx };
            attr.ColorSource = ObjectColorSource.ColorFromLayer;
            doc.Objects.AddMesh(mesh, attr);
        }

        /// <summary>
        /// Adds a text dot label at the given position.
        /// </summary>
        private static void AddSectionLabel(RhinoDoc doc, Point3d position, string text, double height, int layerIdx)
        {
            var attr = new ObjectAttributes { LayerIndex = layerIdx };
            attr.ColorSource = ObjectColorSource.ColorFromLayer;

            var textEnt = new Rhino.Geometry.TextEntity
            {
                Plane = new Plane(position, Vector3d.ZAxis),
                PlainText = text,
                TextHeight = height,
                Justification = TextJustification.BottomCenter
            };
            doc.Objects.AddText(textEnt, attr);
        }

        /// <summary>
        /// Returns the next section label: A, B, C ... Z, AA, AB ...
        /// </summary>
        private static string GetSectionLabel()
        {
            int n = _sectionCounter++;
            if (n < 26)
                return ((char)('A' + n)).ToString();
            // Beyond Z: AA, AB, ...
            int hi = n / 26 - 1;
            int lo = n % 26;
            return ((char)('A' + hi)).ToString() + ((char)('A' + lo)).ToString();
        }

        /// <summary>
        /// Converts millimetres to current model units.
        /// </summary>
        private static double ConvertToModelUnits(RhinoDoc doc, double mm)
        {
            double factor = Rhino.RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
            return mm * factor;
        }

        /// <summary>
        /// Retrieves a stored clipping plane Guid from doc.Strings, or null.
        /// </summary>
        private static Guid? GetClippingPlaneId(RhinoDoc doc, string key)
        {
            string val = doc.Strings.GetValue(key);
            if (!string.IsNullOrEmpty(val) && Guid.TryParse(val, out Guid id))
                return id;
            return null;
        }

        /// <summary>
        /// Removes an existing clipping plane object from the document if stored.
        /// </summary>
        private static void RemoveExistingClippingPlane(RhinoDoc doc, string key)
        {
            Guid? cpId = GetClippingPlaneId(doc, key);
            if (cpId.HasValue && cpId.Value != Guid.Empty)
            {
                doc.Objects.Delete(cpId.Value, true);
                doc.Strings.Delete(key);
            }
        }

        /// <summary>
        /// Gets all RhinoObjects on a given layer.
        /// </summary>
        private static List<RhinoObject> GetLayerObjects(RhinoDoc doc, int layerIdx)
        {
            var result = new List<RhinoObject>();
            var settings = new ObjectEnumeratorSettings
            {
                LayerIndexFilter = layerIdx,
                DeletedObjects = false,
                ActiveObjects = true
            };
            result.AddRange(doc.Objects.GetObjectList(settings));
            return result;
        }

        /// <summary>
        /// Deletes all objects on a given layer.
        /// </summary>
        private static void DeleteLayerObjects(RhinoDoc doc, int layerIdx)
        {
            var objs = GetLayerObjects(doc, layerIdx);
            foreach (var obj in objs)
                doc.Objects.Delete(obj, true);
        }

        /// <summary>
        /// Finds the longest Line geometry among a list of RhinoObjects.
        /// </summary>
        private static Line FindLongestLine(RhinoDoc doc, List<RhinoObject> objs)
        {
            Line best = Line.Unset;
            double bestLen = 0;
            foreach (var obj in objs)
            {
                if (obj.Geometry is LineCurve lc)
                {
                    double len = lc.Line.Length;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        best = lc.Line;
                    }
                }
                else if (obj.Geometry is Curve cv)
                {
                    // Try to approximate as line
                    double len = cv.GetLength();
                    if (len > bestLen)
                    {
                        bestLen = len;
                        best = new Line(cv.PointAtStart, cv.PointAtEnd);
                    }
                }
            }
            return best;
        }

        private sealed class ViewState
        {
            public RhinoViewport Viewport { get; init; }
            public Point3d Target { get; init; }
            public Point3d Location { get; init; }
            public Vector3d Up { get; init; }
            public DisplayModeDescription DisplayMode { get; init; }
            public bool IsParallel { get; init; }
            public double LensLength { get; init; }
        }

        private static ViewState SaveViewState(RhinoViewport viewport)
        {
            if (viewport == null) return null;
            return new ViewState
            {
                Viewport = viewport,
                Target = viewport.CameraTarget,
                Location = viewport.CameraLocation,
                Up = viewport.CameraUp,
                DisplayMode = viewport.DisplayMode,
                IsParallel = viewport.IsParallelProjection,
                LensLength = viewport.Camera35mmLensLength
            };
        }

        private static void RestoreViewState(RhinoDoc doc, ViewState state)
        {
            if (state?.Viewport == null) return;
            try
            {
                if (state.IsParallel) state.Viewport.ChangeToParallelProjection(true);
                else state.Viewport.ChangeToPerspectiveProjection(true, state.LensLength);
                state.Viewport.SetCameraLocations(state.Target, state.Location);
                state.Viewport.CameraUp = state.Up;
                state.Viewport.DisplayMode = state.DisplayMode;
                doc.Views.ActiveView?.Redraw();
            }
            catch
            {
                // Best effort: section captures should never fail solely because view restore failed.
            }
        }

        /// <summary>
        /// Captures the active viewport at the given resolution and returns base64 PNG.
        /// Returns null on failure.
        /// </summary>
        private static string CaptureViewport(RhinoDoc doc, int width, int height)
        {
            try
            {
                var view = doc.Views.ActiveView;
                if (view == null) return null;

                using (var bmp = view.CaptureToBitmap(new System.Drawing.Size(width, height)))
                {
                    if (bmp == null) return null;
                    using (var ms = new System.IO.MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // ── JSON helpers ──────────────────────────────────────────────

        private static Point3d ParsePoint(JToken t)
        {
            return new Point3d(
                t["x"] != null ? (double)t["x"] : 0,
                t["y"] != null ? (double)t["y"] : 0,
                t["z"] != null ? (double)t["z"] : 0);
        }

        private static Vector3d ParseVector(JToken t)
        {
            return new Vector3d(
                t["x"] != null ? (double)t["x"] : 0,
                t["y"] != null ? (double)t["y"] : 0,
                t["z"] != null ? (double)t["z"] : 0);
        }

        private static JObject PointToJObject(Point3d pt)
        {
            return new JObject { ["x"] = pt.X, ["y"] = pt.Y, ["z"] = pt.Z };
        }

        private static JObject VectorToJObject(Vector3d v)
        {
            return new JObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
        }
    }
}
