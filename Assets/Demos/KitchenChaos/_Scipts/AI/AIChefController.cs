using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Netcode;
using Pathfinding;

namespace Kitchen.AI
{
    /// <summary>
    /// AI Chef controller — the "brain" of a single AI chef.
    ///
    /// Implements ICanHoldKitchenObj so the AI can hold items and interact with counters
    /// exactly like a human Player does. Uses direct movement (no NavMesh required).
    ///
    /// State machine:
    ///   Idle → Moving → Interacting → Working → Complete → Idle
    ///            ↑          ↑            ↑
    ///            └── timeout ─┴── fail ───┘
    /// </summary>
    public class AIChefController : NetworkBehaviour, ICanHoldKitchenObj
    {
        #region Inspector Fields

        [Header("Identity")]
        public string chefName = "AI_Chef";
        public Color chefColor = Color.white;
        public int agentId = -1;

        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 3.5f;
        public float moveSpeed
        {
            get => _moveSpeed;
            set { _moveSpeed = value; if (_ai != null) _ai.maxSpeed = value; }
        }
        public float interactionRange = 1.8f;
        [Range(0.1f, 1f)] public float arrivalThreshold = 0.4f;
        public float stuckTimeout = 8.0f;

        /// <summary>Distance offset for approach-point candidates (front/back/left/right of target).</summary>
        [Header("Approach")]
        [SerializeField] private float _approachOffset = 0.5f;

        private IAstarAI _ai;          // A* Pathfinding Project movement component
        private AIPath _aiPath;       // cached AIPath component for settings access

        [Header("Debug")]
        public bool showDebugGizmos = true;
        public string debugState = "idle";

        #endregion

        #region Private State

        private KitchenTask _currentTask;
        private BaseCounter _targetCounter;
        private string _substate = "idle"; // idle | moving | interacting | working | waiting | paused
        private string _pauseCallback;    // what to do after pause: "arrived" | "idle"
        private float _stateTimer;
        private float _moveTimer;
        private float _waitTimer;
        private bool _isHoldingItem;

        // Wander
        [Header("Wander")]
        [SerializeField] private bool _enableWander = true;
        [SerializeField] private float _wanderRadius = 5f;
        [SerializeField] private float _wanderInterval = 3f;
        private float _wanderTimer;
        private bool _isWandering;

        // Anti-stuck
        private float _lastProgressDist = float.MaxValue;
        private float _stuckProgressTimer;

        // Execution phases (for multi-step tasks)
        private enum ExecPhase
        {
            None,
            GotoItem,
            GotoFacility,
            GotoDest
        }
        private ExecPhase _execPhase = ExecPhase.None;
        private KitchenObj _carryTargetItem;
        private Vector3 _carryDestPos;

        // Item holding (ICanHoldKitchenObj)
        private KitchenObj _heldItem;
        private Transform _holdPoint;

        // References
        private KitchenAIManager _aiManager;

        // Debug: last computed approach point (for gizmo comparison)
        private Vector3 _lastApproachPoint;
        private bool _hasApproachPoint;

        #endregion

        #region Public Properties

        public KitchenTask CurrentTask => _currentTask;
        public string Substate => _substate;
        public bool IsIdle => _currentTask == null || _currentTask.status == "completed";
        public KitchenObj HeldItem => _heldItem;

        /// <summary>Apply A* Pathfinding + RVO params after spawn (called by KitchenAIManager).</summary>
        public void SetAIParams(float radius, float maxSpeed, float rvoPriority, float approachOffset)
        {
            if (_aiPath != null)
            {
                _aiPath.radius = radius;
            }
            if (_ai != null)
            {
                _ai.maxSpeed = maxSpeed;
            }
            _approachOffset = approachOffset;

            // RVOController is handled by AIBase — it auto-syncs radius/height from AIPath.
            // Manually set the priority for local avoidance.
            var rvo = GetComponent<Pathfinding.RVO.RVOController>();
            if (rvo != null)
            {
                rvo.priority = rvoPriority;
            }
        }

        /// <summary>Set approach-offset independently (for scene-placed chefs).</summary>
        public void SetApproachOffset(float offset)
        {
            _approachOffset = offset;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            // Use existing hold point from prefab, or create one
            _holdPoint = transform.Find("KitchenObjHoldPoint");
            if (_holdPoint == null)
                _holdPoint = transform.Find("topSpawnPoint"); // Player's hold point name
            if (_holdPoint == null)
            {
                var holdGo = new GameObject("AI_HoldPoint");
                holdGo.transform.SetParent(transform);
                holdGo.transform.localPosition = new Vector3(0, 1.5f, 0.9f);
                _holdPoint = holdGo.transform;
            }

            // Ensure NetworkObject is present
            if (GetComponent<NetworkObject>() == null)
                gameObject.AddComponent<NetworkObject>();

            // Disable physics colliders — A* pathfinding + RVO handles movement/avoidance
            foreach (var col in GetComponents<Collider>())
                col.enabled = false;
            foreach (var col in GetComponentsInChildren<Collider>())
                col.enabled = false;

            // --- A* Pathfinding Project setup ---
            _aiPath = GetComponent<AIPath>();
            if (_aiPath == null)
                _aiPath = gameObject.AddComponent<AIPath>();
            _ai = _aiPath; // IAstarAI interface

            // AIPath defaults (overridden later by SetAIParams)
            _aiPath.radius = 0.5f;
            _aiPath.height = 1.8f;
            _ai.maxSpeed = _moveSpeed;
            _aiPath.rotationSpeed = 180f;
            _aiPath.endReachedDistance = 0.3f;    // must reach close to exact point before arrival
            _aiPath.slowdownDistance = 3f;       // long deceleration to prevent overshoot at high speed
            _aiPath.pickNextWaypointDist = 8f;   // look far ahead = prefer wide open routes
            _aiPath.whenCloseToDestination = CloseToDestinationMode.ContinueToExactDestination;
            _aiPath.constrainInsideGraph = true;   // prevent RVO from pushing agent off navmesh into walls

            // Auto repath: dynamic mode adapts to changing kitchen environment
            _aiPath.autoRepath.mode = AutoRepathPolicy.Mode.Dynamic;

            // RVO Controller for local avoidance (AIBase auto-integrates it)
            var rvo = GetComponent<Pathfinding.RVO.RVOController>();
            if (rvo == null)
                rvo = gameObject.AddComponent<Pathfinding.RVO.RVOController>();
            rvo.radius = 0.5f;
            rvo.height = 1.8f;
            rvo.agentTimeHorizon = 2f;        // look ahead for agent-agent collision
            rvo.obstacleTimeHorizon = 2f;     // look 2s ahead for static obstacles
            rvo.maxNeighbours = 20;           // consider more nearby agents
            rvo.lockWhenNotMoving = false;    // keep avoiding even when stationary
            rvo.priority = 0.5f; // default, overridden by SetAIParams
        }

        private void Start()
        {
            _aiManager = KitchenAIManager.Instance;
            if (_aiManager != null)
            {
                _aiManager.RegisterAgent(this);
            }
        }

        private void Update()
        {
            // Safety: skip if destroyed or exiting play mode
            if (this == null || !isActiveAndEnabled) return;
            if (!IsServer && !IsHost) return;
            if (GameManager.Instance == null || !GameManager.Instance.IsPlaying()) return;

            UpdateStateMachine();
            UpdateMovement();
            UpdateHeldItem();
        }

        private void OnDestroy()
        {
            if (_aiManager != null)
                _aiManager.UnregisterAgent(this);
        }

        #endregion

        #region State Machine

