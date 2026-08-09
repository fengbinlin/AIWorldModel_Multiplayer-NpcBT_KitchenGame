#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Kitchen;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
    /// <summary>
    /// 为缺失的 KitchenObj 枚举生成逻辑预制体（复用现有 Visual），
    /// 写回 KitchenObjSo.prefab，并加入 DefaultNetworkPrefabs。
    /// Menu: Kitchen / Generate Missing KitchenObj Prefabs
    /// </summary>
    public static class KitchenObjPrefabGenerator
    {
        private const string PrefabDir = "Assets/Resources/Prefab/KitchenObj";
        private const string SoDir = "Assets/Resources/So/KitchenObj";
        private const string VisualDir = "Assets/Demos/KitchenChaos/_Assets/PrefabsVisuals/KitchenObjectsVisuals";
        private const string NetworkPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";

        private static readonly Dictionary<KitchenObjEnum, string> VisualProxy = new()
        {
            { KitchenObjEnum.ChickenRaw, "MeatPattyUncooked_Visual" },
            { KitchenObjEnum.ChickenCooked, "MeatPattyCooked_Visual" },
            { KitchenObjEnum.ChickenBurned, "MeatPattyBurned_Visual" },
            { KitchenObjEnum.Fish, "Cabbage_Visual" },
            { KitchenObjEnum.FishSlices, "TomatoSlices_Visual" },
            { KitchenObjEnum.Shrimp, "CheeseBlock_Visual" },
            { KitchenObjEnum.ShrimpSlices, "CheeseSlices_Visual" },
            { KitchenObjEnum.RiceBall, "Bread_Visual" },
            { KitchenObjEnum.Nori, "CheeseSlices_Visual" },
            { KitchenObjEnum.Cream, "CheeseBlock_Visual" },
            { KitchenObjEnum.PizzaDough, "Bread_Visual" },
            { KitchenObjEnum.PizzaUnbaked, "Bread_Visual" },
            { KitchenObjEnum.PizzaBaked, "Bread_Visual" },
            { KitchenObjEnum.ChickenBurger, "Bread_Visual" },
            { KitchenObjEnum.BeefBurger, "Bread_Visual" },
            { KitchenObjEnum.FishSushi, "CabbageSliced_Visual" },
            { KitchenObjEnum.ShrimpSushi, "CabbageSliced_Visual" },
            { KitchenObjEnum.TomatoSalad, "TomatoSlices_Visual" },
            { KitchenObjEnum.CabbageSalad, "CabbageSliced_Visual" },
            { KitchenObjEnum.TomatoCabbageSalad, "CabbageSliced_Visual" },
            { KitchenObjEnum.TomatoShake, "Tomato_Visual" },
            { KitchenObjEnum.CabbageShake, "Cabbage_Visual" },
            { KitchenObjEnum.TomatoCabbageShake, "Cabbage_Visual" },
        };

        [MenuItem("Kitchen/Generate Missing KitchenObj Prefabs")]
        public static void Generate()
        {
            if (!Directory.Exists(PrefabDir))
                Directory.CreateDirectory(PrefabDir);

            var netList = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsPath);
            int created = 0;
            int wired = 0;

            foreach (var kv in VisualProxy)
            {
                var e = kv.Key;
                string name = e.ToString();
                string prefabPath = $"{PrefabDir}/{name}.prefab";
                string soPath = $"{SoDir}/{name}.asset";
                string visualPath = $"{VisualDir}/{kv.Value}.prefab";

                var visual = AssetDatabase.LoadAssetAtPath<GameObject>(visualPath);
                if (visual == null)
                {
                    Debug.LogError($"[KitchenObjPrefabGenerator] Missing visual: {visualPath}");
                    continue;
                }

                GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefabRoot == null)
                {
                    var go = new GameObject(name);
                    go.AddComponent<NetworkObject>();
                    var kitchenObj = go.AddComponent<KitchenObj>();
                    kitchenObj.objEnum = e;
                    // TransformFollower exists on legacy prefabs via script guid fba95087...
                    var followerType = System.Type.GetType("Nico.Components.TransformFollower, Assembly-CSharp");
                    if (followerType != null && go.GetComponent(followerType) == null)
                        go.AddComponent(followerType);

                    var visualInst = (GameObject)PrefabUtility.InstantiatePrefab(visual);
                    visualInst.name = $"{name}_Visual";
                    visualInst.transform.SetParent(go.transform, false);
                    visualInst.transform.localPosition = Vector3.zero;
                    visualInst.transform.localRotation = Quaternion.identity;

                    prefabRoot = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
                    Object.DestroyImmediate(go);
                    created++;
                    Debug.Log($"[KitchenObjPrefabGenerator] Created {prefabPath}");
                }

                var so = AssetDatabase.LoadAssetAtPath<KitchenObjSo>(soPath);
                if (so != null && so.prefab != prefabRoot)
                {
                    so.prefab = prefabRoot;
                    so.kitchenObjEnum = e;
                    EditorUtility.SetDirty(so);
                    wired++;
                }

                if (netList != null && prefabRoot != null)
                    TryAddNetworkPrefab(netList, prefabRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[KitchenObjPrefabGenerator] Done. created={created}, soWired={wired}");
            EditorUtility.DisplayDialog("KitchenObj Prefabs",
                $"Created {created} prefabs, wired {wired} ScriptableObjects.\n" +
                "Note: new items reuse closest existing visuals until dedicated meshes exist.",
                "OK");
        }

        private static void TryAddNetworkPrefab(NetworkPrefabsList list, GameObject prefab)
        {
            var so = new SerializedObject(list);
            var arr = so.FindProperty("m_Prefabs");
            if (arr == null) arr = so.FindProperty("List");
            if (arr == null || !arr.isArray) return;

            for (int i = 0; i < arr.arraySize; i++)
            {
                var prefabProp = arr.GetArrayElementAtIndex(i).FindPropertyRelative("Prefab");
                if (prefabProp != null && prefabProp.objectReferenceValue == prefab)
                    return;
            }

            arr.InsertArrayElementAtIndex(arr.arraySize);
            var entry = arr.GetArrayElementAtIndex(arr.arraySize - 1);
            var p = entry.FindPropertyRelative("Prefab");
            if (p != null) p.objectReferenceValue = prefab;
            var ov = entry.FindPropertyRelative("Override");
            if (ov != null) ov.intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(list);
        }
    }
}
#endif
