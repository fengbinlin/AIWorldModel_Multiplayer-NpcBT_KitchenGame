#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
    /// <summary>
    /// Re-bind catalog roots after manual wrap / FBX→prefab conversion.
    /// Main wrapping is done offline; this menu fixes FBX refs → wrapper prefabs.
    /// </summary>
    public static class WrapIngredientSkinPrefabs
    {
        private const string IngredientDir = "Assets/Resources/So/Skin/SkinAsset/Ingredients";
        private const string CatalogPath = "Assets/Resources/So/Skin/SkinCatalog_Default.asset";

        [MenuItem("Kitchen/Skin/Rebind Catalog To Ingredient Prefab Roots")]
        public static void RebindCatalogRoots()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<Kitchen.Skin.SkinCatalogSo>(CatalogPath);
            if (catalog == null)
            {
                EditorUtility.DisplayDialog("Skin", "Catalog missing:\n" + CatalogPath, "OK");
                return;
            }

            Undo.RecordObject(catalog, "Rebind SkinCatalog visual roots");
            int changed = 0;

            if (catalog.ingredients != null)
            {
                for (int i = 0; i < catalog.ingredients.Count; i++)
                {
                    var e = catalog.ingredients[i];
                    if (e?.visualPrefab == null) continue;

                    var path = AssetDatabase.GetAssetPath(e.visualPrefab);
                    if (string.IsNullOrEmpty(path)) continue;

                    if (path.EndsWith(".fbx"))
                    {
                        var prefabPath = Path.ChangeExtension(path, ".prefab");
                        var wrapper = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (wrapper != null)
                        {
                            e.visualPrefab = wrapper;
                            changed++;
                            continue;
                        }
                    }

                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (root != null && root != e.visualPrefab)
                    {
                        e.visualPrefab = root;
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
            }

            EditorUtility.DisplayDialog("Skin",
                $"Rebound {changed} ingredient visual refs to prefab roots.",
                "OK");
        }
    }
}
#endif