        private void UpdateStateMachine()
        {
            _stateTimer += Time.deltaTime;

            // RVO lock: agents working, interacting, waiting, or paused hold their ground as static obstacles
            var rvo = GetComponent<Pathfinding.RVO.RVOController>();
            if (rvo != null)
                rvo.locked = (_substate == "waiting" || _substate == "working" || _substate == "interacting" || _substate == "paused");

            switch (_substate)
            {
                case "idle":
                    debugState = "idle";
                    if (_enableWander && _currentTask == null)
                    {
                        _wanderTimer += Time.deltaTime;
                        if (_wanderTimer >= _wanderInterval)
                        {
                            _wanderTimer = 0f;
                            StartWander();
                        }
                    }
                    break;

                case "moving":
                    {
                        float dist = _ai.pathPending ? float.MaxValue : _ai.remainingDistance;
                        debugState = _isWandering ? $"wandering d={dist:F1}" : $"moving → ({_ai.destination.x:F0},{_ai.destination.z:F0}) d={dist:F1}";
                        _moveTimer += Time.deltaTime;

                        // Track progress: if distance hasn't decreased significantly in 2s, repath
                        if (dist < _lastProgressDist - 0.1f)
                        {
                            _lastProgressDist = dist;
                            _stuckProgressTimer = 0f;
                        }
                        else
                        {
                            _stuckProgressTimer += Time.deltaTime;
                        }
                        if (_stuckProgressTimer > 2f && !_isWandering)
                        {
                            // Stuck in a crowd — force repath with a small random offset
                            Vector3 jitter = Random.insideUnitSphere * 0.5f;
                            jitter.y = 0;
                            _ai.destination = _ai.destination + jitter;
                            _ai.SearchPath();
                            _stuckProgressTimer = 0f;
                            _lastProgressDist = float.MaxValue;
                        }

                        if (!_ai.pathPending && _ai.reachedDestination)
                        {
                            _ai.isStopped = true;
                            _moveTimer = 0;
                            _lastProgressDist = float.MaxValue;
                            _stuckProgressTimer = 0f;
                            if (_isWandering)
                            {
                                _isWandering = false;
                                _substate = "idle";
                                _wanderTimer = 0f;
                            }
                            else
                            {
                                AIDebugLogger.LogState(chefName, "moving", "arrived",
                                    $"at ({_ai.destination.x:F1},{_ai.destination.z:F1}) dist={dist:F2} time={_moveTimer:F1}s");
                                _substate = "paused";
                                _stateTimer = 0;
                                _pauseCallback = "arrived";
                            }
                        }
                        else if (_moveTimer > stuckTimeout)
                        {
                            if (_isWandering)
                            {
                                _isWandering = false;
                                _substate = "idle";
                            }
                            else
                            {
                                Debug.LogWarning($"[{chefName}] Stuck moving for {stuckTimeout}s, abandoning task");
                                AIDebugLogger.LogWarning(chefName, $"Stuck moving for {stuckTimeout}s (dist={dist:F1})");
                                AbandonTask();
                            }
                        }
                    }
                    break;

                case "interacting":
                    debugState = "interacting";
                    FaceTarget();
                    if (_stateTimer > 0.15f) // brief delay to simulate interaction
                    {
                        ExecuteInteraction();
                    }
                    break;

                case "working":
                    debugState = $"working {_stateTimer:F1}s";
                    FaceTarget();
                    if (_stateTimer >= (_currentTask?.duration ?? 1.0f))
                    {
                        AIDebugLogger.LogState(chefName, "working", "done",
                            $"duration={_stateTimer:F1}s task={_currentTask?.label}");

                        // Check if the processing output is ready
                        if (_currentTask?.type == TaskType.PROCESS &&
                            _targetCounter != null &&
                            _targetCounter.HasKitchenObj())
                        {
                            var counterItem = _targetCounter.GetKitchenObj();
                            if (counterItem.objEnum == _currentTask.outputType)
                            {
                                // Output ready — take it and deliver to ClearCounter
                                AIDebugLogger.Log(chefName, $"Taking processed output {counterItem.objEnum} from {_targetCounter.name}");
                                _targetCounter.Interact(this);

                                if (_heldItem != null)
                                {
                                    // Try to deliver directly to a plate that needs this ingredient
                                    var plateTarget = FindPlateNeedingIngredient(_heldItem.objEnum);
                                    if (plateTarget != null)
                                    {
                                        AIDebugLogger.Log(chefName, $"Delivering {_heldItem.objEnum} directly to plate at {plateTarget.name}");
                                        _execPhase = ExecPhase.GotoDest;
                                        _targetCounter = plateTarget;
                                        if (!MoveTo(plateTarget.transform.position)) { AbandonTask(); return; };
                                        break;
                                    }
                                    // Fallback: drop on nearest free counter
                                    var dropTarget = FindNearestFreeCounter(transform.position);
                                    if (dropTarget != null)
                                    {
                                        AIDebugLogger.Log(chefName, $"Delivering {_heldItem.objEnum} to {dropTarget.name}");
                                        _execPhase = ExecPhase.GotoDest;
                                        _targetCounter = dropTarget;
                                        if (!MoveTo(dropTarget.transform.position)) { AbandonTask(); return; };
                                        break;
                                    }
                                }
                                CompleteTask();
                                break;
                            }
                            else
                            {
                                // Output not ready yet (still processing) — wait and re-check
                                AIDebugLogger.Log(chefName, $"Working done but output not ready (counter has {counterItem.objEnum}), entering wait");
                                _substate = "waiting";
                                _waitTimer = 0;
                                break;
                            }
                        }

                        CompleteTask();
                    }
                    break;

                case "waiting":
                    debugState = $"waiting {_waitTimer:F1}s";
                    _waitTimer += Time.deltaTime;
                    FaceTarget();

                    // Check if we can proceed (periodic re-check)
                    if (_waitTimer > 0.3f && CanProceedFromWaiting())
                    {
                        _substate = "interacting";
                        _stateTimer = 0;
                        debugState = "retrying interaction";
                        // Don't reset _waitTimer — let the timeout catch stuck loops
                        break;
                    }

                    // Task-dependent timeout
                    float maxWait = GetMaxWaitTime();
                    if (_waitTimer > maxWait)
                    {
                        Debug.Log($"[{chefName}] Waited {maxWait}s, abandoning: {_currentTask?.label}");
                        AIDebugLogger.LogWarning(chefName, $"FETCH_PLATE wait timeout ({maxWait}s), abandoning");
                        AbandonTask();
                    }
                    break;

                case "paused":
                    debugState = $"paused {_stateTimer:F2}s";
                    FaceTarget();
                    if (_stateTimer > 0.05f)
                    {
                        if (_pauseCallback == "arrived")
                        {
                            OnArrivedAtTarget();
                        }
                        else // "idle" after task completion
                        {
                            _substate = "idle";
                            debugState = "idle";
                        }
                    }
                    break;
            }
        }

        private void OnArrivedAtTarget()
        {
            // Snap-face the original target immediately (before state transition)
            SnapFaceTarget();

            switch (_execPhase)
            {
                case ExecPhase.GotoItem:
                    // Arrived at item position — pick it up.
                    // If the original item reference became stale (consumed by another agent),
                    // fall back to finding any available item of the required type.
                    if (_carryTargetItem == null && _currentTask?.itemType != 0)
                    {
                        AIDebugLogger.Log(chefName, $"GotoItem: original item gone, searching for {_currentTask.itemType}");
                        _carryTargetItem = FindItemAnywhere(_currentTask.itemType);
                        if (_carryTargetItem != null)
                        {
                            // Re-target to the new item's position
                            var holdingCounter = FindCounterHolding(_carryTargetItem);
                            Vector3 newTarget = holdingCounter != null
                                ? GetApproachPosition(holdingCounter)
                                : _carryTargetItem.transform.position;
                            AIDebugLogger.Log(chefName, $"GotoItem: found alternative {_carryTargetItem.objEnum}, re-targeting");
                            if (!MoveTo(newTarget)) { AbandonTask(); return; };
                            break; // Will re-enter GotoItem on next arrival
                        }
                        AIDebugLogger.LogWarning(chefName, $"GotoItem: no {_currentTask.itemType} found anywhere, abandoning");
                        AbandonTask();
                        break;
                    }

                    // If item is on a counter, interact with the counter (not PickupObjServerRpc)
                    var counterHolding = FindCounterHolding(_carryTargetItem);
                    if (counterHolding != null)
                    {
                        // Item is on a counter — interact with the counter to take it
                        AIDebugLogger.LogState(chefName, "GotoItem", "counter-pickup",
                            $"taking {_carryTargetItem?.objEnum} from {counterHolding.name}");
                        counterHolding.Interact(this);
                        _carryTargetItem = null;
                    }
                    else
                    {
                        // Item is free on ground — use direct pickup
                        PickupItem();
                    }

                    if (_targetCounter != null && _heldItem != null)
                    {
                        // Successfully got the item, now continue to destination
                        if (_currentTask?.type == TaskType.PROCESS)
                        {
                            _execPhase = ExecPhase.GotoFacility;
                            AIDebugLogger.LogState(chefName, "GotoItem", "GotoFacility",
                                $"carrying {_heldItem?.objEnum}, heading to {_targetCounter.name}");
                        }
                        else
                        {
                            _execPhase = ExecPhase.GotoDest;
                            AIDebugLogger.LogState(chefName, "GotoItem", "GotoDest",
                                $"carrying {_heldItem?.objEnum}, heading to {_targetCounter.name}");
                        }
                        if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; };
                    }
                    else if (_heldItem == null)
                    {
                        // Pickup failed — abandon
                        AIDebugLogger.LogWarning(chefName, $"GotoItem: pickup failed for {_carryTargetItem?.objEnum}");
                        AbandonTask();
                    }
                    else
                    {
                        // Got item but no destination — complete
                        _substate = "interacting";
                        _stateTimer = 0;
                    }
                    break;

                case ExecPhase.GotoFacility:
                    // Arrived at facility — drop held item first, then interact
                    DropItemAtFacility();
                    break;

                case ExecPhase.GotoDest:
                    // Arrived at destination — drop item or interact
                    DropItemAtDestination();
                    break;

                default:
                    // Simple task — start interaction
                    _substate = "interacting";
                    _stateTimer = 0;
                    break;
            }
        }

