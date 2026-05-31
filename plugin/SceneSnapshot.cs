// RhinoAIBridge v4.5 — SceneSnapshot.cs
// by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoAIBridge
{
    /// <summary>
    /// In-process indexed cache of the active Rhino document.
    /// 
    /// Why it exists:
    ///   v3 and v4-Phase-1 walked Doc.Objects on EVERY read tool call. For an architect
    ///   mid-session with 2000+ objects, that's a full O(N) walk plus per-object work
    ///   (group-by-type, group-by-layer, bbox unions) — repeated 3–5 times per turn
    ///   as Claude orients itself between mutations.
    /// 
    ///   This class keeps the answers pre-aggregated, indexed by layer/type/name,
    ///   and updated by Rhino doc events. Read tools become O(1) or O(M) where M is
    ///   the size of the result set, not the scene.
    /// 
    /// Threading:
    ///   Rhino fires all doc events on the UI thread. v4 dispatches all commands on
    ///   the UI thread (UiDispatcher). So the snapshot is single-threaded by construction
    ///   — no internal locks. The ONE exception is SceneVersion, which `ping` reads from
    ///   a non-UI thread. We use Interlocked / Volatile for that field.
    /// 
    /// Invalidation policy:
    ///   Surgical event handlers update only the indexes affected.
    ///   ModifyObjectAttributes is the noisy one — it fires for selection state too —
    ///   so we compare old vs new before re-indexing.
    /// </summary>
    public sealed class SceneSnapshot
    {
        // ── Public versioned identity ─────────────────────────────
        public uint DocSerial { get; }                  // RhinoDoc.RuntimeSerialNumber at attach time
        public string DocName { get; private set; }
        private long _sceneVersion;
        /// <summary>Monotonic counter — incremented on every mutation. Cross-thread safe.</summary>
        public long SceneVersion => Interlocked.Read(ref _sceneVersion);

        // ── Per-object metadata ───────────────────────────────────
        public sealed class ObjectMeta
        {
            public Guid Id;
            public ObjectType Type;
            public int LayerIndex;
            public string Name;
            public BoundingBox Bbox;     // world-aligned
            public bool Visible;
            public bool Locked;
        }

        // ── Indexes ───────────────────────────────────────────────
        private readonly Dictionary<Guid, ObjectMeta> _objects = new();
        private readonly Dictionary<int, HashSet<Guid>> _byLayerIndex = new();
        private readonly Dictionary<ObjectType, HashSet<Guid>> _byType = new();
        // Case-insensitive name lookup. Substring matching still O(M) on this index, but M is much smaller than N.
        private readonly Dictionary<string, HashSet<Guid>> _byNameLower = new();

        // Atomic count mirror — readable from non-UI threads (ping).
        private int _count;
        /// <summary>Object count. Cross-thread safe.</summary>
        public int Count => Volatile.Read(ref _count);

        // ── Cached aggregates ─────────────────────────────────────
        private BoundingBox _sceneBbox = BoundingBox.Empty;
        private bool _bboxDirty = true;

        // Layer index → name (refreshed on LayerTableEvent). Cheap; rarely changes mid-session.
        private readonly Dictionary<int, string> _layerNames = new();

        // ── Construction & rebuild ────────────────────────────────
        public SceneSnapshot(RhinoDoc doc)
        {
            DocSerial = doc.RuntimeSerialNumber;
            Rebuild(doc);
        }

        public void Rebuild(RhinoDoc doc)
        {
            _objects.Clear();
            _byLayerIndex.Clear();
            _byType.Clear();
            _byNameLower.Clear();
            _layerNames.Clear();
            _sceneBbox = BoundingBox.Empty;
            _bboxDirty = true;
            Volatile.Write(ref _count, 0);
            DocName = doc?.Name ?? "Untitled";

            if (doc == null) return;

            foreach (var l in doc.Layers.Where(x => !x.IsDeleted))
                _layerNames[l.Index] = l.FullPath ?? l.Name;

            var s = new ObjectEnumeratorSettings { DeletedObjects = false, HiddenObjects = true, LockedObjects = true };
            foreach (var ro in doc.Objects.GetObjectList(s))
                AddInternal(ro);

            BumpVersion();
        }

        // ── Event-driven updates ──────────────────────────────────
        // Each method below corresponds to exactly one Rhino doc event handler.
        // Keep them surgical — only touch indexes that actually changed.

        public void OnAdded(RhinoObject ro)
        {
            if (ro == null) return;
            AddInternal(ro);
            _bboxDirty = true;
            BumpVersion();
        }

        public void OnDeleted(RhinoObject ro)
        {
            if (ro == null) return;
            RemoveInternal(ro.Id);
            _bboxDirty = true;
            BumpVersion();
        }

        public void OnUndeleted(RhinoObject ro)
        {
            if (ro == null) return;
            // Effectively a re-add. RemoveInternal first in case we somehow have stale state.
            RemoveInternal(ro.Id);
            AddInternal(ro);
            _bboxDirty = true;
            BumpVersion();
        }

        public void OnReplaced(RhinoObject newObj)
        {
            // ReplaceRhinoObject preserves the GUID. Geometry/bbox may have changed.
            if (newObj == null) return;
            if (_objects.TryGetValue(newObj.Id, out var meta))
            {
                meta.Bbox = newObj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                meta.Type = newObj.Geometry?.ObjectType ?? meta.Type;
            }
            else
            {
                AddInternal(newObj);
            }
            _bboxDirty = true;
            BumpVersion();
        }

        /// <summary>
        /// ModifyObjectAttributes is the noisiest event — fires for selection state,
        /// color, lock, hide, name, layer. We re-index only when name or layer changes,
        /// and bump scene_version for any meaningful state change (layer, name, visibility,
        /// lock). Pure selection toggles change none of these, so they produce no version bump.
        /// </summary>
        public void OnAttributesModified(RhinoObject ro)
        {
            if (ro == null) return;
            if (!_objects.TryGetValue(ro.Id, out var meta))
            {
                AddInternal(ro);
                BumpVersion();
                return;
            }

            int newLayer = ro.Attributes.LayerIndex;
            string newName = ro.Attributes.Name ?? "";
            bool changed = false;

            if (newLayer != meta.LayerIndex)
            {
                if (_byLayerIndex.TryGetValue(meta.LayerIndex, out var oldSet)) oldSet.Remove(meta.Id);
                if (!_byLayerIndex.TryGetValue(newLayer, out var newSet))
                {
                    newSet = new HashSet<Guid>();
                    _byLayerIndex[newLayer] = newSet;
                }
                newSet.Add(meta.Id);
                meta.LayerIndex = newLayer;
                changed = true;
            }

            if (!string.Equals(newName, meta.Name, StringComparison.Ordinal))
            {
                RemoveFromNameIndex(meta);
                meta.Name = newName;
                AddToNameIndex(meta);
                changed = true;
            }

            // Visibility + lock: compare before assigning so we can detect real changes.
            // Selection toggles don't affect ro.Visible or ro.IsLocked, so they remain
            // noise-free. Actual hide/show or lock/unlock ops correctly bump the version.
            bool newVisible = ro.Visible;
            bool newLocked = ro.IsLocked;
            if (newVisible != meta.Visible || newLocked != meta.Locked)
            {
                meta.Visible = newVisible;
                meta.Locked = newLocked;
                changed = true;
            }

            if (changed) BumpVersion();
        }

        public void OnLayerTableChanged(RhinoDoc doc)
        {
            _layerNames.Clear();
            foreach (var l in doc.Layers.Where(x => !x.IsDeleted))
                _layerNames[l.Index] = l.FullPath ?? l.Name;
            BumpVersion();
        }

        // ── Read API (UI-thread only) ─────────────────────────────

        public IEnumerable<ObjectMeta> All() => _objects.Values;

        public ObjectMeta TryGet(Guid id) => _objects.TryGetValue(id, out var m) ? m : null;

        public BoundingBox SceneBoundingBox()
        {
            if (!_bboxDirty) return _sceneBbox;
            var bb = BoundingBox.Empty;
            foreach (var m in _objects.Values)
                if (m.Bbox.IsValid) bb.Union(m.Bbox);
            _sceneBbox = bb;
            _bboxDirty = false;
            return bb;
        }

        public Dictionary<string, int> CountsByLayerName()
        {
            var d = new Dictionary<string, int>(_byLayerIndex.Count);
            foreach (var kv in _byLayerIndex)
            {
                var name = _layerNames.TryGetValue(kv.Key, out var n) ? n : $"layer_{kv.Key}";
                d[name] = kv.Value.Count;
            }
            return d;
        }

        public Dictionary<string, int> CountsByType()
        {
            var d = new Dictionary<string, int>(_byType.Count);
            foreach (var kv in _byType)
                d[kv.Key.ToString()] = kv.Value.Count;
            return d;
        }

        public IEnumerable<ObjectMeta> ByLayerName(string layerName)
        {
            int idx = -1;
            foreach (var kv in _layerNames)
                if (string.Equals(kv.Value, layerName, StringComparison.Ordinal)) { idx = kv.Key; break; }
            if (idx < 0)
            {
                foreach (var kv in _layerNames)
                {
                    var leaf = kv.Value;
                    int separator = leaf.LastIndexOf("::", StringComparison.Ordinal);
                    if (separator >= 0) leaf = leaf.Substring(separator + 2);
                    if (string.Equals(leaf, layerName, StringComparison.Ordinal)) { idx = kv.Key; break; }
                }
            }
            if (idx < 0) return Array.Empty<ObjectMeta>();
            if (!_byLayerIndex.TryGetValue(idx, out var ids)) return Array.Empty<ObjectMeta>();
            return ids.Select(id => _objects.TryGetValue(id, out var m) ? m : null).Where(m => m != null);
        }

        public IEnumerable<ObjectMeta> ByType(string typeName)
        {
            // typeName is a substring match against ObjectType.ToString(), to match v3 behavior
            var matches = new List<ObjectMeta>();
            string needle = typeName.ToLowerInvariant();
            foreach (var kv in _byType)
            {
                if (kv.Key.ToString().ToLowerInvariant().Contains(needle))
                {
                    foreach (var id in kv.Value)
                        if (_objects.TryGetValue(id, out var m)) matches.Add(m);
                }
            }
            return matches;
        }

        public IEnumerable<ObjectMeta> ByNameSubstring(string substring)
        {
            // Case-insensitive substring match on the name index.
            // Index keys are full lowercase names; we still walk the keys, but typically
            // |distinct names| ≪ |objects|, so this is M-shaped not N-shaped.
            string needle = substring.ToLowerInvariant().Replace("*", "");
            if (string.IsNullOrEmpty(needle)) return Array.Empty<ObjectMeta>();
            var matches = new List<ObjectMeta>();
            foreach (var kv in _byNameLower)
            {
                if (kv.Key.Contains(needle))
                    foreach (var id in kv.Value)
                        if (_objects.TryGetValue(id, out var m)) matches.Add(m);
            }
            return matches;
        }

        public string LayerNameOf(ObjectMeta m) =>
            _layerNames.TryGetValue(m.LayerIndex, out var n) ? n : "";

        // ── Internal helpers ──────────────────────────────────────
        private void AddInternal(RhinoObject ro)
        {
            var meta = new ObjectMeta
            {
                Id = ro.Id,
                Type = ro.Geometry?.ObjectType ?? ObjectType.None,
                LayerIndex = ro.Attributes.LayerIndex,
                Name = ro.Attributes.Name ?? "",
                Bbox = ro.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty,
                Visible = ro.Visible,
                Locked = ro.IsLocked,
            };
            // Skip if already present (defensive against double-fire of AddRhinoObject).
            if (_objects.ContainsKey(meta.Id)) return;
            _objects[meta.Id] = meta;
            Interlocked.Increment(ref _count);

            if (!_byLayerIndex.TryGetValue(meta.LayerIndex, out var lset))
            {
                lset = new HashSet<Guid>();
                _byLayerIndex[meta.LayerIndex] = lset;
            }
            lset.Add(meta.Id);

            if (!_byType.TryGetValue(meta.Type, out var tset))
            {
                tset = new HashSet<Guid>();
                _byType[meta.Type] = tset;
            }
            tset.Add(meta.Id);

            AddToNameIndex(meta);
        }

        private void RemoveInternal(Guid id)
        {
            if (!_objects.TryGetValue(id, out var meta)) return;
            _objects.Remove(id);
            Interlocked.Decrement(ref _count);
            if (_byLayerIndex.TryGetValue(meta.LayerIndex, out var lset)) lset.Remove(id);
            if (_byType.TryGetValue(meta.Type, out var tset)) tset.Remove(id);
            RemoveFromNameIndex(meta);
        }

        private void AddToNameIndex(ObjectMeta m)
        {
            if (string.IsNullOrEmpty(m.Name)) return;
            var key = m.Name.ToLowerInvariant();
            if (!_byNameLower.TryGetValue(key, out var set))
            {
                set = new HashSet<Guid>();
                _byNameLower[key] = set;
            }
            set.Add(m.Id);
        }

        private void RemoveFromNameIndex(ObjectMeta m)
        {
            if (string.IsNullOrEmpty(m.Name)) return;
            var key = m.Name.ToLowerInvariant();
            if (_byNameLower.TryGetValue(key, out var set))
            {
                set.Remove(m.Id);
                if (set.Count == 0) _byNameLower.Remove(key);
            }
        }

        private void BumpVersion() => Interlocked.Increment(ref _sceneVersion);
    }
}
