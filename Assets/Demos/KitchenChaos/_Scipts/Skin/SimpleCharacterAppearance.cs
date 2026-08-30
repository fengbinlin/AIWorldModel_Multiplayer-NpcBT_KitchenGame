using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// Embeds CharacterSimple under PlayerVisual. Catalog SO is not used for characters —
    /// presentation lives on <see cref="CharacterSkinVisual"/> on the skin prefab itself.
    /// </summary>
    public static class SimpleCharacterAppearance
    {
        public const string DefaultCharacterResourcePath = "So/Skin/SkinAsset/Characters/CharacterSimple";
        public const string PlayerVisualPath = "PlayerVisual";
        public const string EmbeddedCharacterName = "CharacterVisual";

        /// <summary>
        /// Ensures CharacterSimple (+ CharacterSkinVisual) exists under PlayerVisual.
        /// Replaces any previous Layer-lab / old visual.
        /// </summary>
        public static CharacterSkinVisual EnsureEmbeddedCharacter(
            GameObject hostRoot,
            GameObject characterPrefab = null,
            float localScale = 2.5f)
        {
            if (hostRoot == null) return null;

            var playerVisual = hostRoot.transform.Find(PlayerVisualPath);
            if (playerVisual == null)
            {
                Debug.LogWarning($"[SimpleCharacterAppearance] Missing '{PlayerVisualPath}' on {hostRoot.name}");
                return null;
            }

            if (characterPrefab == null)
                characterPrefab = Resources.Load<GameObject>(DefaultCharacterResourcePath);
            if (characterPrefab == null)
            {
                Debug.LogWarning(
                    $"[SimpleCharacterAppearance] Missing prefab Resources/{DefaultCharacterResourcePath}");
                return null;
            }

            for (int i = playerVisual.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(playerVisual.GetChild(i).gameObject);

            var inst = Object.Instantiate(characterPrefab, playerVisual);
            inst.name = EmbeddedCharacterName;
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one * localScale;

            var visual = inst.GetComponent<CharacterSkinVisual>();
            if (visual == null)
                visual = inst.AddComponent<CharacterSkinVisual>();
            visual.Prepare();
            return visual;
        }

        public static CharacterSkinVisual FindVisual(GameObject hostRoot)
        {
            if (hostRoot == null) return null;
            var embedded = hostRoot.transform.Find($"{PlayerVisualPath}/{EmbeddedCharacterName}");
            if (embedded != null)
            {
                var v = embedded.GetComponent<CharacterSkinVisual>();
                if (v != null) return v;
            }
            return hostRoot.GetComponentInChildren<CharacterSkinVisual>(true);
        }

        public static void ApplyTint(GameObject hostRoot, Color color)
        {
            var visual = FindVisual(hostRoot);
            if (visual != null)
                visual.SetTint(color);
        }
    }
}
