#if UNITY_EDITOR
using Kitchen.Skin;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
    /// <summary>
    /// Embeds CharacterSimple (+ CharacterSkinVisual) into AIPlayer.prefab under PlayerVisual.
    /// </summary>
    public static class EmbedLayerLabCharacterIntoAiPlayer
    {
        private const string AiPlayerPrefabPath = "Assets/Resources/Prefab/AIPlayer.prefab";
        private const string CharacterPrefabPath =
            "Assets/Resources/So/Skin/SkinAsset/Characters/CharacterSimple.prefab";
        private const string MaterialPath =
            "Assets/Resources/So/Skin/SkinAsset/Characters/lilToonMaterialDemo 1.mat";

        [MenuItem("Kitchen/Skin/Embed CharacterSimple Into AIPlayer Prefab")]
        public static void Embed()
        {
            var aiPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AiPlayerPrefabPath);
            var charPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPrefabPath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (aiPrefab == null)
            {
                EditorUtility.DisplayDialog("Embed Character", $"Missing {AiPlayerPrefabPath}", "OK");
                return;
            }
            if (charPrefab == null)
            {
                EditorUtility.DisplayDialog("Embed Character", $"Missing {CharacterPrefabPath}", "OK");
                return;
            }

            EnsureCharacterSkinVisualOnPrefab(charPrefab, mat);

            string assetPath = AssetDatabase.GetAssetPath(aiPrefab);
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var playerVisual = root.transform.Find(SimpleCharacterAppearance.PlayerVisualPath);
                if (playerVisual == null)
                {
                    EditorUtility.DisplayDialog("Embed Character", "AIPlayer missing PlayerVisual child.", "OK");
                    return;
                }

                for (int i = playerVisual.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(playerVisual.GetChild(i).gameObject);

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(charPrefab, playerVisual);
                inst.name = SimpleCharacterAppearance.EmbeddedCharacterName;
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one * Kitchen.AI.Visual.AIChefVisualInstaller.DefaultVisualLocalScale;

                var visual = inst.GetComponent<CharacterSkinVisual>();
                if (visual == null)
                    visual = inst.AddComponent<CharacterSkinVisual>();
                visual.Prepare();

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                EditorUtility.DisplayDialog(
                    "Embed Character",
                    "Embedded CharacterSimple under AIPlayer/PlayerVisual.\n" +
                    "CharacterSkinVisual owns materials/eyes/tint (catalog characters = empty).",
                    "OK");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [MenuItem("Kitchen/Skin/Ensure CharacterSkinVisual On CharacterSimple")]
        public static void EnsureOnCharacterSimple()
        {
            var charPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPrefabPath);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (charPrefab == null)
            {
                EditorUtility.DisplayDialog("CharacterSkinVisual", $"Missing {CharacterPrefabPath}", "OK");
                return;
            }

            EnsureCharacterSkinVisualOnPrefab(charPrefab, mat);
            EditorUtility.DisplayDialog(
                "CharacterSkinVisual",
                "CharacterSimple now has CharacterSkinVisual (eyes Sphere* stay black).",
                "OK");
        }

        private static void EnsureCharacterSkinVisualOnPrefab(GameObject charPrefab, Material bodyMat)
        {
            string path = AssetDatabase.GetAssetPath(charPrefab);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var visual = root.GetComponent<CharacterSkinVisual>();
                if (visual == null)
                    visual = root.AddComponent<CharacterSkinVisual>();

                var so = new SerializedObject(visual);
                if (bodyMat != null)
                    so.FindProperty("bodyMaterial").objectReferenceValue = bodyMat;
                so.FindProperty("disableAnimation").boolValue = true;
                so.FindProperty("subscribeToSkinManagerTint").boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
