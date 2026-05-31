// RhinoAIBridge v4.5 — SceneSnapshotRegistry.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

using System;
using System.Collections.Concurrent;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;

namespace RhinoAIBridge
{
    /// <summary>
    /// Maintains a SceneSnapshot per open RhinoDoc, wires Rhino's document events
    /// to the snapshots, and exposes Get(doc) to the rest of the plugin.
    /// 
    /// Single instance per process — initialized once when AIBridgeServer starts,
    /// torn down on stop.
    /// </summary>
    public static class SceneSnapshotRegistry
    {
        // Keyed by doc serial. Snapshots are cheap to drop on doc close.
        // ConcurrentDictionary because the dict itself is touched by background tasks
        // doing read-only Get() calls (e.g. ping); the per-snapshot data is single-threaded.
        private static readonly ConcurrentDictionary<uint, SceneSnapshot> _snapshots = new();
        private static bool _wired;

        public static void Initialize()
        {
            if (_wired) return;
            _wired = true;

            RhinoDoc.AddRhinoObject += OnAdded;
            RhinoDoc.DeleteRhinoObject += OnDeleted;
            RhinoDoc.UndeleteRhinoObject += OnUndeleted;
            RhinoDoc.ReplaceRhinoObject += OnReplaced;
            RhinoDoc.ModifyObjectAttributes += OnAttrsModified;
            RhinoDoc.LayerTableEvent += OnLayerTableEvent;
            RhinoDoc.NewDocument += OnNewOrOpenedDocument;
            RhinoDoc.EndOpenDocument += OnEndOpenDocument;
            RhinoDoc.CloseDocument += OnCloseDocument;

            // Build lazily on first use so large documents do not delay the listener.

            AIBridgeLogger.Log(LogLevel.INFO, "Scene", "SceneSnapshotRegistry initialized");
        }

        public static void Shutdown()
        {
            if (!_wired) return;
            _wired = false;
            try
            {
                RhinoDoc.AddRhinoObject -= OnAdded;
                RhinoDoc.DeleteRhinoObject -= OnDeleted;
                RhinoDoc.UndeleteRhinoObject -= OnUndeleted;
                RhinoDoc.ReplaceRhinoObject -= OnReplaced;
                RhinoDoc.ModifyObjectAttributes -= OnAttrsModified;
                RhinoDoc.LayerTableEvent -= OnLayerTableEvent;
                RhinoDoc.NewDocument -= OnNewOrOpenedDocument;
                RhinoDoc.EndOpenDocument -= OnEndOpenDocument;
                RhinoDoc.CloseDocument -= OnCloseDocument;
            }
            catch { }
            _snapshots.Clear();
        }

        /// <summary>Get the snapshot for the given doc, building it lazily.</summary>
        public static SceneSnapshot Get(RhinoDoc doc)
        {
            if (doc == null) return null;
            return GetOrBuild(doc);
        }

        /// <summary>Convenience accessor for the active doc snapshot.</summary>
        public static SceneSnapshot Active => Get(RhinoDoc.ActiveDoc);

        private static SceneSnapshot GetOrBuild(RhinoDoc doc)
        {
            return _snapshots.GetOrAdd(doc.RuntimeSerialNumber, _ =>
            {
                var s = new SceneSnapshot(doc);
                AIBridgeLogger.Log(LogLevel.DEBUG, "Scene",
                    $"Built snapshot for doc serial={doc.RuntimeSerialNumber} name={doc.Name} count={s.Count}");
                return s;
            });
        }

        // ── Rhino event handlers ───────────────────────────────────
        // All fire on the UI thread.

        private static void OnAdded(object sender, RhinoObjectEventArgs e)
        {
            var doc = e.TheObject?.Document;
            if (doc != null) GetOrBuild(doc).OnAdded(e.TheObject);
        }

        private static void OnDeleted(object sender, RhinoObjectEventArgs e)
        {
            var doc = e.TheObject?.Document;
            if (doc != null) GetOrBuild(doc).OnDeleted(e.TheObject);
        }

        private static void OnUndeleted(object sender, RhinoObjectEventArgs e)
        {
            var doc = e.TheObject?.Document;
            if (doc != null) GetOrBuild(doc).OnUndeleted(e.TheObject);
        }

        private static void OnReplaced(object sender, RhinoReplaceObjectEventArgs e)
        {
            var doc = e.NewRhinoObject?.Document ?? e.OldRhinoObject?.Document;
            if (doc != null) GetOrBuild(doc).OnReplaced(e.NewRhinoObject);
        }

        private static void OnAttrsModified(object sender, RhinoModifyObjectAttributesEventArgs e)
        {
            // RhinoObject is on `e.RhinoObject` in modern RhinoCommon; fall back if needed.
            var ro = e.RhinoObject;
            var doc = ro?.Document;
            if (doc != null) GetOrBuild(doc).OnAttributesModified(ro);
        }

        private static void OnLayerTableEvent(object sender, LayerTableEventArgs e)
        {
            var doc = e.Document;
            if (doc != null) GetOrBuild(doc).OnLayerTableChanged(doc);
        }

        private static void OnNewOrOpenedDocument(object sender, DocumentEventArgs e)
        {
            // New doc: build a fresh snapshot. Don't block the event — just enqueue lazy build.
            if (e.Document != null) GetOrBuild(e.Document);
        }

        private static void OnEndOpenDocument(object sender, DocumentOpenEventArgs e)
        {
            // Force a rebuild — the doc's contents are now fully loaded.
            if (e.Document == null) return;
            if (_snapshots.TryGetValue(e.Document.RuntimeSerialNumber, out var snap))
                snap.Rebuild(e.Document);
            else
                GetOrBuild(e.Document);
        }

        private static void OnCloseDocument(object sender, DocumentEventArgs e)
        {
            if (e.Document == null) return;
            _snapshots.TryRemove(e.Document.RuntimeSerialNumber, out _);
        }

        /// <summary>
        /// Force-drop and immediately rebuild the snapshot for the active doc.
        /// Use when the caller needs object counts to reflect the absolute current
        /// state of the document (e.g. after a bulk import or external edit).
        /// </summary>
        public static SceneSnapshot ForceRebuild(RhinoDoc doc)
        {
            if (doc == null) return null;
            _snapshots.TryRemove(doc.RuntimeSerialNumber, out _);
            return GetOrBuild(doc);
        }
    }
}