        #endregion

        #region Movement

        /// <summary>
        /// Unified movement method. Generates 4 cardinal candidate points (front/back/left/right
        /// at _approachOffset) around the original target, picks the one closest to the target
        /// that lies on the NavMesh, and starts moving there.
        /// Returns false if no candidate is on the NavMesh — caller decides how to handle failure.
        /// </summary>
        public bool MoveTo(Vector3 originalTarget)
        {
            Vector3 bestPoint = originalTarget;
            float bestDist = float.MaxValue;
            bool found = false;

            // 4 cardinal direction candidates at _approachOffset
            Vector3[] offsets = new Vector3[]
            {
                new Vector3(_approachOffset, 0, 0),
                new Vector3(-_approachOffset, 0, 0),
                new Vector3(0, 0, _approachOffset),
                new Vector3(0, 0, -_approachOffset),
            };

            if (AstarPath.active != null)
            {
                foreach (var offset in offsets)
                {
                    var candidate = originalTarget + offset;
                    var nearest = AstarPath.active.GetNearest(candidate);
                    // Only accept if the candidate itself is on the NavMesh (within tight tolerance)
                    if (nearest.node != null && Vector3.Distance(nearest.position, candidate) < 0.01f)
                    {
                        float dist = Vector3.Distance(candidate, originalTarget);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestPoint = candidate;
                            found = true;
                        }
                    }
                }
            }
            else
            {
                // No AstarPath in scene — use original target as-is
                found = true;
            }

            if (!found)
            {
                // Fallback: no candidate on NavMesh → use GetNearest on the original target
                if (AstarPath.active != null)
                {
                    var fallback = AstarPath.active.GetNearest(originalTarget);
                    if (fallback.node != null)
                    {
                        bestPoint = fallback.position;
                        found = true;
                        Debug.Log($"[{chefName}] MoveTo: approach failed, fallback GetNearest → ({bestPoint.x:F1},{bestPoint.z:F1})");
                    }
                }
            }

            if (!found)
            {
                Debug.LogWarning($"[{chefName}] MoveTo: no valid navmesh point near ({originalTarget.x:F1},{originalTarget.z:F1}) offset={_approachOffset}");
                return false;
            }

