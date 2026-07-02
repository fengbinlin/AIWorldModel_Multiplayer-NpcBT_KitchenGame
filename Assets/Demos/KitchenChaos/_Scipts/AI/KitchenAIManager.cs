using System.Collections.Generic;
using System.Linq;
using Kitchen.Player;
using Kitchen.Visual;
using Nico.Network;
using Pathfinding;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// Central AI scheduler — the "brain" of the kitchen.
    ///
    /// Runs a scheduling loop every SCHEDULE_INTERVAL seconds:
    ///   1. Sync blackboard state
    ///   2. Generate all candidate tasks
    ///   3. Score and greedily assign to idle agents
    ///   4. Dispatch tasks to AIChefControllers
    ///
    /// Also provides deadlock detection and force-serve logic.
    /// </summary>
    public class KitchenAIManager : MonoBehaviour
    {
        public static KitchenAIManager Instance { get; private set; }

        [Header("Scheduling")]
        [SerializeField] private float _scheduleInterval = 0.5f;
        [SerializeField] private bool _autoStart = true;

        [Header("AI Chef Spawning")]
        [SerializeField] private GameObject _aiChefPrefab;
        [SerializeField] private List<Transform> _spawnPoints = new();
        [SerializeField] private float _aiMoveSpeed = 8f;
        [SerializeField] private float _aiInteractionRange = 2f;
        [Range(0.1f, 1f)][SerializeField] private float _aiArrivalThreshold = 0.4f;
        [SerializeField] private float _aiStuckTimeout = 8f;
        [SerializeField] private float _aiAgentRadius = 0.9f;
        [Range(0f, 1f)][SerializeField] private float _aiRvoBasePriority = 0.5f;
        [SerializeField] private float _aiApproachOffset = 0.5f;
        [SerializeField] private List<Color> _aiColors = new()
        {
            Color.red, Color.blue, Color.green, Color.yellow,
            Color.cyan, Color.magenta, new Color(1f, 0.5f, 0f), new Color(0.5f, 0f, 1f),
        };

        [Header("AI Chef References (scene-placed)")]
        [SerializeField] private List<AIChefController> _aiChefs = new();

        [Header("Debug")]
        [SerializeField] private bool _verboseLogging = true;

        // ===== Blackboard =====
        private KitchenBlackboard _blackboard;
        public KitchenBlackboard Blackboard => _blackboard;
        private float _scheduleTimer;

        // ===== Agent Registry =====
        private List<AgentState> _agentStates = new();
        private int _nextAgentId = 1;

        // ===== State =====
        private bool _isInitialized;
        private int _schedulerCycle;
        private float _zeroTaskTimer; // Time spent with 0 tasks available
        private float _lastBlackboardDumpTime; // Throttle blackboard dumps

        #region Unity Lifecycle

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            _blackboard = new KitchenBlackboard();
        }

        private void Start()
        {
            if (_autoStart)
                Initialize();
        }

        private void Update()
        {
            // Flush debug log periodically
            AIDebugLogger.Update();

            if (!_isInitialized) return;
            if (!GameManager.Instance.IsPlaying()) return;
            if (!NetworkManager.Singleton.IsServer) return;

            // Sync blackboard state
            _blackboard.SyncItems();
            SyncFacilityStates();
            SyncAgentPositions();

            // Scheduling loop
            _scheduleTimer += Time.deltaTime;
            if (_scheduleTimer >= _scheduleInterval)
            {
                _scheduleTimer = 0f;
                RunScheduler();
            }
        }

        #endregion

        #region Initialization

        public void Initialize()
        {
            if (_isInitialized) return;
            _blackboard.ScanFacilities();
            _blackboard.LoadRecipes();

            // Note: We do NOT convert RecastGraph → GridGraph.
            // GridGraph does NOT support NavmeshCut in this A* version
            // (enableNavmeshCutting only exists on NavmeshBase subclasses).
            // RecastGraph with enableNavmeshCutting=true handles NavmeshCut
            // correctly, and for 4 small agent-radius cuts the performance
            // difference is negligible.

            // Ensure 4 graphs (one per AI). Each AI pathfinds on its own
            // graph which has cuts from the other 3 AIs but not its own.
            EnsureGraphs(4);

            // Spawn AI chefs from prefab at each spawn point
            if (_aiChefPrefab != null && _spawnPoints.Count > 0)
            {
                int colorIdx = 0;
                foreach (var spawnPoint in _spawnPoints)
                {
                    if (spawnPoint == null) continue;
                    var chefObj = Instantiate(_aiChefPrefab, spawnPoint.position, spawnPoint.rotation);
                    chefObj.tag = "Untagged";
                    chefObj.layer = LayerMask.NameToLayer("Default");

                    // Disable Player and network transform components (AI uses A* Pathfinding Project)
                    var playerComp = chefObj.GetComponent<Player.Player>();
                    if (playerComp != null) playerComp.enabled = false;
                    var cnt = chefObj.GetComponent<ClientNetworkTransform>();
                    if (cnt != null) cnt.enabled = false;

                    // Strip visual/animation components from PlayerVisual child
                    var visual = chefObj.transform.Find("PlayerVisual");
                    if (visual != null)
                    {
                        Destroy(visual.GetComponent<Animator>());
                        Destroy(visual.GetComponent<PlayerVisual>());
                        Destroy(visual.GetComponent<PlayerAnimator>());
                        Destroy(visual.GetComponent<ClientNetworkAnimator>());
                    }

                    var chef = chefObj.GetComponent<AIChefController>();
                    if (chef == null)
                        chef = chefObj.AddComponent<AIChefController>();
                    chef.moveSpeed = _aiMoveSpeed;
                    chef.interactionRange = _aiInteractionRange;
                    chef.arrivalThreshold = _aiArrivalThreshold;
                    chef.stuckTimeout = _aiStuckTimeout;

                    float rvoPriority = Mathf.Clamp01(_aiRvoBasePriority + colorIdx * 0.05f);
                    chef.SetAIParams(colorIdx, _aiAgentRadius, _aiMoveSpeed, rvoPriority, _aiApproachOffset);

                    // Assign distinct color
                    Color c = _aiColors.Count > 0
                        ? _aiColors[colorIdx % _aiColors.Count]
                        : Color.HSVToRGB((float)colorIdx / _spawnPoints.Count, 0.8f, 0.9f);
                    chef.chefColor = c;
                    foreach (var r in chefObj.GetComponentsInChildren<Renderer>())
                    {
                        foreach (var m in r.materials)
                            m.color = c;
                    }
                    colorIdx++;

                    _aiChefs.Add(chef);
                    RegisterAgent(chef);
                }
            }

            // Register scene-placed AI chefs
            foreach (var chef in _aiChefs)
            {
                if (chef != null && !_agentStates.Any(a => a.controller == chef))
                    RegisterAgent(chef);
            }

            // Also find any AIChefControllers in the scene not in the list
            var allChefs = FindObjectsOfType<AIChefController>();
            foreach (var chef in allChefs)
            {
                if (!_aiChefs.Contains(chef))
                {
                    _aiChefs.Add(chef);
                    RegisterAgent(chef);
                }
            }

            // Apply common params to ALL chefs (including scene-placed)
            int graphIdx = 0;
            foreach (var chef in _aiChefs)
            {
                if (chef == null) continue;
                chef.interactionRange = _aiInteractionRange;
                chef.arrivalThreshold = _aiArrivalThreshold;
                chef.stuckTimeout = _aiStuckTimeout;
                chef.SetApproachOffset(_aiApproachOffset);
                if (chef.NavGraphIndex < 0)
                    chef.SetAIParams(graphIdx++, _aiAgentRadius, _aiMoveSpeed,
                        Mathf.Clamp01(_aiRvoBasePriority + graphIdx * 0.05f), _aiApproachOffset);
            }

            // Final full scan: now that every AI's NavmeshCut is enabled with the
            // correct per-graph graphMask, regenerate all graphs so each graph
            // has only the cuts meant for it (its own AI excluded).
            AstarPath.active.Scan();
            AstarPath.active.navmeshUpdates.ForceUpdate();
            AstarPath.active.FlushGraphUpdates();

            _isInitialized = true;
            Debug.Log($"[KitchenAIManager] Initialized with {_agentStates.Count} AI chefs, " +
                      $"{_blackboard.facilities.Count} facilities, " +
                      $"{_blackboard.allRecipes.Count} recipes, " +
                      $"{AstarPath.active.data.graphs.Length} navmesh graphs");
            AIDebugLogger.Log("Init", $"KitchenAIManager initialized: {_agentStates.Count} chefs, " +
                $"{_blackboard.facilities.Count} facilities, " +
                $"{AstarPath.active.data.graphs.Length} graphs");
        }

        /// <summary>
        /// One-time conversion: RecastGraph → GridGraph.
        /// GridGraph handles NavmeshCut updates by just marking nodes unwalkable
        /// (instant), vs RecastGraph which re-triangulates tiles (slow, async).
        /// Reads all scan parameters from the existing RecastGraph so the user's
        /// scene setup (bounds, layer mask, slopes, etc.) is preserved.
        /// </summary>
        private static void ConvertToGridGraph()
        {
            if (AstarPath.active == null)
            {
                Debug.LogWarning("[KitchenAIManager] ConvertToGridGraph: AstarPath.active is null, skipping.");
                return;
            }
            var data = AstarPath.active.data;
            if (data == null || data.graphs == null || data.graphs.Length == 0)
            {
                Debug.LogWarning("[KitchenAIManager] ConvertToGridGraph: no graphs to convert (data or graphs array is null/empty).");
                return;
            }

            // Guard against null graph entries (can happen if scene serialization was
            // interrupted, e.g. power loss during edit).
            if (data.graphs[0] == null)
            {
                Debug.LogWarning("[KitchenAIManager] ConvertToGridGraph: graph[0] is null (corrupted data). " +
                    "Skipping conversion — EnsureGraphs will rebuild from scratch.");
                return;
            }

            var recast = data.graphs[0] as RecastGraph;
            if (recast == null) return; // Already GridGraph or other type

            Debug.Log("[KitchenAIManager] Converting RecastGraph → GridGraph (dynamic obstacles prefer grid)...");

            // --- Read RecastGraph scan settings ---
            var center       = recast.forcedBoundsCenter;
            var size         = recast.forcedBoundsSize;
            var layerMask    = recast.collectionSettings.layerMask;
            var maxSlope     = recast.maxSlope;
            var climb        = recast.walkableClimb;
            var height       = recast.walkableHeight;
            var charRadius   = recast.characterRadius;
            var initPenalty  = recast.initialPenalty;

            // Derive node size from Recast's cellSize, clamped to reasonable range
            float nodeSize   = Mathf.Clamp(recast.cellSize, 0.2f, 0.6f);
            int gridWidth    = Mathf.CeilToInt(size.x / nodeSize);
            int gridDepth    = Mathf.CeilToInt(size.z / nodeSize);

            // Remove all existing RecastGraphs (iterate backwards, skip nulls)
            for (int i = data.graphs.Length - 1; i >= 0; i--)
            {
                if (data.graphs[i] != null)
                    data.RemoveGraph(data.graphs[i]);
            }

            // --- Create 4 independent GridGraphs ---
            for (int i = 0; i < 4; i++)
            {
                var g = data.AddGraph(typeof(GridGraph)) as GridGraph;
                g.center              = center;
                g.SetDimensions(gridWidth, gridDepth, nodeSize);
                g.maxSlope            = maxSlope;
                g.maxStepHeight       = climb;
                g.cutCorners          = true;
                g.neighbours          = NumNeighbours.Eight;
                g.initialPenalty      = initPenalty;
                g.collision.type      = Pathfinding.Graphs.Grid.ColliderType.Capsule;
                g.collision.diameter  = charRadius / nodeSize * 2f; // convert radius→diameter in node units
                g.collision.height    = height;
                g.collision.mask      = layerMask;
            }

            AstarPath.active.Scan();
            Debug.Log($"[KitchenAIManager] GridGraph conversion done: {gridWidth}x{gridDepth} nodeSize={nodeSize:F2}");
        }

        /// <summary>
        /// Ensure we have enough graphs (one per AI).
        /// Copies scan settings from graph 0 to new graphs manually (no reflection).
        /// Handles both RecastGraph and GridGraph. Each graph gets independent data
        /// when Scan() runs.
        /// Each AI's NavmeshCut.graphMask excludes its own graph, and its
        /// Seeker.graphMask restricts pathfinding to only its own graph.
        /// </summary>
        private static void EnsureGraphs(int count)
        {
            if (AstarPath.active == null) return;
            var data = AstarPath.active.data;
            if (data == null || data.graphs == null) return;

            // Count valid (non-null) graphs — corrupted data may have null entries
            int validCount = 0;
            for (int i = 0; i < data.graphs.Length; i++)
                if (data.graphs[i] != null) validCount++;

            if (validCount >= count) return;

            // Find the first valid graph to use as template
            NavGraph template = null;
            for (int i = 0; i < data.graphs.Length; i++)
            {
                if (data.graphs[i] != null)
                {
                    template = data.graphs[i];
                    break;
                }
            }

            if (template == null)
            {
                // No valid graphs at all — create fresh GridGraphs
                Debug.LogWarning("[KitchenAIManager] EnsureGraphs: no valid graph templates, creating default GridGraphs.");
                // Remove all null entries first (iterate backwards)
                for (int i = data.graphs.Length - 1; i >= 0; i--)
                {
                    if (data.graphs[i] != null)
                        data.RemoveGraph(data.graphs[i]);
                }
                // Now data.graphs should be empty — create 4 default GridGraphs
                for (int i = 0; i < count; i++)
                {
                    var g = data.AddGraph(typeof(GridGraph)) as GridGraph;
                    if (g != null)
                    {
                        g.SetDimensions(50, 50, 0.5f);
                        g.center = Vector3.zero;
                        g.maxSlope = 45f;
                        g.collision.type = Pathfinding.Graphs.Grid.ColliderType.Capsule;
                        g.collision.diameter = 1f;
                        g.collision.height = 1.8f;
                        g.collision.mask = LayerMask.GetMask("Default");
                    }
                }
                AstarPath.active.Scan();
                AstarPath.active.navmeshUpdates.ForceUpdate();
                Debug.Log($"[KitchenAIManager] EnsureGraphs: created {count} default GridGraphs (no template available).");
                return;
            }

            var type = template.GetType();
            int existing = validCount;

            Debug.Log($"[KitchenAIManager] Expanding graphs from {existing} to {count} (type={type.Name})");

            // --- RecastGraph path ---
            var recastTemplate = template as RecastGraph;
            if (recastTemplate != null)
            {
                for (int i = existing; i < count; i++)
                {
                    var newGraph = data.AddGraph(type) as RecastGraph;
                    if (newGraph == null) continue;

                    newGraph.forcedBoundsCenter = recastTemplate.forcedBoundsCenter;
                    newGraph.forcedBoundsSize   = recastTemplate.forcedBoundsSize;
                    newGraph.rotation           = recastTemplate.rotation;
                    newGraph.dimensionMode      = recastTemplate.dimensionMode;
                    newGraph.characterRadius    = recastTemplate.characterRadius;
                    newGraph.walkableHeight     = recastTemplate.walkableHeight;
                    newGraph.walkableClimb      = recastTemplate.walkableClimb;
                    newGraph.maxSlope           = recastTemplate.maxSlope;
                    newGraph.cellSize           = recastTemplate.cellSize;
                    newGraph.useTiles           = recastTemplate.useTiles;
                    newGraph.editorTileSize     = recastTemplate.editorTileSize;
                    newGraph.maxEdgeLength      = recastTemplate.maxEdgeLength;
                    newGraph.contourMaxError    = recastTemplate.contourMaxError;
                    newGraph.minRegionSize      = recastTemplate.minRegionSize;
                    newGraph.collectionSettings.collectionMode              = recastTemplate.collectionSettings.collectionMode;
                    newGraph.collectionSettings.layerMask                    = recastTemplate.collectionSettings.layerMask;
                    newGraph.collectionSettings.tagMask                     = recastTemplate.collectionSettings.tagMask;
                    newGraph.collectionSettings.rasterizeMeshes             = recastTemplate.collectionSettings.rasterizeMeshes;
                    newGraph.collectionSettings.rasterizeColliders          = recastTemplate.collectionSettings.rasterizeColliders;
                    newGraph.collectionSettings.rasterizeTerrain            = recastTemplate.collectionSettings.rasterizeTerrain;
                    newGraph.collectionSettings.rasterizeTrees              = recastTemplate.collectionSettings.rasterizeTrees;
                    newGraph.collectionSettings.terrainHeightmapDownsamplingFactor = recastTemplate.collectionSettings.terrainHeightmapDownsamplingFactor;
                    newGraph.collectionSettings.colliderRasterizeDetail     = recastTemplate.collectionSettings.colliderRasterizeDetail;
                    newGraph.enableNavmeshCutting = recastTemplate.enableNavmeshCutting;
                    newGraph.initialPenalty       = recastTemplate.initialPenalty;
                    newGraph.scanEmptyGraph       = recastTemplate.scanEmptyGraph;
                    newGraph.perLayerModifications = recastTemplate.perLayerModifications;
                }
            }
            // --- GridGraph path ---
            else
            {
                var gridTemplate = template as GridGraph;
                if (gridTemplate != null)
                {
                    for (int i = existing; i < count; i++)
                    {
                        var newGraph = data.AddGraph(type) as GridGraph;
                        if (newGraph == null) continue;

                        newGraph.center              = gridTemplate.center;
                        newGraph.SetDimensions(gridTemplate.width, gridTemplate.depth, gridTemplate.nodeSize);
                        newGraph.rotation            = gridTemplate.rotation;
                        newGraph.maxSlope            = gridTemplate.maxSlope;
                        newGraph.maxStepHeight       = gridTemplate.maxStepHeight;
                        newGraph.cutCorners          = gridTemplate.cutCorners;
                        newGraph.neighbours          = gridTemplate.neighbours;
                        newGraph.initialPenalty      = gridTemplate.initialPenalty;
                        newGraph.collision.type      = gridTemplate.collision.type;
                        newGraph.collision.diameter  = gridTemplate.collision.diameter;
                        newGraph.collision.height    = gridTemplate.collision.height;
                        newGraph.collision.mask      = gridTemplate.collision.mask;
                        newGraph.collision.collisionOffset = gridTemplate.collision.collisionOffset;
                        newGraph.collision.fromHeight       = gridTemplate.collision.fromHeight;
                        newGraph.collision.heightMask       = gridTemplate.collision.heightMask;
                        newGraph.collision.thickRaycast     = gridTemplate.collision.thickRaycast;
                        newGraph.collision.unwalkableWhenNoGround = gridTemplate.collision.unwalkableWhenNoGround;
                        newGraph.erodeIterations     = gridTemplate.erodeIterations;
                    }
                }
                else
                {
                    Debug.LogWarning($"[KitchenAIManager] Unknown graph type '{type.Name}' — adding empty graphs.");
                    for (int i = existing; i < count; i++)
                        data.AddGraph(type);
                }
            }

            AstarPath.active.Scan();
            AstarPath.active.navmeshUpdates.ForceUpdate();
            Debug.Log($"[KitchenAIManager] Graph expansion complete: {data.graphs.Length} graphs scanned");
        }

        #endregion

        #region Agent Registry

        public void RegisterAgent(AIChefController chef)
        {
            if (_agentStates.Any(a => a.controller == chef))
                return;

            var state = new AgentState
            {
                agentId = _nextAgentId++,
                controller = chef,
            };
            chef.agentId = state.agentId;
            _agentStates.Add(state);
            _blackboard.agents.Add(state);

            Debug.Log($"[KitchenAIManager] Registered agent {state.agentId}: {chef.chefName}");
        }

        public void UnregisterAgent(AIChefController chef)
        {
            var state = _agentStates.Find(a => a.controller == chef);
            if (state != null)
            {
                _agentStates.Remove(state);
                _blackboard.agents.Remove(state);
            }
        }

        #endregion

        #region Scheduling Loop

        private void RunScheduler()
        {
            _schedulerCycle++;
            AIDebugLogger.LogSchedulerCycle(_schedulerCycle, _agentStates.Count,
                _blackboard.taskPool.Count, _blackboard.activeOrders.Count);

            // Force-sync navmesh cuts BEFORE any agent repaths.
            // NavmeshCut updates are normally async (may take several frames),
            // so agents would pathfind on stale navmesh and walk through cuts.
            AstarPath.active?.navmeshUpdates.ForceUpdate();
            AstarPath.active?.FlushGraphUpdates();

            // Force all moving agents to repath — dynamic kitchen environment
            // may have changed (items spawned, counters updated, agents moved).
            foreach (var agent in _agentStates)
            {
                if (agent.substate == "moving" && agent.controller != null)
                    agent.controller.ForceRepath();
            }

            // Release completed AND abandoned task reservations
            foreach (var agent in _agentStates)
            {
                if (agent.currentTask != null &&
                    (agent.currentTask.status == "completed" || agent.currentTask.status == "abandoned"))
                {
                    AIDebugLogger.LogTaskComplete(agent.agentId, agent.controller?.chefName ?? "?",
                        agent.currentTask, $"released (status={agent.currentTask.status})");
                    KitchenTaskGenerator.ReleaseReservations(agent.currentTask, _blackboard);
                    agent.currentTask = null;
                    agent.substate = "idle";
                }
            }

            // Sync active orders from DeliveryManager
            SyncActiveOrders();

            // Generate all candidate tasks
            var allTasks = KitchenTaskGenerator.GenerateAllTasks(_blackboard);

            // Get idle agents
            var idleAgents = _agentStates.Where(a => a.IsIdle).ToList();
            if (idleAgents.Count == 0)
            {
                _blackboard.taskPool = allTasks;
                // Don't log this — it's normal when all agents are busy
                return;
            }

            if (allTasks.Count == 0 && _blackboard.activeOrders.Count > 0 && idleAgents.Count > 0)
            {
                _zeroTaskTimer += _scheduleInterval;
                if (_zeroTaskTimer > 8f)
                {
                    Debug.LogWarning($"[KitchenAIManager] {_zeroTaskTimer:F0}s with 0 tasks, {idleAgents.Count} idle, {_blackboard.activeOrders.Count} orders — no tasks available!");
                    _zeroTaskTimer = 0f;
                    // Regenerate tasks immediately
                    allTasks = KitchenTaskGenerator.GenerateAllTasks(_blackboard);
                }
                else
                {
                    // Throttle blackboard dumps to once every 10s
                    if (Time.time - _lastBlackboardDumpTime > 10f)
                    {
                        _lastBlackboardDumpTime = Time.time;
                        AIDebugLogger.LogWarning("Scheduler", $"{idleAgents.Count} idle agents but 0 candidate tasks with {_blackboard.activeOrders.Count} orders ({_zeroTaskTimer:F0}s). Dumping blackboard...");
                        AIDebugLogger.LogBlackboardSnapshot(_blackboard);
                    }
                }
            }
            else
            {
                _zeroTaskTimer = 0f; // Reset timer when tasks exist
                AIDebugLogger.Log("Scheduler", $"{idleAgents.Count} idle agents, {allTasks.Count} candidate tasks");
            }

            // Score and assign
            var assignments = KitchenTaskGenerator.GreedyAssign(idleAgents, allTasks, _blackboard);

            // Dispatch tasks to AI controllers
            foreach (var assignment in assignments)
            {
                assignment.agent.controller.AssignTask(assignment.task);
            }

            // Deadlock detection
            CheckDeadlock();
        }

        private void SyncActiveOrders()
        {
            var deliveryManager = DeliveryManager.Instance;
            if (deliveryManager == null) return;

            _blackboard.activeOrders.Clear();
            _blackboard.activeOrderIds.Clear();
            var waitingQueue = deliveryManager.GetWaitingQueue();
            var activeOrderIdSet = new HashSet<int>();

            // Count current occurrences per recipe
            var currentCounts = new Dictionary<RecipeSo, int>();
            foreach (var recipe in waitingQueue)
                currentCounts[recipe] = currentCounts.GetValueOrDefault(recipe, 0) + 1;

            // Ensure each recipe has enough stable IDs. IDs persist across cycles
            // regardless of queue ordering — we just match count, not position.
            foreach (var kvp in currentCounts)
            {
                var recipe = kvp.Key;
                int needed = kvp.Value;

                if (!_blackboard.recipeOrderIdLists.TryGetValue(recipe, out var idList))
                {
                    idList = new List<int>();
                    _blackboard.recipeOrderIdLists[recipe] = idList;
                }

                // Add new IDs if more occurrences than before
                while (idList.Count < needed)
                {
                    int newId = _blackboard.nextOrderId++;
                    idList.Add(newId);
                    _blackboard.orderEntryTimes[newId] = Time.time; // Record entry time
                    string label = idList.Count == 1 ? "new volatile ID" : $"duplicate #{idList.Count}";
                    AIDebugLogger.Log("Scheduler", $"Order #{newId} ← {recipe.recipeName} ({label})");
                }

                // Remove excess IDs if fewer occurrences now (orders served/removed)
                while (idList.Count > needed)
                {
                    int removedId = idList[idList.Count - 1];
                    idList.RemoveAt(idList.Count - 1);
                    _blackboard.orderEntryTimes.Remove(removedId);
                    _blackboard.ReleaseOrderPlate(removedId);
                    AIDebugLogger.Log("Scheduler", $"Order #{removedId} ({recipe.recipeName}) left queue — cleaned up");
                }
            }

            // Clean up recipes completely removed from queue
            var staleRecipes = new List<RecipeSo>();
            foreach (var kvp in _blackboard.recipeOrderIdLists)
            {
                if (!currentCounts.ContainsKey(kvp.Key))
                    staleRecipes.Add(kvp.Key);
            }
            foreach (var recipe in staleRecipes)
            {
                var idList = _blackboard.recipeOrderIdLists[recipe];
                foreach (int oldId in idList)
                {
                    _blackboard.orderEntryTimes.Remove(oldId);
                    _blackboard.ReleaseOrderPlate(oldId);
                    AIDebugLogger.Log("Scheduler", $"Order #{oldId} ({recipe.recipeName}) left queue — cleaned up");
                }
                _blackboard.recipeOrderIdLists.Remove(recipe);
            }

            // Populate activeOrders and activeOrderIds from the stable ID lists.
            // Use a per-recipe index to assign IDs in order.
            var recipeIdx = new Dictionary<RecipeSo, int>();
            foreach (var recipe in waitingQueue)
            {
                _blackboard.activeOrders.Add(recipe);
                int idx = recipeIdx.GetValueOrDefault(recipe, 0);
                recipeIdx[recipe] = idx + 1;
                var idList = _blackboard.recipeOrderIdLists[recipe];
                int orderId = idx < idList.Count ? idList[idx] : 0;
                _blackboard.activeOrderIds.Add(orderId);
                activeOrderIdSet.Add(orderId);
            }

        }

        private void SyncFacilityStates()
        {
            foreach (var fac in _blackboard.facilities)
            {
                if (fac.counter == null) continue;

                bool hasItem = fac.counter.HasKitchenObj();

                // Update occupancy: if it has an item and isn't reserved, mark occupied
                if (hasItem && fac.state == "free")
                {
                    fac.state = "occupied";
                    fac.timer = 5f; // Default occupancy timeout
                }
                else if (!hasItem && fac.state == "occupied")
                {
                    fac.state = "free";
                    fac.occupiedByAgent = -1;
                    fac.timer = 0;
                }

                // Update timer for occupied facilities
                if (fac.state == "occupied")
                {
                    fac.timer -= Time.deltaTime;
                    if (fac.timer <= 0)
                    {
                        fac.state = "free";
                        fac.occupiedByAgent = -1;
                        fac.timer = 0;
                    }
                }
            }
        }

        private void SyncAgentPositions()
        {
            foreach (var state in _agentStates)
            {
                if (state.controller != null)
                {
                    state.position = state.controller.transform.position;
                    state.substate = state.controller.Substate;
                }
            }
        }

        private void CheckDeadlock()
        {
            // Track how long each agent has been in its current substate
            foreach (var agent in _agentStates)
            {
                agent.stuckTimer += _scheduleInterval;
                if (agent.currentTask != null && agent.currentTask.status == "assigned")
                    agent.stuckTimer = 0; // Reset on new assignment
            }

            // Count agents that are truly stuck
            int stuckCount = 0;
            int waitingCount = 0;
            int stuckMovingCount = 0;
            int stuckIdleCount = 0;

            foreach (var agent in _agentStates)
            {
                if (agent.substate == "idle" && agent.stuckTimer > 5f) // Idle for >5s with tasks available
                {
                    stuckCount++;
                    stuckIdleCount++;
                }
                else if (agent.substate == "waiting" && agent.stuckTimer > 3f) // Waiting for >3s
                {
                    stuckCount++;
                    waitingCount++;
                }
                else if (agent.substate == "moving" && agent.stuckTimer > 15f) // Moving for >15s
                {
                    stuckCount++;
                    stuckMovingCount++;
                }
            }

            // Deadlock: ALL agents stuck for minimum time, AND tasks are available
            if ((waitingCount + stuckMovingCount) > 0 &&
                stuckCount >= _agentStates.Count &&
                _blackboard.taskPool.Count > 0)
            {
                Debug.LogWarning($"[KitchenAIManager] Deadlock detected! " +
                    $"waiting={waitingCount} stuckMoving={stuckMovingCount} idle={stuckCount - waitingCount - stuckMovingCount}");
                AIDebugLogger.LogDeadlock("DETECTED — force-breaking", _agentStates);

                foreach (var agent in _agentStates)
                {
                    if ((agent.substate == "waiting" || (agent.substate == "moving" && agent.stuckTimer > 15f))
                        && agent.currentTask != null)
                    {
                        AIDebugLogger.LogTaskAbandon(agent.agentId, agent.controller?.chefName ?? "?",
                            agent.currentTask, "deadlock-break");

                        // Use the controller's AbandonTask to properly reset ALL state
                        if (agent.controller != null)
                        {
                            agent.controller.ForceAbandonTask();
                        }

                        KitchenTaskGenerator.ReleaseReservations(agent.currentTask, _blackboard);
                        agent.currentTask = null;
                        agent.substate = "idle";
                        agent.stuckTimer = 0;
                    }
                }

                _scheduleTimer = _scheduleInterval;
            }
            else if (stuckCount >= _agentStates.Count - 1 && (waitingCount + stuckMovingCount) > 0)
            {
                // Throttled near-deadlock warning
                if (Time.time - _lastBlackboardDumpTime > 10f)
                {
                    _lastBlackboardDumpTime = Time.time;
                    AIDebugLogger.LogWarning("Scheduler", $"Near-deadlock: {stuckCount}/{_agentStates.Count} stuck " +
                        $"(waiting={waitingCount} stuckMoving={stuckMovingCount})");
                }
            }
        }

        #endregion

        #region Task Lifecycle Callbacks

        /// <summary>
        /// Called by AIChefController when it completes or abandons a task.
        /// </summary>
        public void OnAgentTaskCompleted(AIChefController chef, KitchenTask task)
        {
            KitchenTaskGenerator.ReleaseReservations(task, _blackboard);

            // When SERVE completes, release the order's plate
            if (task.status == "completed" && task.type == TaskType.SERVE && task.orderId != 0)
            {
                _blackboard.ReleaseOrderPlate(task.orderId);
                AIDebugLogger.Log("Scheduler", $"Order #{task.orderId} served — releasing plate");
            }

            // Record role specialization
            var agentState = _agentStates.Find(a => a.controller == chef);
            if (agentState != null)
            {
                if (!agentState.roleCounts.ContainsKey(task.type))
                    agentState.roleCounts[task.type] = 0;
                agentState.roleCounts[task.type]++;
            }

            // Trigger immediate re-schedule if agent is now idle
            _scheduleTimer = _scheduleInterval; // Force next frame's schedule
        }

        #endregion

        #region Debug

        [ContextMenu("Force Schedule")]
        public void ForceSchedule()
        {
            if (!_isInitialized) Initialize();
            _scheduleTimer = _scheduleInterval;
        }

        [ContextMenu("Print Blackboard State")]
        public void PrintBlackboardState()
        {
            Debug.Log($"=== Blackboard State ===");
            Debug.Log($"  Facilities: {_blackboard.facilities.Count}");
            foreach (var f in _blackboard.facilities)
                Debug.Log($"    {f.counter.name} [{f.type}] = {f.state}");
            Debug.Log($"  Items: {_blackboard.items.Count}");
            foreach (var i in _blackboard.items)
                Debug.Log($"    {i.itemType} stage={i.stage} carried={i.carriedByAgent} reserved={i.reservedByTask}");
            Debug.Log($"  Agents: {_agentStates.Count}");
            foreach (var a in _agentStates)
                Debug.Log($"    Agent {a.agentId} substate={a.substate} task={a.currentTask?.label ?? "none"}");
            Debug.Log($"  Active Orders: {_blackboard.activeOrders.Count}");
            Debug.Log($"  Task Pool: {_blackboard.taskPool.Count}");
        }

        #endregion
    }
}
