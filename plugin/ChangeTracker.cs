// RhinoAIBridge v4.5 -- ChangeTracker.cs
// Object-level change tracking. Thread-safe circular buffer (1000 events).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoAIBridge
{
    public enum ChangeType { Added, Deleted, Modified, AttributeChanged }

    public class ChangeEvent
    {
        public int Version;
        public DateTime Timestamp;
        public string ObjectId;
        public ChangeType Change;
        public string ObjectType;
        public string Layer;
    }

    public static class ChangeTracker
    {
        private const int MAX_EVENTS = 1000;
        private static readonly List<ChangeEvent> _log = new List<ChangeEvent>(MAX_EVENTS + 10);
        private static readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private static int _version = 0;
        private static bool _initialized = false;

        public static int CurrentVersion => _version;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            RhinoDoc.AddRhinoObject     += (s, e) => Record(e.ObjectId.ToString(), ChangeType.Added,    e.TheObject, e.TheObject?.Document);
            RhinoDoc.DeleteRhinoObject  += (s, e) => Record(e.ObjectId.ToString(), ChangeType.Deleted,  null,        e.TheObject?.Document);
            RhinoDoc.UndeleteRhinoObject += (s, e) => Record(e.ObjectId.ToString(), ChangeType.Added,   e.TheObject, e.TheObject?.Document);
            RhinoDoc.ReplaceRhinoObject += (s, e) => {
                var obj = e.NewRhinoObject ?? e.OldRhinoObject;
                var id  = (e.NewRhinoObject?.Id ?? (e.OldRhinoObject?.Id ?? Guid.Empty)).ToString();
                var doc = obj?.Document;
                Record(id, ChangeType.Modified, e.NewRhinoObject, doc);
            };
            RhinoDoc.ModifyObjectAttributes += (s, e) => {
                var obj = e.RhinoObject;
                if (obj != null)
                    Record(obj.Id.ToString(), ChangeType.AttributeChanged, obj, obj.Document);
            };
        }

        private static void Record(string id, ChangeType ct, RhinoObject obj, RhinoDoc doc)
        {
            if (string.IsNullOrEmpty(id)) return;
            var ev = new ChangeEvent
            {
                Version    = Interlocked.Increment(ref _version),
                Timestamp  = DateTime.UtcNow,
                ObjectId   = id,
                Change     = ct,
                ObjectType = obj?.ObjectType.ToString() ?? "Unknown",
                Layer      = (obj != null && doc != null)
                    ? (doc.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "")
                    : ""
            };
            _lock.EnterWriteLock();
            try
            {
                _log.Add(ev);
                if (_log.Count > MAX_EVENTS) _log.RemoveRange(0, _log.Count - MAX_EVENTS);
            }
            finally { _lock.ExitWriteLock(); }
        }

        public static (JArray added, JArray deleted, JArray modified, int toVersion, bool truncated) GetDiff(int fromVersion)
        {
            _lock.EnterReadLock();
            List<ChangeEvent> events; int toVer; bool truncated;
            try
            {
                events = _log.Where(e => e.Version > fromVersion).ToList();
                toVer = _version;
                // The circular buffer holds MAX_EVENTS. If events older than fromVersion
                // were evicted, the diff is INCOMPLETE — callers must fall back to a full
                // re-query instead of trusting a silently partial answer.
                int oldestTracked = _log.Count > 0 ? _log[0].Version : _version + 1;
                truncated = fromVersion + 1 < oldestTracked && fromVersion < _version;
            }
            finally { _lock.ExitReadLock(); }

            var latest = events.GroupBy(e => e.ObjectId).Select(g => g.OrderByDescending(e => e.Version).First()).ToList();
            var added = new JArray(); var deleted = new JArray(); var modified = new JArray();
            foreach (var ev in latest)
            {
                switch (ev.Change)
                {
                    case ChangeType.Added:
                        added.Add(new JObject { ["id"] = ev.ObjectId, ["type"] = ev.ObjectType, ["layer"] = ev.Layer });
                        break;
                    case ChangeType.Deleted:
                        deleted.Add(new JObject { ["id"] = ev.ObjectId });
                        break;
                    default:
                        modified.Add(new JObject { ["id"] = ev.ObjectId, ["type"] = ev.ObjectType, ["layer"] = ev.Layer, ["changes"] = new JArray("geometry") });
                        break;
                }
            }
            return (added, deleted, modified, toVer, truncated);
        }

        public static JArray GetLog(int limit = 50, int sinceVersion = 0)
        {
            _lock.EnterReadLock();
            List<ChangeEvent> events;
            try { events = _log.Where(e => e.Version > sinceVersion).OrderByDescending(e => e.Version).Take(limit).ToList(); }
            finally { _lock.ExitReadLock(); }
            var result = new JArray();
            foreach (var ev in events)
                result.Add(new JObject { ["version"] = ev.Version, ["ts"] = ev.Timestamp.ToString("o"),
                    ["id"] = ev.ObjectId, ["change"] = ev.Change.ToString(), ["type"] = ev.ObjectType, ["layer"] = ev.Layer });
            return result;
        }
    }
}
