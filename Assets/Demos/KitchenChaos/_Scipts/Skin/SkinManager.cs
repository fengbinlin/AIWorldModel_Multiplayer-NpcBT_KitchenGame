using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// 场景皮肤管理器：食材/柜子走 Catalog SO。
    /// 角色不走 Catalog —— 表现逻辑在 CharacterSimple 的 CharacterSkinVisual 上；
    /// 仅在需要广播染色时通过 <see cref="CharacterTintRequested"/> 订阅通讯。
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class SkinManager : MonoBehaviour
    {
        public static SkinManager Instance { get; private set; }

        [Header("优先：皮肤数据包 SO（多套皮肤换这个引用）")]
        [Header("配置来源")]
        [SerializeField] private SkinConfigSource configSource = SkinConfigSource.InlineThenCatalog;
        [Header("ID 粒度")]
        [SerializeField] private SkinIdGranularity ingredientIdGranularity = SkinIdGranularity.Kind;
        [SerializeField] private SkinIdGranularity counterIdGranularity = SkinIdGranularity.Kind;
        [SerializeField] private SkinIdGranularity characterIdGranularity = SkinIdGranularity.Kind;
        [Header("皮肤数据包 SO（可选，保留用于食材/柜子）")]
        [SerializeField] private SkinCatalogSo catalogSo;

        [Header("角色形象（不走 Catalog SO；表现在 CharacterSkinVisual 上）")]
        [Tooltip("留空则从 Resources 加载 CharacterSimple。")]
        [SerializeField] private GameObject characterPrefab;
        [SerializeField] private float characterVisualLocalScale = 2.5f;
        [Tooltip("关闭后不再挂 Animancer（默认关闭）。")]
        [SerializeField] private bool enableCharacterAnimation;

        [Header("Inline 食材配置")]
        [SerializeField] private List<IngredientSkinEntry> inlineIngredients = new();
        [Header("Inline 柜子配置")]
        [SerializeField] private List<CounterSkinEntry> inlineCounters = new();
        [Header("Inline 角色配置（已弃用，Catalog/Inline 角色库不再使用）")]
        [SerializeField] private List<CharacterSkinEntry> inlineCharacters = new();
        [Header("Inline ID 选择")]
        [SerializeField] private int inlineDefaultSkinId;
        [SerializeField] private List<IngredientSkinOverride> inlineIngredientOverrides = new();
        [SerializeField] private List<CounterSkinOverride> inlineCounterOverrides = new();
        [SerializeField] private List<CharacterSkinOverride> inlineCharacterOverrides = new();

        [Tooltip("未配置 Catalog SO 时，运行时空 Catalog 所使用的全局 skinId。")]
        [SerializeField] private int fallbackDefaultSkinId;

        private SkinCatalogSo _active;
        private int? _runtimeGlobalSkinId;
        private readonly Dictionary<SkinCounterKind, int> _runtimeCounterSkinIds = new();
        private readonly Dictionary<SkinCharacterKind, int> _runtimeCharacterSkinIds = new();
        private readonly Dictionary<KitchenObjEnum, int> _runtimeIngredientSkinIds = new();
        private int _sessionAppearanceSeed;

        /// <summary>
        /// Optional broadcast for character tint. CharacterSkinVisual may subscribe;
        /// preferred path is host calling CharacterSkinVisual.SetTint directly.
        /// </summary>
        public event Action<GameObject, Color> CharacterTintRequested;

        public SkinCatalogSo ActiveCatalog => _active;
        public SkinConfigSource ConfigSource => configSource;
        public SkinIdGranularity IngredientIdGranularity => ingredientIdGranularity;
        public SkinIdGranularity CounterIdGranularity => counterIdGranularity;
        public SkinIdGranularity CharacterIdGranularity => characterIdGranularity;
        public bool EnableCharacterAnimation => enableCharacterAnimation;
        public int SessionAppearanceSeed => _sessionAppearanceSeed;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            ResolveCatalog();
            BeginAppearanceSession();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public void SetCatalog(SkinCatalogSo catalog)
        {
            catalogSo = catalog;
            ResolveCatalog();
        }

        private void ResolveCatalog()
        {
            if (catalogSo != null)
            {
                _active = catalogSo;
                return;
            }

            _active = ScriptableObject.CreateInstance<SkinCatalogSo>();
            _active.defaultSkinId = fallbackDefaultSkinId;
            _active.name = "RuntimeEmptySkinCatalog";
            Debug.LogWarning("[SkinManager] No SkinCatalogSo assigned; using empty runtime catalog.");
        }

        public static SkinManager EnsureExists()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("SkinManager");
            return go.AddComponent<SkinManager>();
        }

        /// <summary>Runtime-only global selection; this is intentionally not persisted.</summary>
        public void SetRuntimeGlobalSkinId(int skinId) => _runtimeGlobalSkinId = skinId;

        public void SetRuntimeIngredientSkinId(KitchenObjEnum kind, int skinId)
            => _runtimeIngredientSkinIds[kind] = skinId;

        public void SetRuntimeCounterSkinId(SkinCounterKind kind, int skinId)
            => _runtimeCounterSkinIds[kind] = skinId;

        public void SetRuntimeCharacterSkinId(SkinCharacterKind kind, int skinId)
            => _runtimeCharacterSkinIds[kind] = skinId;

        public void ClearRuntimeSkinIds()
        {
            _runtimeGlobalSkinId = null;
            _runtimeIngredientSkinIds.Clear();
            _runtimeCounterSkinIds.Clear();
            _runtimeCharacterSkinIds.Clear();
        }

        /// <summary>
        /// New play session seed so randomized outfits differ across plays.
        /// </summary>
        public void BeginAppearanceSession()
        {
            unchecked
            {
                _sessionAppearanceSeed = Environment.TickCount
                    ^ (Guid.NewGuid().GetHashCode() * 397)
                    ^ (Time.frameCount * 7919);
            }
        }

        /// <summary>
        /// Embed CharacterSimple under PlayerVisual. Tint/materials live on CharacterSkinVisual.
        /// Catalog SO character list is ignored.
        /// </summary>
        public CharacterSkinVisual ApplyCharacterAppearance(GameObject root, int appearanceIndex = 0)
        {
            if (root == null) return null;
            _ = appearanceIndex;

            return SimpleCharacterAppearance.EnsureEmbeddedCharacter(
                root,
                characterPrefab,
                characterVisualLocalScale);
        }

        /// <summary>
        /// Prefer calling CharacterSkinVisual.SetTint on the skin itself.
        /// This helper also raises <see cref="CharacterTintRequested"/> for subscribers.
        /// </summary>
        public void ApplyCharacterTint(GameObject root, Color color)
        {
            if (root == null) return;
            SimpleCharacterAppearance.ApplyTint(root, color);
            CharacterTintRequested?.Invoke(root, color);
        }

        public void SetEnableCharacterAnimation(bool enabled) => enableCharacterAnimation = enabled;

        /// <summary>
        /// Re-applies currently resolved ids to already spawned objects.
        /// Useful after runtime randomization; no catalog asset is modified.
        /// </summary>
        public void RefreshAllVisuals()
        {
            foreach (var applier in FindObjectsOfType<KitchenObjSkinApplier>(true))
                applier.Refresh();
            foreach (var applier in FindObjectsOfType<CounterSkinApplier>(true))
                applier.Refresh();
            foreach (var applier in FindObjectsOfType<CharacterSkinApplier>(true))
                applier.Refresh();
        }

        public int ResolveSkinId(SkinIdGranularity granularity, int kindSkinId, int specificSkinId)
        {
            return granularity switch
            {
                SkinIdGranularity.Global => _runtimeGlobalSkinId ?? ResolveGlobalSkinId(),
                SkinIdGranularity.Kind => kindSkinId,
                SkinIdGranularity.Specific => specificSkinId,
                _ => kindSkinId,
            };
        }

        public int GetIngredientSkinId(KitchenObjEnum kind)
        {
            if (_runtimeIngredientSkinIds.TryGetValue(kind, out var runtimeId)) return runtimeId;
            if (UseInlineBeforeCatalog() && TryGetInlineId(inlineIngredientOverrides, kind, out var inlineId))
                return inlineId;
            if (ActiveCatalog != null) return ActiveCatalog.ResolveIngredientSkinId(kind);
            return TryGetInlineId(inlineIngredientOverrides, kind, out inlineId) ? inlineId : inlineDefaultSkinId;
        }

        public int GetCounterSkinId(SkinCounterKind kind)
        {
            if (_runtimeCounterSkinIds.TryGetValue(kind, out var runtimeId)) return runtimeId;
            if (UseInlineBeforeCatalog() && TryGetInlineId(inlineCounterOverrides, kind, out var inlineId))
                return inlineId;
            if (ActiveCatalog != null) return ActiveCatalog.ResolveCounterSkinId(kind);
            return TryGetInlineId(inlineCounterOverrides, kind, out inlineId) ? inlineId : inlineDefaultSkinId;
        }

        public int GetCharacterSkinId(SkinCharacterKind kind)
        {
            if (_runtimeCharacterSkinIds.TryGetValue(kind, out var runtimeId)) return runtimeId;
            if (UseInlineBeforeCatalog() && TryGetInlineId(inlineCharacterOverrides, kind, out var inlineId))
                return inlineId;
            if (ActiveCatalog != null) return ActiveCatalog.ResolveCharacterSkinId(kind);
            return TryGetInlineId(inlineCharacterOverrides, kind, out inlineId) ? inlineId : inlineDefaultSkinId;
        }

        private bool UseInlineBeforeCatalog()
            => configSource == SkinConfigSource.InlineThenCatalog ||
               configSource == SkinConfigSource.InlineOnly;

        private int ResolveGlobalSkinId()
        {
            if (UseInlineBeforeCatalog()) return inlineDefaultSkinId;
            if (ActiveCatalog != null) return ActiveCatalog.defaultSkinId;
            return inlineDefaultSkinId;
        }

        private static bool TryGetInlineId(
            List<IngredientSkinOverride> overrides, KitchenObjEnum kind, out int skinId)
        {
            skinId = 0;
            foreach (var entry in overrides)
                if (entry != null && entry.kind == kind) { skinId = entry.skinId; return true; }
            return false;
        }

        private static bool TryGetInlineId(
            List<CounterSkinOverride> overrides, SkinCounterKind kind, out int skinId)
        {
            skinId = 0;
            foreach (var entry in overrides)
                if (entry != null && entry.kind == kind) { skinId = entry.skinId; return true; }
            return false;
        }

        private static bool TryGetInlineId(
            List<CharacterSkinOverride> overrides, SkinCharacterKind kind, out int skinId)
        {
            skinId = 0;
            foreach (var entry in overrides)
                if (entry != null && entry.kind == kind) { skinId = entry.skinId; return true; }
            return false;
        }

        public int GetIngredientSkinId(KitchenObjEnum kind, SkinIdGranularity granularity, int specificSkinId)
        {
            if (granularity == SkinIdGranularity.Manager)
                granularity = ingredientIdGranularity;
            return ResolveSkinId(granularity, GetIngredientSkinId(kind), specificSkinId);
        }

        public int GetCounterSkinId(SkinCounterKind kind, SkinIdGranularity granularity, int specificSkinId)
        {
            if (granularity == SkinIdGranularity.Manager)
                granularity = counterIdGranularity;
            return ResolveSkinId(granularity, GetCounterSkinId(kind), specificSkinId);
        }

        public int GetCharacterSkinId(SkinCharacterKind kind, SkinIdGranularity granularity, int specificSkinId)
        {
            if (granularity == SkinIdGranularity.Manager)
                granularity = characterIdGranularity;
            return ResolveSkinId(granularity, GetCharacterSkinId(kind), specificSkinId);
        }

        public bool TryGetIngredientEntry(KitchenObjEnum kind, out IngredientSkinEntry entry, out int skinId)
            => TryGetIngredientEntry(kind, SkinIdGranularity.Kind, 0, out entry, out skinId);

        public bool TryGetIngredientEntry(KitchenObjEnum kind, SkinIdGranularity granularity,
            int specificSkinId, out IngredientSkinEntry entry, out int skinId)
        {
            entry = null;
            skinId = GetIngredientSkinId(kind, granularity, specificSkinId);
            entry = FindIngredient(kind, skinId);
            return entry != null;
        }

        public bool TryGetCounterEntry(SkinCounterKind kind, out CounterSkinEntry entry, out int skinId)
            => TryGetCounterEntry(kind, SkinIdGranularity.Kind, 0, out entry, out skinId);

        public bool TryGetCounterEntry(SkinCounterKind kind, SkinIdGranularity granularity,
            int specificSkinId, out CounterSkinEntry entry, out int skinId)
        {
            entry = null;
            skinId = GetCounterSkinId(kind, granularity, specificSkinId);
            entry = FindCounter(kind, skinId);
            return entry != null;
        }

        public bool TryGetCharacterEntry(SkinCharacterKind kind, out CharacterSkinEntry entry, out int skinId)
            => TryGetCharacterEntry(kind, SkinIdGranularity.Kind, 0, out entry, out skinId);

        public bool TryGetCharacterEntry(SkinCharacterKind kind, SkinIdGranularity granularity,
            int specificSkinId, out CharacterSkinEntry entry, out int skinId)
        {
            entry = null;
            skinId = GetCharacterSkinId(kind, granularity, specificSkinId);
            entry = FindCharacter(kind, skinId);
            return entry != null;
        }

        private IngredientSkinEntry FindIngredient(KitchenObjEnum kind, int skinId)
        {
            bool inlineFirst = configSource == SkinConfigSource.InlineThenCatalog ||
                               configSource == SkinConfigSource.InlineOnly;
            if (inlineFirst)
            {
                var inline = FindInline(inlineIngredients, kind, skinId, inlineDefaultSkinId);
                if (inline != null) return inline;
            }

            if (configSource != SkinConfigSource.InlineOnly && ActiveCatalog != null)
            {
                var catalogEntry = ActiveCatalog.FindIngredient(kind, skinId);
                if (catalogEntry != null) return catalogEntry;
            }

            return !inlineFirst && configSource != SkinConfigSource.CatalogOnly
                ? FindInline(inlineIngredients, kind, skinId, inlineDefaultSkinId)
                : null;
        }

        private CounterSkinEntry FindCounter(SkinCounterKind kind, int skinId)
        {
            bool inlineFirst = configSource == SkinConfigSource.InlineThenCatalog ||
                               configSource == SkinConfigSource.InlineOnly;
            if (inlineFirst)
            {
                var inline = FindInline(inlineCounters, kind, skinId, inlineDefaultSkinId);
                if (inline != null) return inline;
            }

            if (configSource != SkinConfigSource.InlineOnly && ActiveCatalog != null)
            {
                var catalogEntry = ActiveCatalog.FindCounter(kind, skinId);
                if (catalogEntry != null) return catalogEntry;
            }

            return !inlineFirst && configSource != SkinConfigSource.CatalogOnly
                ? FindInline(inlineCounters, kind, skinId, inlineDefaultSkinId)
                : null;
        }

        private CharacterSkinEntry FindCharacter(SkinCharacterKind kind, int skinId)
        {
            bool inlineFirst = configSource == SkinConfigSource.InlineThenCatalog ||
                               configSource == SkinConfigSource.InlineOnly;
            if (inlineFirst)
            {
                var inline = FindInline(inlineCharacters, kind, skinId, inlineDefaultSkinId);
                if (inline != null) return inline;
            }

            if (configSource != SkinConfigSource.InlineOnly && ActiveCatalog != null)
            {
                var catalogEntry = ActiveCatalog.FindCharacter(kind, skinId);
                if (catalogEntry != null) return catalogEntry;
            }

            return !inlineFirst && configSource != SkinConfigSource.CatalogOnly
                ? FindInline(inlineCharacters, kind, skinId, inlineDefaultSkinId)
                : null;
        }

        private static IngredientSkinEntry FindInline(
            List<IngredientSkinEntry> entries, KitchenObjEnum kind, int skinId, int defaultId)
        {
            IngredientSkinEntry fallback = null;
            foreach (var entry in entries)
            {
                if (entry == null || entry.kind != kind) continue;
                if (entry.skinId == skinId) return entry;
                if (entry.skinId == defaultId) fallback = entry;
            }
            return fallback;
        }

        private static CounterSkinEntry FindInline(
            List<CounterSkinEntry> entries, SkinCounterKind kind, int skinId, int defaultId)
        {
            CounterSkinEntry fallback = null;
            foreach (var entry in entries)
            {
                if (entry == null || entry.kind != kind) continue;
                if (entry.skinId == skinId) return entry;
                if (entry.skinId == defaultId) fallback = entry;
            }
            return fallback;
        }

        private static CharacterSkinEntry FindInline(
            List<CharacterSkinEntry> entries, SkinCharacterKind kind, int skinId, int defaultId)
        {
            CharacterSkinEntry fallback = null;
            foreach (var entry in entries)
            {
                if (entry == null || entry.kind != kind) continue;
                if (entry.skinId == skinId) return entry;
                if (entry.skinId == defaultId) fallback = entry;
            }
            return fallback;
        }

        public static SkinCounterKind CounterKindFromComponent(BaseCounter counter)
        {
            if (counter == null) return SkinCounterKind.Clear;
            if (counter.name != null && counter.name.StartsWith("Wall_"))
                return SkinCounterKind.Wall;
            return counter switch
            {
                ContainerCounter => SkinCounterKind.Container,
                CuttingCounter => SkinCounterKind.Cutting,
                StoveCounter => SkinCounterKind.Stove,
                OvenCounter => SkinCounterKind.Oven,
                BlenderCounter => SkinCounterKind.Blender,
                PlatesCounter => SkinCounterKind.Plates,
                DeliveryCounter => SkinCounterKind.Delivery,
                TrashCounter => SkinCounterKind.Trash,
                ClearCounter => SkinCounterKind.Clear,
                _ => SkinCounterKind.Clear,
            };
        }
    }
}
