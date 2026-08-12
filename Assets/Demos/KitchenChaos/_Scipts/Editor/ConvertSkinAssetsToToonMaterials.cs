#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kitchen.EditorTools
{
    /// <summary>
    /// Batch-convert SkinAsset (Counters / Ingredients / Characters) materials to TCP2 Hybrid
    /// using the user's Blender demo mats as the style template. All toon mats live under
    /// Assets/Resources/So/Skin/SkinAsset/ToonMaterials/.
    /// </summary>
    public static class ConvertSkinAssetsToToonMaterials
    {
        private const string SkinRoot = "Assets/Resources/So/Skin/SkinAsset";
        private const string ToonDir = SkinRoot + "/ToonMaterials";
        private const string TemplatePathA = ToonDir + "/Robot Hybrid Crisp.mat";
        private const string TemplatePathB = ToonDir + "/Robot Hybrid Crisp 1.mat";
        private const string TemplatePathLegacyA = SkinRoot + "/Counters/Robot Hybrid Crisp.mat";
        private const string TemplatePathLegacyB = SkinRoot + "/Counters/Robot Hybrid Crisp 1.mat";
        private const string Tcp2ShaderGuid = "df5bb027d94a6c44bb32b3c31ec1303f";
        private const string ReportPath = ToonDir + "/_convert_report.txt";

        private static readonly HashSet<string> SkipNameContains = new(StringComparer.OrdinalIgnoreCase)
        {
            "Particle", "Sizzling", "Trail", "UI", "Font",
        };

        [MenuItem("Kitchen/Skin/Convert SkinAssets To Toon Materials (TCP2)")]
        public static void ConvertAll()
        {
            var report = new StringBuilder();
            report.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Convert SkinAssets → TCP2 Toon");

            EnsureFolder(ToonDir);
            var template = EnsureTemplate(report);
            if (template == null || template.shader == null)
            {
                EditorUtility.DisplayDialog("Toon Convert",
                    "Missing TCP2 template material.\nExpected:\n" + TemplatePathA,
                    "OK");
                return;
            }

            // Cache: source material instance id → toon material
            var map = new Dictionary<int, Material>();
            // Also map by asset path for stability across prefab loads
            var pathMap = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

            // Prefill already-converted mats in ToonDir
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { ToonDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                pathMap[path] = mat;
            }

            int matsCreated = 0;
            int prefabsTouched = 0;
            int slotsReplaced = 0;

            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[]
            {
                SkinRoot + "/Counters",
                SkinRoot + "/Ingredients",
                SkinRoot + "/Characters",
            });

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (var prefabGuid in prefabGuids)
                {
                    var prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
                    if (string.IsNullOrEmpty(prefabPath)) continue;

                    bool dirty = false;
                    var root = PrefabUtility.LoadPrefabContents(prefabPath);
                    try
                    {
                        var renderers = root.GetComponentsInChildren<Renderer>(true);
                        foreach (var renderer in renderers)
                        {
                            if (renderer == null) continue;
                            // Skip pure particle renderers (keep VFX look)
                            if (renderer is ParticleSystemRenderer) continue;

                            var shared = renderer.sharedMaterials;
                            if (shared == null || shared.Length == 0) continue;

                            bool changed = false;
                            for (int i = 0; i < shared.Length; i++)
                            {
                                var src = shared[i];
                                if (src == null) continue;
                                if (ShouldSkipMaterial(src)) continue;
                                if (IsAlreadyTcp2(src))
                                {
                                    // Move demo mats into ToonDir if still outside
                                    TryRelocateExistingTcp2(src, pathMap, report);
                                    continue;
                                }

                                var toon = GetOrCreateToonMaterial(src, template, map, pathMap, ref matsCreated, report);
                                if (toon == null || toon == src) continue;
                                shared[i] = toon;
                                changed = true;
                                slotsReplaced++;
                            }

                            if (changed)
                            {
                                renderer.sharedMaterials = shared;
                                dirty = true;
                            }
                        }

                        if (dirty)
                        {
                            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                            prefabsTouched++;
                            report.AppendLine($"PREFAB  {prefabPath}");
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(root);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            // Relocate leftover demo mats still under Counters/
            RelocateDemoMatsIfNeeded(report);

            File.WriteAllText(ReportPath, report.ToString());
            AssetDatabase.Refresh();

            var msg =
                $"Done.\n\nPrefabs updated: {prefabsTouched}\nMaterial slots replaced: {slotsReplaced}\nToon mats created: {matsCreated}\n\nFolder:\n{ToonDir}\n\nReport:\n{ReportPath}";
            Debug.Log("[ToonConvert] " + msg.Replace('\n', ' '));
            EditorUtility.DisplayDialog("Toon Convert", msg, "OK");
            EditorUtility.RevealInFinder(ToonDir);
        }

        private static Material EnsureTemplate(StringBuilder report)
        {
            var template = AssetDatabase.LoadAssetAtPath<Material>(TemplatePathA)
                           ?? AssetDatabase.LoadAssetAtPath<Material>(TemplatePathB)
                           ?? AssetDatabase.LoadAssetAtPath<Material>(TemplatePathLegacyA)
                           ?? AssetDatabase.LoadAssetAtPath<Material>(TemplatePathLegacyB);

            if (template == null)
            {
                // Fallback: find any TCP2 Hybrid Outline material in project
                foreach (var guid in AssetDatabase.FindAssets("t:Material Robot Hybrid Crisp"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                    if (m != null && IsAlreadyTcp2(m))
                    {
                        template = m;
                        break;
                    }
                }
            }

            if (template != null)
                report.AppendLine($"TEMPLATE  {AssetDatabase.GetAssetPath(template)}  shader={template.shader.name}");
            return template;
        }

        private static void RelocateDemoMatsIfNeeded(StringBuilder report)
        {
            TryMoveAsset(TemplatePathLegacyA, TemplatePathA, report);
            TryMoveAsset(TemplatePathLegacyB, TemplatePathB, report);
        }

        private static void TryMoveAsset(string from, string to, StringBuilder report)
        {
            if (!File.Exists(from) && !File.Exists(from.Replace('/', Path.DirectorySeparatorChar)))
            {
                // Unity path check
                if (AssetDatabase.LoadAssetAtPath<Material>(from) == null) return;
            }

            if (AssetDatabase.LoadAssetAtPath<Material>(from) == null) return;
            if (AssetDatabase.LoadAssetAtPath<Material>(to) != null)
            {
                // Destination exists — leave source (may still be referenced); optional delete later
                return;
            }

            EnsureFolder(Path.GetDirectoryName(to)!.Replace('\\', '/'));
            var err = AssetDatabase.MoveAsset(from, to);
            if (string.IsNullOrEmpty(err))
                report.AppendLine($"MOVE  {from} → {to}");
            else
                report.AppendLine($"MOVE_FAIL  {from} → {to}  ({err})");
        }

        private static void TryRelocateExistingTcp2(
            Material src, Dictionary<string, Material> pathMap, StringBuilder report)
        {
            var path = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(path)) return;
            if (path.StartsWith(ToonDir, StringComparison.OrdinalIgnoreCase))
            {
                pathMap[path] = src;
                return;
            }

            // Only auto-move the demo Robot Hybrid mats from Counters/
            if (path == TemplatePathLegacyA || path == TemplatePathLegacyB)
            {
                var dest = path == TemplatePathLegacyA ? TemplatePathA : TemplatePathB;
                TryMoveAsset(path, dest, report);
                var moved = AssetDatabase.LoadAssetAtPath<Material>(dest);
                if (moved != null) pathMap[dest] = moved;
            }
        }

        private static Material GetOrCreateToonMaterial(
            Material src,
            Material template,
            Dictionary<int, Material> idMap,
            Dictionary<string, Material> pathMap,
            ref int created,
            StringBuilder report)
        {
            if (idMap.TryGetValue(src.GetInstanceID(), out var cached) && cached != null)
                return cached;

            var srcPath = AssetDatabase.GetAssetPath(src);
            var safeName = SanitizeFileName(src.name);
            if (string.IsNullOrEmpty(safeName)) safeName = "Mat";
            var destPath = $"{ToonDir}/Toon_{safeName}.mat";

            // Avoid collisions for same name / different sources
            if (!string.IsNullOrEmpty(srcPath))
            {
                var hash = Hash16(srcPath);
                // If a same-named mat already exists from another source, uniquify
                var existing = AssetDatabase.LoadAssetAtPath<Material>(destPath);
                if (existing != null)
                {
                    // If it was built for this source path marker, reuse
                    idMap[src.GetInstanceID()] = existing;
                    pathMap[destPath] = existing;
                    return existing;
                }

                // uniquify by short hash when name collision risk with different folder sources
                destPath = $"{ToonDir}/Toon_{safeName}_{hash}.mat";
            }

            if (pathMap.TryGetValue(destPath, out var fromPath) && fromPath != null)
            {
                idMap[src.GetInstanceID()] = fromPath;
                return fromPath;
            }

            var existingAtDest = AssetDatabase.LoadAssetAtPath<Material>(destPath);
            if (existingAtDest != null)
            {
                idMap[src.GetInstanceID()] = existingAtDest;
                pathMap[destPath] = existingAtDest;
                return existingAtDest;
            }

            var toon = new Material(template)
            {
                name = $"Toon_{safeName}",
            };
            CopyAppearance(src, toon);
            AssetDatabase.CreateAsset(toon, destPath);
            created++;
            report.AppendLine($"CREATE  {destPath}  ←  {(string.IsNullOrEmpty(srcPath) ? src.name : srcPath)}");

            idMap[src.GetInstanceID()] = toon;
            pathMap[destPath] = toon;
            return toon;
        }

        private static void CopyAppearance(Material src, Material dst)
        {
            // Textures
            TryCopyTex(src, dst, "_BaseMap", "_MainTex");
            TryCopyTex(src, dst, "_MainTex", "_BaseMap");
            TryCopyTex(src, dst, "_BumpMap");
            TryCopyTex(src, dst, "_EmissionMap");
            TryCopyTex(src, dst, "_OcclusionMap");
            TryCopyTex(src, dst, "_MetallicGlossMap");
            TryCopyTex(src, dst, "_SpecGlossMap");

            // Colors
            TryCopyColor(src, dst, "_BaseColor", "_Color");
            TryCopyColor(src, dst, "_Color", "_BaseColor");
            TryCopyColor(src, dst, "_EmissionColor");

            // Normal map flag
            if (dst.HasProperty("_UseNormalMap") && src.GetTexture("_BumpMap") != null)
            {
                dst.SetFloat("_UseNormalMap", 1f);
                dst.EnableKeyword("TCP2_BUMP");
            }

            // Keep template outline / ramp / rim settings — that's the "demo" look.
            // Emission: enable if source had non-black emission texture or color
            if (dst.HasProperty("_UseEmission"))
            {
                var emTex = src.HasProperty("_EmissionMap") ? src.GetTexture("_EmissionMap") : null;
                var emCol = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.black;
                bool useEm = emTex != null || emCol.maxColorComponent > 0.01f;
                dst.SetFloat("_UseEmission", useEm ? 1f : 0f);
            }
        }

        private static void TryCopyTex(Material src, Material dst, string srcProp, string alsoDstProp = null)
        {
            if (!src.HasProperty(srcProp)) return;
            var tex = src.GetTexture(srcProp);
            if (tex == null) return;
            if (dst.HasProperty(srcProp))
            {
                dst.SetTexture(srcProp, tex);
                dst.SetTextureScale(srcProp, src.GetTextureScale(srcProp));
                dst.SetTextureOffset(srcProp, src.GetTextureOffset(srcProp));
            }
            if (!string.IsNullOrEmpty(alsoDstProp) && dst.HasProperty(alsoDstProp) && dst.GetTexture(alsoDstProp) == null)
            {
                dst.SetTexture(alsoDstProp, tex);
                if (src.HasProperty(alsoDstProp))
                {
                    dst.SetTextureScale(alsoDstProp, src.GetTextureScale(srcProp));
                    dst.SetTextureOffset(alsoDstProp, src.GetTextureOffset(srcProp));
                }
            }
        }

        private static void TryCopyColor(Material src, Material dst, string srcProp, string alsoDstProp = null)
        {
            if (!src.HasProperty(srcProp)) return;
            var c = src.GetColor(srcProp);
            if (dst.HasProperty(srcProp)) dst.SetColor(srcProp, c);
            if (!string.IsNullOrEmpty(alsoDstProp) && dst.HasProperty(alsoDstProp))
                dst.SetColor(alsoDstProp, c);
        }

        private static bool IsAlreadyTcp2(Material mat)
        {
            if (mat == null || mat.shader == null) return false;
            var path = AssetDatabase.GetAssetPath(mat.shader);
            if (!string.IsNullOrEmpty(path))
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (guid == Tcp2ShaderGuid) return true;
            }
            return mat.shader.name.IndexOf("TCP2", StringComparison.OrdinalIgnoreCase) >= 0
                   || mat.shader.name.IndexOf("Toony Colors", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldSkipMaterial(Material mat)
        {
            if (mat == null) return true;
            var n = mat.name ?? string.Empty;
            foreach (var token in SkipNameContains)
            {
                if (n.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Skip transparent UI / particles shaders
            var shaderName = mat.shader != null ? mat.shader.name : string.Empty;
            if (shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (mat.renderQueue >= (int)RenderQueue.Transparent &&
                shaderName.IndexOf("Lit", StringComparison.OrdinalIgnoreCase) < 0 &&
                shaderName.IndexOf("TCP2", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // Keep unknown transparent materials unless they're URP Lit/SimpleLit
            }

            return false;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Mat";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_').Trim('_');
        }

        private static string Hash16(string s)
        {
            unchecked
            {
                int h = 23;
                for (int i = 0; i < s.Length; i++)
                    h = h * 31 + s[i];
                return ((uint)h).ToString("x8");
            }
        }

        private static void EnsureFolder(string unityPath)
        {
            if (AssetDatabase.IsValidFolder(unityPath)) return;
            var parts = unityPath.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
#endif
