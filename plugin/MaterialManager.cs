using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Newtonsoft.Json.Linq;

namespace RhinoAIBridge
{
    /// <summary>
    /// Handles applying downloaded PBR materials as Rhino render materials and editing existing materials.
    /// Called on the UI thread from CommandHandler.cs.
    /// </summary>
    public static class MaterialManager
    {
        // ─────────────────────────────────────────────────────────────────────────
        //  ApplyDownloadedMaterial
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new Rhino material from downloaded PBR texture maps and assigns it to a layer.
        /// </summary>
        public static JObject ApplyDownloadedMaterial(JObject p, RhinoDoc doc)
        {
            try
            {
                string layerName    = p["layer_name"]?.ToString() ?? string.Empty;
                string materialName = p["material_name"]?.ToString() ?? "AI_Material";
                JObject maps        = p["maps"] as JObject ?? new JObject();
                double uvRepeat     = p["uv_repeat"]?.ToObject<double>() ?? 1.0;
                string previewHex   = p["preview_color"]?.ToString();

                if (string.IsNullOrWhiteSpace(layerName))
                    return Error("layer_name is required");

                Layer layer = FindLayerByName(doc, layerName);
                if (layer == null)
                    return Error(string.Format("Layer '{0}' not found", layerName));

                var mat = new Rhino.DocObjects.Material();
                mat.Name = materialName;

                var mapsApplied = new List<string>();

                // Albedo / Diffuse
                string albedoPath = maps["albedo"]?.ToString();
                if (!string.IsNullOrEmpty(albedoPath) && File.Exists(albedoPath))
                {
                    var tex = BuildTexture(albedoPath, uvRepeat);
                    mat.SetTexture(tex, TextureType.Bitmap);
                    mapsApplied.Add("albedo");
                }
                else if (!string.IsNullOrEmpty(previewHex))
                {
                    try { mat.DiffuseColor = ParseColor(previewHex); }
                    catch { }
                }

                // Roughness stored in PBR_Roughness slot
                string roughnessPath = maps["roughness"]?.ToString();
                if (!string.IsNullOrEmpty(roughnessPath) && File.Exists(roughnessPath))
                {
                    var tex = BuildTexture(roughnessPath, uvRepeat);
                    mat.SetTexture(tex, TextureType.PBR_Roughness);
                    mapsApplied.Add("roughness");
                }

                // Normal map stored as Bump
                string normalPath = maps["normal"]?.ToString();
                if (!string.IsNullOrEmpty(normalPath) && File.Exists(normalPath))
                {
                    var tex = BuildTexture(normalPath, uvRepeat);
                    mat.SetTexture(tex, TextureType.Bump);
                    mapsApplied.Add("normal");
                }

                // Metallic stored in PBR_Metallic slot
                string metallicPath = maps["metallic"]?.ToString();
                if (!string.IsNullOrEmpty(metallicPath) && File.Exists(metallicPath))
                {
                    var tex = BuildTexture(metallicPath, uvRepeat);
                    mat.SetTexture(tex, TextureType.PBR_Metallic);
                    mapsApplied.Add("metallic");
                }

                // Ambient Occlusion stored in Ambient slot
                string aoPath = maps["ao"]?.ToString();
                if (!string.IsNullOrEmpty(aoPath) && File.Exists(aoPath))
                {
                    var tex = BuildTexture(aoPath, uvRepeat);
                    mat.SetTexture(tex, TextureType.PBR_AmbientOcclusion);
                    mapsApplied.Add("ao");
                }

                // Displacement stored in PBR_Displacement slot
                string dispPath = maps["displacement"]?.ToString();
                if (!string.IsNullOrEmpty(dispPath) && File.Exists(dispPath))
                {
                    var tex = BuildTexture(dispPath, uvRepeat);
                    mat.SetTexture(tex, TextureType.PBR_Displacement);
                    mapsApplied.Add("displacement");
                }

                int matIdx = doc.Materials.Add(mat);
                if (matIdx < 0)
                    return Error("Failed to add material to document");

                layer.RenderMaterialIndex = matIdx;
                doc.Layers.Modify(layer, layer.Index, false);
                doc.Views.Redraw();

                return new JObject
                {
                    ["status"]         = "ok",
                    ["material_name"]  = materialName,
                    ["material_index"] = matIdx,
                    ["layer"]          = layerName,
                    ["uv_repeat"]      = uvRepeat,
                    ["maps_applied"]   = new JArray(mapsApplied.Cast<object>().ToArray())
                };
            }
            catch (Exception ex)
            {
                return Error("ApplyDownloadedMaterial exception: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  EditMaterial
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Edits properties of an existing Rhino material found by layer name or material name.
        /// </summary>
        public static JObject EditMaterial(JObject p, RhinoDoc doc)
        {
            try
            {
                string layerName    = p["layer_name"]?.ToString();
                string materialName = p["material_name"]?.ToString();

                int matIdx = -1;

                if (!string.IsNullOrEmpty(layerName))
                {
                    Layer layer = FindLayerByName(doc, layerName);
                    if (layer == null)
                        return Error(string.Format("Layer '{0}' not found", layerName));
                    matIdx = layer.RenderMaterialIndex;
                }
                else if (!string.IsNullOrEmpty(materialName))
                {
                    for (int i = 0; i < doc.Materials.Count; i++)
                    {
                        var m = doc.Materials[i];
                        if (m != null && string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase))
                        {
                            matIdx = i;
                            break;
                        }
                    }
                }

                if (matIdx < 0)
                    return Error("Material not found -- provide layer_name or material_name");

                var mat = doc.Materials[matIdx];
                if (mat == null)
                    return Error(string.Format("Material at index {0} is null", matIdx));

                var updated = new Dictionary<string, object>();

                // Roughness
                if (p["roughness"] != null)
                {
                    double r = p["roughness"].ToObject<double>();
                    r = Math.Max(0.0, Math.Min(1.0, r));
                    mat.ReflectionGlossiness = 1.0 - r;
                    updated["roughness"] = r;
                }

                // Metallic
                if (p["metallic"] != null)
                {
                    double metal = p["metallic"].ToObject<double>();
                    metal = Math.Max(0.0, Math.Min(1.0, metal));
                    mat.Reflectivity = metal;
                    updated["metallic"] = metal;
                }

                // Diffuse color
                if (p["diffuse_color"] != null)
                {
                    Color c = ParseColor(p["diffuse_color"].ToString());
                    mat.DiffuseColor = c;
                    updated["diffuse_color"] = ColorToHex(c);
                }

                // Transparency
                if (p["transparency"] != null)
                {
                    double t = p["transparency"].ToObject<double>();
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    mat.Transparency = t;
                    updated["transparency"] = t;
                }

                // Emission color
                if (p["emission_color"] != null)
                {
                    Color ec = ParseColor(p["emission_color"].ToString());
                    mat.EmissionColor = ec;
                    updated["emission_color"] = ColorToHex(ec);
                }

                // Emission intensity (encode as gray emission color if emission_color not also provided)
                if (p["emission_intensity"] != null && p["emission_color"] == null)
                {
                    double intensity = p["emission_intensity"].ToObject<double>();
                    Color cur = mat.EmissionColor;
                    int v = (int)Math.Max(0, Math.Min(255, intensity * 255.0));
                    mat.EmissionColor = Color.FromArgb(cur.A, v, v, v);
                    updated["emission_intensity"] = intensity;
                }

                // Texture scale (UV repeat multiplier)
                if (p["texture_scale"] != null)
                {
                    double scale = p["texture_scale"].ToObject<double>();
                    ApplyTextureTransform(mat, scale, null);
                    updated["texture_scale"] = scale;
                }

                // Texture rotation
                if (p["texture_rotation"] != null)
                {
                    double degrees = p["texture_rotation"].ToObject<double>();
                    ApplyTextureTransform(mat, null, degrees);
                    updated["texture_rotation"] = degrees;
                }

                bool saved = doc.Materials.Modify(mat, matIdx, false);
                if (!saved)
                    return Error("doc.Materials.Modify returned false");

                doc.Views.Redraw();

                var updatedJson = new JObject();
                foreach (var kv in updated)
                    updatedJson[kv.Key] = JToken.FromObject(kv.Value);

                return new JObject
                {
                    ["status"]             = "ok",
                    ["material_name"]      = mat.Name,
                    ["material_index"]     = matIdx,
                    ["updated_properties"] = updatedJson
                };
            }
            catch (Exception ex)
            {
                return Error("EditMaterial exception: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  ListMaterials
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all materials in the document: index, name, diffuse_color, has_texture,
        /// and which layers are assigned to each material.
        /// </summary>
        public static JObject ListMaterials(JObject p, RhinoDoc doc)
        {
            try
            {
                bool dedupe  = p["dedupe"]?.ToObject<bool>() ?? false;
                int  limit   = p["limit"]?.ToObject<int>()  ?? 500;
                int  offset  = p["offset"]?.ToObject<int>() ?? 0;
                bool inclObj = p["include_object_materials"]?.ToObject<bool>() ?? false;

                // Build material-index to layer-names map
                var layersByMat = new Dictionary<int, List<string>>();
                foreach (Layer layer in doc.Layers)
                {
                    if (layer == null || layer.IsDeleted) continue;
                    int idx2 = layer.RenderMaterialIndex;
                    if (idx2 >= 0)
                    {
                        if (!layersByMat.ContainsKey(idx2))
                            layersByMat[idx2] = new List<string>();
                        layersByMat[idx2].Add(layer.FullPath);
                    }
                }

                // Optionally collect per-object material indices
                var objectMatIndices = new HashSet<int>();
                if (inclObj)
                {
                    foreach (var obj in doc.Objects)
                    {
                        if (obj == null || obj.IsDeleted) continue;
                        int mi = obj.Attributes.MaterialIndex;
                        if (mi >= 0) objectMatIndices.Add(mi);
                    }
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var materialList = new List<JObject>();

                for (int i = 0; i < doc.Materials.Count; i++)
                {
                    var mat = doc.Materials[i];
                    if (mat == null || mat.IsDeleted) continue;

                    bool hasTexture = mat.GetTexture(TextureType.Bitmap) != null
                                   || mat.GetTexture(TextureType.Bump) != null;

                    List<string> assignedLayers;
                    layersByMat.TryGetValue(i, out assignedLayers);
                    bool assignedToObject = objectMatIndices.Contains(i);

                    // Dedup by name: skip if a material with same name already added
                    if (dedupe && !string.IsNullOrEmpty(mat.Name))
                    {
                        if (!seen.Add(mat.Name)) continue;
                    }

                    materialList.Add(new JObject
                    {
                        ["index"]              = i,
                        ["name"]               = mat.Name,
                        ["diffuse_color"]      = ColorToHex(mat.DiffuseColor),
                        ["has_texture"]        = hasTexture,
                        ["assigned_layers"]    = new JArray((assignedLayers ?? new List<string>()).Cast<object>().ToArray()),
                        ["assigned_to_object"] = assignedToObject,
                    });
                }

                int total = materialList.Count;
                var page  = materialList.Skip(offset).Take(limit).ToList();

                return new JObject
                {
                    ["status"]    = "ok",
                    ["total"]     = total,
                    ["offset"]    = offset,
                    ["limit"]     = limit,
                    ["count"]     = page.Count,
                    ["materials"] = new JArray(page.Cast<object>().ToArray()),
                };
            }
            catch (Exception ex)
            {
                return Error("ListMaterials exception: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  GetMaterial
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns full properties of a single material identified by layer name or material index.
        /// </summary>
        public static JObject GetMaterial(JObject p, RhinoDoc doc)
        {
            try
            {
                string layerName = p["layer_name"]?.ToString();
                int matIdx = -1;

                if (!string.IsNullOrEmpty(layerName))
                {
                    Layer layer = FindLayerByName(doc, layerName);
                    if (layer == null)
                        return Error(string.Format("Layer '{0}' not found", layerName));
                    matIdx = layer.RenderMaterialIndex;
                }
                else if (p["material_index"] != null)
                {
                    matIdx = p["material_index"].ToObject<int>();
                }

                if (matIdx < 0 || matIdx >= doc.Materials.Count)
                    return Error("Material not found -- provide layer_name or material_index");

                var mat = doc.Materials[matIdx];
                if (mat == null)
                    return Error(string.Format("Material at index {0} is null or deleted", matIdx));

                var texturesJson = new JObject();

                // Value tuples require a language version flag in older csproj setups;
                // use explicit structs to stay safe.
                var textureSlots = new TextureSlotInfo[]
                {
                    new TextureSlotInfo(TextureType.Bitmap,               "albedo"),
                    new TextureSlotInfo(TextureType.Bump,                 "normal_or_bump"),
                    new TextureSlotInfo(TextureType.PBR_Roughness,        "roughness"),
                    new TextureSlotInfo(TextureType.PBR_Metallic,         "metallic"),
                    new TextureSlotInfo(TextureType.PBR_AmbientOcclusion, "ao"),
                    new TextureSlotInfo(TextureType.PBR_Displacement,     "displacement"),
                };

                double uvRepeatU = 1.0;
                double uvRepeatV = 1.0;

                foreach (var slot in textureSlots)
                {
                    var tex = mat.GetTexture(slot.Type);
                    if (tex == null) continue;

                    Transform xf = tex.UvwTransform;
                    double su = xf.M00;
                    double sv = xf.M11;

                    if (slot.Type == TextureType.Bitmap)
                    {
                        uvRepeatU = su;
                        uvRepeatV = sv;
                    }

                    texturesJson[slot.Key] = new JObject
                    {
                        ["path"]        = tex.FileName,
                        ["uv_repeat_u"] = su,
                        ["uv_repeat_v"] = sv
                    };
                }

                return new JObject
                {
                    ["status"]         = "ok",
                    ["material_index"] = matIdx,
                    ["name"]           = mat.Name,
                    ["diffuse_color"]  = ColorToHex(mat.DiffuseColor),
                    ["specular_color"] = ColorToHex(mat.SpecularColor),
                    ["emission_color"] = ColorToHex(mat.EmissionColor),
                    ["ambient_color"]  = ColorToHex(mat.AmbientColor),
                    ["transparency"]   = mat.Transparency,
                    ["reflectivity"]   = mat.Reflectivity,
                    ["glossiness"]     = mat.ReflectionGlossiness,
                    ["roughness"]      = 1.0 - mat.ReflectionGlossiness,
                    ["bump_scale"] = 1.0,
                    ["uv_repeat_u"]    = uvRepeatU,
                    ["uv_repeat_v"]    = uvRepeatV,
                    ["textures"]       = texturesJson
                };
            }
            catch (Exception ex)
            {
                return Error("GetMaterial exception: " + ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Public Helper Methods
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds a layer by full path or short name (case-insensitive).
        /// </summary>
        public static Layer FindLayerByName(RhinoDoc doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            int idx = doc.Layers.FindByFullPath(name, -1);
            if (idx >= 0) return doc.Layers[idx];

            foreach (Layer layer in doc.Layers)
            {
                if (layer == null || layer.IsDeleted) continue;
                if (string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase))
                    return layer;
                if (string.Equals(layer.FullPath, name, StringComparison.OrdinalIgnoreCase))
                    return layer;
            }
            return null;
        }

        /// <summary>
        /// Parses a hex color string ("#RRGGBB" or "#AARRGGBB") into a System.Drawing.Color.
        /// Returns Color.White on failure.
        /// </summary>
        public static Color ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.White;
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            if (hex.Length == 8)
            {
                int a = Convert.ToInt32(hex.Substring(0, 2), 16);
                int r = Convert.ToInt32(hex.Substring(2, 2), 16);
                int g = Convert.ToInt32(hex.Substring(4, 2), 16);
                int b = Convert.ToInt32(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            return Color.White;
        }

        /// <summary>
        /// Converts a System.Drawing.Color to a "#RRGGBB" hex string.
        /// </summary>
        public static string ColorToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Private Helpers
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Constructs a Rhino.DocObjects.Texture with repeating wrap and UV scale set via UvwTransform.
        /// </summary>
        public static Rhino.DocObjects.Texture BuildTexture(string filePath, double uvRepeat)
        {
            var tex = new Rhino.DocObjects.Texture();
            tex.FileName     = filePath;
            tex.WrapU        = Rhino.DocObjects.TextureUvwWrapping.Repeat;
            tex.WrapV        = Rhino.DocObjects.TextureUvwWrapping.Repeat;
            var uvXf = Transform.Identity;
            uvXf.M00 = uvRepeat;
            uvXf.M11 = uvRepeat;
            tex.UvwTransform = uvXf;
            return tex;
        }

        /// <summary>
        /// Pre-multiplies a scale and/or rotation transform onto every texture slot of a material.
        /// Pass null for either to skip that component.
        /// </summary>
        private static void ApplyTextureTransform(
            Rhino.DocObjects.Material mat,
            double? scaleMult,
            double? rotationDeg)
        {
            var slots = new TextureType[]
            {
                TextureType.Bitmap,
                TextureType.Bump,
                TextureType.PBR_Roughness,
                TextureType.PBR_Metallic,
                TextureType.PBR_AmbientOcclusion,
                TextureType.PBR_Displacement
            };

            foreach (var slot in slots)
            {
                var tex = mat.GetTexture(slot);
                if (tex == null) continue;

                Transform current = tex.UvwTransform;

                if (scaleMult.HasValue)
                {
                    var scaleXf = Transform.Identity;
                    scaleXf.M00 = scaleMult.Value;
                    scaleXf.M11 = scaleMult.Value;
                    current = scaleXf * current;
                }

                if (rotationDeg.HasValue)
                {
                    double radians = rotationDeg.Value * (Math.PI / 180.0);
                    Transform rotXf = Transform.Rotation(radians, Vector3d.ZAxis, Point3d.Origin);
                    current = rotXf * current;
                }

                tex.UvwTransform = current;
                mat.SetTexture(tex, slot);
            }
        }

        /// <summary>
        /// Builds a standard error response JObject.
        /// </summary>
        private static JObject Error(string message)
        {
            return new JObject
            {
                ["status"]  = "error",
                ["message"] = message
            };
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Inner helper type (avoids C# 7 value-tuple dependency)
        // ─────────────────────────────────────────────────────────────────────────

        private struct TextureSlotInfo
        {
            public readonly TextureType Type;
            public readonly string Key;

            public TextureSlotInfo(TextureType type, string key)
            {
                Type = type;
                Key  = key;
            }
        }
    }
}
