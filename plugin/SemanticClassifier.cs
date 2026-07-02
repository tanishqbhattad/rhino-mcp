// RhinoAIBridge v4.5 -- SemanticClassifier.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoAIBridge
{
    public enum ArchType { Wall, Slab, Column, Core, FacadePanel, Opening, Stair, Massing, Generic, Unclassified }

    public class ClassifiedObject
    {
        public string Id;
        public ArchType Type;
        public int LevelIndex = -1;
        public BoundingBox BBox;
        public string Layer;
        public Vector3d Facing = Vector3d.Unset;
        public string Orientation = null;
    }

    public static class SemanticClassifier
    {
        private static int _cachedSceneVersion = -1;
        private static List<ClassifiedObject> _cachedResult = null;
        private static readonly object _cacheLock = new object();

        public static List<ClassifiedObject> Classify(RhinoDoc doc, bool forceRefresh = false)
        {
            if (doc == null) return new List<ClassifiedObject>();
            lock (_cacheLock)
            {
                int sv = (int)(SceneSnapshotRegistry.Active?.SceneVersion ?? -1);
                if (!forceRefresh && _cachedResult != null && _cachedSceneVersion == sv)
                    return _cachedResult;
                var result = new List<ClassifiedObject>();
                foreach (var obj in doc.Objects.Where(o => !o.IsDeleted && o.Visible))
                {
                    var bb = obj.Geometry?.GetBoundingBox(accurate: false) ?? BoundingBox.Unset;
                    if (!bb.IsValid) continue;
                    var layer = doc.Layers[obj.Attributes.LayerIndex]?.FullPath ?? "";
                    var atype = ClassifyObject(bb, layer);
                    var co = new ClassifiedObject { Id = obj.Id.ToString(), BBox = bb, Layer = layer, Type = atype };
                    if (atype == ArchType.Opening || atype == ArchType.FacadePanel || atype == ArchType.Wall)
                        co.Facing = DominantHorizontalNormal(obj.Geometry, bb);
                    result.Add(co);
                }
                AssignLevels(result);
                AssignOrientation(doc, result);
                _cachedResult = result;
                _cachedSceneVersion = sv;
                return result;
            }
        }

        private static ArchType ClassifyObject(BoundingBox bb, string layer)
        {
            var ll = layer.ToLowerInvariant();
            if (Has(ll, "wall"))                               return ArchType.Wall;
            if (Has(ll, "slab", "floor", "ceiling", "deck"))  return ArchType.Slab;
            if (Has(ll, "column", "col", "pillar"))            return ArchType.Column;
            if (Has(ll, "core"))                               return ArchType.Core;
            if (Has(ll, "facade", "cladding", "panel"))        return ArchType.FacadePanel;
            if (Has(ll, "window", "door", "opening", "glaz")) return ArchType.Opening;
            if (Has(ll, "stair", "ramp", "lift", "elev"))     return ArchType.Stair;
            if (Has(ll, "mass", "massing", "envelope"))        return ArchType.Massing;
            if (Has(ll, "generic"))                            return ArchType.Generic;

            double dx = bb.Max.X - bb.Min.X;
            double dy = bb.Max.Y - bb.Min.Y;
            double dz = bb.Max.Z - bb.Min.Z;
            double planMin = Math.Min(dx, dy), planMax = Math.Max(dx, dy);
            double planArea = dx * dy;

            if (dz < 600 && planArea > 5_000_000 && dz > 0 && planMax / dz > 5) return ArchType.Slab;
            if (dz > 1000 && dx < 2000 && dy < 2000 && Math.Abs(dx - dy) < 500 && dz > Math.Max(dx, dy) * 2) return ArchType.Column;
            if (dz > 1500 && planMin < 800 && planMax > 1500 && planMin > 0 && dz / planMin > 3) return ArchType.Wall;
            if (planMin < 400 && planArea < 20_000_000 && dz > 500) return ArchType.FacadePanel;
            if (dx > 3000 && dy > 3000 && dz > 3000) return ArchType.Massing;
            return ArchType.Unclassified;
        }

        private static bool Has(string layer, params string[] kws) => kws.Any(k => layer.Contains(k));

        private static void AssignLevels(List<ClassifiedObject> objects)
        {
            var zVals = objects
                .Where(o => o.Type == ArchType.Slab || (o.BBox.Max.Z - o.BBox.Min.Z) < 600)
                .Select(o => Math.Round(o.BBox.Min.Z / 100) * 100)
                .Distinct().OrderBy(z => z).ToList();
            var levels = new List<double>();
            foreach (var z in zVals)
                if (levels.Count == 0 || z - levels.Last() > 300) levels.Add(z);
            foreach (var co in objects)
            {
                double objZ = co.BBox.Min.Z; int best = -1; double bestDist = double.MaxValue;
                for (int i = 0; i < levels.Count; i++) { double d = Math.Abs(objZ - levels[i]); if (d < bestDist) { bestDist = d; best = i; } }
                if (best >= 0 && bestDist < 1500) co.LevelIndex = best;
            }
        }

        // v4.9: facing/orientation -- the missing piece for semantic selection
        // like "south-facing windows on level 3". +Y is treated as North, +X East.
        private static void AssignOrientation(RhinoDoc doc, List<ClassifiedObject> objects)
        {
            // volume-weighted centroid: structural bulk anchors 'outward'; stray/thin
            // objects barely move it, so opposite facades resolve to opposite compass dirs.
            double cx = 0, cy = 0, cz = 0, wsum = 0;
            foreach (var o in objects)
            {
                var s = o.BBox.Max - o.BBox.Min;
                double w = Math.Max(Math.Abs(s.X) * Math.Abs(s.Y) * Math.Abs(s.Z), 1.0);
                var c = o.BBox.Center;
                cx += c.X * w; cy += c.Y * w; cz += c.Z * w; wsum += w;
            }
            var bc = wsum > 0 ? new Point3d(cx / wsum, cy / wsum, cz / wsum) : Point3d.Origin;
            foreach (var co in objects)
            {
                if (co.Facing == Vector3d.Unset) continue;
                var outward = co.BBox.Center - bc; outward.Z = 0;
                if (co.Facing * outward < 0) co.Facing = -co.Facing;
                co.Orientation = BearingToCompass(co.Facing);
            }
        }

        // Largest near-vertical face normal (the glazing/wall plane), falling back to the
        // thinnest horizontal bbox axis for crude or boxy geometry.
        private static Vector3d DominantHorizontalNormal(GeometryBase geo, BoundingBox bb)
        {
            Vector3d best = Vector3d.Unset; double bestArea = 0;
            Brep brep = geo as Brep ?? (geo as Extrusion)?.ToBrep();
            if (brep != null)
            {
                foreach (var face in brep.Faces)
                {
                    var nrm = face.NormalAt(face.Domain(0).Mid, face.Domain(1).Mid);
                    var horiz = new Vector3d(nrm.X, nrm.Y, 0);
                    if (horiz.Length < 0.5) continue;
                    var d = face.GetBoundingBox(false).Diagonal;
                    double[] dims = { Math.Abs(d.X), Math.Abs(d.Y), Math.Abs(d.Z) };
                    Array.Sort(dims);
                    double area = dims[1] * dims[2];
                    if (area > bestArea) { bestArea = area; best = horiz; }
                }
            }
            if (best == Vector3d.Unset)
            {
                double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y;
                best = dx <= dy ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0);
            }
            best.Z = 0;
            if (best.Length < 1e-9) return Vector3d.Unset;
            best.Unitize();
            return best;
        }

        private static readonly string[] _compass = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        private static string BearingToCompass(Vector3d n)
        {
            double deg = Math.Atan2(n.X, n.Y) * 180.0 / Math.PI;
            if (deg < 0) deg += 360;
            return _compass[(int)Math.Round(deg / 45.0) % 8];
        }

        public static ArchType ParseType(string name)
        {
            var s = (name ?? "").ToLowerInvariant();
            if (s.Contains("wall")) return ArchType.Wall;
            if (s.Contains("slab") || s.Contains("floor") || s.Contains("ceiling")) return ArchType.Slab;
            if (s.Contains("column") || s.Contains("pillar")) return ArchType.Column;
            if (s.Contains("core")) return ArchType.Core;
            if (s.Contains("facade") || s.Contains("panel") || s.Contains("clad")) return ArchType.FacadePanel;
            if (s.Contains("window") || s.Contains("door") || s.Contains("opening") || s.Contains("glaz")) return ArchType.Opening;
            if (s.Contains("stair") || s.Contains("ramp")) return ArchType.Stair;
            if (s.Contains("mass") || s.Contains("envelope")) return ArchType.Massing;
            return ArchType.Generic;
        }

        private static string NormalizeOrientation(string o)
        {
            var s = (o ?? "").Trim().ToLowerInvariant().Replace("-facing", "").Replace(" facing", "").Trim();
            switch (s)
            {
                case "n": case "north": return "N";
                case "ne": case "northeast": case "north-east": return "NE";
                case "e": case "east": return "E";
                case "se": case "southeast": case "south-east": return "SE";
                case "s": case "south": return "S";
                case "sw": case "southwest": case "south-west": return "SW";
                case "w": case "west": return "W";
                case "nw": case "northwest": case "north-west": return "NW";
                default: return null;
            }
        }

        // Semantic query: filter classified objects by type + level + facing orientation.
        public static List<ClassifiedObject> Query(RhinoDoc doc, string type, int? level, string orientation)
        {
            var cl = Classify(doc);
            IEnumerable<ClassifiedObject> q = cl;
            if (!string.IsNullOrWhiteSpace(type) && type.ToLowerInvariant() != "all")
            {
                var t = ParseType(type);
                q = q.Where(c => c.Type == t);
            }
            if (level.HasValue) q = q.Where(c => c.LevelIndex == level.Value);
            if (!string.IsNullOrWhiteSpace(orientation))
            {
                var want = NormalizeOrientation(orientation);
                if (want != null) q = q.Where(c => c.Orientation == want);
            }
            return q.ToList();
        }

        public static JObject AnalyzeArchitecture(RhinoDoc doc)
        {
            var cl = Classify(doc);
            if (cl.Count == 0) return new JObject { ["status"] = "ok", ["message"] = "No classifiable geometry", ["total_objects"] = 0 };
            var levelArr = cl.Where(c => c.LevelIndex >= 0).GroupBy(c => c.LevelIndex).OrderBy(g => g.Key)
                .Select(g => new JObject { ["index"] = g.Key, ["elevation"] = Math.Round(g.Min(c => c.BBox.Min.Z), 0), ["object_count"] = g.Count() }).ToList();
            var systems = new JObject();
            foreach (ArchType t in Enum.GetValues(typeof(ArchType)))
            {
                var grp = cl.Where(c => c.Type == t).ToList();
                if (grp.Count == 0) continue;
                systems[t.ToString().ToLower()] = new JObject { ["count"] = grp.Count, ["ids"] = new JArray(grp.Select(c => c.Id)) };
            }
            return new JObject { ["status"] = "ok", ["total_objects"] = cl.Count, ["level_count"] = levelArr.Count,
                ["levels"] = new JArray(levelArr), ["systems"] = systems, ["grid"] = DetectGrid(cl),
                ["unclassified_ratio"] = cl.Count > 0 ? Math.Round((double)cl.Count(c => c.Type == ArchType.Unclassified) / cl.Count, 3) : 0 };
        }

        public static JObject GetBuildingSystems(RhinoDoc doc, string system)
        {
            var cl = Classify(doc);
            bool all = string.IsNullOrEmpty(system) || system == "all";
            var types = all ? Enum.GetValues(typeof(ArchType)).Cast<ArchType>().ToList() : MapSystem(system);
            var systems = new JObject();
            foreach (var t in types)
            {
                var grp = cl.Where(c => c.Type == t).ToList();
                if (grp.Count == 0 && !all) continue;
                systems[t.ToString().ToLower()] = new JArray(grp.Select(c => new JObject {
                    ["id"] = c.Id, ["level"] = c.LevelIndex, ["layer"] = c.Layer, ["orientation"] = c.Orientation,
                    ["size"] = new JArray { Math.Round(c.BBox.Max.X-c.BBox.Min.X,0), Math.Round(c.BBox.Max.Y-c.BBox.Min.Y,0), Math.Round(c.BBox.Max.Z-c.BBox.Min.Z,0) }
                }));
            }
            return new JObject { ["status"] = "ok", ["systems"] = systems };
        }

        public static JObject GetLevelSummary(RhinoDoc doc, int? levelIndex)
        {
            var cl = Classify(doc);
            var levels = new JArray();
            foreach (var g in cl.Where(c => c.LevelIndex >= 0).GroupBy(c => c.LevelIndex).OrderBy(g => g.Key))
            {
                if (levelIndex.HasValue && g.Key != levelIndex.Value) continue;
                var byType = new JObject();
                foreach (var t in g.GroupBy(c => c.Type)) byType[t.Key.ToString().ToLower()] = t.Count();
                levels.Add(new JObject { ["index"] = g.Key, ["elevation"] = Math.Round(g.Min(c => c.BBox.Min.Z),0), ["object_count"] = g.Count(), ["by_type"] = byType });
            }
            return new JObject { ["status"] = "ok", ["levels"] = levels };
        }

        public static JObject FindUnassigned(RhinoDoc doc, double minVolume)
        {
            var cl = Classify(doc);
            var items = cl.Where(c => c.Type == ArchType.Unclassified).Where(c => {
                var s = c.BBox.Max - c.BBox.Min; return s.X * s.Y * s.Z >= minVolume;
            }).Select(c => new JObject { ["id"] = c.Id, ["layer"] = c.Layer,
                ["size"] = new JArray { Math.Round(c.BBox.Max.X-c.BBox.Min.X,0), Math.Round(c.BBox.Max.Y-c.BBox.Min.Y,0), Math.Round(c.BBox.Max.Z-c.BBox.Min.Z,0) }
            }).ToList();
            return new JObject { ["status"] = "ok", ["count"] = items.Count, ["objects"] = new JArray(items) };
        }

        public static JObject DetectDesignPatterns(RhinoDoc doc)
        {
            var cl = Classify(doc);
            var modules = cl
                .GroupBy(c => $"{Math.Round((c.BBox.Max.X-c.BBox.Min.X)/100)*100}x{Math.Round((c.BBox.Max.Y-c.BBox.Min.Y)/100)*100}x{Math.Round((c.BBox.Max.Z-c.BBox.Min.Z)/100)*100}")
                .Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(5)
                .Select(g => new JObject { ["size_key"] = g.Key, ["count"] = g.Count() }).ToList();
            return new JObject { ["status"] = "ok", ["grid"] = DetectGrid(cl), ["repeated_modules"] = new JArray(modules),
                ["level_count"] = cl.Where(c => c.LevelIndex >= 0).Select(c => c.LevelIndex).Distinct().Count() };
        }

        private static JObject DetectGrid(List<ClassifiedObject> objects)
        {
            var cols = objects.Where(c => c.Type == ArchType.Column).ToList();
            if (cols.Count < 4) return new JObject { ["detected"] = false, ["reason"] = "need >= 4 columns" };
            var xs = cols.Select(c => Math.Round((c.BBox.Min.X+c.BBox.Max.X)/2/100)*100).OrderBy(x=>x).Distinct().ToList();
            var ys = cols.Select(c => Math.Round((c.BBox.Min.Y+c.BBox.Max.Y)/2/100)*100).OrderBy(y=>y).Distinct().ToList();
            double xSp = DomSpacing(xs), ySp = DomSpacing(ys);
            return new JObject { ["detected"] = xSp > 0 || ySp > 0, ["x_spacing"] = xSp, ["y_spacing"] = ySp, ["column_count"] = cols.Count };
        }

        private static double DomSpacing(List<double> sorted)
        {
            if (sorted.Count < 2) return 0;
            var gaps = sorted.Zip(sorted.Skip(1), (a,b) => b-a).Where(g => g > 100).ToList();
            if (gaps.Count == 0) return 0;
            return gaps.GroupBy(g => Math.Round(g/100)*100).OrderByDescending(g => g.Count()).First().Key;
        }

        private static List<ArchType> MapSystem(string name) => name?.ToLowerInvariant() switch {
            "structure"   => new List<ArchType> { ArchType.Column, ArchType.Slab, ArchType.Core },
            "envelope"    => new List<ArchType> { ArchType.Wall, ArchType.FacadePanel },
            "openings"    => new List<ArchType> { ArchType.Opening },
            "circulation" => new List<ArchType> { ArchType.Stair },
            _             => new List<ArchType> { ArchType.Unclassified }
        };
    }
}