            _ai.destination = bestPoint;
            _ai.SearchPath();
            _ai.isStopped = false;
            _substate = "moving";
            _moveTimer = 0;
            _lastProgressDist = float.MaxValue;
            _stuckProgressTimer = 0f;
            _lastApproachPoint = bestPoint;
            _hasApproachPoint = true;
            debugState = $"move → ({bestPoint.x:F1}, {bestPoint.z:F1})";
            return true;
        }

        private void UpdateMovement()
        {
            if (_substate != "moving") return;

            // AIPath handles vertical positioning automatically via graph constraints.
            // No Y-clamping needed.
        }

        private void StartWander()
        {
            Vector3 randomDir = Random.insideUnitSphere * _wanderRadius;
            randomDir.y = 0;
            Vector3 target = transform.position + randomDir;

            if (!MoveTo(target))
            {
                // No valid approach point — skip this wander
                _isWandering = false;
                _substate = "idle";
                return;
            }
            _isWandering = true;
        }

        #endregion

        #region Task Execution

        /// <summary>
        /// Called by KitchenAIManager when a task is assigned to this chef.
        /// </summary>
        public void AssignTask(KitchenTask task)
        {
            _isWandering = false; // Cancel any wandering
            _currentTask = task;
            _execPhase = ExecPhase.None;
            _carryTargetItem = null;
            _waitTimer = 0;

            AIDebugLogger.LogAssignment(agentId, chefName, task);

            ExecuteTask(task);
        }

        private void ExecuteTask(KitchenTask task)
        {
            AIDebugLogger.LogState(chefName, "idle", $"executing {task.type}",
                $"task={task.label} facility={task.targetFacility?.name ?? "none"} item={task.targetItem?.objEnum.ToString() ?? "none"}");

            switch (task.type)
            {
                case TaskType.FETCH:
                    ExecuteFetch(task);
                    break;
                case TaskType.PROCESS:
                    ExecuteProcess(task);
                    break;
                case TaskType.FETCH_PLATE:
                    ExecuteFetchPlate(task);
                    break;
                case TaskType.ADD_TO_PLATE:
                    ExecuteAddToPlate(task);
                    break;
                case TaskType.SERVE:
                    ExecuteServe(task);
                    break;
                case TaskType.TRASH:
                    ExecuteTrash(task);
                    break;
            }
        }

        private void ExecuteFetch(KitchenTask task)
        {
            // Phase 1: Go to ContainerCounter, get item
            // Phase 2: Go to nearest processing facility, drop item
            var counter = task.targetFacility;
            if (counter == null)
            {
                Debug.LogWarning($"[{chefName}] FETCH task has no target facility");
                AbandonTask();
                return;
            }

            // If we're already holding the right item (by type), skip to phase 2
            if (_heldItem != null && task.outputType != 0 && _heldItem.objEnum == task.outputType)
            {
                AIDebugLogger.Log(chefName, $"ExecuteFetch: already holding {_heldItem.objEnum}, finding drop target");
                // Find destination facility for this ingredient
                _targetCounter = FindDropTarget(task.outputType);
                if (_targetCounter != null && _targetCounter != counter)
                {
                    _execPhase = ExecPhase.GotoDest;
                    if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; }
                }
                else
                {
                    // Just drop at the source counter or nearby clear counter
                    var dropTarget = FindNearestFreeCounter(counter.transform.position);
                    _targetCounter = dropTarget ?? counter;
                    _execPhase = ExecPhase.GotoDest;
                    if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; }
                }
                return;
            }

            // Phase 1: Go to ContainerCounter
            _targetCounter = counter;
            _execPhase = ExecPhase.None;
            if (!MoveTo(counter.transform.position)) { AbandonTask(); return; }
        }

        /// <summary>
        /// Find the best facility to drop a raw ingredient at.
        /// Cuttable → CuttingCounter, Cookable → StoveCounter, Other → ClearCounter.
        /// Now checks blackboard reservations to avoid conflicts.
        /// </summary>
        private BaseCounter FindDropTarget(KitchenObjEnum ingredient)
        {
            var bb = _aiManager?.Blackboard;
            HashSet<BaseCounter> reservedCounters = null;
            if (bb != null)
            {
                reservedCounters = new HashSet<BaseCounter>(
                    bb.facilities
                        .Where(f => f.state == "reserved" && f.reservedByAgent != agentId)
                        .Select(f => f.counter));
            }

            // Raw ingredients that can be cut → CuttingCounter
            if (DataTableManager.Sigleton.CanProcess(ingredient, FacilityEnum.CuttingCounter))
            {
                var cc = FindObjectsOfType<CuttingCounter>()
                    .FirstOrDefault(c => !c.HasKitchenObj()
                        && (reservedCounters == null || !reservedCounters.Contains(c)));
                if (cc != null)
                {
                    AIDebugLogger.Log(chefName, $"FindDropTarget({ingredient}) → CuttingCounter {cc.name}");
                    return cc;
                }
            }
            // Raw ingredients that can be cooked → StoveCounter
            if (DataTableManager.Sigleton.CanProcess(ingredient, FacilityEnum.StoveCounter))
            {
                var sc = FindObjectsOfType<StoveCounter>()
                    .FirstOrDefault(c => !c.HasKitchenObj()
                        && (reservedCounters == null || !reservedCounters.Contains(c)));
                if (sc != null)
                {
                    AIDebugLogger.Log(chefName, $"FindDropTarget({ingredient}) → StoveCounter {sc.name}");
                    return sc;
                }
            }
            // Non-processable ingredients (like Bread) — try direct-to-plate first
            var plateTarget = FindPlateNeedingIngredient(ingredient);
            if (plateTarget != null)
            {
                AIDebugLogger.Log(chefName, $"FindDropTarget({ingredient}) → plate at {plateTarget.name}");
                return plateTarget;
            }
            var clear = FindObjectsOfType<ClearCounter>()
                .FirstOrDefault(c => !c.HasKitchenObj()
                    && (reservedCounters == null || !reservedCounters.Contains(c)));
            if (clear != null)
            {
                AIDebugLogger.Log(chefName, $"FindDropTarget({ingredient}) → ClearCounter {clear.name}");
                return clear;
            }
            AIDebugLogger.LogWarning(chefName, $"FindDropTarget({ingredient}) → fallback nearest free");
            return FindNearestFreeCounter(transform.position, reservedCounters);
        }

        private BaseCounter FindNearestFreeCounter(Vector3 near, HashSet<BaseCounter> reserved = null)
        {
            var counters = FindObjectsOfType<BaseCounter>();
            BaseCounter best = null;
            float bestDist = float.MaxValue;
            foreach (var c in counters)
            {
                if (!(c is ClearCounter)) continue;
                if (c.HasKitchenObj()) continue;
                if (reserved != null && reserved.Contains(c)) continue;
                float d = Vector3.Distance(near, c.transform.position);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            if (best == null)
            {
                AIDebugLogger.LogWarning(chefName, "FindNearestFreeCounter: NO free counter found!");
            }
            return best;
        }

        /// <summary>
        /// Find a counter holding a plate that still needs the given ingredient.
        /// Returns null if no matching plate exists — caller falls back to a free counter.
        /// </summary>
        private BaseCounter FindPlateNeedingIngredient(KitchenObjEnum ingredient)
        {
            var bb = _aiManager?.Blackboard;
            if (bb == null) return null;

            BaseCounter best = null;
            float bestDist = float.MaxValue;

            foreach (var kv in bb.orderPlate)
            {
                int orderId = kv.Key;
                var plate = kv.Value;
                if (plate == null || plate.gameObject == null) continue;
                if (plate.GetIngredients().Contains(ingredient)) continue;

                // Find the order associated with this plate and check it needs this ingredient
                int orderIdx = bb.activeOrderIds.IndexOf(orderId);
                if (orderIdx < 0 || orderIdx >= bb.activeOrders.Count) continue;
                var order = bb.activeOrders[orderIdx];
                if (!bb.recipeStepChains.TryGetValue(order.recipeName, out var steps)) continue;
                if (!steps.Any(s => s.taskType == TaskType.ADD_TO_PLATE && s.inputType == ingredient))
                    continue;

                var holder = plate.GetHolder();
                BaseCounter plateCounter = holder as BaseCounter;
                if (plateCounter == null) continue;

                float d = Vector3.Distance(transform.position, plateCounter.transform.position);
                if (d < bestDist) { bestDist = d; best = plateCounter; }
            }

            return best;
        }

        private void ExecuteProcess(KitchenTask task)
        {
            var counter = task.targetFacility;
            if (counter == null)
            {
                AIDebugLogger.LogWarning(chefName, $"ExecuteProcess: no target facility");
                AbandonTask();
                return;
            }

            _targetCounter = counter;

            // If holding an unrelated item, drop it first at nearest free counter
            if (_heldItem != null &&
                _heldItem.objEnum != task.itemType &&
                _heldItem.objEnum != task.outputType)
            {
                AIDebugLogger.Log(chefName, $"ExecuteProcess: dropping unrelated {_heldItem.objEnum} before PROCESS");
                var freeCounter = FindNearestFreeCounter(transform.position);
                if (freeCounter != null)
                {
                    freeCounter.Interact(this);
                }
                else
                {
                    // Drop on ground
                    KitchenObjFactory.Instance.DropObjServerRpc(
                        _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                        Vector3.down, 0f, default);
                    ClearKitchenObj();
                }
            }

            Vector3 approachPos = GetApproachPosition(counter);

            if (_heldItem != null && task.itemType != 0 && _heldItem.objEnum == task.itemType)
            {
                // Holding the right input — go drop it and start processing
                AIDebugLogger.Log(chefName, $"ExecuteProcess: holding {_heldItem.objEnum}, → GotoFacility {counter.name}");
                _execPhase = ExecPhase.GotoFacility;
                if (!MoveTo(counter.transform.position)) { AbandonTask(); return; }
            }
            else if (counter.HasKitchenObj())
            {
                string counterItem = counter.GetKitchenObj().objEnum.ToString();
                AIDebugLogger.Log(chefName, $"ExecuteProcess: counter {counter.name} has {counterItem}, → direct interact");
                _execPhase = ExecPhase.None;
                if (!MoveTo(counter.transform.position)) { AbandonTask(); return; }
            }
            else
            {
                // Counter is empty — find the input item elsewhere and bring it here
                KitchenObj foundItem = FindItemAnywhere(task.itemType);
                if (foundItem != null)
                {
                    var holdingCounter = FindCounterHolding(foundItem);
                    Vector3 pickupPos = holdingCounter != null
                        ? GetApproachPosition(holdingCounter)
                        : foundItem.transform.position;
                    AIDebugLogger.Log(chefName, $"ExecuteProcess: self-fetching {task.itemType} from {(holdingCounter != null ? holdingCounter.name : "ground")} → {counter.name}");
                    _carryTargetItem = foundItem;
                    _carryDestPos = approachPos;
                    _execPhase = ExecPhase.GotoItem;
                    if (!MoveTo(pickupPos)) { AbandonTask(); return; };
                }
                else
                {
                    AIDebugLogger.LogWarning(chefName, $"ExecuteProcess: no {task.itemType} found, abandoning");
                    AbandonTask();
                }
            }
        }

        private void ExecuteFetchPlate(KitchenTask task)
        {
            // Phase 1: Go to PlatesCounter (targetFacility), get plate
            // Phase 2: Go to ClearCounter (destFacility), place plate
            var platesCounter = task.targetFacility;
            var dropCounter = task.destFacility;

            if (_heldItem != null && _heldItem.objEnum == KitchenObjEnum.Plate)
            {
                AIDebugLogger.Log(chefName, "ExecuteFetchPlate: already holding a plate, delivering");
                // Already holding a plate — go deliver it
                _targetCounter = dropCounter ?? FindNearestFreeCounter(transform.position);
                _execPhase = ExecPhase.GotoDest;
                if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; }
                return;
            }

            _targetCounter = platesCounter ?? FindObjectsOfType<PlatesCounter>().FirstOrDefault();
            if (_targetCounter == null)
            {
                Debug.LogWarning($"[{chefName}] FETCH_PLATE: no PlatesCounter found");
                AbandonTask();
                return;
            }
            _execPhase = ExecPhase.None;
            if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; }
        }

        private void ExecuteAddToPlate(KitchenTask task)
        {
            // Pick up the ingredient → go to the order's plate → add to plate.
            // CRITICAL: find the plate at EXECUTION time via orderPlate, not at
            // task generation time. The plate may have moved between cycles.
            var ingredient = task.targetItem;
            int orderId = task.orderId;

            // Re-find the correct plate for this order right now
            BaseCounter correctPlateCounter = null;
            if (orderId != 0)
            {
                var orderPlate = _aiManager?.Blackboard?.FindPlateForOrder(orderId);
                if (orderPlate != null)
                {
                    var holder = orderPlate.GetHolder();
                    correctPlateCounter = holder as BaseCounter;
                }
            }

            // Fallback to the task's recorded counter
            if (correctPlateCounter == null)
                correctPlateCounter = task.targetFacility;

            // Check if already holding the right ingredient
            if (_heldItem != null && task.itemType != 0 && _heldItem.objEnum == task.itemType)
            {
                AIDebugLogger.Log(chefName, $"ExecuteAddToPlate: already holding {_heldItem.objEnum}, going to plate at {(correctPlateCounter != null ? correctPlateCounter.name : "?")}");
                _targetCounter = correctPlateCounter;
                _execPhase = ExecPhase.GotoDest;
                if (_targetCounter != null)
                {
                    if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; }
                }
                else
                    AbandonTask();
                return;
            }

            // Re-find the ingredient fresh (don't trust stale reference)
            if (ingredient == null || ingredient.gameObject == null)
            {
                ingredient = FindItemAnywhere(task.itemType);
                if (ingredient != null) task.targetItem = ingredient;
            }

            if (ingredient != null)
            {
                var holdingCounter = FindCounterHolding(ingredient);
                Vector3 pickupPos = holdingCounter != null
                    ? GetApproachPosition(holdingCounter)
                    : ingredient.transform.position;

                AIDebugLogger.Log(chefName, $"ExecuteAddToPlate: picking up {ingredient.objEnum} from {(holdingCounter != null ? holdingCounter.name : "ground")}");

                _carryTargetItem = ingredient;
                _targetCounter = correctPlateCounter;
                _execPhase = ExecPhase.GotoItem;
                if (!MoveTo(pickupPos)) { AbandonTask(); return; };
                return;
            }

            Debug.LogWarning($"[{chefName}] ADD_TO_PLATE: no ingredient to pick up");
            AbandonTask();
        }

        private void ExecuteServe(KitchenTask task)
        {
            var counter = task.targetFacility;
            if (counter == null)
            {
                Debug.LogWarning($"[{chefName}] SERVE task has no target facility");
                AbandonTask();
                return;
            }

            _targetCounter = counter;

            // Find the finished dish
            if (task.targetItem != null)
            {
                _carryTargetItem = task.targetItem;
                _carryDestPos = GetApproachPosition(counter);
                _execPhase = ExecPhase.GotoItem;
                if (!MoveTo(task.targetItem.transform.position)) { AbandonTask(); return; };
            }
            else
            {
                // Find any plate with ingredients
                var plates = FindObjectsOfType<KitchenObj>();
                KitchenObj bestPlate = null;
                float bestDist = float.MaxValue;

                foreach (var p in plates)
                {
                    if (p.objEnum == KitchenObjEnum.Plate && p.IsFree)
                    {
                        var plateComp = p.GetComponent<Plate>();
                        if (plateComp != null && plateComp.GetIngredients().Count > 0)
                        {
                            float d = Vector3.Distance(transform.position, p.transform.position);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestPlate = p;
                            }
                        }
                    }
                }

                if (bestPlate != null)
                {
                    _carryTargetItem = bestPlate;
                    _carryDestPos = GetApproachPosition(counter);
                    _execPhase = ExecPhase.GotoItem;
                    if (!MoveTo(bestPlate.transform.position)) { AbandonTask(); return; };
                }
                else
                {
                    // No plate found — abandon
                    Debug.Log($"[{chefName}] No finished plate found for SERVE");
                    AbandonTask();
                }
            }
        }

        private void ExecuteTrash(KitchenTask task)
        {
            // Pick up the waste item → go to TrashCounter → discard
            var wasteItem = task.targetItem;
            var trashCounter = task.targetFacility;

            if (wasteItem == null)
            {
                AIDebugLogger.LogWarning(chefName, "ExecuteTrash: no waste item to pick up");
                AbandonTask();
                return;
            }

            // If already holding the waste item, go to trash counter
            if (_heldItem != null && _heldItem == wasteItem)
            {
                AIDebugLogger.Log(chefName, $"ExecuteTrash: holding {_heldItem.objEnum}, heading to TrashCounter");
                _targetCounter = trashCounter;
                _execPhase = ExecPhase.GotoDest;
                if (!MoveTo(trashCounter.transform.position)) { AbandonTask(); return; }
                return;
            }

            // Go pick up the waste item first
            var holdingCounter = FindCounterHolding(wasteItem);
            Vector3 pickupPos = holdingCounter != null
                ? GetApproachPosition(holdingCounter)
                : wasteItem.transform.position;

            AIDebugLogger.Log(chefName, $"ExecuteTrash: picking up {wasteItem.objEnum} from {(holdingCounter != null ? holdingCounter.name : "ground")}");

            _carryTargetItem = wasteItem;
            _targetCounter = trashCounter;
            _execPhase = ExecPhase.GotoItem;
            if (!MoveTo(pickupPos)) { AbandonTask(); return; };
        }

        #endregion

        #region Interaction Execution

        private void ExecuteInteraction()
        {
            if (_targetCounter == null)
            {
                CompleteTask();
                return;
            }

            // Require AI to actually be within range of the counter before interacting
            float distToTarget = Vector3.Distance(transform.position, _targetCounter.transform.position);
            if (distToTarget > interactionRange)
            {
                // Not close enough — move closer first
                _substate = "moving";
                _stateTimer = 0;
                if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; };
                return;
            }

            switch (_currentTask?.type)
            {
                case TaskType.FETCH:
                    // Interact with ContainerCounter to spawn item into hand
                    if (_heldItem == null)
                    {
                        _targetCounter.Interact(this);
                    }

                    if (_heldItem != null)
                    {
                        // Tag item as belonging to this order (prevents cross-order theft)
                        if (_currentTask?.orderId != 0)
                            _aiManager?.Blackboard?.TagItemForOrder(_heldItem, _currentTask.orderId);

                        // Successfully got the item — now deliver it to a processing facility
                        var dropTarget = FindDropTarget(_currentTask.outputType);
                        if (dropTarget != null && dropTarget != _targetCounter)
                        {
                            Debug.Log($"[{chefName}] FETCH got {_heldItem.objEnum}, delivering to {dropTarget.name}");
                            AIDebugLogger.Log(chefName, $"FETCH got {_heldItem.objEnum}, delivering to {dropTarget.name}");
                            _targetCounter = dropTarget;
                            _execPhase = ExecPhase.GotoDest;
                            if (!MoveTo(dropTarget.transform.position)) { AbandonTask(); return; };
                        }
                        else
                        {
                            // No ideal drop target — find any free ClearCounter
                            var fallback = FindNearestFreeCounter(transform.position);
                            if (fallback != null)
                            {
                                Debug.Log($"[{chefName}] FETCH got {_heldItem.objEnum}, fallback to {fallback.name}");
                                AIDebugLogger.Log(chefName, $"FETCH got {_heldItem.objEnum}, fallback drop to {fallback.name}");
                                _targetCounter = fallback;
                                _execPhase = ExecPhase.GotoDest;
                                if (!MoveTo(fallback.transform.position)) { AbandonTask(); return; };
                            }
                            else
                            {
                                // Really no counter — just place on ground via DropItem
                                Debug.LogWarning($"[{chefName}] FETCH got {_heldItem.objEnum}, no counter — dropping on ground");
                                AIDebugLogger.LogWarning(chefName, $"FETCH: no drop target for {_heldItem.objEnum}, dropping on ground");
                                KitchenObjFactory.Instance.DropObjServerRpc(
                                    _heldItem.NetworkObject,
                                    transform.position + transform.forward * 0.5f,
                                    Vector3.down,
                                    0f,
                                    default);
                                ClearKitchenObj();
                                CompleteTask();
                            }
                        }
                    }
                    else
                    {
                        // Still waiting for spawn
                        _substate = "waiting";
                        _waitTimer = 0;
                    }
                    break;

                case TaskType.PROCESS:
                    HandleProcessInteraction();
                    break;

                case TaskType.FETCH_PLATE:
                    // Get plate from PlatesCounter
                    if (_heldItem == null)
                        _targetCounter.Interact(this);
                    if (_heldItem != null && _heldItem.objEnum == KitchenObjEnum.Plate)
                    {
                        // Got plate — record it for this order, then deliver to any free ClearCounter
                        if (_currentTask?.orderId != 0 && _heldItem is Plate plate)
                            _aiManager?.Blackboard?.AssignPlateToOrder(_currentTask.orderId, plate);

                        var dropTarget = _currentTask?.destFacility ?? FindNearestFreeCounter(transform.position);
                        Debug.Log($"[{chefName}] Got plate, delivering to {dropTarget.name}");
                        _targetCounter = dropTarget;
                        _execPhase = ExecPhase.GotoDest;
                        if (!MoveTo(dropTarget.transform.position)) { AbandonTask(); return; };
                    }
                    else
                    {
                        _substate = "waiting";
                        _waitTimer = 0;
                    }
                    break;

                case TaskType.ADD_TO_PLATE:
                    // Add held ingredient to plate on counter
                    if (_heldItem != null && _targetCounter != null && _targetCounter.HasKitchenObj())
                    {
                        var onCounter = _targetCounter.GetKitchenObj();
                        if (onCounter is Plate)
                        {
                            Debug.Log($"[{chefName}] Adding {_heldItem.objEnum} to plate");
                            _targetCounter.Interact(this);
                            CompleteTask();
                        }
                        else
                        {
                            Debug.LogWarning($"[{chefName}] ADD_TO_PLATE: no Plate on {_targetCounter.name}");
                            CompleteTask();
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[{chefName}] ADD_TO_PLATE: missing held item or plate");
                        CompleteTask();
                    }
                    break;

                case TaskType.SERVE:
                    // Put plate on delivery counter
                    if (_heldItem != null)
                    {
                        _targetCounter.Interact(this);
                        // If plate is still in hand, the order was rejected.
                        // Discard the bad plate at TrashCounter instead of polluting a ClearCounter.
                        if (_heldItem != null && _heldItem is Plate)
                        {
                            AIDebugLogger.LogWarning(chefName, $"SERVE failed — discarding bad plate");
                            var trash = FindObjectsOfType<TrashCounter>().FirstOrDefault();
                            if (trash != null)
                            {
                                _targetCounter = trash;
                                _execPhase = ExecPhase.GotoDest;
                                if (!MoveTo(trash.transform.position)) { AbandonTask(); return; };
                                return; // will arrive, interact with trash, then CompleteTask
                            }
                        }
                        CompleteTask();
                    }
                    else
                    {
                        CompleteTask();
                    }
                    break;

                case TaskType.TRASH:
                    // Put waste item in trash
                    if (_heldItem != null && _targetCounter is TrashCounter)
                    {
                        AIDebugLogger.Log(chefName, $"Trashing {_heldItem.objEnum}");
                        _targetCounter.Interact(this);
                        CompleteTask();
                    }
                    else if (_heldItem != null)
                    {
                        // Arrived at trash but can't interact? Just drop and complete
                        AIDebugLogger.LogWarning(chefName, $"TRASH: can't trash {_heldItem.objEnum}, dropping");
                        CompleteTask();
                    }
                    else
                    {
                        CompleteTask();
                    }
                    break;

                default:
                    CompleteTask();
                    break;
            }
        }

        private void HandleProcessInteraction()
        {
            var counter = _targetCounter;
            if (counter == null || _currentTask == null) { CompleteTask(); return; }

            bool hasItem = counter.HasKitchenObj();
            var counterItem = hasItem ? counter.GetKitchenObj() : null;
            var counterType = counterItem?.objEnum.ToString() ?? "empty";

            AIDebugLogger.Log(chefName, $"HandleProcess: held={_heldItem?.objEnum.ToString() ?? "none"} " +
                $"counterHas={hasItem} counterItem={counterType} " +
                $"taskInput={_currentTask.itemType} taskOutput={_currentTask.outputType}");

            // === CASE 1: Holding input, counter empty → place item and start ===
            if (_heldItem != null && _currentTask.itemType != 0 &&
                _heldItem.objEnum == _currentTask.itemType && !hasItem)
            {
                AIDebugLogger.LogState(chefName, "placing", _heldItem.objEnum.ToString(), $"→ {counter.name}");
                counter.Interact(this);
                _substate = "waiting";
                _waitTimer = 0;
                return;
            }

            // === CASE 2: Counter has OUTPUT → take it and complete ===
            // Also handles edge case: AI holds input but output is already ready (abandoned cook)
            if (hasItem && counterItem.objEnum == _currentTask.outputType)
            {
                // If holding something else, drop it first
                if (_heldItem != null && _heldItem.objEnum != _currentTask.outputType)
                {
                    AIDebugLogger.Log(chefName, $"HandleProcess: dropping held {_heldItem.objEnum} to take ready output {counterItem.objEnum}");
                    var dropSpot = FindNearestFreeCounter(transform.position);
                    if (dropSpot != null) dropSpot.Interact(this);
                    else { ClearKitchenObj(); }
                }
                AIDebugLogger.LogState(chefName, "taking output", counterItem.objEnum.ToString(), $"from {counter.name}");
                counter.Interact(this);
                // Tag the output as belonging to this order
                if (_heldItem != null && _currentTask?.orderId != 0)
                    _aiManager?.Blackboard?.TagItemForOrder(_heldItem, _currentTask.orderId);
                CompleteTask();
                return;
            }

            // === CASE 3: Counter has INPUT → start processing ===
            if (_heldItem == null && hasItem && counterItem.objEnum == _currentTask.itemType)
            {
                if (counter is CuttingCounter cc)
                {
                    AIDebugLogger.LogState(chefName, "start cutting", _currentTask.itemType.ToString(),
                        $"→ {_currentTask.outputType} on {counter.name}");
                    cc.PublicStartCutting();
                    _substate = "working";
                    _stateTimer = 0;
                }
                else if (counter is StoveCounter sc)
                {
                    AIDebugLogger.LogState(chefName, "start/subscribe cooking", _currentTask.itemType.ToString(),
                        $"→ {_currentTask.outputType} on {counter.name}");

                    // Check if output is ALREADY ready (cooking happened before we subscribed)
                    if (counter.HasKitchenObj() &&
                        counter.GetKitchenObj().objEnum == _currentTask.outputType)
                    {
                        AIDebugLogger.Log(chefName, $"Output {_currentTask.outputType} already ready, taking it now");
                        counter.Interact(this);
                        CompleteTask();
                        return;
                    }

                    sc.OnCookingStageChange += OnStoveStageChanged;
                    _substate = "waiting";
                    _waitTimer = 0;
                }
                return;
            }

            // === CASE 4: Counter has BURNED/other item → clear it to free the facility ===
            // Handles both: AI empty-handed, and AI holding input (must clear counter first)
            if (hasItem && counterItem != null &&
                counterItem.objEnum != _currentTask.itemType &&
                counterItem.objEnum != _currentTask.outputType)
            {
                // If holding the input, temporarily put it down
                KitchenObj heldBeforeClear = _heldItem;
                if (_heldItem != null && _heldItem.objEnum == _currentTask.itemType)
                {
                    AIDebugLogger.Log(chefName, $"HandleProcess: temporarily dropping held {_heldItem.objEnum} to clear counter");
                    var tempDrop = FindNearestFreeCounter(transform.position);
                    if (tempDrop != null) tempDrop.Interact(this);
                }

                AIDebugLogger.LogWarning(chefName, $"HandleProcess: clearing unwanted {counterItem.objEnum} from {counter.name}");
                counter.Interact(this); // Take the burned/wrong item off

                // Drop the cleared item on a nearby free counter (or ground as last resort)
                if (_heldItem != null)
                {
                    var freeDrop = FindNearestFreeCounter(transform.position);
                    if (freeDrop != null)
                    {
                        AIDebugLogger.Log(chefName, $"Moving cleared {_heldItem.objEnum} to {freeDrop.name}");
                        freeDrop.Interact(this);
                    }
                    else
                    {
                        AIDebugLogger.LogWarning(chefName, $"Dropping cleared {_heldItem.objEnum} on ground");
                        KitchenObjFactory.Instance.DropObjServerRpc(
                            _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                            Vector3.down, 0f, default);
                        ClearKitchenObj();
                    }
                }

                // Now re-fetch the input we put aside (if any) and continue
                if (heldBeforeClear != null)
                {
                    AIDebugLogger.Log(chefName, $"Re-fetching input {heldBeforeClear.objEnum} after clearing counter");
                    _carryTargetItem = heldBeforeClear;
                    _execPhase = ExecPhase.GotoItem;
                    if (!MoveTo(heldBeforeClear.transform.position)) { AbandonTask(); return; };
                    return;
                }

                // We were empty-handed — must self-fetch the input and bring it here
                if (_currentTask.itemType != 0)
                {
                    AIDebugLogger.Log(chefName, $"Counter cleared — self-fetching {_currentTask.itemType} to retry PROCESS");
                    var foundItem = FindItemAnywhere(_currentTask.itemType);
                    if (foundItem != null)
                    {
                        var holdingCounter = FindCounterHolding(foundItem);
                        Vector3 pickupPos = holdingCounter != null
                            ? GetApproachPosition(holdingCounter)
                            : foundItem.transform.position;
                        _carryTargetItem = foundItem;
                        _carryDestPos = GetApproachPosition(counter);
                        _execPhase = ExecPhase.GotoItem;
                        if (!MoveTo(pickupPos)) { AbandonTask(); return; };
                        return;
                    }
                }

                // No input available — abandon
                AIDebugLogger.LogWarning(chefName, $"Counter {counter.name} cleared but no {_currentTask.itemType} to retry — abandoning");
                AbandonTask();
                return;
            }

            // === CASE 5: Counter empty and we don't hold input → abandon ===
            if (!hasItem && _heldItem == null)
            {
                AIDebugLogger.LogWarning(chefName, $"HandleProcess: counter empty, nothing to process — abandoning");
                AbandonTask();
                return;
            }

            // === CASE 6: Other edge case → drop held item and abandon ===
            // (e.g., agent holding unrelated item)
            if (_heldItem != null)
            {
                AIDebugLogger.LogWarning(chefName, $"HandleProcess edge case: dropping unrelated {_heldItem.objEnum}");
                var freeCounter = FindNearestFreeCounter(transform.position);
                if (freeCounter != null) freeCounter.Interact(this);
                else { ClearKitchenObj(); }
            }
            AIDebugLogger.LogWarning(chefName, $"HandleProcess: edge case — abandoning");
            AbandonTask();
        }

        #endregion

        #region Item Handling (ICanHoldKitchenObj)

        public Transform GetHoldTransform() => _holdPoint;

        public KitchenObj GetKitchenObj() => _heldItem;

        public void SetKitchenObj(KitchenObj newKitchenObj)
        {
            _heldItem = newKitchenObj;
            _isHoldingItem = newKitchenObj != null;
        }

        public bool HasKitchenObj() => _heldItem != null;

        public void ClearKitchenObj()
        {
            _heldItem = null;
            _isHoldingItem = false;
        }

        public NetworkObject GetNetworkObject()
        {
            // Try cached NetworkObject first, then GetComponent
            return gameObject.GetComponent<NetworkObject>();
        }

        private void UpdateHeldItem()
        {
            if (_heldItem != null && _holdPoint != null)
            {
                // Visual positioning is handled by KitchenObj's holder system
            }
        }

        private void PickupItem()
        {
            if (_carryTargetItem == null)
            {
                AIDebugLogger.LogWarning(chefName, "PickupItem: _carryTargetItem is null");
                return;
            }

            Debug.Log($"[{chefName}] Picking up {_carryTargetItem.objEnum}");

            // Verify item is actually free before attempting pickup
            if (!_carryTargetItem.IsFree)
            {
                AIDebugLogger.LogWarning(chefName,
                    $"PickupItem: {_carryTargetItem.objEnum} is NOT free (holder={(_carryTargetItem.GetHolder() as UnityEngine.Object)?.name ?? "?"})");
                _carryTargetItem = null;
                AbandonTask();
                return;
            }

            // Direct pickup (server-side RPC — synchronous on host)
            var factory = KitchenObjFactory.Instance;
            if (factory != null)
            {
                KitchenObjFactory.Instance.PickupObjServerRpc(
                    _carryTargetItem.NetworkObject,
                    GetNetworkObject());
            }

            // Verify pickup succeeded (RPC runs synchronously on host)
            if (_heldItem == null || _heldItem != _carryTargetItem)
            {
                AIDebugLogger.LogWarning(chefName,
                    $"PickupItem: RPC call completed but _heldItem is NOT {_carryTargetItem?.objEnum}. held={_heldItem?.objEnum.ToString() ?? "null"}");
                _carryTargetItem = null;
                AbandonTask();
                return;
            }

            AIDebugLogger.Log(chefName, $"PickupItem: successfully picked up {_heldItem.objEnum}");
            _carryTargetItem = null;
        }

        private void DropItemAtFacility()
        {
            if (_heldItem != null && _targetCounter != null)
            {
                _targetCounter.Interact(this);
                AIDebugLogger.Log(chefName, $"DropItemAtFacility: {_heldItem?.objEnum} → {_targetCounter.name}");
                Debug.Log($"[{chefName}] Dropped item at {_targetCounter.name}");
            }

            // Now start the actual work
            _substate = "interacting";
            _stateTimer = 0;
        }

        private void DropItemAtDestination()
        {
            if (_heldItem != null && _targetCounter != null)
            {
                _targetCounter.Interact(this);
                Debug.Log($"[{chefName}] Dropped item at destination {_targetCounter.name}");
            }

            _execPhase = ExecPhase.None;
            CompleteTask();
        }

        #endregion

        #region Task Completion

        private void CleanupTask()
        {
            // Unsubscribe from stove events
            if (_targetCounter is StoveCounter sc)
                sc.OnCookingStageChange -= OnStoveStageChanged;

            int taskOrderId = _currentTask?.orderId ?? 0;
            TaskType? taskType = _currentTask?.type;

            // Drop held item so scheduler can find it for other tasks
            if (_heldItem != null)
            {
                BaseCounter dropCounter = null;

                // For SERVE tasks, discard bad plates at TrashCounter
                if (taskType == TaskType.SERVE && _heldItem is Plate)
                {
                    dropCounter = FindObjectsOfType<TrashCounter>().FirstOrDefault() as BaseCounter;
                }
                // Prefer the order's own plate location
                if (dropCounter == null && taskOrderId != 0)
                {
                    var blackboard = _aiManager?.Blackboard;
                    if (blackboard != null)
                    {
                        var orderPlate = blackboard.FindPlateForOrder(taskOrderId);
                        if (orderPlate != null && _heldItem is Plate)
                        {
                            var holder = orderPlate.GetHolder();
                            if (holder is BaseCounter bc && !bc.HasKitchenObj())
                                dropCounter = bc;
                        }
                    }
                }

                // Prefer direct-to-plate delivery for processed ingredients
                if (dropCounter == null && _heldItem != null)
                {
                    dropCounter = FindPlateNeedingIngredient(_heldItem.objEnum);
                }
                // Fallback to nearest free counter, or any counter if all occupied
                if (dropCounter == null)
                {
                    dropCounter = FindNearestFreeCounter(transform.position);
                    if (dropCounter == null)
                    {
                        float bestDist = float.MaxValue;
                        foreach (var c in FindObjectsOfType<ClearCounter>())
                        {
                            float d = Vector3.Distance(transform.position, c.transform.position);
                            if (d < bestDist) { bestDist = d; dropCounter = c; }
                        }
                    }
                }

                if (dropCounter != null)
                {
                    float dist = Vector3.Distance(transform.position, dropCounter.transform.position);
                    if (dist < interactionRange)
                    {
                        // Close enough — drop now, then mark complete
                        AIDebugLogger.Log(chefName, $"CleanupTask: dropping {_heldItem.objEnum} at {dropCounter.name}");
                        dropCounter.Interact(this);
                    }
                    else
                    {
                        // Too far — move there first. Keep task marked as not-done
                        // so the scheduler doesn't assign a new task prematurely.
                        if (_currentTask != null) _currentTask.status = "executing";
                        AIDebugLogger.Log(chefName, $"CleanupTask: moving to {dropCounter.name} to drop {_heldItem.objEnum} (dist={dist:F1})");
                        _targetCounter = dropCounter;
                        _execPhase = ExecPhase.GotoDest;
                        if (!MoveTo(dropCounter.transform.position))
                        {
                            // Can't approach — drop on ground instead
                            AIDebugLogger.Log(chefName, $"CleanupTask: can't approach {dropCounter.name}, dropping on ground");
                            KitchenObjFactory.Instance.DropObjServerRpc(
                                _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                                Vector3.down, 0f, default);
                            ClearKitchenObj();
                            if (_currentTask != null) { _aiManager?.OnAgentTaskCompleted(this, _currentTask); _currentTask = null; }
                            _substate = "idle";
                            _execPhase = ExecPhase.None;
                            _targetCounter = null;
                            return;
                        }
                        return;
                    }
                }
                else
                {
                    AIDebugLogger.Log(chefName, $"CleanupTask: dropping {_heldItem.objEnum} on ground");
                    KitchenObjFactory.Instance.DropObjServerRpc(
                        _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                        Vector3.down, 0f, default);
                    ClearKitchenObj();
                }
            }

            // Only signal completion AFTER item is successfully placed (or no item to place)
            if (_currentTask != null)
            {
                _aiManager?.OnAgentTaskCompleted(this, _currentTask);
                _currentTask = null;
            }

            _substate = "paused";
            _stateTimer = 0;
            _pauseCallback = "idle";
            _execPhase = ExecPhase.None;
            _targetCounter = null;
            _carryTargetItem = null;
            _moveTimer = 0;
            _waitTimer = 0;
            _ai.isStopped = true;
            debugState = "paused";
        }

        private void CompleteTask()
        {
            AIDebugLogger.LogTaskComplete(agentId, chefName, _currentTask, "completed");
            if (_currentTask != null) _currentTask.status = "completed";
            CleanupTask();
        }

        private void AbandonTask()
        {
            AIDebugLogger.LogTaskAbandon(agentId, chefName, _currentTask,
                $"substate={_substate} phase={_execPhase} timer={_stateTimer:F1}");
            if (_currentTask != null) _currentTask.status = "abandoned";
            CleanupTask();
        }

        /// <summary>
        /// Force-abandon the current task without calling back to the manager.
        /// Used by KitchenAIManager deadlock detection to cleanly reset agent state.
        /// </summary>
        public void ForceAbandonTask()
        {
            if (_currentTask == null) return;

            // Unsubscribe from stove events
            if (_targetCounter is StoveCounter sc)
                sc.OnCookingStageChange -= OnStoveStageChanged;

            // Drop held item so scheduler can find it
            if (_heldItem != null)
            {
                var dropCounter = FindNearestFreeCounter(transform.position);
                if (dropCounter != null)
                    dropCounter.Interact(this);
                else
                {
                    KitchenObjFactory.Instance.DropObjServerRpc(
                        _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                        Vector3.down, 0f, default);
                    ClearKitchenObj();
                }
            }

            _currentTask = null;
            _substate = "idle";
            _execPhase = ExecPhase.None;
            _targetCounter = null;
            _carryTargetItem = null;
            _moveTimer = 0f;
            _waitTimer = 0f;
            _stateTimer = 0f;
            _ai.isStopped = true;
            debugState = "idle";
        }

        /// <summary>
        /// Periodically check if waiting can end early (e.g., plate spawned, item cooked).
        /// </summary>
        private bool CanProceedFromWaiting()
        {
            if (_currentTask == null || _targetCounter == null) return false;

            switch (_currentTask.type)
            {
                case TaskType.FETCH_PLATE:
                    // Check if we got a plate from PlatesCounter
                    if (_heldItem != null && _heldItem.objEnum == KitchenObjEnum.Plate)
                        return true;
                    // Or check if PlatesCounter now has plates
                    if (_targetCounter is PlatesCounter pc && pc.plateCount > 0)
                        return true;
                    break;

                case TaskType.FETCH:
                    // Got the ingredient
                    if (_heldItem != null) return true;
                    break;

                case TaskType.PROCESS:
                    // Check if processing is done (output item on counter)
                    if (_targetCounter.HasKitchenObj())
                    {
                        var objOnCounter = _targetCounter.GetKitchenObj();
                        debugState = $"wait-chk {objOnCounter.objEnum} vs {_currentTask.outputType}";
                        if (_currentTask.outputType != 0 &&
                            objOnCounter.objEnum == _currentTask.outputType)
                        {
                            Debug.Log($"[{chefName}] PROCESS output ready: {objOnCounter.objEnum} on {_targetCounter.name}");
                            return true;
                        }
                    }
                    else
                    {
                        debugState = "wait-chk counter empty";
                    }
                    break;

                case TaskType.ADD_TO_PLATE:
                    // Ingredient is in hand and plate is on counter
                    if (_heldItem != null && _targetCounter.HasKitchenObj() &&
                        _targetCounter.GetKitchenObj() is Plate)
                        return true;
                    break;
            }
            return false;
        }

        /// <summary>
        /// Called when a StoveCounter changes cooking stage.
        /// If the desired output is ready, grab it before it burns.
        /// </summary>
        private void OnStoveStageChanged(KitchenObjEnum? currentStage)
        {
            if (_currentTask == null || _targetCounter == null) return;
            if (_currentTask.type != TaskType.PROCESS) return;
            if (!currentStage.HasValue) return;

            AIDebugLogger.Log(chefName, $"Stove stage changed: {currentStage.Value} (want {_currentTask.outputType})");

            if (currentStage.Value == _currentTask.outputType &&
                _targetCounter.HasKitchenObj())
            {
                Debug.Log($"[{chefName}] Stove output ready: {currentStage.Value}, grabbing before it burns!");
                AIDebugLogger.LogState(chefName, "stove grab", currentStage.Value.ToString(),
                    "output ready, taking before burn");
                // Unsubscribe immediately
                if (_targetCounter is StoveCounter sc)
                    sc.OnCookingStageChange -= OnStoveStageChanged;
                // Take the item
                _targetCounter.Interact(this);

                // Deliver cooked output — prefer direct-to-plate if possible
                if (_heldItem != null)
                {
                    var plateTarget = FindPlateNeedingIngredient(_heldItem.objEnum);
                    var dropTarget = plateTarget ?? FindNearestFreeCounter(transform.position);
                    if (dropTarget != null)
                    {
                        AIDebugLogger.Log(chefName, $"Delivering cooked {_heldItem.objEnum} to {dropTarget.name}" +
                            (plateTarget != null ? " (direct-to-plate)" : ""));
                        _execPhase = ExecPhase.GotoDest;
                        _targetCounter = dropTarget;
                        if (!MoveTo(dropTarget.transform.position)) { CompleteTask(); return; }
                        return;
                    }
                }

                CompleteTask();
            }
        }

        private float GetMaxWaitTime()
        {
            if (_currentTask == null) return 2f;
            switch (_currentTask.type)
            {
                case TaskType.FETCH_PLATE: return 10f;  // plates spawn every 4s
                case TaskType.PROCESS:     return 12f;  // cooking can take multiple stages
                case TaskType.FETCH:       return 5f;
                default:                   return 4f;
            }
        }

        /// <summary>
        /// Find an item of the given type anywhere: on ground or on any counter.
        /// Only returns items that are actually free to pick up (not being processed).
        /// </summary>
        private KitchenObj FindItemAnywhere(KitchenObjEnum itemType)
        {
            KitchenObj bestItem = null;
            float bestDist = float.MaxValue;

            // Check ground items (free-standing)
            foreach (var item in FindObjectsOfType<KitchenObj>())
            {
                if (item == null || item.objEnum != itemType) continue;
                if (!item.IsFree) continue;
                float d = Vector3.Distance(transform.position, item.transform.position);
                if (d < bestDist) { bestDist = d; bestItem = item; }
            }

            // Check items on counters (IsFree is always false for counter-held items,
            // but they can be picked up via counter.Interact())
            foreach (var counter in FindObjectsOfType<BaseCounter>())
            {
                if (counter == null || !counter.HasKitchenObj()) continue;
                var item = counter.GetKitchenObj();
                if (item == null || item.objEnum != itemType) continue;
                // Skip items on StoveCounter or CuttingCounter (being actively processed)
                if (counter is StoveCounter || counter is CuttingCounter) continue;
                // Skip items on PlateCounter, DeliveryCounter, TrashCounter
                if (counter is PlatesCounter || counter is DeliveryCounter) continue;
                float d = Vector3.Distance(transform.position, counter.transform.position);
                if (d < bestDist) { bestDist = d; bestItem = item; }
            }

            return bestItem;
        }

        #endregion

        /// <summary>
        /// Find the BaseCounter that currently holds the given KitchenObj.
        /// Returns null if the item is not on any counter (free on ground or being carried).
        /// </summary>
        private BaseCounter FindCounterHolding(KitchenObj item)
        {
            if (item == null) return null;
            foreach (var c in FindObjectsOfType<BaseCounter>())
            {
                if (c.HasKitchenObj() && c.GetKitchenObj() == item)
                    return c;
            }
            return null;
        }

        #region Utility

        /// <summary>Immediately snap to face the target counter (called on arrival).</summary>
        private void SnapFaceTarget()
        {
            if (_targetCounter == null) return;
            Vector3 dir = _targetCounter.transform.position - transform.position;
            dir.y = 0;
            if (dir.magnitude < 0.01f) return;
            transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        /// <summary>Smoothly rotate to face the target counter.</summary>
        private void FaceTarget()
        {
            if (_targetCounter == null) return;
            Vector3 dir = _targetCounter.transform.position - transform.position;
            dir.y = 0;
            if (dir.magnitude < 0.01f) return;
            Quaternion targetRot = Quaternion.LookRotation(dir.normalized);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 15f * Time.deltaTime);
        }

        /// <summary>
        /// Return the counter center as the movement target.
        /// AIPath will path as close as possible to the counter on the navmesh.
        /// Arrival is triggered when within interactionRange * arrivalThreshold of this point.
        /// </summary>
        private Vector3 GetApproachPosition(BaseCounter counter)
        {
            Vector3 pos = counter.transform.position;
            pos.y = 0;
            return pos;
        }

        #endregion

        #region Debug

        private void OnDrawGizmos()
        {
            if (!showDebugGizmos) return;

            // Draw chef position
            Gizmos.color = chefColor;
            Gizmos.DrawWireSphere(transform.position, 0.3f);

            // Draw interaction radius
            Gizmos.color = new Color(chefColor.r, chefColor.g, chefColor.b, 0.3f);
            Gizmos.DrawWireSphere(transform.position, interactionRange);
            Gizmos.color = new Color(chefColor.r, chefColor.g, chefColor.b, 0.1f);
            Gizmos.DrawWireSphere(transform.position, interactionRange * arrivalThreshold);

            // Draw target
            if (_substate == "moving" && _ai != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(transform.position, _ai.destination);
                Gizmos.DrawWireSphere(_ai.destination, 0.3f);
            }

            // Draw selected approach point (larger cyan circle for comparison)
            if (_hasApproachPoint)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(_lastApproachPoint, 0.4f);
            }

            // Draw approach candidates (4 cardinal points around target counter)
            if (_targetCounter != null && _approachOffset > 0)
            {
                Vector3 center = _targetCounter.transform.position;
                center.y = 0;
                Vector3[] offsets = new Vector3[]
                {
                    new Vector3(_approachOffset, 0, 0),
                    new Vector3(-_approachOffset, 0, 0),
                    new Vector3(0, 0, _approachOffset),
                    new Vector3(0, 0, -_approachOffset),
                };

                foreach (var offset in offsets)
                {
                    var candidate = center + offset;
                    bool onNavMesh = false;
                    if (AstarPath.active != null)
                    {
                        var nearest = AstarPath.active.GetNearest(candidate);
                        onNavMesh = nearest.node != null && Vector3.Distance(nearest.position, candidate) < 0.01f;
                    }
                    Gizmos.color = onNavMesh ? Color.green : Color.red;
                    Gizmos.DrawWireSphere(candidate, 0.15f);
                    // Cross to mark rejected candidates
                    if (!onNavMesh)
                    {
                        Gizmos.DrawLine(candidate + Vector3.one * 0.1f, candidate - Vector3.one * 0.1f);
                    }
                }
            }

            // Draw held item indicator
            if (_heldItem != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(transform.position + Vector3.up * 1.5f, 0.2f);
            }

            // Draw name
#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2.5f,
                $"{chefName}\n{debugState}");
#endif
        }

        #endregion
    }
}
