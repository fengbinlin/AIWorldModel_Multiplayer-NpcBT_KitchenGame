using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// Host-side bootstrap: embeds CharacterSimple under PlayerVisual.
    /// Presentation (material / eyes / tint / no-anim) lives on <see cref="CharacterSkinVisual"/>.
    /// Does not read SkinCatalog character entries.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-40)]
    public class CharacterSkinApplier : MonoBehaviour
    {
        [SerializeField] private SkinCharacterKind characterKind = SkinCharacterKind.AIPlayer;
        [SerializeField] private int appearanceIndex;
        [SerializeField] private bool applyOnAwake;

        private bool _applied;
        private CharacterSkinVisual _visual;

        public CharacterSkinVisual Visual => _visual;

        private void Awake()
        {
            if (applyOnAwake)
                Apply();
        }

        public void Apply()
        {
            if (_applied) return;

            var mgr = SkinManager.Instance ?? SkinManager.EnsureExists();
            _visual = mgr.ApplyCharacterAppearance(gameObject, appearanceIndex);
            _applied = true;
        }

        public void SetTint(Color color)
        {
            if (_visual == null)
                _visual = SimpleCharacterAppearance.FindVisual(gameObject);
            if (_visual != null)
                _visual.SetTint(color);
            else
                (SkinManager.Instance ?? SkinManager.EnsureExists()).ApplyCharacterTint(gameObject, color);
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
