using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// 食材逻辑根上：按 SkinManager 套 Mesh+Material 或整包 Visual 预制体（生成时一次）。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public class KitchenObjSkinApplier : MonoBehaviour
    {
        [Header("ID Resolution")]
        [SerializeField] private SkinIdGranularity idGranularity = SkinIdGranularity.Manager;
        [SerializeField] private int specificSkinId;

        [Header("Direct Inspector Fallback (not persisted in a catalog SO)")]
        [SerializeField] private SkinMeshMaterial directMeshMaterial = new();
        [SerializeField] private GameObject directVisualPrefab;
        [SerializeField] private SkinPreferMode directPreferMode = SkinPreferMode.PreferMeshMaterial;
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
            var kitchenObj = GetComponent<KitchenObj>();
            if (kitchenObj == null) return;

            var mgr = SkinManager.Instance ?? SkinManager.EnsureExists();
            mgr.TryGetIngredientEntry(kitchenObj.objEnum, idGranularity, specificSkinId,
                out var entry, out var skinId);
            bool catalogHasVisual = entry != null &&
                                    ((entry.meshMaterial != null && entry.meshMaterial.HasAny) ||
                                     entry.visualPrefab != null);
            if (!catalogHasVisual)
            {
                entry = new IngredientSkinEntry
                {
                    kind = kitchenObj.objEnum,
                    skinId = specificSkinId,
                    meshMaterial = directMeshMaterial,
                    visualPrefab = directVisualPrefab,
                    preferMode = directPreferMode,
                };
            }

            bool hasMm = entry.meshMaterial != null && entry.meshMaterial.HasAny;
            bool hasPrefab = entry.visualPrefab != null;
            var mode = SkinCatalogSo.ResolveMode(hasMm, hasPrefab, entry.preferMode);
            if (mode == SkinResolvedMode.None)
            {
                Debug.LogWarning($"[KitchenObjSkin] No visual for {kitchenObj.objEnum} skinId={skinId}");
                return;
            }

            if (mode == SkinResolvedMode.Prefab)
            {
                SkinMeshUtil.ReplaceVisualChild(transform, entry.visualPrefab);
            }
            else
            {
                // 默认：找视觉子物体上的 MeshFilter 替换
                Transform visual = FindVisualChild();
                if (visual != null)
                    SkinMeshUtil.ApplyMeshMaterialToFirstMesh(visual, entry.meshMaterial.mesh, entry.meshMaterial.material);
                else
                    SkinMeshUtil.ApplyMeshMaterialToFirstMesh(transform, entry.meshMaterial.mesh, entry.meshMaterial.material);
            }

            _applied = true;
        }

        private Transform FindVisualChild()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var c = transform.GetChild(i);
                if (c.name.Contains("_Visual") || c.name.EndsWith("Visual") || c.name.Contains("mesh"))
                    return c;
            }
            return transform.childCount > 0 ? transform.GetChild(0) : null;
        }

        public static void ApplyOn(GameObject go)
        {
            if (go == null) return;
            var applier = go.GetComponent<KitchenObjSkinApplier>();
            if (applier == null)
                applier = go.AddComponent<KitchenObjSkinApplier>();
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
