using System.Collections.Generic;
using System.Text;
using Kitchen.AI;
using Kitchen.PGC;
using Pathfinding;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 场景级 PGC：本轮菜谱 → 设施/Mission 推断 → 网格布局 → 刷柜 → 出生点。
    /// 挂到 NPC 场景；在 AI 启动前调用 <see cref="RunFullPipeline"/>。
    /// </summary>
    public class PGCManager : MonoBehaviour
    {
        private const string DefaultRecipeResourcePath = "So/Recipes/";

        public static PGCManager Instance { get; private set; }

        [Header("默认开局菜谱")]
        [Tooltip("关闭「随机菜谱」时，本轮只从这份列表里刷单。")]
        [SerializeField]
        private List<RecipeSo> defaultRecipes = new();

        [Header("随机菜谱")]
        [SerializeField]
        private bool useRandomRecipes;

        [SerializeField, Min(1)]
        private int randomRecipeCount = 5;

        [Tooltip("随机抽取的候选全集。若为空，则从 Resources/So/Recipes/ 加载全部。")]
        [SerializeField]
        private List<RecipeSo> randomSourceRecipes = new();

        [Header("布局参数")]
        [SerializeField] private int playW = 16;
        [SerializeField] private int playH = 10;
        [SerializeField] private int dilateKernel = 3;
        [SerializeField] private float pathRandomness = 1.4f;
        [SerializeField] private int extraEdges = 1;
        [SerializeField] private float cellSize = 1.5f;
        [SerializeField] private int layoutSeed = 42;
        [SerializeField] private bool randomizeLayoutSeed = true;

        [Header("出生点")]
        [SerializeField, Min(1)] private int spawnCount = 4;

        [Header("刷场景")]
        [SerializeField] private bool destroyExistingCounters = true;
        [SerializeField] private bool networkSpawnCounters = true;
        [SerializeField] private bool runOnStart; // 一般由 LocalPlayBootstrap 调用

        [Header("调试")]
        [SerializeField] private bool logRoundPool = true;

        private readonly List<RecipeSo> _roundRecipes = new();
        private bool _roundBuilt;
        private PGCLayoutResult _lastLayout;
        private Transform _spawnedRoot;

        public bool UseRandomRecipes => useRandomRecipes;
        public int RandomRecipeCount => randomRecipeCount;
        public IReadOnlyList<RecipeSo> DefaultRecipes => defaultRecipes;
        public IReadOnlyList<RecipeSo> RoundRecipes => _roundRecipes;
        public bool HasRoundPool => _roundBuilt && _roundRecipes.Count > 0;
        public PGCLayoutResult LastLayout => _lastLayout;
        public IReadOnlyList<Vector3> SpawnPoints =>
            _lastLayout != null ? _lastLayout.spawnPoints : System.Array.Empty<Vector3>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void Start()
        {
            if (runOnStart)
                RunFullPipeline();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>
        /// 开局重建本轮菜谱池。已建过且 force=false 时跳过（避免 ReadyToStart 再随机一次）。
        /// </summary>
        public void BuildRoundPool(bool force = false)
        {
            if (_roundBuilt && !force)
                return;

            _roundRecipes.Clear();
            _roundBuilt = false;

            if (useRandomRecipes)
            {
                var source = CollectSourceRecipes();
                int count = Mathf.Clamp(randomRecipeCount, 1, Mathf.Max(1, source.Count));
                PickUniqueRandom(source, count, _roundRecipes);
            }
            else
            {
                foreach (var recipe in defaultRecipes)
                {
                    if (recipe != null && !_roundRecipes.Contains(recipe))
                        _roundRecipes.Add(recipe);
                }
            }

            if (_roundRecipes.Count == 0)
            {
                Debug.LogWarning(
                    "[PGCManager] Round recipe pool is empty; falling back to all Resources recipes.");
                _roundRecipes.AddRange(CollectAllResourceRecipes());
            }

            _roundBuilt = true;
            if (logRoundPool)
                LogRoundPool();
        }

        public RecipeSo PickRandomFromRound()
        {
            if (!_roundBuilt)
                BuildRoundPool();

            if (_roundRecipes.Count == 0)
                return null;

            return _roundRecipes[Random.Range(0, _roundRecipes.Count)];
        }

        /// <summary>
        /// 完整管线：菜谱池 → 设施/Mission → 布局 → 刷柜 → 出生点写入 AIManager。
        /// 须在 Host/Server 就绪后、AI Initialize 之前调用。
        /// </summary>
        public PGCLayoutResult RunFullPipeline()
        {
            BuildRoundPool(force: true);

            RecipeFacilityInferencer.Infer(_roundRecipes, out var facilities, out var edges);

            int seed = randomizeLayoutSeed
                ? Random.Range(0, int.MaxValue)
                : layoutSeed;

            var layoutParams = new PGCLayoutParams
            {
                playW = playW,
                playH = playH,
                kernel = dilateKernel,
                randomness = pathRandomness,
                extraEdges = extraEdges,
                spawnCount = spawnCount,
                cellSize = cellSize,
                seed = seed,
            };

            var generator = new KitchenLayoutGenerator();
            _lastLayout = generator.Generate(facilities, edges, layoutParams);

            bool net = networkSpawnCounters
                       && NetworkManager.Singleton != null
                       && NetworkManager.Singleton.IsServer;

            if (_spawnedRoot != null)
                Destroy(_spawnedRoot.gameObject);

            _spawnedRoot = PGCKitchenSpawner.Spawn(
                _lastLayout, transform, destroyExistingCounters, net);

            ApplySpawnPointsToAIManager();

            // 按新地图尺寸重设 GridGraph，并 Scan（覆盖场景里 A* 的 scanOnStartup 旧图）
            RescanAstar(_lastLayout);

            Debug.Log(
                $"[PGCManager] Pipeline done: recipes={_roundRecipes.Count}, " +
                $"facilities={_lastLayout.facilities.Count}, clears={_lastLayout.clearCount}, " +
                $"walls={_lastLayout.wallCount}, spawns={_lastLayout.spawnPoints.Count}, " +
                $"map={_lastLayout.playW}x{_lastLayout.playH}, seed={seed}");

            return _lastLayout;
        }

        private void ApplySpawnPointsToAIManager()
        {
            var ai = FindObjectOfType<KitchenAIManager>();
            if (ai == null || _lastLayout == null) return;
            ai.SetSpawnWorldPositions(_lastLayout.spawnPoints);
        }

        /// <summary>
        /// 根据 PGC 布局重设 A* 图范围（GridGraph 或 RecastGraph），再全量 Scan。
        /// NPC_PGC 场景用的是 RecastGraph；只改 GridGraph 会导致导航仍是场景里那块偏小的旧 bounds，
        /// AI 落在图外只会直线冲并卡障碍。
        /// </summary>
        private static void RescanAstar(PGCLayoutResult layout)
        {
            if (AstarPath.active == null)
            {
                Debug.LogWarning("[PGCManager] Scene has no AstarPath; skip nav scan.");
                return;
            }

            // 确保新刷出的柜子碰撞体已进入物理世界，再 bake 不可走区域
            Physics.SyncTransforms();

            if (layout == null)
            {
                AstarPath.active.Scan();
                Debug.Log("[PGCManager] A* Scan complete (no layout bounds).");
                return;
            }

            float cell = Mathf.Max(0.1f, layout.cellSize);
            float pad = cell * 2f;
            float worldW = layout.totalW * cell + pad * 2f;
            float worldD = layout.totalH * cell + pad * 2f;

            var center = layout.centerWorld;
            if (center == Vector3.zero)
            {
                center = new Vector3(
                    layout.origin.x + (layout.totalW - 1) * 0.5f * cell,
                    0f,
                    layout.origin.z + (layout.totalH - 1) * 0.5f * cell);
            }

            bool resized = false;

            // --- Recast Graph（NPC_PGC 当前配置）---
            RecastGraph rg = null;
            if (AstarPath.active.data != null)
            {
                rg = AstarPath.active.data.recastGraph;
                if (rg == null)
                {
                    foreach (var g in AstarPath.active.data.graphs)
                    {
                        if (g is RecastGraph found)
                        {
                            rg = found;
                            break;
                        }
                    }
                }
            }

            if (rg != null)
            {
                // Recast 范围固定（与场景厨房地面一致），不随布局 cell 动态放大
                const float recastSizeX = 24f;
                const float recastSizeZ = 15f;
                float height = Mathf.Max(4f, rg.forcedBoundsSize.y);
                var boundsCenter = new Vector3(center.x, height * 0.5f, center.z);
                rg.forcedBoundsCenter = boundsCenter;
                rg.forcedBoundsSize = new Vector3(recastSizeX, height, recastSizeZ);
                resized = true;
                Debug.Log(
                    $"[PGCManager] A* RecastGraph resized → center={boundsCenter}, " +
                    $"size={recastSizeX:F0}x{height:F1}x{recastSizeZ:F0}");
            }

            // --- Grid Graph（若场景同时/改用网格图）---
            GridGraph gg = null;
            if (AstarPath.active.data != null)
            {
                gg = AstarPath.active.data.gridGraph;
                if (gg == null)
                {
                    foreach (var g in AstarPath.active.data.graphs)
                    {
                        if (g is GridGraph found)
                        {
                            gg = found;
                            break;
                        }
                    }
                }
            }

            if (gg != null)
            {
                float nodeSize = Mathf.Max(0.25f, cell * 0.5f);
                int width = Mathf.Max(16, Mathf.CeilToInt(worldW / nodeSize));
                int depth = Mathf.Max(16, Mathf.CeilToInt(worldD / nodeSize));

                gg.center = center;
                gg.SetDimensions(width, depth, nodeSize);
                gg.center = center;
                gg.UpdateTransform();
                resized = true;
                Debug.Log(
                    $"[PGCManager] A* GridGraph resized → center={center}, " +
                    $"nodes={width}x{depth}, nodeSize={nodeSize:F2}, " +
                    $"world≈{worldW:F1}x{worldD:F1}");
            }

            if (!resized)
                Debug.LogWarning("[PGCManager] AstarPath has neither RecastGraph nor GridGraph; Scan() only.");

            AstarPath.active.Scan();
            Debug.Log("[PGCManager] A* Scan complete.");
        }

        private List<RecipeSo> CollectSourceRecipes()
        {
            var source = new List<RecipeSo>();
            if (randomSourceRecipes != null && randomSourceRecipes.Count > 0)
            {
                foreach (var recipe in randomSourceRecipes)
                {
                    if (recipe != null && !source.Contains(recipe))
                        source.Add(recipe);
                }
            }

            if (source.Count == 0)
                source.AddRange(CollectAllResourceRecipes());

            return source;
        }

        private static List<RecipeSo> CollectAllResourceRecipes()
        {
            var loaded = Resources.LoadAll<RecipeSo>(DefaultRecipeResourcePath);
            var list = new List<RecipeSo>(loaded.Length);
            foreach (var recipe in loaded)
            {
                if (recipe != null && !list.Contains(recipe))
                    list.Add(recipe);
            }

            return list;
        }

        private static void PickUniqueRandom(List<RecipeSo> source, int count, List<RecipeSo> dst)
        {
            if (source == null || source.Count == 0 || count <= 0)
                return;

            var bag = new List<RecipeSo>(source);
            count = Mathf.Min(count, bag.Count);
            for (int i = 0; i < count; i++)
            {
                int j = Random.Range(i, bag.Count);
                (bag[i], bag[j]) = (bag[j], bag[i]);
                dst.Add(bag[i]);
            }
        }

        private void LogRoundPool()
        {
            var sb = new StringBuilder();
            sb.Append("[PGCManager] Round pool (").Append(_roundRecipes.Count).Append(") ");
            sb.Append(useRandomRecipes ? $"random/{randomRecipeCount}" : "default").Append(": ");
            for (int i = 0; i < _roundRecipes.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(_roundRecipes[i] != null ? _roundRecipes[i].recipeName : "null");
            }

            Debug.Log(sb.ToString());
        }
    }
}
