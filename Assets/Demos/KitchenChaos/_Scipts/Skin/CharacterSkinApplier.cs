using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// 角色：替换 AIPlayer/PlayerVisual/VisualPrefab（整包预制体）。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-40)]
    public class CharacterSkinApplier : MonoBehaviour
    {
        [Header("ID Resolution")]
        [SerializeField] private SkinIdGranularity idGranularity = SkinIdGranularity.Manager;
        [SerializeField] private int specificSkinId;

        [Header("Direct Inspector Fallback (not persisted in a catalog SO)")]
        [SerializeField] private GameObject directVisualPrefab;
        [SerializeField] private SkinCharacterKind characterKind = SkinCharacterKind.AIPlayer;
        [SerializeField] private bool applyOnAwake;
        [SerializeField] private string playerVisualPath = "PlayerVisual";
        [SerializeField] private string visualPrefabChildName = "VisualPrefab";

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
            mgr.TryGetCharacterEntry(characterKind, idGranularity, specificSkinId,
                out var entry, out var skinId);
            if (entry == null || entry.visualPrefab == null)
            {
                entry = new CharacterSkinEntry
                {
                    kind = characterKind,
                    skinId = specificSkinId,
                    visualPrefab = directVisualPrefab,
                };
            }

            if (entry.visualPrefab == null)
            {
                Debug.LogWarning($"[CharacterSkin] No prefab for {characterKind} skinId={skinId}");
                return;
            }

            var playerVisual = transform.Find(playerVisualPath);
            if (playerVisual == null)
            {
                Debug.LogWarning($"[CharacterSkin] Missing '{playerVisualPath}' on {name}");
                return;
            }

            var visualPrefabTf = playerVisual.Find(visualPrefabChildName);
            if (visualPrefabTf == null)
            {
                // 若没有 VisualPrefab 节点，直接在 PlayerVisual 下换整包
                ReplaceChildrenWithPrefab(playerVisual, entry.visualPrefab, visualPrefabChildName);
            }
            else
            {
                ReplaceChildrenWithPrefab(visualPrefabTf, entry.visualPrefab, null);
            }

            var pv = playerVisual.GetComponent<Kitchen.Visual.PlayerVisual>();
            if (pv != null)
                pv.RebindFromHierarchy();

            _applied = true;
        }

        private static void ReplaceChildrenWithPrefab(Transform parent, GameObject prefab, string forceRootName)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
                Object.Destroy(parent.GetChild(i).gameObject);

            var inst = Object.Instantiate(prefab);
            // 若给的是 PlayerVisual 整包：优先取其 VisualPrefab 子树，否则取其子物体
            Transform content = inst.transform.Find("VisualPrefab");
            if (content == null && inst.transform.childCount > 0 &&
                inst.GetComponent<Kitchen.Visual.PlayerVisual>() != null)
            {
                // PlayerVisual 根下直接是 Head/Body
                while (inst.transform.childCount > 0)
                {
                    var child = inst.transform.GetChild(0);
                    child.SetParent(parent, false);
                    child.localPosition = Vector3.zero;
                }
                Object.Destroy(inst);
                return;
            }

            if (content != null)
            {
                while (content.childCount > 0)
                {
                    var child = content.GetChild(0);
                    child.SetParent(parent, false);
                }
                Object.Destroy(inst);
                return;
            }

            inst.transform.SetParent(parent, false);
            if (!string.IsNullOrEmpty(forceRootName))
                inst.name = forceRootName;
            else
                inst.name = prefab.name;
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
        }

        public static void ApplyOn(GameObject go, SkinCharacterKind kind)
        {
            if (go == null) return;
            var applier = go.GetComponent<CharacterSkinApplier>();
            if (applier == null)
                applier = go.AddComponent<CharacterSkinApplier>();
            applier.characterKind = kind;
            applier._applied = false;
            applier.Apply();
        }

        public void SetSpecificSkinId(int skinId, bool refresh = true)
        {
            specificSkinId = skinId;
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
