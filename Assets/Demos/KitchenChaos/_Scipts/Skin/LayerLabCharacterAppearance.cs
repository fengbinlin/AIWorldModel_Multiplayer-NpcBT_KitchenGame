using System.Collections.Generic;
using System.Reflection;
using Layer_lab._3D_Casual_Character;
using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// Layer-lab modular character appearance helpers (CharacterDemo-style Parts toggling).
    /// Does not use SkinCatalog character entries.
    /// </summary>
    public static class LayerLabCharacterAppearance
    {
        public const string DefaultCharacterResourcePath = "So/Skin/SkinAsset/Characters/Character_1";
        public const string PlayerVisualPath = "PlayerVisual";
        public const string EmbeddedCharacterName = "CharacterVisual";

        private static readonly MethodInfo SetRootMethod = typeof(CharacterBase).GetMethod(
            "SetRoot",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// Ensures a Layer-lab character exists under PlayerVisual (destroys old mesh VisualPrefab).
        /// Returns the character root (with CharacterBase).
        /// </summary>
        public static CharacterBase EnsureEmbeddedCharacter(
            GameObject hostRoot,
            GameObject characterPrefab = null,
            float localScale = 2.5f)
        {
            if (hostRoot == null) return null;

            var playerVisual = hostRoot.transform.Find(PlayerVisualPath);
            if (playerVisual == null)
            {
                Debug.LogWarning($"[LayerLabAppearance] Missing '{PlayerVisualPath}' on {hostRoot.name}");
                return null;
            }

            var existing = playerVisual.GetComponentInChildren<CharacterBase>(true);
            if (existing != null)
            {
                existing.transform.localScale = Vector3.one * localScale;
                ForceSetRoot(existing);
                return existing;
            }

            var existingNamed = playerVisual.Find(EmbeddedCharacterName);
            if (existingNamed != null)
            {
                var cbExisting = existingNamed.GetComponent<CharacterBase>()
                                 ?? existingNamed.gameObject.AddComponent<CharacterBase>();
                existingNamed.localScale = Vector3.one * localScale;
                ForceSetRoot(cbExisting);
                return cbExisting;
            }

            // Prefab already has a Layer-lab character under PlayerVisual (no CharacterBase yet).
            for (int i = 0; i < playerVisual.childCount; i++)
            {
                var child = playerVisual.GetChild(i);
                if (FindParts(child) == null) continue;
                if (child.GetComponentInChildren<Animator>(true) == null) continue;

                child.name = EmbeddedCharacterName;
                child.localPosition = Vector3.zero;
                child.localRotation = Quaternion.identity;
                child.localScale = Vector3.one * localScale;
                var cbFromChild = child.GetComponent<CharacterBase>() ?? child.gameObject.AddComponent<CharacterBase>();
                ForceSetRoot(cbFromChild);
                return cbFromChild;
            }

            if (characterPrefab == null)
                characterPrefab = Resources.Load<GameObject>(DefaultCharacterResourcePath);
            if (characterPrefab == null)
            {
                Debug.LogWarning(
                    $"[LayerLabAppearance] Missing character prefab at Resources/{DefaultCharacterResourcePath}");
                return null;
            }

            // Clear legacy VisualPrefab / Head / Body mesh hierarchy.
            for (int i = playerVisual.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(playerVisual.GetChild(i).gameObject);

            var inst = Object.Instantiate(characterPrefab, playerVisual);
            inst.name = EmbeddedCharacterName;
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one * localScale;

            var cb = inst.GetComponent<CharacterBase>() ?? inst.AddComponent<CharacterBase>();
            ForceSetRoot(cb);
            return cb;
        }

        public static void ApplyRandomAppearance(CharacterBase character, int seed)
        {
            if (character == null) return;
            ForceSetRoot(character);
            var rng = new System.Random(seed);

            SafeSet(character, PartsType.Hair, character.PartsHair, rng, allowNone: false);
            SafeSet(character, PartsType.Face, character.PartsFace, rng, allowNone: false);
            SafeSet(character, PartsType.Headgear, character.PartsHeadGear, rng, allowNone: true);
            SafeSet(character, PartsType.Top, character.PartsTop, rng, allowNone: false);
            SafeSet(character, PartsType.Bottom, character.PartsBottom, rng, allowNone: false);
            SafeSet(character, PartsType.Eyewear, character.PartsEyewear, rng, allowNone: true);
            SafeSet(character, PartsType.Bag, character.PartsBag, rng, allowNone: true);
            SafeSet(character, PartsType.Shoes, character.PartsShoes, rng, allowNone: false);
            SafeSet(character, PartsType.Glove, character.PartsGlove, rng, allowNone: true);
        }

        public static void ApplyDefaultAppearance(CharacterBase character)
        {
            if (character == null) return;
            ForceSetRoot(character);
            EnsureAtLeastOne(character, PartsType.Hair, character.PartsHair);
            EnsureAtLeastOne(character, PartsType.Face, character.PartsFace);
            EnsureAtLeastOne(character, PartsType.Top, character.PartsTop);
            EnsureAtLeastOne(character, PartsType.Bottom, character.PartsBottom);
            EnsureAtLeastOne(character, PartsType.Shoes, character.PartsShoes);
        }

        private static Transform FindParts(Transform root)
        {
            if (root == null) return null;
            var direct = root.Find("Parts");
            if (direct != null) return direct;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == "Parts")
                    return t;
            }
            return null;
        }

        private static void SafeSet(
            CharacterBase character,
            PartsType type,
            List<GameObject> list,
            System.Random rng,
            bool allowNone)
        {
            if (list == null || list.Count == 0) return;
            int idx;
            if (allowNone && rng.Next(0, 4) == 0)
                idx = -1;
            else
                idx = rng.Next(0, list.Count);
            SafeSetItem(character, type, idx);
        }

        private static void EnsureAtLeastOne(CharacterBase character, PartsType type, List<GameObject> list)
        {
            if (list == null || list.Count == 0) return;
            bool any = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].activeSelf)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
                SafeSetItem(character, type, 0);
        }

        /// <summary>
        /// Same as CharacterBase.SetItem but guards CheckBody when Body variants are missing.
        /// </summary>
        private static void SafeSetItem(CharacterBase character, PartsType type, int idx)
        {
            if (character == null) return;
            try
            {
                character.SetItem(type, idx);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[LayerLabAppearance] SetItem({type},{idx}) failed: {e.Message}");
                // Fallback: toggle list directly without CheckBody.
                var list = type switch
                {
                    PartsType.Hair => character.PartsHair,
                    PartsType.Face => character.PartsFace,
                    PartsType.Headgear => character.PartsHeadGear,
                    PartsType.Top => character.PartsTop,
                    PartsType.Bottom => character.PartsBottom,
                    PartsType.Bag => character.PartsBag,
                    PartsType.Shoes => character.PartsShoes,
                    PartsType.Glove => character.PartsGlove,
                    PartsType.Eyewear => character.PartsEyewear,
                    _ => null,
                };
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null)
                        list[i].SetActive(i == idx);
                }
            }
        }

        private static void ForceSetRoot(CharacterBase character)
        {
            SetRootMethod?.Invoke(character, null);
        }
    }
}
