#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
    /// <summary>
    /// Swaps SkinAsset visual prefab mesh children with Tripo3D FBX + ToonMaterial,
    /// preserving the previous renderer footprint (max X/Z for ingredients, full bounds
    /// for counter meshes). Tripo FBX is Z-up — converted to Unity Y-up on import.
    /// </summary>
    public static class ReplaceTripo3DSkinVisuals
    {
        private const string TripoRoot = "Assets/ImportedResources/Tripo3DModel";
        private const string SkinRoot = "Assets/Resources/So/Skin/SkinAsset";
        private const string ReportPath = TripoRoot + "/_replace_skin_visuals_report.txt";

        /// <summary>Tripo exports Z-up; kitchen visuals expect Y-up (Unity default).</summary>
        private static readonly Quaternion TripoZUpToYUp = Quaternion.Euler(-90f, 0f, 0f);

        private enum ReplacementMode
        {
            Ingredient,
            PlatesCounterMesh,
        }

        private readonly struct Mapping
        {
            public readonly string TripoFolder;
            public readonly string SkinRelativePath;
            public readonly ReplacementMode Mode;

            public Mapping(string tripoFolder, string skinRelativePath, ReplacementMode mode)
            {
                TripoFolder = tripoFolder;
                SkinRelativePath = skinRelativePath;
                Mode = mode;
            }
        }

        private static readonly Mapping[] Mappings =
        {
            new("番茄片", "Ingredients/Skin_TomatoSlices_Visual.prefab", ReplacementMode.Ingredient),
            new("番茄奶昔", "Ingredients/Skin_TomatoShake_Visual.prefab", ReplacementMode.Ingredient),
            new("番茄包菜沙拉", "Ingredients/Skin_TomatoCabbageSalad_Visual.prefab", ReplacementMode.Ingredient),
            new("番茄包菜奶昔", "Ingredients/Skin_TomatoCabbageShake_Visual.prefab", ReplacementMode.Ingredient),
            new("奶油", "Ingredients/Skin_Cream_Visual.prefab", ReplacementMode.Ingredient),
            new("包菜沙拉", "Ingredients/Skin_CabbageSalad_Visual.prefab", ReplacementMode.Ingredient),
            new("包菜奶昔", "Ingredients/Skin_CabbageShake_Visual.prefab", ReplacementMode.Ingredient),
            new("鸡腿汉堡", "Ingredients/Skin_ChickenBurger_Visual.prefab", ReplacementMode.Ingredient),
            new("牛肉汉堡", "Ingredients/Skin_BeefBurger_Visual.prefab", ReplacementMode.Ingredient),
            new("不锈钢橱柜", "Counters/Skin_PlatesCounter_Visual.prefab", ReplacementMode.PlatesCounterMesh),
        };

        private static readonly HashSet<string> KeepChildNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CircleSprite",
            "PlateSpawnPoint",
        };

        [MenuItem("Kitchen/Skin/Replace Visuals With Tripo3D Models")]
        public static void ReplaceAll()
        {
            RunReplace(replaceExisting: true);
        }

        /// <summary>
        /// Re-applies Z-up → Y-up on TripoModel / Tripo KitchenCounter nodes already in skin prefabs.
        /// Prefer re-running Replace All so footprint scale is recomputed after rotation.
        /// </summary>
        [MenuItem("Kitchen/Skin/Fix Tripo3D Visual Orientation (Z-up → Y-up)")]
        public static void FixOrientationOnly()
        {
            RunFixOrientationOnly();
        }

        private static void RunReplace(bool replaceExisting)
        {
            var report = new StringBuilder();
            report.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Replace SkinAsset visuals ← Tripo3DModel");
            int ok = 0;
            int fail = 0;

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var map in Mappings)
                {
                    if (TryReplace(map, report))
                        ok++;
                    else
                        fail++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            File.WriteAllText(ReportPath, report.ToString());

            foreach (var line in report.ToString().Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("FAIL") || line.Contains("SKIP"))
                    Debug.LogWarning("[TripoReplace] " + line.Trim());
                else
                    Debug.Log("[TripoReplace] " + line.Trim());
            }

            EditorUtility.DisplayDialog("Tripo3D Replace",
                $"Done: {ok} updated, {fail} skipped/failed.\nReport:\n{ReportPath}",
                "OK");
        }

        /// <summary>Batchmode: Unity -batchmode -executeMethod Kitchen.EditorTools.ReplaceTripo3DSkinVisuals.ReplaceAllBatch</summary>
        public static void ReplaceAllBatch()
        {
            ReplaceAll();
            EditorApplication.Exit(0);
        }

        private static bool TryReplace(Mapping map, StringBuilder report)
        {
            var skinPath = $"{SkinRoot}/{map.SkinRelativePath}".Replace('\\', '/');
            if (!TryFindTripoAssets(map.TripoFolder, out var fbxPath, out var toonMat, out var err))
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: {err}");
                return false;
            }

            var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbxAsset == null)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: cannot load FBX {fbxPath}");
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(skinPath);
            if (root == null)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: cannot open prefab");
                return false;
            }

            try
            {
                switch (map.Mode)
                {
                    case ReplacementMode.Ingredient:
                        return ReplaceIngredientVisual(root, skinPath, fbxAsset, toonMat, map, report);
                    case ReplacementMode.PlatesCounterMesh:
                        return ReplacePlatesCounterMesh(root, skinPath, fbxAsset, toonMat, map, report);
                    default:
                        report.AppendLine($"FAIL {map.SkinRelativePath}: unknown mode");
                        return false;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool ReplaceIngredientVisual(
            GameObject root,
            string skinPath,
            GameObject fbxAsset,
            Material toonMat,
            Mapping map,
            StringBuilder report)
        {
            var visualChildren = CollectRemovableVisualChildren(root.transform);
            if (visualChildren.Count == 0)
            {
                report.AppendLine($"SKIP {map.SkinRelativePath}: no visual children");
                return false;
            }

            float targetFootprint = MeasureTransformsFootprint(visualChildren);
            if (targetFootprint < 1e-5f)
            {
                report.AppendLine($"SKIP {map.SkinRelativePath}: old footprint too small");
                return false;
            }

            foreach (var child in visualChildren)
                UnityEngine.Object.DestroyImmediate(child.gameObject);

            var instance = InstantiateModel(fbxAsset, root.transform, toonMat, "TripoModel");
            if (instance == null)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: instantiate failed");
                return false;
            }

            float nativeFootprint = MeasureHierarchyFootprint(instance);
            if (nativeFootprint < 1e-5f)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: new model has no renderers");
                return false;
            }

            float scale = Mathf.Clamp(targetFootprint / nativeFootprint, 0.05f, 50f);
            instance.transform.localScale = Vector3.one * scale;
            AlignBottomCenter(instance.transform);

            PrefabUtility.SaveAsPrefabAsset(root, skinPath);
            report.AppendLine(
                $"OK {Path.GetFileName(skinPath)} ← {map.TripoFolder} " +
                $"footprint {targetFootprint:F3} scale={scale:F3}");
            return true;
        }

        private static bool ReplacePlatesCounterMesh(
            GameObject root,
            string skinPath,
            GameObject fbxAsset,
            Material toonMat,
            Mapping map,
            StringBuilder report)
        {
            var counterMesh = root.transform
                .Cast<Transform>()
                .FirstOrDefault(t => t.name == "KitchenCounter");
            if (counterMesh == null)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: KitchenCounter child missing");
                return false;
            }

            var targetSize = MeasureRendererBounds(counterMesh.gameObject).size;
            float targetMax = Mathf.Max(targetSize.x, targetSize.y, targetSize.z);
            if (targetMax < 1e-5f)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: KitchenCounter bounds too small");
                return false;
            }

            var parent = counterMesh.parent;
            var siblingIndex = counterMesh.GetSiblingIndex();
            var localPos = counterMesh.localPosition;
            UnityEngine.Object.DestroyImmediate(counterMesh.gameObject);

            var instance = InstantiateModel(fbxAsset, parent, toonMat, "KitchenCounter");
            if (instance == null)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: instantiate failed");
                return false;
            }

            instance.transform.SetSiblingIndex(siblingIndex);
            instance.transform.localPosition = localPos;
            // TripoZUpToYUp already applied in InstantiateModel; do not restore old mesh rotation.

            var nativeBounds = MeasureRendererBounds(instance);
            float nativeMax = Mathf.Max(nativeBounds.size.x, nativeBounds.size.y, nativeBounds.size.z);
            if (nativeMax < 1e-5f)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: Tripo counter mesh empty");
                return false;
            }

            float scale = Mathf.Clamp(targetMax / nativeMax, 0.05f, 50f);
            instance.transform.localScale = Vector3.one * scale;

            PrefabUtility.SaveAsPrefabAsset(root, skinPath);
            report.AppendLine(
                $"OK {Path.GetFileName(skinPath)} ← {map.TripoFolder} " +
                $"counter size {targetMax:F3} scale={scale:F3}");
            return true;
        }

        private static List<Transform> CollectRemovableVisualChildren(Transform root)
        {
            var list = new List<Transform>();
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (KeepChildNames.Contains(child.name))
                    continue;
                if (HasMeshVisual(child))
                    list.Add(child);
            }
            return list;
        }

        private static bool HasMeshVisual(Transform t)
        {
            if (t.GetComponent<MeshRenderer>() != null || t.GetComponent<SkinnedMeshRenderer>() != null)
                return true;
            return t.GetComponentsInChildren<MeshRenderer>(true).Length > 0
                   || t.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0;
        }

        private static GameObject InstantiateModel(
            GameObject fbxAsset,
            Transform parent,
            Material toonMat,
            string objectName)
        {
            var instance = PrefabUtility.InstantiatePrefab(fbxAsset, parent) as GameObject;
            if (instance == null)
                return null;

            instance.name = objectName;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = TripoZUpToYUp;
            instance.transform.localScale = Vector3.one;

            if (toonMat != null)
                ApplyMaterial(instance, toonMat);

            return instance;
        }

        private static void RunFixOrientationOnly()
        {
            var report = new StringBuilder();
            report.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Fix Tripo3D Z-up → Y-up");
            int ok = 0;
            int fail = 0;

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var map in Mappings)
                {
                    var skinPath = $"{SkinRoot}/{map.SkinRelativePath}".Replace('\\', '/');
                    if (TryFixOrientationInPrefab(skinPath, map, report))
                        ok++;
                    else
                        fail++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            File.WriteAllText(ReportPath, report.ToString());

            EditorUtility.DisplayDialog("Tripo3D Orientation",
                $"Fixed {ok} prefabs ({fail} skipped).\n" +
                "If scale still looks wrong, run Replace Visuals With Tripo3D Models again.\n" +
                $"Report: {ReportPath}",
                "OK");
        }

        private static bool TryFixOrientationInPrefab(string skinPath, Mapping map, StringBuilder report)
        {
            if (!File.Exists(skinPath))
            {
                report.AppendLine($"SKIP {map.SkinRelativePath}: missing");
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(skinPath);
            if (root == null)
            {
                report.AppendLine($"FAIL {map.SkinRelativePath}: cannot open");
                return false;
            }

            try
            {
                var nodes = FindTripoVisualNodes(root.transform, map.Mode);
                if (nodes.Count == 0)
                {
                    report.AppendLine($"SKIP {map.SkinRelativePath}: no Tripo visual node");
                    return false;
                }

                foreach (var node in nodes)
                {
                    node.localRotation = TripoZUpToYUp;
                    AlignBottomCenter(node);
                }

                PrefabUtility.SaveAsPrefabAsset(root, skinPath);
                report.AppendLine($"OK {Path.GetFileName(skinPath)}: rotation fixed ({nodes.Count} node(s))");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static List<Transform> FindTripoVisualNodes(Transform root, ReplacementMode mode)
        {
            var list = new List<Transform>();
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (KeepChildNames.Contains(child.name))
                    continue;

                bool isTripo = child.name == "TripoModel"
                               || (mode == ReplacementMode.PlatesCounterMesh && child.name == "KitchenCounter");
                if (isTripo && HasMeshVisual(child))
                    list.Add(child);
            }
            return list;
        }

        private static void ApplyMaterial(GameObject root, Material mat)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SpriteRenderer)
                    continue;

                var shared = renderer.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                    shared[i] = mat;
                renderer.sharedMaterials = shared;
            }
        }

        private static float MeasureTransformsFootprint(IEnumerable<Transform> transforms)
        {
            float max = 0f;
            foreach (var t in transforms)
            {
                if (t == null) continue;
                max = Mathf.Max(max, MeasureHierarchyFootprint(t.gameObject));
            }
            return max;
        }

        private static float MeasureHierarchyFootprint(GameObject go)
        {
            var size = MeasureRendererBounds(go).size;
            return Mathf.Max(size.x, size.z);
        }

        private static Bounds MeasureRendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null && r is not SpriteRenderer)
                .ToArray();
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.zero);

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }

        private static void AlignBottomCenter(Transform modelRoot)
        {
            var renderers = modelRoot.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is MeshRenderer or SkinnedMeshRenderer)
                .ToArray();
            if (renderers.Length == 0)
                return;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);

            var parent = modelRoot.parent;
            Vector3 parentOrigin = parent != null ? parent.position : Vector3.zero;
            Vector3 desiredPivot = new Vector3(b.center.x, b.min.y, b.center.z);
            modelRoot.position += parentOrigin - desiredPivot;
        }

        private static bool TryFindTripoAssets(
            string folderName,
            out string fbxPath,
            out Material toonMat,
            out string error)
        {
            fbxPath = null;
            toonMat = null;
            error = null;

            var dir = $"{TripoRoot}/{folderName}";
            if (!AssetDatabase.IsValidFolder(dir))
            {
                error = $"missing folder {dir}";
                return false;
            }

            var fbxGuids = AssetDatabase.FindAssets("tripo_convert t:Model", new[] { dir });
            if (fbxGuids.Length == 0)
            {
                error = $"no tripo_convert FBX in {dir}";
                return false;
            }

            fbxPath = AssetDatabase.GUIDToAssetPath(fbxGuids[0]);
            toonMat = AssetDatabase.LoadAssetAtPath<Material>($"{dir}/ToonMaterial.mat");
            if (toonMat == null)
                Debug.LogWarning($"[TripoReplace] {folderName}: ToonMaterial.mat missing, keeping FBX defaults");

            return true;
        }
    }
}
#endif
