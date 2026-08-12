#if UNITY_EDITOR
using Layer_lab._3D_Casual_Character;
using Kitchen.Skin;
using UnityEditor;
using UnityEngine;

namespace Kitchen.EditorTools
{
    /// <summary>
    /// Embeds Layer-lab Character into AIPlayer.prefab under PlayerVisual
    /// so runtime no longer depends on SO character library entries.
    /// </summary>
    public static class EmbedLayerLabCharacterIntoAiPlayer
    {
        private const string AiPlayerPrefabPath = "Assets/Resources/Prefab/AIPlayer.prefab";
        private const string CharacterPrefabPath =
            "Assets/Resources/So/Skin/SkinAsset/Characters/Character_1.prefab";

        [MenuItem("Kitchen/Skin/Embed LayerLab Character Into AIPlayer Prefab")]
        public static void Embed()
        {
            var aiPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AiPlayerPrefabPath);
            var charPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPrefabPath);
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

            string assetPath = AssetDatabase.GetAssetPath(aiPrefab);
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var playerVisual = root.transform.Find(LayerLabCharacterAppearance.PlayerVisualPath);
                if (playerVisual == null)
                {
                    EditorUtility.DisplayDialog("Embed Character", "AIPlayer missing PlayerVisual child.", "OK");
                    return;
                }

                // Remove legacy VisualPrefab / old CharacterVisual.
                for (int i = playerVisual.childCount - 1; i >= 0; i--)
                    Object.DestroyImmediate(playerVisual.GetChild(i).gameObject);

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(charPrefab, playerVisual);
                inst.name = LayerLabCharacterAppearance.EmbeddedCharacterName;
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one * Kitchen.AI.Visual.AIChefVisualInstaller.DefaultVisualLocalScale;

                if (inst.GetComponent<CharacterBase>() == null)
                    inst.AddComponent<CharacterBase>();

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                EditorUtility.DisplayDialog(
                    "Embed Character",
                    "Embedded CharacterVisual under AIPlayer/PlayerVisual.\n" +
                    "SkinManager.randomizeCharacterAppearance controls random Parts at spawn.",
                    "OK");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
