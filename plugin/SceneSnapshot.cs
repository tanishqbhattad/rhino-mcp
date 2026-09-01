// RhinoAIBridge v4.8 - SceneSnapshot.cs
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
    /// Read tools become O(1) or O(M) where M is the result size, not the scene.
    ///
    /// Threading (v4.8 / protocol 5):
    ///   Writers are Rhino doc events - always the UI thread. Readers are now BOTH the
    ///   UI thread (dispatched commands) AND TCP worker threads (multiplexed snapshot
    ///   reads answer without a UI hop, so they stay sub-ms while a long script runs).
    ///   A ReaderWriterLockSlim guards every index; SceneVersion/Count additionally
    ///   stay Interlocked/Volatile so ping never takes the lock.
    ///
    /// v4.8 also caches per-object validity (IsValidGeo/IsSolid), filled lazily by
    /// validate_objects and invalidated on geometry replace - so whole-scene validation
    /// no longer needs a hard 100-object cap.
    /// </summary>
    public sealed class SceneSnapshot
    {
        // ── Public versioned identity ─────────────────────────────
        public uint DocSerial { get; }
        public string DocName { get; private set; }
        private long _sceneVersion;
        /// <summary>Monotonic counter - incremented on every mutation. Cross-thread safe.</summary>
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
            // v4.8: lazy validity cache. null = not yet computed. Cleared on replace.
            public bool? IsValidGeo;
            public bool? IsSolid;
        }

        // ── Indexes (guarded by _rw) ──────────────────────────────
        private readonly ReaderWriterLockSlim _rw = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly Dictionary<Guid, ObjectMeta> _objects = new();
        private readonly Dictionary<int, HashSet<Guid>> _byLayerIndex = new();
        private readonly Dictionary<ObjectType, HashSet<Guid>> _byType = new();
        private readonly Dictionary<string, HashSet<Guid>> _byNameLower = new();

        // Atomic count mirror - readable from non-UI threads without the lock (ping).
        private int _count;
        public int Count => Volatile.Read(ref _count);

        // ── Cached aggregates ─────────────────────────────────────
        private BoundingBox _sceneBbox = BoundingBox.Empty;
        private bool _bboxDirty = true;

        private readonly Dictionary<int, string> _layerNames = new();

        // ── Construction & rebuild ────────────────────────────────
        public SceneSnapshot(RhinoDoc doc)
        {
            DocSerial = doc.RuntimeSerialNumber;
            Rebuild(doc);
        }

        public void Rebuild(RhinoDoc doc)
        {
            _rw.EnterWriteLock();
            try
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
            }
            finally { _rw.ExitWriteLock(); }
            BumpVersion();
        }

        // ── Event-driven updates (UI thread) ──────────────────────

        public void OnAdded(RhinoObject ro)
        {
            if (ro == null) return;
            _rw.EnterWriteLock();
            try { AddInternal(ro); _bboxDirty = true; }
            finally { _rw.ExitWriteLock(); }
            BumpVersion();
        }

        public void OnDeleted(RhinoObject ro)
        {
            if (ro == null) return;
            _rw.EnterWriteLock();
            try { RemoveInternal(ro.Id); _bboxDirty = true; }
            finally { _rw.ExitWriteLock(); }
            BumpVersion();
        }

        public void OnUndeleted(RhinoObject ro)
        {
            if (ro == null) return;
            _rw.EnterWriteLock();
            try { RemoveInternal(ro.Id); AddInternal(ro); _bboxDirty = true; }
            finally { _rw.ExitWriteLock(); }
            BumpVersion();
        }

        public void OnReplaced(RhinoObject newObj)
        {
            // ReplaceRhinoObject preserves the GUID. Geometry/bbox/type may have changed.
            if (newObj == null) return;
            _rw.EnterWriteLock();
            try
            {
                if (_objects.TryGetValue(newObj.Id, out var meta))
                {
                    meta.Bbox = newObj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
                    meta.IsValidGeo = null;   // validity unknown after replace
                    meta.IsSolid = null;
                    var newType = newObj.Geometry?.ObjectType ?? meta.Type;
                    if (newType != meta.Type)
                    {
                        // Keep the type index consistent - a replace can change geometry type
                        // (e.g. curve -> brep), and a stale index made type queries miss objects.
                        if (_byType.TryGetValue(meta.Type, out var oldSet)) oldSet.Remove(meta.Id);
                        if (!_byType.TryGetValue(newType, out var newSet))
                        {
                            newSet = new HashSet<Guid>();
                            _byType[newType] = newSet;
                        }
                        newSet.Add(meta.Id);
                        meta.Type = newType;
                    }
                }
                else
                {
                    AddInternal(newObj);
                }
                _bboxDirty = true;
            }
            finally { _rw.ExitWriteLock(); }
            BumpVersion();
        }

        /// <summary>
        /// ModifyObjectAttributes is the noisiest event - re-index only when name or
        /// layer changes; bump the version for any meaningful state change.
        /// </summary>
        public void OnAttributesModified(RhinoObject ro)
        {
            if (ro == null) return;
            bool changed = false;
            _rw.EnterWriteLock();
            try
            {
                if (!_objects.TryGetValue(ro.Id, out var meta))
                {
                    AddInternal(ro);
                    changed = true;
                }
                else
                {
                    int newLayer = ro.Attributes.LayerIndex;
                    string newName = ro.Attributes.Name ?? "";

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

                    bool newVisible = ro.Visible;
                    bool newLocked = ro.IsLocked;
                    if (newVisible != meta.Visible || newLocked != meta.Locked)
                    {
                        meta.Visible = newVisible;
                        meta.Locked = newLocked;
                        changed = true;
                    }
                }
            }
            finally { _rw.ExitWriteLock(); }
            if (changed) BumpVersion();
        }

        public void OnLayerTableChanged(RhinoDoc doc)
        {
            _rw.EnterWriteLock();
            try
            {
                _layerNames.Clear();
                foreach (var l in doc.Layers.Where(x => !x.IsDeleted))
                    _layerNames[l.Index] = l.FullPath ?? l.Name;
            }
            finally { _rw.ExitWriteLock(); }
            BumpVersion();
        }

        // ── Read API (UI thread OR TCP worker threads) ────────────

        /// <summary>Materialized copy - safe to enumerate after the lock is released.</summary>
        public List<ObjectMeta> All()
        {
            _rw.EnterReadLock();
            try { return _objects.Values.ToList(); }
            finally { _rw.ExitReadLock(); }
        }

        public ObjectMeta TryGet(Guid id)
        {
            _rw.EnterReadLock();
            try { return _objects.TryGetValue(id, out var m) ? m : null; }
            finally { _rw.ExitReadLock(); }
        }

        public BoundingBox SceneBoundingBox()
        {
            _rw.EnterUpgradeableReadLock();
            try
            {
                if (!_bboxDirty) return _sceneBbox;
                _rw.EnterWriteLock();
                try
                {
                    var bb = BoundingBox.Empty;
                    foreach (var m in _objects.Values)
                        if (m.Bbox.IsValid) bb.Union(m.Bbox);
                    _sceneBbox = bb;
                    _bboxDirty = false;
                    return bb;
                }
                finally { _rw.ExitWriteLock(); }
            }
            finally { _rw.ExitUpgradeableReadLock(); }
        }

        /// <summary>
        /// Per-layer object counts keyed by layer INDEX.
        ///
        /// v4.14 (field report A1): CountsByLayerName() keys by FullPath while callers
        /// looked up by leaf Name, so every nested layer reported object_count 0 - and
        /// duplicate leaf names overwrote each other in the dictionary. Index is the only
        /// unambiguous key.
        /// </summary>
        public Dictionary<int, int> CountsByLayerIndex()
        {
            _rw.EnterReadLock();
            try
            {
                var d = new Dictionary<int, int>(_byLayerIndex.Count);
                foreach (var kv in _byLayerIndex) d[kv.Key] = kv.Value.Count;
                return d;
            }
            finally { _rw.ExitReadLock(); }
        }

        public Dictionary<string, int> CountsByLayerName()
        {
            _rw.EnterReadLock();
            try
            {
                var d = new Dictionary<string, int>(_byLayerIndex.Count);
                foreach (var kv in _byLayerIndex)
                {
                    var name = _layerNames.TryGetValue(kv.Key, out var n) ? n : $"layer_{kv.Key}";
                    d[name] = kv.Value.Count;
                }
                return d;
            }
            finally { _rw.ExitReadLock(); }
        }

        public Dictionary<string, int> CountsByType()
        {
            _rw.EnterReadLock();
            try
            {
                var d = new Dictionary<string, int>(_byType.Count);
                foreach (var kv in _byType)
                    d[kv.Key.ToString()] = kv.Value.Count;
                return d;
            }
            finally { _rw.ExitReadLock(); }
        }

        /// <summary>
        /// Objects on a layer AND, by default, all of its descendants.
        ///
        /// v4.14 (field report A2): this used to resolve to ONE layer index - exact
        /// full-path match, else the FIRST layer whose leaf name matched. Two bugs fell
        /// out of that in real models, where nested trees are the norm and leaf names
        /// repeat (two "Piers" layers under different parents):
        ///   - "by_layer:NotreDame::03_Facade" returned nothing, because every object
        ///     actually lives on a CHILD of that layer;
        ///   - a bare leaf name silently picked whichever duplicate came first.
        /// Now every matching layer and its subtree contribute.
        /// </summary>
        public List<ObjectMeta> ByLayerName(string layerName, bool includeDescendants = true)
        {
            _rw.EnterReadLock();
            try
            {
                var indices = new List<int>();
                string prefix = layerName + "::";
                foreach (var kv in _layerNames)
                {
                    if (string.Equals(kv.Value, layerName, StringComparison.OrdinalIgnoreCase) ||
                        (includeDescendants && kv.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        indices.Add(kv.Key);
                }
                if (indices.Count == 0)
                {
                    // Fall back to leaf-name matching - but take EVERY match, not the first.
                    foreach (var kv in _layerNames)
                    {
                        var leaf = kv.Value;
                        int sep = leaf.LastIndexOf("::", StringComparison.Ordinal);
                        if (sep >= 0) leaf = leaf.Substring(sep + 2);
                        if (string.Equals(leaf, layerName, StringComparison.OrdinalIgnoreCase))
                        {
                            indices.Add(kv.Key);
                            if (includeDescendants)
                            {
                                string sub = kv.Value + "::";
                                foreach (var kv2 in _layerNames)
                                    if (kv2.Value.StartsWith(sub, StringComparison.OrdinalIgnoreCase))
                                        indices.Add(kv2.Key);
                            }
                        }
                    }
                }
                if (indices.Count == 0) return new List<ObjectMeta>();

                var seen = new HashSet<Guid>();
                var result = new List<ObjectMeta>();
                foreach (var idx in indices)
                {
                    if (!_byLayerIndex.TryGetValue(idx, out var ids)) continue;
                    foreach (var id in ids)
                        if (seen.Add(id) && _objects.TryGetValue(id, out var m) && m != null)
                            result.Add(m);
                }
                return result;
            }
            finally { _rw.ExitReadLock(); }
        }

        public List<ObjectMeta> ByType(string typeName)
        {
            _rw.EnterReadLock();
            try
            {
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
            finally { _rw.ExitReadLock(); }
        }

        public List<ObjectMeta> ByNameSubstring(string substring)
        {
            _rw.EnterReadLock();
            try
            {
                string needle = substring.ToLowerInvariant().Replace("*", "");
                if (string.IsNullOrEmpty(needle)) return new List<ObjectMeta>();
                var matches = new List<ObjectMeta>();
                foreach (var kv in _byNameLower)
                {
                    if (kv.Key.Contains(needle))
                        foreach (var id in kv.Value)
                            if (_objects.TryGetValue(id, out var m)) matches.Add(m);
                }
                return matches;
            }
            finally { _rw.ExitReadLock(); }
        }

        public string LayerNameOf(ObjectMeta m)
        {
            _rw.EnterReadLock();
            try { return _layerNames.TryGetValue(m.LayerIndex, out var n) ? n : ""; }
            finally { _rw.ExitReadLock(); }
        }

        /// <summary>Store computed validity for an object (UI thread, from validate_objects).</summary>
        public void SetValidity(Guid id, bool isValid, bool isSolid)
        {
            _rw.EnterWriteLock();
            try
            {
                if (_objects.TryGetValue(id, out var m)) { m.IsValidGeo = isValid; m.IsSolid = isSolid; }
            }
            finally { _rw.ExitWriteLock(); }
        }

        // ── Internal helpers (call under write lock) ──────────────
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
