// RhinoAIBridge v4.5 -- DesignMemory.cs
// Stores AI-generated metadata inside the .3dm file.
// Document-level data: RhinoDoc.Strings (key-value pairs, persisted in .3dm)
// Object-level data:   RhinoObject.Attributes.UserDictionary (ArchivableDictionary)

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoAIBridge
{
    public static class DesignMemory
    {
        // -- Document-level keys
        private const string K_BRIEF    = "ai:brief";
        private const string K_SESSIONS = "ai:sessions";
        private const string K_GROUPS   = "ai:groups";
        private const string K_RULES    = "ai:rules";

        // -- Object-level UserDictionary keys
        public const string OK_PROVENANCE = "ai_provenance";
        public const string OK_SESSION    = "ai_session";
        public const string OK_GROUP      = "ai_group";
        public const string OK_RELATIONS  = "ai_relations";
        public const string OK_LABEL      = "ai_label";
        public const string OK_RULE       = "ai_rule";

        private static RhinoDoc Doc => RhinoDoc.ActiveDoc;
        private static string CurrentSession => $"sess_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        private static readonly object _memoryLock = new object();

        // -- Document brief
        public static void SetBrief(string brief, bool append = false, bool allowEmpty = false)
        {
            if (Doc == null) return;
            lock (_memoryLock)
            {
                string existing = Doc.Strings.GetValue(K_BRIEF) ?? "";
                if (append)
                {
                    if (string.IsNullOrEmpty(brief)) return;
                    Doc.Strings.SetString(K_BRIEF, string.IsNullOrEmpty(existing) ? brief : existing + "\n" + brief);
                    return;
                }
                if (string.IsNullOrWhiteSpace(brief) && !allowEmpty && !string.IsNullOrEmpty(existing)) return;
                Doc.Strings.SetString(K_BRIEF, brief);
            }
        }
        public static string GetBrief() => Doc?.Strings.GetValue(K_BRIEF) ?? "";

        // -- Session log
        public static void AddSession(string summary)
        {
            if (Doc == null) return;
            lock (_memoryLock)
            {
                var log = GetSessionLog();
                log.Add(new JObject {
                    ["ts"]      = DateTime.UtcNow.ToString("o"),
                    ["summary"] = summary
                });
                while (log.Count > 50) log.RemoveAt(0);
                Doc.Strings.SetString(K_SESSIONS, log.ToString(Formatting.None));
            }
        }

        public static JArray GetSessionLog()
        {
            var raw = Doc?.Strings.GetValue(K_SESSIONS);
            if (string.IsNullOrEmpty(raw)) return new JArray();
            try { return JArray.Parse(raw); } catch { return new JArray(); }
        }

        // -- Named groups
        public static void NameGroup(string groupName, IEnumerable<string> ids)
        {
            if (Doc == null) return;
            lock (_memoryLock)
            {
                var groups = GetAllGroups();
                groups[groupName] = new JArray(ids);
                Doc.Strings.SetString(K_GROUPS, groups.ToString(Formatting.None));
            }
        }

        public static JArray GetGroup(string groupName)
        {
            var groups = GetAllGroups();
            return groups[groupName] as JArray ?? new JArray();
        }

        public static JObject GetAllGroups()
        {
            var raw = Doc?.Strings.GetValue(K_GROUPS);
            if (string.IsNullOrEmpty(raw)) return new JObject();
            try { return JObject.Parse(raw); } catch { return new JObject(); }
        }

        // -- Global rules
        public static void AddRule(string rule)
        {
            if (Doc == null) return;
            lock (_memoryLock)
            {
                var rules = GetRules();
                rules.Add(rule);
                Doc.Strings.SetString(K_RULES, rules.ToString(Formatting.None));
            }
        }

        public static JArray GetRules()
        {
            var raw = Doc?.Strings.GetValue(K_RULES);
            if (string.IsNullOrEmpty(raw)) return new JArray();
            try { return JArray.Parse(raw); } catch { return new JArray(); }
        }

        // -- Object tagging
        public static void TagObject(RhinoObject obj, Dictionary<string, string> tags)
        {
            if (obj == null) return;
            var ud = obj.Attributes.UserDictionary;
            foreach (var kv in tags)
                ud.Set(kv.Key, kv.Value);
            Doc?.Objects.ModifyAttributes(obj, obj.Attributes, quiet: true);
        }

        public static void AutoTag(RhinoObject obj, string tool, string paramsSummary, string label = null)
        {
            if (obj == null) return;
            var tags = new Dictionary<string, string>
            {
                [OK_PROVENANCE] = $"{tool} {paramsSummary}".Trim(),
                [OK_SESSION]    = CurrentSession,
            };
            if (!string.IsNullOrEmpty(label)) tags[OK_LABEL] = label;
            TagObject(obj, tags);
        }

        public static JObject GetObjectTags(RhinoObject obj)
        {
            if (obj == null) return new JObject();
            var ud = obj.Attributes.UserDictionary;
            var result = new JObject();
            foreach (var key in new[] { OK_PROVENANCE, OK_SESSION, OK_GROUP, OK_RELATIONS, OK_LABEL, OK_RULE })
            {
                if (ud.TryGetString(key, out string val))
                    result[key] = val;
            }
            return result;
        }

        // -- Search memory
        public static JArray SearchMemory(string query, RhinoDoc doc)
        {
            if (doc == null) return new JArray();
            var q = query.ToLowerInvariant();
            var hits = new JArray();

            var brief = GetBrief();
            if (brief.ToLowerInvariant().Contains(q))
                hits.Add(new JObject { ["source"] = "brief", ["text"] = brief });

            foreach (JObject s in GetSessionLog())
            {
                var sum = s["summary"]?.ToString() ?? "";
                if (sum.ToLowerInvariant().Contains(q))
                    hits.Add(new JObject { ["source"] = "session", ["ts"] = s["ts"], ["text"] = sum });
            }

            foreach (var kv in GetAllGroups())
            {
                if (kv.Key.ToLowerInvariant().Contains(q))
                    hits.Add(new JObject { ["source"] = "group", ["group"] = kv.Key, ["ids"] = kv.Value });
            }

            const int MAX_HITS = 50;
            foreach (var obj in doc.Objects.Where(o => !o.IsDeleted))
            {
                if (hits.Count >= MAX_HITS) break;
                var tags = GetObjectTags(obj);
                if (tags.Count == 0) continue;
                bool match = tags.Properties().Any(prop => prop.Value.ToString().ToLowerInvariant().Contains(q));
                if (match)
                    hits.Add(new JObject {
                        ["source"] = "object",
                        ["id"]     = obj.Id.ToString(),
                        ["tags"]   = tags
                    });
            }

            return hits;
        }

        // -- Related objects
        public static JArray GetRelatedObjects(RhinoObject obj, string relation, RhinoDoc doc)
        {
            if (obj == null || doc == null) return new JArray();
            var ud = obj.Attributes.UserDictionary;
            if (!ud.TryGetString(OK_RELATIONS, out string raw)) return new JArray();

            JObject rels;
            try { rels = JObject.Parse(raw); } catch { return new JArray(); }

            var results = new JArray();
            IEnumerable<(string rel, string id)> candidates;

            if (string.IsNullOrEmpty(relation))
            {
                candidates = rels.Properties().SelectMany(p =>
                {
                    var arr = p.Value as JArray ?? new JArray(p.Value);
                    return arr.Select(v => (rel: p.Name, id: v.ToString()));
                });
            }
            else
            {
                var arr = rels[relation] as JArray ?? new JArray();
                candidates = arr.Select(v => (rel: relation, id: v.ToString()));
            }

            foreach (var (rel, idStr) in candidates)
            {
                if (Guid.TryParse(idStr, out var g))
                {
                    var related = doc.Objects.FindId(g);
                    if (related != null)
                        results.Add(new JObject {
                            ["relation"] = rel,
                            ["id"]       = idStr,
                            ["type"]     = related.ObjectType.ToString()
                        });
                }
            }
            return results;
        }
    }
}
