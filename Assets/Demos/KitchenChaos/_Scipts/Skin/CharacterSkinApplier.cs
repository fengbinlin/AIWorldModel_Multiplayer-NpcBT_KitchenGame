using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// Applies Layer-lab character appearance under PlayerVisual.
    /// Character looks are no longer resolved from SkinCatalog SO character entries;
    /// SkinManager controls embed + optional per-chef / per-session randomization.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-40)]
    public class CharacterSkinApplier : MonoBehaviour
    {
        [Header("Character kind (legacy; appearance no longer uses SO library)")]
        [SerializeField] private SkinCharacterKind characterKind = SkinCharacterKind.AIPlayer;

        [Tooltip("Stable index for this chef within a play session (AI0, AI1, …). Player can use 0.")]
        [SerializeField] private int appearanceIndex;

        [SerializeField] private bool applyOnAwake;

        private bool _applied;

        private void Awake()
        {
            if (applyOnAwake)
                Apply();
        }

        public void Apply()
        {
            if (_applied) return;

            var mgr = SkinManager.Instance ?? SkinManager.EnsureExists();
            mgr.ApplyCharacterAppearance(gameObject, appearanceIndex);

            var playerVisual = transform.Find(LayerLabCharacterAppearance.PlayerVisualPath);
            var pv = playerVisual != null
                ? playerVisual.GetComponent<Kitchen.Visual.PlayerVisual>()
                : null;
            if (pv != null)
                pv.RebindFromHierarchy();

            _applied = true;
        }

        public static void ApplyOn(GameObject go, SkinCharacterKind kind)
        {
            ApplyOn(go, kind, appearanceIndex: 0);
        }

        public static void ApplyOn(GameObject go, int appearanceIndex)
        {
            ApplyOn(go, SkinCharacterKind.AIPlayer, appearanceIndex);
        }

        public static void ApplyOn(GameObject go, SkinCharacterKind kind, int appearanceIndex)
        {
            if (go == null) return;
            var applier = go.GetComponent<CharacterSkinApplier>();
            if (applier == null)
                applier = go.AddComponent<CharacterSkinApplier>();
            applier.characterKind = kind;
            applier.appearanceIndex = appearanceIndex;
            applier._applied = false;
            applier.Apply();
        }

        public void SetAppearanceIndex(int index, bool refresh = true)
        {
            appearanceIndex = index;
            if (refresh) Refresh();
            else _applied = false;
        }

        public void Refresh()
        {
            _applied = false;
            Apply();
        }
    }
}
