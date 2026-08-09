#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Kitchen.Skin;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
    public static class SkinCatalogCreator
    {
        private const string Dir = "Assets/Resources/So/Skin";
        private const string AssetPath = Dir + "/SkinCatalog_Default.asset";

        private const string VisualRoot = "Assets/Demos/KitchenChaos/_Assets/PrefabsVisuals/";
        private const string IngredientVisualDir = VisualRoot + "KitchenObjectsVisuals/";
        private const string CounterVisualDir = VisualRoot + "CountersVisuals/";
        private const string PlayerVisualPath = VisualRoot + "PlayerVisual.prefab";
        private const string KitchenCounterFbx = "Assets/Demos/KitchenChaos/_Assets/Meshes/Kitchen Counter.fbx";

        [MenuItem("Kitchen/Skin/Create Default SkinCatalog")]
        public static void CreateDefault()
        {
            var catalog = LoadOrCreateEmpty();
            Selection.activeObject = catalog;
            EditorUtility.DisplayDialog("SkinCatalog",
                "Asset ready:\n" + AssetPath + "\n\nUse menu:\nKitchen/Skin/Fill Default SkinCatalog From Project\nto populate from current visuals.",
                "OK");
        }

        [MenuItem("Kitchen/Skin/Fill Default SkinCatalog From Project")]
        public static void FillDefaultFromProject()
        {
            var catalog = LoadOrCreateEmpty();
            Undo.RecordObject(catalog, "Fill Default SkinCatalog");

            catalog.defaultSkinId = 0;
            catalog.ingredientOverrides = new List<IngredientSkinOverride>();
            catalog.counterOverrides = new List<CounterSkinOverride>();
            catalog.characterOverrides = new List<CharacterSkinOverride>();
            catalog.ingredients = BuildIngredients();
            catalog.counters = BuildCounters();
            catalog.characters = BuildCharacters();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Selection.activeObject = catalog;

            EditorUtility.DisplayDialog("SkinCatalog",
                $"Filled {AssetPath}\n" +
                $"ingredients={catalog.ingredients.Count}, counters={catalog.counters.Count}, characters={catalog.characters.Count}\n" +
                "preferMode=PreferPrefab (current visuals).",
                "OK");
        }

        [MenuItem("Kitchen/Skin/Add SkinManager To Active Scene")]
        public static void AddSkinManagerToScene()
        {
            var existing = Object.FindObjectOfType<SkinManager>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorUtility.DisplayDialog("SkinManager", "Scene already has SkinManager.", "OK");
                return;
            }

            var go = new GameObject("SkinManager");
            var mgr = go.AddComponent<SkinManager>();
            var catalog = AssetDatabase.LoadAssetAtPath<SkinCatalogSo>(AssetPath);
            if (catalog == null)
                FillDefaultFromProject();
            catalog = AssetDatabase.LoadAssetAtPath<SkinCatalogSo>(AssetPath);
            var so = new SerializedObject(mgr);
            so.FindProperty("catalogSo").objectReferenceValue = catalog;
            so.ApplyModifiedPropertiesWithoutUndo();
            Undo.RegisterCreatedObjectUndo(go, "Add SkinManager");
            Selection.activeGameObject = go;
        }

        private static SkinCatalogSo LoadOrCreateEmpty()
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            var existing = AssetDatabase.LoadAssetAtPath<SkinCatalogSo>(AssetPath);
            if (existing != null)
                return existing;

            var catalog = ScriptableObject.CreateInstance<SkinCatalogSo>();
            catalog.defaultSkinId = 0;
            AssetDatabase.CreateAsset(catalog, AssetPath);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        private static List<IngredientSkinEntry> BuildIngredients()
        {
            // kind -> visual prefab name (proxy for missing dedicated meshes)
            var map = new Dictionary<KitchenObjEnum, string>
            {
                { KitchenObjEnum.Tomato, "Tomato_Visual" },
                { KitchenObjEnum.TomatoSlices, "TomatoSlices_Visual" },
                { KitchenObjEnum.CheeseBlock, "CheeseBlock_Visual" },
                { KitchenObjEnum.Bread, "Bread_Visual" },
                { KitchenObjEnum.Cabbage, "Cabbage_Visual" },
                { KitchenObjEnum.MeatPattyUncooked, "MeatPattyUncooked_Visual" },
                { KitchenObjEnum.CabbageSlices, "CabbageSliced_Visual" },
                { KitchenObjEnum.MeatPattyCooked, "MeatPattyCooked_Visual" },
                { KitchenObjEnum.CheeseSlices, "CheeseSlices_Visual" },
                { KitchenObjEnum.MeatPattyBurned, "MeatPattyBurned_Visual" },
                { KitchenObjEnum.Plate, "Plate_Visual" },
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

            var list = new List<IngredientSkinEntry>();
            foreach (KitchenObjEnum kind in System.Enum.GetValues(typeof(KitchenObjEnum)))
            {
                if (!map.TryGetValue(kind, out var visualName))
                    continue;
                list.Add(new IngredientSkinEntry
                {
                    kind = kind,
                    skinId = 0,
                    meshMaterial = new SkinMeshMaterial(),
                    visualPrefab = LoadPrefab(IngredientVisualDir + visualName + ".prefab"),
                    preferMode = SkinPreferMode.PreferPrefab,
                });
            }
            return list;
        }

        private static List<CounterSkinEntry> BuildCounters()
        {
            TryLoadCounterBody(out var bodyMesh, out var bodyMat);

            var map = new (SkinCounterKind kind, string visualName, bool hasBody)[]
            {
                (SkinCounterKind.Clear, "ClearCounter_Visual", true),
                (SkinCounterKind.Container, "ContainerCounter_Visual", true),
                (SkinCounterKind.Cutting, "CuttingCounter_Visual", true),
                (SkinCounterKind.Stove, "StoveCounter_Visual", true),
                (SkinCounterKind.Oven, "StoveCounter_Visual", true),
                (SkinCounterKind.Blender, "ClearCounter_Visual", true),
                (SkinCounterKind.Plates, "PlatesCounter_Visual", true),
                (SkinCounterKind.Delivery, "DeliveryCounter_Visual", true),
                (SkinCounterKind.Trash, "TrashCounter_Visual", false),
                (SkinCounterKind.Wall, "ClearCounter_Visual", true),
            };

            var list = new List<CounterSkinEntry>();
            foreach (var (kind, visualName, hasBody) in map)
            {
                var parts = new List<CounterPartVisual>();
                if (hasBody)
                {
                    parts.Add(new CounterPartVisual
                    {
                        slot = CounterPartSlot.CounterBody,
                        mesh = bodyMesh,
                        material = bodyMat,
                    });
                }

                if (kind == SkinCounterKind.Cutting)
                {
                    parts.Add(new CounterPartVisual { slot = CounterPartSlot.Knife });
                    parts.Add(new CounterPartVisual { slot = CounterPartSlot.CuttingBoard });
                }

                list.Add(new CounterSkinEntry
                {
                    kind = kind,
                    skinId = 0,
                    parts = parts,
                    visualPrefab = LoadPrefab(CounterVisualDir + visualName + ".prefab"),
                    preferMode = SkinPreferMode.PreferPrefab,
                });
            }
            return list;
        }

        private static List<CharacterSkinEntry> BuildCharacters()
        {
            var playerVisual = LoadPrefab(PlayerVisualPath);
            return new List<CharacterSkinEntry>
            {
                new() { kind = SkinCharacterKind.Player, skinId = 0, visualPrefab = playerVisual },
                new() { kind = SkinCharacterKind.AIPlayer, skinId = 0, visualPrefab = playerVisual },
            };
        }

        private static GameObject LoadPrefab(string path)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
                Debug.LogWarning("[SkinCatalog] Missing prefab: " + path);
            return go;
        }

        private static void TryLoadCounterBody(out Mesh mesh, out Material material)
        {
            mesh = null;
            material = null;
            var assets = AssetDatabase.LoadAllAssetsAtPath(KitchenCounterFbx);
            if (assets == null) return;

            foreach (var a in assets)
            {
                if (mesh == null && a is Mesh m && a.name.Contains("Kitchen"))
                    mesh = m;
                if (material == null && a is Material mat)
                    material = mat;
            }

            // Fallback: first mesh / first material in FBX
            if (mesh == null)
            {
                foreach (var a in assets)
                {
                    if (a is Mesh m)
                    {
                        mesh = m;
                        break;
                    }
                }
            }
            if (material == null)
            {
                foreach (var a in assets)
                {
                    if (a is Material mat)
                    {
                        material = mat;
                        break;
                    }
                }
            }
        }
    }
}
#endif
