using System.Collections.Generic;
using System.Linq;
using Kitchen.Player;
using Kitchen.Visual;
using Nico.Network;
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
            if (_isInitialized) return; // Already initialized — skip
            _blackboard.ScanFacilities();
            _blackboard.LoadRecipes();
            // Fresh start

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

                    // RVO priority: higher = more dominant in crowds. Offset per agent for differentiation.
                    float rvoPriority = Mathf.Clamp01(_aiRvoBasePriority + colorIdx * 0.05f);
                    chef.SetAIParams(_aiAgentRadius, _aiMoveSpeed, rvoPriority, _aiApproachOffset);

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

            // Apply common params to ALL chefs (including scene-placed ones that skipped prefab spawning)
            foreach (var chef in _aiChefs)
            {
                if (chef == null) continue;
                chef.interactionRange = _aiInteractionRange;
                chef.arrivalThreshold = _aiArrivalThreshold;
                chef.stuckTimeout = _aiStuckTimeout;
                // approachOffset: set directly (not via SetAIParams, which is called for spawned chefs)
                chef.SetApproachOffset(_aiApproachOffset);
            }

            _isInitialized = true;
            Debug.Log($"[KitchenAIManager] Initialized with {_agentStates.Count} AI chefs, " +
                      $"{_blackboard.facilities.Count} facilities, " +
                      $"{_blackboard.allRecipes.Count} recipes");
            AIDebugLogger.Log("Init", $"KitchenAIManager initialized: {_agentStates.Count} chefs, " +
                $"{_blackboard.facilities.Count} facilities, {_blackboard.allRecipes.Count} recipes");
            AIDebugLogger.Log("Init", $"Log file: {AIDebugLogger.GetLogPath()}");
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
