using System.Collections.Generic;
using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// 柜子逻辑根上：按部件槽套 Mesh+Material，或整包替换 *_Visual。
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public class CounterSkinApplier : MonoBehaviour
    {
        [Header("ID Resolution")]
        [SerializeField] private SkinIdGranularity idGranularity = SkinIdGranularity.Manager;
        [SerializeField] private int specificSkinId;

        [Header("Direct Inspector Fallback (not persisted in a catalog SO)")]
        [SerializeField] private List<CounterPartVisual> directParts = new();
        [SerializeField] private GameObject directVisualPrefab;
        [SerializeField] private SkinPreferMode directPreferMode = SkinPreferMode.PreferMeshMaterial;
        [SerializeField] private bool applyOnStart;
        [Tooltip("本柜声明用到的槽；为空则自动扫描子物体")]
        [SerializeField] private List<CounterPartSlot> usedSlots = new();

        private bool _applied;

        private void Start()
        {
            if (applyOnStart)
                Apply();
        }

        public void Apply()
        {
            if (_applied) return;
            var counter = GetComponent<BaseCounter>();
            if (counter == null) return;

            var kind = SkinManager.CounterKindFromComponent(counter);
            var mgr = SkinManager.Instance ?? SkinManager.EnsureExists();
            mgr.TryGetCounterEntry(kind, idGranularity, specificSkinId,
                out var entry, out var skinId);
            bool catalogHasVisual = entry != null &&
                                    ((entry.parts != null && HasAnyPartData(entry)) ||
                                     entry.visualPrefab != null);
            if (!catalogHasVisual)
            {
                entry = new CounterSkinEntry
                {
                    kind = kind,
                    skinId = specificSkinId,
                    parts = directParts,
                    visualPrefab = directVisualPrefab,
                    preferMode = directPreferMode,
                };
            }

            bool hasParts = entry.parts != null && entry.parts.Count > 0 && HasAnyPartData(entry);
            bool hasPrefab = entry.visualPrefab != null;
            var mode = SkinCatalogSo.ResolveMode(hasParts, hasPrefab, entry.preferMode);
            if (mode == SkinResolvedMode.None)
            {
                // 无皮肤数据时保持预制体原样（正常）
                return;
            }

            if (mode == SkinResolvedMode.Prefab)
            {
                var inst = SkinMeshUtil.ReplaceVisualChild(transform, entry.visualPrefab);
                if (inst != null && counter is ContainerCounter)
                {
                    var visual = inst.GetComponent<ContainerCounterVisual>();
                    if (visual == null)
                        visual = inst.AddComponent<ContainerCounterVisual>();
                    // 换肤可能发生在 Start 之后（PGC 在 Spawn 后再 ApplyOn），补一次订阅
                    visual.TrySubscribe();
                }
                else if (inst != null && counter is PlatesCounter)
                {
                    var visual = inst.GetComponent<PlatesCounterVisual>();
                    if (visual == null)
                        visual = inst.AddComponent<PlatesCounterVisual>();
                    visual.TryBuildStack();
                }

                _applied = true;
                return;
            }

            // Mesh+Material：按槽位写到 Visual 子物体
            var slotMap = BuildSlotMap();
            foreach (var part in entry.parts)
            {
                if (part == null || !part.HasAny) continue;
                if (usedSlots.Count > 0 && !usedSlots.Contains(part.slot)) continue;
                if (!slotMap.TryGetValue(part.slot, out var target) || target == null) continue;
                SkinMeshUtil.ApplyMeshMaterial(target.gameObject, part.mesh, part.material);
            }

            _applied = true;
        }

        private static bool HasAnyPartData(CounterSkinEntry entry)
        {
            foreach (var p in entry.parts)
                if (p != null && p.HasAny) return true;
            return false;
        }

        private Dictionary<CounterPartSlot, Transform> BuildSlotMap()
        {
            var map = new Dictionary<CounterPartSlot, Transform>();

            // 显式 Marker 优先
            foreach (var marker in GetComponentsInChildren<CounterPartSlotMarker>(true))
            {
                if (marker == null) continue;
                map[marker.slot] = marker.transform;
            }

            // 按常见名字自动推断
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == transform) continue;
                if (!TryInferSlot(t.name, out var slot)) continue;
                if (!map.ContainsKey(slot))
                    map[slot] = t;
            }

            return map;
        }

        private static bool TryInferSlot(string name, out CounterPartSlot slot)
        {
            slot = CounterPartSlot.CounterBody;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Replace(" ", "").Replace("_", "").ToLowerInvariant();

            if (n.Contains("knife") || n.Contains("刀"))
            {
                slot = CounterPartSlot.Knife;
                return true;
            }
            if (n.Contains("chopping") || n.Contains("cuttingboard") || n.Contains("board") || n.Contains("菜板"))
            {
                slot = CounterPartSlot.CuttingBoard;
                return true;
            }
            if (n.Contains("pot") || n.Contains("pan") || n.Contains("frying") || n.Contains("锅"))
            {
                slot = CounterPartSlot.Pot;
                return true;
            }
            if (n.Contains("stove") || n.Contains("burner") || n.Contains("炉"))
            {
                slot = CounterPartSlot.StoveBurner;
                return true;
            }
            if (n.Contains("trash") || n.Contains("bin"))
            {
                slot = CounterPartSlot.TrashBin;
                return true;
            }
            if (n.Contains("door") || n.Contains("lid"))
            {
                slot = CounterPartSlot.ContainerDoor;
                return true;
            }
            if (n.Contains("kitchencounter") || n == "counter" || n.Contains("counterbody"))
            {
                slot = CounterPartSlot.CounterBody;
                return true;
            }

            return false;
        }

        public static void ApplyOn(GameObject go)
        {
            if (go == null) return;
            var applier = go.GetComponent<CounterSkinApplier>();
            if (applier == null)
                applier = go.AddComponent<CounterSkinApplier>();
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
