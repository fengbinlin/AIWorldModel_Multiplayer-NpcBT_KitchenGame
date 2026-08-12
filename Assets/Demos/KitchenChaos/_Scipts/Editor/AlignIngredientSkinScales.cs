#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
        /// <summary>
        /// Scales each ingredient skin's CHILD model so its world AABB footprint
        /// matches Bread × real-world relative proportion (not all equal to bread).
        /// </summary>
        public static class AlignIngredientSkinScales
        {
        private const string IngredientDir = "Assets/Resources/So/Skin/SkinAsset/Ingredients";
        private const string BreadPath = IngredientDir + "/Skin_Bread_Visual.prefab";

        [MenuItem("Kitchen/Skin/Align Ingredient Scales (relative to Bread)")]
        public static void AlignToBread()
        {
            var bread = AssetDatabase.LoadAssetAtPath<GameObject>(BreadPath);
            if (bread == null)
            {
                EditorUtility.DisplayDialog("Align Scales", "Missing bread prefab:\n" + BreadPath, "OK");
                return;
            }

            float breadSize = MeasurePrefabMaxSize(bread);
            if (breadSize < 0.001f)
            {
                EditorUtility.DisplayDialog("Align Scales", "Bread bounds too small / missing renderers.", "OK");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { IngredientDir });
            int changed = 0;
            var report = new List<string>
            {
                $"Bread baseline max-size = {breadSize:F3}"
            };

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.Replace('\\', '/') == BreadPath)
                        continue;

                    float relative = IngredientVisualProportion.GetRelativeFootprintBySkinName(
                        Path.GetFileName(path));
                    float targetSize = breadSize * relative;

                    if (!TryAlignPrefab(path, targetSize, out float native, out float newScale, out float oldScale))
                    {
                        report.Add($"SKIP {Path.GetFileName(path)}");
                        continue;
                    }

                    changed++;
                    report.Add(
                        $"{Path.GetFileName(path)}: rel={relative:F2} native={native:F3} scale {oldScale:F2} → {newScale:F2}");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            foreach (var line in report)
                Debug.Log("[AlignSkinScale] " + line);

            EditorUtility.DisplayDialog("Align Scales",
                $"Aligned {changed} prefabs to bread × relative proportions (bread≈{breadSize:F2}).\nSee Console.",
                "OK");
        }

        /// <summary>Batchmode entry: Unity -batchmode -executeMethod Kitchen.EditorTools.AlignIngredientSkinScales.AlignToBreadBatch</summary>
        public static void AlignToBreadBatch()
        {
            AlignToBread();
            EditorApplication.Exit(0);
        }

        private static bool TryAlignPrefab(string path, float targetSize,
            out float nativeMax, out float newScale, out float oldScale)
        {
            nativeMax = 0f;
            newScale = 1f;
            oldScale = 1f;

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root == null || root.transform.childCount == 0)
                    return false;

                // Measure at scale=1 on all direct children (and nested)
                var scaleBackup = new List<(Transform t, Vector3 scale)>();
                CollectScalableTransforms(root.transform, scaleBackup);
                if (scaleBackup.Count == 0)
                    return false;

                oldScale = AverageUniformScale(scaleBackup);

                // Temporarily set all scalable transforms to 1 to read native size
                foreach (var (t, _) in scaleBackup)
                    t.localScale = Vector3.one;

                // Force update
                nativeMax = MeasureHierarchyMaxSize(root);
                if (nativeMax < 1e-5f)
                {
                    // restore
                    foreach (var (t, s) in scaleBackup)
                        t.localScale = s;
                    return false;
                }

                newScale = targetSize / nativeMax;
                // Clamp to sane range
                newScale = Mathf.Clamp(newScale, 0.05f, 50f);

                var uniform = new Vector3(newScale, newScale, newScale);
                foreach (var (t, _) in scaleBackup)
                    t.localScale = uniform;

                PrefabUtility.SaveAsPrefabAsset(root, path);
                return true;
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Scalable nodes = direct children of visual root (the model wrappers).
        /// If a child has no renderer but has grandchildren, scale the child itself.
        /// </summary>
        private static void CollectScalableTransforms(Transform root, List<(Transform t, Vector3 scale)> list)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                list.Add((c, c.localScale));
            }
        }

        private static float AverageUniformScale(List<(Transform t, Vector3 scale)> list)
        {
            float sum = 0f;
            int n = 0;
            foreach (var (_, s) in list)
            {
                sum += (s.x + s.y + s.z) / 3f;
                n++;
            }
            return n > 0 ? sum / n : 1f;
        }

        private static float MeasurePrefabMaxSize(GameObject prefab)
        {
            var tmp = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            try
            {
                return MeasureHierarchyMaxSize(tmp);
            }
            finally
            {
                if (tmp != null)
                    UnityEngine.Object.DestroyImmediate(tmp);
            }
        }

        private static float MeasureHierarchyMaxSize(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return 0f;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                b.Encapsulate(renderers[i].bounds);
            }

            var size = b.size;
            // Horizontal footprint — keep height free so tall items are not overshrunk.
            return Mathf.Max(size.x, size.z);
        }
    }
}
#endif
