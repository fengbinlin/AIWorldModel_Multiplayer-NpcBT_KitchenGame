using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Netcode;
using Pathfinding;
using Kitchen;

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
        [SerializeField] private float _approachOffset = 1.0f;

        private IAstarAI _ai;          // A* Pathfinding Project movement component
        private AIPath _aiPath;       // cached AIPath component for settings access

        [Header("Debug")]
        public bool showDebugGizmos = true;
        public string debugState = "idle";

        #endregion

        #region Private State

        private KitchenTask _currentTask;
        private BaseCounter _targetCounter;
        private string _substate = "idle"; // idle | moving | interacting | working | waiting | paused | postInteract
        private string _pauseCallback;    // what to do after pause: "arrived" | "idle" | "moveAfterWait"
        private float _stateTimer;
        private bool _facingComplete;     // true when smooth rotation to face target is done
        private Vector3 _pendingMovePos;  // stored destination for post-interact MoveTo
        private float _moveTimer;
        private float _waitTimer;
        private bool _isHoldingItem;

        // Wander
        [Header("Wander")]
        [SerializeField] private bool _enableWander = true;
        [SerializeField] private int _idleWanderPointCount = 12;
        [SerializeField] private float _wanderRadius = 3f;
        [SerializeField] private float _wanderInterval = 0.8f;
        [SerializeField] private float _wanderNavmeshTolerance = 0.5f;
        private readonly List<Vector3> _idleWanderPoints = new();
        private float _wanderTimer;
        private bool _isWandering;
        private int _lastWanderPointIndex = -1;

        // Anti-stuck
        private float _lastProgressDist = float.MaxValue;
        private float _stuckProgressTimer;
        private Vector3 _spawnPosition;
        private Vector3 _moveTarget;

        [Header("Anti-Stuck Detour")]
        [SerializeField] private float _detourRadius = 2.5f;
        [SerializeField] private float _detourAngleStep = 60f;
        private enum DetourPhase { None, ToTransit }
        private DetourPhase _detourPhase = DetourPhase.None;
        private bool _isDetouring;
        private Vector3 _detourOriginalDest;
        private int _detourAttempt;

        // Yield behavior — sidestep to let oncoming agent pass (fallback)
        private bool _isYielding;
        private Vector3 _yieldOriginalDest;

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

        /// <summary>Fired when the chef performs an interact action (maps to E key in recordings).</summary>
        public event System.Action OnInteractionPerformed;

        /// <summary>Apply A* Pathfinding + RVO params after spawn.</summary>
        public void SetAIParams(float radius, float maxSpeed, float rvoPriority, float approachOffset)
        {
            // 过大半径会在 Recast 收窄后的走廊里反复卡死（旧默认 0.9）
            float r = Mathf.Clamp(radius, 0.25f, 0.55f);
            if (_aiPath != null) _aiPath.radius = r;
            if (_ai != null) _ai.maxSpeed = maxSpeed;
            _approachOffset = approachOffset;

            var rvo = GetComponent<Pathfinding.RVO.RVOController>();
            if (rvo != null)
            {
                rvo.radius = r;
                rvo.priority = rvoPriority;
            }
        }

        /// <summary>Force immediate path recalculation (called by scheduler).</summary>
        public void ForceRepath()
        {
            if (_ai != null && !_ai.isStopped && _aiPath != null && _aiPath.enabled)
            {
                _ai.SearchPath();
            }
        }

        /// <summary>Set approach-offset independently (for scene-placed chefs).</summary>
        public void SetApproachOffset(float offset)
        {
            _approachOffset = offset;
        }

        /// <summary>Set birth/spawn position used for detour waypoints.</summary>
        public void SetSpawnPosition(Vector3 position)
        {
            _spawnPosition = position;
            _spawnPosition.y = 0f;
        }

        /// <summary>
        /// Pre-generate idle wander points on the navmesh around spawn centers.
        /// </summary>
        public void BuildIdleWanderPoints(IReadOnlyList<Vector3> centers)
        {
            _idleWanderPoints.Clear();
            if (AstarPath.active == null) return;

            var centerList = new List<Vector3>();
            if (centers != null)
            {
                foreach (var c in centers)
                {
                    if (c == Vector3.zero) continue;
                    var flat = c;
                    flat.y = 0f;
                    centerList.Add(flat);
                }
            }
            if (centerList.Count == 0)
            {
                if (_spawnPosition != Vector3.zero)
                    centerList.Add(_spawnPosition);
                else
                    centerList.Add(transform.position);
            }

            int pointsPerCenter = Mathf.Max(3, Mathf.CeilToInt((float)_idleWanderPointCount / centerList.Count));
            foreach (var center in centerList)
            {
                int added = 0;
                int attempts = 0;
                int maxAttempts = pointsPerCenter * 10;
                while (added < pointsPerCenter && attempts < maxAttempts)
                {
                    attempts++;
                    Vector3 offset = Random.insideUnitSphere * _wanderRadius;
                    offset.y = 0f;
                    Vector3 candidate = center + offset;

                    var nearest = AstarPath.active.GetNearest(candidate);
                    if (nearest.node == null || Vector3.Distance(nearest.position, candidate) > _wanderNavmeshTolerance)
                        continue;

                    Vector3 point = nearest.position;
                    bool duplicate = false;
                    foreach (var existing in _idleWanderPoints)
                    {
                        if (Vector3.Distance(existing, point) < 0.6f)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (duplicate) continue;

                    _idleWanderPoints.Add(point);
                    added++;
                }
            }

            if (_idleWanderPoints.Count == 0)
            {
                var fallbackCenter = _spawnPosition != Vector3.zero ? _spawnPosition : transform.position;
                var nearest = AstarPath.active.GetNearest(fallbackCenter);
                if (nearest.node != null)
                    _idleWanderPoints.Add(nearest.position);
            }
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

            // Disable physics colliders — RVO handles movement/avoidance
            foreach (var col in GetComponents<Collider>())
                col.enabled = false;
            foreach (var col in GetComponentsInChildren<Collider>())
                col.enabled = false;

            // --- A* Pathfinding Project setup ---
            _aiPath = GetComponent<AIPath>();
            if (_aiPath == null) _aiPath = gameObject.AddComponent<AIPath>();
            _ai = _aiPath;

            // Funnel：把三角形折线收成走廊可见路径，拐角才能贴边走
            if (GetComponent<FunnelModifier>() == null)
                gameObject.AddComponent<FunnelModifier>();

            _aiPath.radius = 0.4f;
            _aiPath.height = 1.8f;
            _ai.maxSpeed = _moveSpeed;
            _aiPath.rotationSpeed = 360f;
            _aiPath.endReachedDistance = 0.35f;
            // 旧值 pickNextWaypointDist=8 / slowdown=3：会瞄准 8m 外路径点，直线穿柜子；
            // 走廊对齐时碰巧通，拐角时就会“明明能绕却硬撞”。默认约 2，厨房用更紧一点。
            _aiPath.slowdownDistance = 1.0f;
            _aiPath.pickNextWaypointDist = 1.25f;
            _aiPath.whenCloseToDestination = CloseToDestinationMode.Stop;
            _aiPath.constrainInsideGraph = true;
            _aiPath.autoRepath.mode = AutoRepathPolicy.Mode.Dynamic;

            // RVO Controller for local avoidance (AIBase auto-integrates it)
            var rvo = GetComponent<Pathfinding.RVO.RVOController>();
            if (rvo == null)
                rvo = gameObject.AddComponent<Pathfinding.RVO.RVOController>();
            rvo.radius = 0.4f;
            rvo.height = 1.8f;
            rvo.agentTimeHorizon = 2f;
            rvo.obstacleTimeHorizon = 1f; // 静态障碍主要靠 Recast；RVO 障碍视野过大易把人“挤”进墙角
            rvo.maxNeighbours = 10;
            rvo.lockWhenNotMoving = false;
            rvo.priority = 0.5f; // default, overridden by SetAIParams
        }

        private void Start()
        {
            if (_spawnPosition == Vector3.zero)
                SetSpawnPosition(transform.position);

            _aiManager = KitchenAIManager.Instance;
            if (_aiManager != null)
            {
                _aiManager.RegisterAgent(this);
                BuildIdleWanderPoints(_aiManager.GetSpawnPositions());
            }
            else
            {
                BuildIdleWanderPoints(null);
            }

            // Start wandering soon after spawn when idle
            _wanderTimer = _wanderInterval;
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

            // RVO locked when stationary, unlocked when moving.
            bool isStationary = (_substate == "waiting" || _substate == "working" || _substate == "interacting" || _substate == "paused" || _substate == "postInteract");

            var rvo = GetComponent<Pathfinding.RVO.RVOController>();
            if (rvo != null) rvo.locked = isStationary;

            // Freeze pathfinding while stationary
            if (_ai != null)
                _ai.isStopped = isStationary;
            if (_aiPath != null)
                _aiPath.enabled = !isStationary;

            switch (_substate)
            {
                case "idle":
                    debugState = _isWandering ? "wandering" : $"idle roam ({_idleWanderPoints.Count} pts)";
                    if (_enableWander && _currentTask == null)
                    {
                        if (_idleWanderPoints.Count == 0)
                        {
                            if (_aiManager != null)
                                BuildIdleWanderPoints(_aiManager.GetSpawnPositions());
                            else
                                BuildIdleWanderPoints(null);
                        }

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

                        // Track progress: if distance hasn't decreased significantly, repath
                        if (dist < _lastProgressDist - 0.1f)
                        {
                            _lastProgressDist = dist;
                            _stuckProgressTimer = 0f;
                        }
                        else
                        {
                            _stuckProgressTimer += Time.deltaTime;
                        }
                        if (_stuckProgressTimer > 0.8f && !_isWandering)
                        {
                            if (_stuckProgressTimer > 2f && !_isYielding && !_isDetouring)
                            {
                                // Long deadlock — detour via a rotated waypoint near spawn
                                if (!TrySpawnDetour())
                                    TrySidestepYield();
                            }
                            else if (!_isYielding && !_isDetouring)
                            {
                                // 只重寻路，不要随机改 destination（会偏出 NavMesh，表现成直线撞墙）
                                if (_hasApproachPoint)
                                    _ai.destination = _lastApproachPoint;
                                _ai.SearchPath();
                                _stuckProgressTimer = 0.8f;
                                _lastProgressDist = float.MaxValue;
                            }
                        }

                        if (!_ai.pathPending && _ai.reachedDestination)
                        {
                            if (_isDetouring && _detourPhase == DetourPhase.ToTransit)
                            {
                                AIDebugLogger.Log(chefName,
                                    $"Detour: reached spawn transit, resuming to ({_detourOriginalDest.x:F1},{_detourOriginalDest.z:F1})");
                                _ai.destination = _detourOriginalDest;
                                _ai.SearchPath();
                                _ai.isStopped = false;
                                _detourPhase = DetourPhase.None;
                                _isDetouring = false;
                                _moveTimer = 0f;
                                _lastProgressDist = float.MaxValue;
                                _stuckProgressTimer = 0f;
                                debugState = $"detour → final ({_detourOriginalDest.x:F0},{_detourOriginalDest.z:F0})";
                                break;
                            }

                            _ai.isStopped = true;
                            _moveTimer = 0;
                            _lastProgressDist = float.MaxValue;
                            _stuckProgressTimer = 0f;

                            if (_isYielding)
                            {
                                // Reached sidestep position — pause to let other agent pass
                                _isYielding = false;
                                AIDebugLogger.Log(chefName, $"Yield: reached sidestep, pausing before resuming to original dest");
                                _substate = "paused";
                                _stateTimer = 0;
                                _pauseCallback = "resumeAfterYield";
                                break;
                            }

                            if (_isWandering)
                            {
                                _isWandering = false;
                                _substate = "idle";
                                _wanderTimer = Random.Range(0.2f, _wanderInterval * 0.6f);
                            }
                            else
                            {
                                // Sequence: arrive → freeze AI → smooth rotate → wait → operate
                                // Freeze ALL AI movement/rotation so only FaceTarget() controls rotation.
                                _ai.isStopped = true;
                                if (_aiPath != null) _aiPath.enableRotation = false;
                                var rvoLock = GetComponent<Pathfinding.RVO.RVOController>();
                                if (rvoLock != null) rvoLock.locked = true;
                                _facingComplete = false;
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
                                PerformInteract(_targetCounter);

                                if (_heldItem != null)
                                {
                                    // PROCESS output is staged on a clear counter. The explicit
                                    // ADD_TO_PLATE task is the only path allowed to modify a plate.
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
                        // 手里有货、等空柜：去新目标柜，不要直接 interacting（会再误触邻柜）
                        if (_heldItem != null && _execPhase == ExecPhase.GotoDest)
                        {
                            if (_targetCounter == null || !CanPlaceHeldItemOn(_targetCounter))
                            {
                                var free = FindNearestFreeCounter(transform.position);
                                if (free != null) _targetCounter = free;
                            }
                            if (_targetCounter != null)
                            {
                                debugState = $"wait → retarget {_targetCounter.name}";
                                if (!MoveTo(_targetCounter.transform.position)) { AbandonTask(); return; }
                                break;
                            }
                        }

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
                    {
                        FaceTarget(); // smooth rotation toward target

                        // Check if we're facing the target closely enough
                        float angleToTarget = GetAngleToTarget();
                        const float faceThreshold = 5f;  // degrees — consider "facing" when within 5°
                        const float minPostFaceWait = 0.12f; // extra wait after rotation completes
                        const float maxPauseTimeout = 3f;    // safety net

                        if (!_facingComplete && angleToTarget < faceThreshold)
                        {
                            _facingComplete = true;
                            _stateTimer = 0f; // Reset timer: start post-face wait phase
                        }

                        if (!_facingComplete)
                            debugState = $"rotating → target ({angleToTarget:F0}°)";
                        else
                            debugState = $"facing done, wait {_stateTimer:F2}s";

                        if (_pauseCallback == "arrived")
                        {
                            bool canProceed = _facingComplete && _stateTimer > minPostFaceWait;
                            bool timedOut = _stateTimer > maxPauseTimeout;
                            if (canProceed || timedOut)
                            {
                                OnArrivedAtTarget();
                            }
                        }
                        else if (_pauseCallback == "resumeAfterYield")
                        {
                            // After sidestep yield: wait for oncoming agent to pass
                            debugState = $"yield wait {_stateTimer:F1}s";
                            if (_stateTimer > 0.8f)
                            {
                                // Resume toward original destination
                                AIDebugLogger.Log(chefName, "Yield: resuming toward original destination");
                                _ai.destination = _yieldOriginalDest;
                                _ai.SearchPath();
                                _ai.isStopped = false;
                                if (_aiPath != null) _aiPath.enableRotation = true;
                                _substate = "moving";
                                _moveTimer = 0;
                                _lastProgressDist = float.MaxValue;
                                _stuckProgressTimer = 0f;
                            }
                        }
                        else // "idle" after task completion (CleanupTask path)
                        {
                            if (_facingComplete && _stateTimer > 0.05f)
                            {
                                if (_aiPath != null) _aiPath.enableRotation = true;
                                _substate = "idle";
                                _wanderTimer = _wanderInterval;
                                debugState = "idle";
                            }
                        }
                    }
                    break;

                case "postInteract":
                    // Brief pause after interaction (pickup/drop) before moving to next target.
                    // No FaceTarget here — rotation toward next dest happens naturally in MoveTo.
                    debugState = $"post-interact wait {_stateTimer:F2}s";
                    if (_stateTimer > 0.15f)
                    {
                        if (!MoveTo(_pendingMovePos)) { AbandonTask(); return; };
                    }
                    break;
            }
        }

        private void OnArrivedAtTarget()
        {
            // AI rotation stays frozen during interaction — only re-enabled
            // later in MoveTo() when the agent actually needs to move again.
            // This ensures: face target → interact → then turn to next target.

            switch (_execPhase)
            {
                case ExecPhase.GotoItem:
                    // Arrived at item position — pick it up.
                    // If the original item reference became stale (consumed by another agent),
                    // fall back to finding any available item of the required type.
                    if (_carryTargetItem == null && _currentTask?.itemType != 0)
                    {
                        AIDebugLogger.Log(chefName, $"GotoItem: original item gone, searching for {_currentTask.itemType}");
                        _carryTargetItem = FindItemAnywhere(
                            _currentTask.itemType,
                            _currentTask.orderId);
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
                        PerformInteract(counterHolding);
                        _carryTargetItem = null;
                    }
                    else
                    {
                        // Item is free on ground — use direct pickup
                        PickupItem();
                    }

                    if (_targetCounter != null && _heldItem != null)
                    {
                        // Successfully got the item — brief pause, then move to dest
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
                        // Pause briefly after pickup — rotation toward next dest
                        // happens naturally when MoveTo re-enables AIPath rotation.
                        _pendingMovePos = _targetCounter.transform.position;
                        _substate = "postInteract";
                        _stateTimer = 0;
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
                // Recast 最近点很少刚好落在候选点上；0.01 过严会导致永远走 fallback（柜子中心/障碍内）。
                const float onMeshTolerance = 0.55f;
                foreach (var offset in offsets)
                {
                    var candidate = originalTarget + offset;
                    var nearest = AstarPath.active.GetNearest(candidate);
                    if (nearest.node != null && Vector3.Distance(nearest.position, candidate) < onMeshTolerance)
                    {
                        // 用实际可走点，而不是理想偏移点
                        float dist = Vector3.Distance(nearest.position, originalTarget);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestPoint = nearest.position;
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
            if (_aiPath != null) _aiPath.enableRotation = true;
            _isYielding = false;
            _isDetouring = false;
            _detourPhase = DetourPhase.None;
            _moveTarget = bestPoint;
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
            if (_idleWanderPoints.Count == 0)
            {
                if (_aiManager != null)
                    BuildIdleWanderPoints(_aiManager.GetSpawnPositions());
                else
                    BuildIdleWanderPoints(null);
            }
            if (_idleWanderPoints.Count == 0)
                return;

            int index = _lastWanderPointIndex;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                index = Random.Range(0, _idleWanderPoints.Count);
                if (index != _lastWanderPointIndex || _idleWanderPoints.Count == 1)
                    break;
            }
            _lastWanderPointIndex = index;
            Vector3 target = _idleWanderPoints[index];

            if (!MoveToWander(target))
            {
                _isWandering = false;
                _substate = "idle";
                _wanderTimer = _wanderInterval * 0.5f;
                return;
            }
            _isWandering = true;
        }

        private bool MoveToWander(Vector3 navmeshPoint)
        {
            if (_ai == null) return false;

            _ai.destination = navmeshPoint;
            _ai.SearchPath();
            _ai.isStopped = false;
            if (_aiPath != null) _aiPath.enableRotation = true;
            _isYielding = false;
            _isDetouring = false;
            _detourPhase = DetourPhase.None;
            _moveTarget = navmeshPoint;
            _substate = "moving";
            _moveTimer = 0;
            _lastProgressDist = float.MaxValue;
            _stuckProgressTimer = 0f;
            _hasApproachPoint = false;
            debugState = $"wander → ({navmeshPoint.x:F1}, {navmeshPoint.z:F1})";
            return true;
        }

        /// <summary>
        /// Pick a navmesh point rotated around the spawn position as a transit waypoint,
        /// move there first, then resume the original destination.
        /// </summary>
        private bool TrySpawnDetour()
        {
            if (_ai == null || AstarPath.active == null) return false;

            Vector3 center = _spawnPosition;
            if (center == Vector3.zero)
                center = transform.position;
            center.y = transform.position.y;

            _detourOriginalDest = _moveTarget;
            if (_detourOriginalDest == Vector3.zero)
                _detourOriginalDest = _ai.destination;

            _detourAttempt++;
            float agentOffset = agentId >= 0 ? agentId * _detourAngleStep : 0f;
            float baseAngle = (agentOffset + _detourAttempt * _detourAngleStep) % 360f;

            Vector3 myPos = transform.position;
            myPos.y = center.y;
            Vector3 bestTransit = Vector3.zero;
            float bestScore = float.MinValue;

            for (int i = 0; i < 6; i++)
            {
                float angleDeg = baseAngle + i * _detourAngleStep;
                float angleRad = angleDeg * Mathf.Deg2Rad;
                Vector3 candidate = center + new Vector3(Mathf.Cos(angleRad), 0f, Mathf.Sin(angleRad)) * _detourRadius;

                var nearest = AstarPath.active.GetNearest(candidate);
                if (nearest.node == null || Vector3.Distance(nearest.position, candidate) > 0.5f)
                    continue;

                Vector3 transit = nearest.position;
                transit.y = myPos.y;
                float distFromMe = Vector3.Distance(transit, myPos);
                if (distFromMe < 0.8f)
                    continue;

                // Prefer waypoints that spread agents apart and break the direct line to target
                Vector3 toTarget = _detourOriginalDest - myPos;
                toTarget.y = 0f;
                Vector3 toTransit = transit - myPos;
                toTransit.y = 0f;
                float alignment = toTarget.sqrMagnitude > 0.01f && toTransit.sqrMagnitude > 0.01f
                    ? Vector3.Dot(toTarget.normalized, toTransit.normalized)
                    : 0f;
                float score = distFromMe - alignment * 2f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTransit = transit;
                }
            }

            if (bestTransit == Vector3.zero)
                return false;

            _detourPhase = DetourPhase.ToTransit;
            _isDetouring = true;
            _ai.destination = bestTransit;
            _ai.SearchPath();
            _ai.isStopped = false;
            _stuckProgressTimer = 0f;
            _lastProgressDist = float.MaxValue;
            AIDebugLogger.Log(chefName,
                $"Detour: heading to spawn transit ({bestTransit.x:F1},{bestTransit.z:F1}), then original target");
            debugState = $"detour → transit ({bestTransit.x:F0},{bestTransit.z:F0})";
            return true;
        }

        /// <summary>
        /// When stuck in a head-on deadlock, try to sidestep perpendicular to the
        /// movement direction to let the oncoming agent pass.
        /// </summary>
        private void TrySidestepYield()
        {
            if (_ai == null || AstarPath.active == null) return;

            Vector3 myPos = transform.position;
            Vector3 moveDir = (_ai.destination - myPos).normalized;
            if (moveDir.magnitude < 0.1f) moveDir = transform.forward;

            // Try left and right perpendicular directions (1.5 units offset)
            Vector3[] sideDirs = new[]
            {
                new Vector3(-moveDir.z, 0, moveDir.x),  // left
                new Vector3(moveDir.z, 0, -moveDir.x),  // right
            };

            foreach (var sideDir in sideDirs)
            {
                for (float dist = 1.2f; dist <= 2.0f; dist += 0.4f)
                {
                    Vector3 candidate = myPos + sideDir * dist;
                    var nearest = AstarPath.active.GetNearest(candidate);
                    if (nearest.node != null && Vector3.Distance(nearest.position, candidate) < 0.3f)
                    {
                        _yieldOriginalDest = _ai.destination;
                        _isYielding = true;
                        _ai.destination = nearest.position;
                        _ai.SearchPath();
                        _stuckProgressTimer = 0f;
                        _lastProgressDist = float.MaxValue;
                        AIDebugLogger.Log(chefName, $"Yield: sidestep to ({nearest.position.x:F1},{nearest.position.z:F1})");
                        return;
                    }
                }
            }

            // No valid sidestep — repath to last known approach point (avoid off-mesh jitter)
            if (_hasApproachPoint)
                _ai.destination = _lastApproachPoint;
            _ai.SearchPath();
            _stuckProgressTimer = 0f;
            _lastProgressDist = float.MaxValue;
            AIDebugLogger.Log(chefName, "Yield: no sidestep available, large jitter");
        }

        #endregion

        #region Task Execution

        /// <summary>
        /// Called by KitchenAIManager when a task is assigned to this chef.
        /// </summary>
        public void AssignTask(KitchenTask task)
        {
            _isWandering = false; // Cancel any wandering
            _isYielding = false;  // Cancel any yield
            _isDetouring = false;
            _detourPhase = DetourPhase.None;
            _detourAttempt = 0;
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
            if (_heldItem != null
                && task.outputType != 0
                && _heldItem.objEnum == task.outputType
                && _heldItem.BoundOrderId == task.orderId)
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
        /// Finds a temporary clear counter for an item produced by FETCH or PROCESS.
        /// Processing and plating are separate, explicit tasks; this method must never
        /// shortcut an item into a facility or onto a plate.
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

            var clear = FindObjectsOfType<ClearCounter>()
                .FirstOrDefault(c => IsUsableClearCounter(c)
                    && !c.HasKitchenObj()
                    && (reservedCounters == null || !reservedCounters.Contains(c)));
            if (clear != null)
            {
                AIDebugLogger.Log(chefName, $"FindDropTarget({ingredient}) → ClearCounter {clear.name}");
                return clear;
            }
            AIDebugLogger.LogWarning(chefName, $"FindDropTarget({ingredient}) → fallback nearest free");
            return FindNearestFreeCounter(transform.position, reservedCounters);
        }

        private BaseCounter FindNearestFreeCounter(
            Vector3 near,
            HashSet<BaseCounter> reserved = null,
            BaseCounter exclude = null)
        {
            var counters = FindObjectsOfType<BaseCounter>();
            BaseCounter best = null;
            float bestDist = float.MaxValue;
            foreach (var c in counters)
            {
                if (!IsUsableClearCounter(c)) continue;
                if (exclude != null && c == exclude) continue;
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

        private Vector3 GetGroundDropPosition(Vector3 fallback)
        {
            var bb = _aiManager?.Blackboard;
            if (bb != null && bb.TryGetNearestGroundDropPosition(transform.position, out var position))
                return position + Vector3.up * 0.35f;
            return fallback;
        }

        /// <summary>真正可摆放的空台（排除墙占位）。</summary>
        private static bool IsUsableClearCounter(BaseCounter c)
        {
            if (c == null || !(c is ClearCounter)) return false;
            if (c.name != null && c.name.StartsWith("Wall_")) return false;
            return true;
        }

        /// <summary>
        /// 手里物品能否放到该柜：空柜，或柜上/手里有盘子可叠放。
        /// </summary>
        private bool CanPlaceHeldItemOn(BaseCounter counter)
        {
            if (_heldItem == null || counter == null) return false;
            if (!counter.HasKitchenObj()) return true;
            if (counter.GetKitchenObj() is Plate) return true;
            if (_heldItem is Plate) return true;
            return false;
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
                    PerformInteract(freeCounter);
                }
                else
                {
                    // Drop on ground
                    KitchenObjFactory.Instance.DropObjServerRpc(
                        _heldItem.NetworkObject, GetGroundDropPosition(
                            transform.position + transform.forward * 0.5f),
                        Vector3.down, 0f, default);
                    ClearKitchenObj();
                }
            }

            Vector3 approachPos = GetApproachPosition(counter);

            if (_heldItem != null
                && task.itemType != 0
                && _heldItem.objEnum == task.itemType
                && _heldItem.BoundOrderId == task.orderId)
            {
                // Holding the right input — go drop it and start processing
                AIDebugLogger.Log(chefName, $"ExecuteProcess: holding {_heldItem.objEnum}, → GotoFacility {counter.name}");
                _execPhase = ExecPhase.GotoFacility;
                if (!MoveTo(counter.transform.position)) { AbandonTask(); return; }
            }
            else if (counter.HasKitchenObj())
            {
                var onCounter = counter.GetKitchenObj();
                bool ready = onCounter.objEnum == task.itemType
                             || onCounter.objEnum == task.outputType
                             || (onCounter is Plate readyPlate
                                 && (readyPlate.GetIngredients().Contains(task.itemType)
                                     || readyPlate.GetIngredients().Contains(task.outputType)));
                if (ready)
                {
                    AIDebugLogger.Log(chefName, $"ExecuteProcess: counter {counter.name} has {onCounter.objEnum}, → wait/interact");
                    _execPhase = ExecPhase.None;
                    if (!MoveTo(counter.transform.position)) { AbandonTask(); return; }
                    return;
                }
            }

            // Holding a plate that already has the input ingredient (assembled dish → oven/blender)
            if (_heldItem is Plate heldPlate && heldPlate.GetIngredients().Contains(task.itemType))
            {
                AIDebugLogger.Log(chefName, $"ExecuteProcess: holding plate with {task.itemType} → {counter.name}");
                _execPhase = ExecPhase.GotoFacility;
                if (!MoveTo(counter.transform.position)) { AbandonTask(); return; }
                return;
            }

            // Counter empty / wrong — find KitchenObj or plate with input
            KitchenObj foundItem = FindItemAnywhere(task.itemType, task.orderId)
                ?? FindPlateHoldingIngredient(task.itemType, task.orderId);
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
                ingredient = FindItemAnywhere(task.itemType, task.orderId);
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
                if (task.targetItem is Plate targetPlate
                    && targetPlate.BoundOrderId != 0
                    && targetPlate.BoundOrderId != task.orderId)
                {
                    AIDebugLogger.LogWarning(
                        chefName,
                        $"SERVE rejected plate bound to order {targetPlate.BoundOrderId}, task order {task.orderId}");
                    AbandonTask();
                    return;
                }
                _carryTargetItem = task.targetItem;
                _carryDestPos = GetApproachPosition(counter);
                _execPhase = ExecPhase.GotoItem;
                // A plate is normally sitting on a counter. Move to the
                // counter's interaction point instead of the plate mesh pivot,
                // which can be inside the counter and unreachable by NavMesh.
                var holdingCounter = FindCounterHolding(task.targetItem);
                var pickupPos = holdingCounter != null
                    ? GetApproachPosition(holdingCounter)
                    : task.targetItem.transform.position;
                if (!MoveTo(pickupPos)) { AbandonTask(); return; };
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
                    var holdingCounter = FindCounterHolding(bestPlate);
                    var pickupPos = holdingCounter != null
                        ? GetApproachPosition(holdingCounter)
                        : bestPlate.transform.position;
                    if (!MoveTo(pickupPos)) { AbandonTask(); return; };
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

        private void PerformInteract(BaseCounter counter)
        {
            if (counter == null) return;
            if (_currentTask != null)
            {
                _currentTask.objectBId = counter.NetworkObject != null
                    ? counter.NetworkObject.NetworkObjectId
                    : 0UL;
                if (_heldItem != null)
                    _currentTask.objectAId = _heldItem.RuntimeObjectId;
            }
            counter.Interact(this);
            if (_currentTask != null && _heldItem != null)
                _currentTask.objectAId = _heldItem.RuntimeObjectId;
            OnInteractionPerformed?.Invoke();
        }

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
                    // Spawn requested type (container may not match new ingredients).
                    if (_heldItem == null && _currentTask != null && _currentTask.outputType != 0)
                    {
                        var container = _targetCounter as ContainerCounter;
                        if (container != null && container.objEnum == _currentTask.outputType)
                            KitchenObjOperator.SpawnKitchenObjForOrderRpc(
                                _currentTask.outputType,
                                this,
                                _currentTask.orderId);
                        else
                            KitchenObjOperator.SpawnKitchenObjForOrderRpc(
                                _currentTask.outputType,
                                this,
                                _currentTask.orderId);
                    }
                    else if (_heldItem == null)
                    {
                        PerformInteract(_targetCounter);
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
                                    GetGroundDropPosition(
                                        transform.position + transform.forward * 0.5f),
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
                    {
                        if (_targetCounter is PlatesCounter platesCounter
                            && platesCounter.plateCount > 0
                            && _currentTask?.orderId != 0)
                        {
                            KitchenObjOperator.SpawnKitchenObjForOrderRpc(
                                KitchenObjEnum.Plate,
                                this,
                                _currentTask.orderId);
                            platesCounter.RemovePlateServerRpc();
                            OnInteractionPerformed?.Invoke();
                        }
                        else
                        {
                            PerformInteract(_targetCounter);
                        }
                    }
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
                        if (onCounter is Plate plate)
                        {
                            int orderId = _currentTask?.orderId ?? 0;
                            var blackboard = _aiManager?.Blackboard;
                            bool plateOwned = plate.BoundOrderId == orderId;
                            bool itemOwned = blackboard == null
                                || blackboard.TryClaimItemForOrder(_heldItem, orderId);

                            if (!plateOwned || !itemOwned)
                            {
                                AIDebugLogger.LogWarning(
                                    chefName,
                                    $"ADD_TO_PLATE rejected: plateOrder={plate.BoundOrderId}, taskOrder={orderId}, item={_heldItem.objEnum}");
                                AbandonTask();
                                break;
                            }

                            Debug.Log($"[{chefName}] Adding {_heldItem.objEnum} to plate #{orderId}");
                            if (KitchenObjOperator.PutToPlate(_heldItem, plate, orderId))
                                CompleteTask();
                            else
                                AbandonTask();
                        }
                        else
                        {
                            Debug.LogWarning($"[{chefName}] ADD_TO_PLATE: no Plate on {_targetCounter.name}");
                            AbandonTask();
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[{chefName}] ADD_TO_PLATE: missing held item or plate");
                        AbandonTask();
                    }
                    break;

                case TaskType.SERVE:
                    // Put plate on delivery counter
                    if (_heldItem != null)
                    {
                        PerformInteract(_targetCounter);
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
                        AIDebugLogger.LogWarning(chefName, "SERVE: plate was not held at delivery");
                        AbandonTask();
                    }
                    break;

                case TaskType.TRASH:
                    // Put waste item in trash
                    if (_heldItem != null && _targetCounter is TrashCounter)
                    {
                        AIDebugLogger.Log(chefName, $"Trashing {_heldItem.objEnum}");
                        PerformInteract(_targetCounter);
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

            bool PlateHas(KitchenObjEnum t) =>
                counterItem is Plate cp && cp.GetIngredients().Contains(t);
            bool HeldPlateHas(KitchenObjEnum t) =>
                _heldItem is Plate hp && hp.GetIngredients().Contains(t);

            // === CASE 1: Holding input (or plate with input), counter empty → place ===
            if (_heldItem != null && _currentTask.itemType != 0 && !hasItem
                && (_heldItem.objEnum == _currentTask.itemType || HeldPlateHas(_currentTask.itemType)))
            {
                AIDebugLogger.LogState(chefName, "placing", _heldItem.objEnum.ToString(), $"→ {counter.name}");
                PerformInteract(counter);
                _substate = "waiting";
                _waitTimer = 0;
                return;
            }

            // === CASE 2: Counter has OUTPUT (KitchenObj or plate ingredient) → take it ===
            if (hasItem && (_currentTask.outputType != 0)
                && (counterItem.objEnum == _currentTask.outputType || PlateHas(_currentTask.outputType)))
            {
                if (_heldItem != null && _heldItem.objEnum != _currentTask.outputType
                    && !(_heldItem is Plate))
                {
                    AIDebugLogger.Log(chefName, $"HandleProcess: dropping held {_heldItem.objEnum} to take ready output");
                    var dropSpot = FindNearestFreeCounter(transform.position);
                    if (dropSpot != null) PerformInteract(dropSpot);
                    else { ClearKitchenObj(); }
                }
                AIDebugLogger.LogState(chefName, "taking output", _currentTask.outputType.ToString(), $"from {counter.name}");
                PerformInteract(counter);
                if (_heldItem != null && _currentTask?.orderId != 0)
                {
                    if (_heldItem is Plate outPlate)
                        _aiManager?.Blackboard?.AssignPlateToOrder(_currentTask.orderId, outPlate);
                    else
                        _aiManager?.Blackboard?.TagItemForOrder(_heldItem, _currentTask.orderId);
                }
                CompleteTask();
                return;
            }

            // === CASE 3: Counter has INPUT → start processing ===
            if (_heldItem == null && hasItem
                && (counterItem.objEnum == _currentTask.itemType || PlateHas(_currentTask.itemType)))
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

                    if (counter.HasKitchenObj() &&
                        counter.GetKitchenObj().objEnum == _currentTask.outputType)
                    {
                        AIDebugLogger.Log(chefName, $"Output {_currentTask.outputType} already ready, taking it now");
                        PerformInteract(counter);
                        CompleteTask();
                        return;
                    }

                    sc.OnCookingStageChange += OnStoveStageChanged;
                    _substate = "waiting";
                    _waitTimer = 0;
                }
                else if (counter is TimedFacilityCounter tfc)
                {
                    AIDebugLogger.LogState(chefName, "wait oven/blender", _currentTask.itemType.ToString(),
                        $"→ {_currentTask.outputType} on {counter.name}");
                    tfc.OnCookingStageChange += OnStoveStageChanged;
                    _substate = "waiting";
                    _waitTimer = 0;
                }
                return;
            }

            // === CASE 4: Counter has an unrelated item (not input, not output) ===
            if (hasItem && counterItem != null &&
                counterItem.objEnum != _currentTask.itemType &&
                counterItem.objEnum != _currentTask.outputType &&
                !PlateHas(_currentTask.itemType) && !PlateHas(_currentTask.outputType))
            {
                bool isBurned = PlateAssemblyMatcher.IsBurnedWaste(counterItem.objEnum);

                if (isBurned)
                {
                    // Burned items block stoves and are waste — clearing is justified.
                    AIDebugLogger.LogWarning(chefName, $"HandleProcess: clearing burned {counterItem.objEnum} from {counter.name}");

                    // Drop held item first if carrying it
                    if (_heldItem != null)
                    {
                        var tempDrop = FindNearestFreeCounter(transform.position);
                        if (tempDrop != null) PerformInteract(tempDrop);
                        else
                        {
                            KitchenObjFactory.Instance.DropObjServerRpc(
                                _heldItem.NetworkObject, GetGroundDropPosition(
                                    transform.position + transform.forward * 0.5f),
                                Vector3.down, 0f, default);
                            ClearKitchenObj();
                        }
                    }

                    // Take the burned item off
                    PerformInteract(counter);

                    // Drop burned item on nearest free counter or ground
                    if (_heldItem != null)
                    {
                        var freeDrop = FindNearestFreeCounter(transform.position);
                        if (freeDrop != null) PerformInteract(freeDrop);
                        else
                        {
                            KitchenObjFactory.Instance.DropObjServerRpc(
                                _heldItem.NetworkObject, GetGroundDropPosition(
                                    transform.position + transform.forward * 0.5f),
                                Vector3.down, 0f, default);
                            ClearKitchenObj();
                        }
                    }

                    // Counter is now free — re-fetch input if we had one set aside
                    // (the heldBeforeClear tracking is no longer needed since we just
                    // drop everything and let the scheduler reassign)
                    AIDebugLogger.Log(chefName, $"Burned item cleared from {counter.name}, abandoning to let scheduler reassign");
                    AbandonTask();
                    return;
                }
                else
                {
                    // Facility is occupied by someone else's valid ingredient.
                    // Do NOT clear it — that's unnatural instant teleporting.
                    // Drop our held item nearby and abandon gracefully.
                    AIDebugLogger.LogWarning(chefName, $"HandleProcess: {counter.name} occupied by {counterItem.objEnum}, not clearing — abandoning");

                    if (_heldItem != null)
                    {
                        var dropSpot = FindNearestFreeCounter(transform.position);
                        if (dropSpot != null) PerformInteract(dropSpot);
                        else
                        {
                            KitchenObjFactory.Instance.DropObjServerRpc(
                                _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                                Vector3.down, 0f, default);
                            ClearKitchenObj();
                        }
                    }
                    AbandonTask();
                    return;
                }
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
                if (freeCounter != null) PerformInteract(freeCounter);
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
                OnInteractionPerformed?.Invoke();
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
            if (_currentTask != null)
                _currentTask.objectAId = _heldItem.RuntimeObjectId;
            _carryTargetItem = null;
        }

        private bool TryPutHeldItemOnOrderPlate(Plate plate)
        {
            if (plate == null || _heldItem == null || _currentTask == null)
                return false;

            int orderId = _currentTask.orderId;
            if (orderId == 0 || plate.BoundOrderId != orderId)
                return false;

            var blackboard = _aiManager?.Blackboard;
            if (blackboard != null
                && !blackboard.TryClaimItemForOrder(_heldItem, orderId))
                return false;

            return KitchenObjOperator.PutToPlate(_heldItem, plate, orderId);
        }

        private void DropItemAtFacility()
        {
            if (_heldItem != null && _targetCounter != null)
            {
                if (CanPlaceHeldItemOn(_targetCounter))
                {
                    if (_targetCounter.GetKitchenObj() is Plate plate
                        && _heldItem is not Plate
                        && _currentTask?.orderId != 0)
                    {
                        if (!TryPutHeldItemOnOrderPlate(plate))
                        {
                            AbandonTask();
                            return;
                        }
                    }
                    else
                    {
                        PerformInteract(_targetCounter);
                    }
                    AIDebugLogger.Log(chefName, $"DropItemAtFacility: → {_targetCounter.name}, held={_heldItem?.objEnum.ToString() ?? "null"}");
                    Debug.Log($"[{chefName}] Dropped item at {_targetCounter.name}");
                }
                else
                {
                    // 加工台被占：改投其他同型空闲台，或暂存到空台
                    var alt = FindDropTarget(_heldItem.objEnum);
                    if (alt != null && alt != _targetCounter)
                    {
                        AIDebugLogger.Log(chefName,
                            $"DropItemAtFacility: {_targetCounter.name} busy, retarget → {alt.name}");
                        _targetCounter = alt;
                        _execPhase = ExecPhase.GotoFacility;
                        if (!MoveTo(alt.transform.position)) { AbandonTask(); return; }
                        return;
                    }

                    var clear = FindNearestFreeCounter(transform.position);
                    if (clear != null)
                    {
                        AIDebugLogger.Log(chefName,
                            $"DropItemAtFacility: facility busy, park on {clear.name}");
                        _targetCounter = clear;
                        _execPhase = ExecPhase.GotoDest;
                        if (!MoveTo(clear.transform.position)) { AbandonTask(); return; }
                        return;
                    }

                    _substate = "waiting";
                    _waitTimer = 0;
                    return;
                }
            }

            // Now start the actual work
            _substate = "interacting";
            _stateTimer = 0;
        }

        private void DropItemAtDestination()
        {
            if (_heldItem == null || _targetCounter == null)
            {
                _execPhase = ExecPhase.None;
                CompleteTask();
                return;
            }

            // 目标可放（空柜 / 可叠盘）才交互
            if (CanPlaceHeldItemOn(_targetCounter))
            {
                var heldBefore = _heldItem;
                if (_targetCounter.GetKitchenObj() is Plate plate
                    && _heldItem is not Plate
                    && _currentTask?.orderId != 0)
                {
                    if (!TryPutHeldItemOnOrderPlate(plate))
                    {
                        AbandonTask();
                        return;
                    }
                }
                else
                {
                    PerformInteract(_targetCounter);
                }
                // 放成功：手里空了；或手里仍是盘子（从柜上叠了食材）
                if (_heldItem == null || (heldBefore is Plate && _heldItem is Plate))
                {
                    Debug.Log($"[{chefName}] Dropped/stacked at destination {_targetCounter.name}");
                    _execPhase = ExecPhase.None;
                    CompleteTask();
                    return;
                }

                // 理论上可放却仍拿着 — 短暂等待后重试同一柜，不要误排除
                AIDebugLogger.LogWarning(chefName,
                    $"DropItemAtDestination: place on {_targetCounter.name} failed, waiting retry");
                _substate = "waiting";
                _waitTimer = 0;
                return;
            }

            // 目标已被占用且无法叠放：走到另一张空闲空台，禁止就地丢到邻柜
            var alt = FindNearestFreeCounter(transform.position, exclude: _targetCounter);
            if (alt != null)
            {
                AIDebugLogger.Log(chefName,
                    $"DropItemAtDestination: {_targetCounter.name} occupied, moving to free {alt.name}");
                Debug.Log($"[{chefName}] Target {_targetCounter.name} busy → move to {alt.name}");
                _targetCounter = alt;
                _execPhase = ExecPhase.GotoDest;
                if (!MoveTo(alt.transform.position)) { AbandonTask(); return; }
                return;
            }

            AIDebugLogger.LogWarning(chefName, "DropItemAtDestination: no free counter, waiting");
            _substate = "waiting";
            _waitTimer = 0;
        }

        #endregion

        #region Task Completion

        private void CleanupTask()
        {
            // Unsubscribe from stove / oven / blender events
            if (_targetCounter is StoveCounter sc)
                sc.OnCookingStageChange -= OnStoveStageChanged;
            if (_targetCounter is TimedFacilityCounter tfc)
                tfc.OnCookingStageChange -= OnStoveStageChanged;

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
                    // 只允许在「本来要去的那张柜」上就近放下；邻柜即使在 interactionRange 内也必须走过去
                    bool isIntendedTarget = dropCounter == _targetCounter;
                    if (isIntendedTarget && dist < interactionRange && CanPlaceHeldItemOn(dropCounter))
                    {
                        AIDebugLogger.Log(chefName, $"CleanupTask: dropping {_heldItem.objEnum} at {dropCounter.name}");
                        PerformInteract(dropCounter);
                    }
                    else
                    {
                        // Walk to the free counter first (never dump onto a neighbor in-range).
                        if (_currentTask != null) _currentTask.status = "executing";
                        AIDebugLogger.Log(chefName, $"CleanupTask: moving to {dropCounter.name} to drop {_heldItem.objEnum} (dist={dist:F1})");
                        _targetCounter = dropCounter;
                        _execPhase = ExecPhase.GotoDest;
                        if (!MoveTo(dropCounter.transform.position))
                        {
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

            if (_targetCounter is StoveCounter sc)
                sc.OnCookingStageChange -= OnStoveStageChanged;
            if (_targetCounter is TimedFacilityCounter tfc)
                tfc.OnCookingStageChange -= OnStoveStageChanged;

            // Drop held item so scheduler can find it — must walk to free counter, no neighbor dump
            if (_heldItem != null)
            {
                var dropCounter = FindNearestFreeCounter(transform.position);
                if (dropCounter != null)
                {
                    _currentTask = null;
                    _targetCounter = dropCounter;
                    _execPhase = ExecPhase.GotoDest;
                    if (MoveTo(dropCounter.transform.position))
                    {
                        debugState = $"force-abandon drop → {dropCounter.name}";
                        return;
                    }
                }

                KitchenObjFactory.Instance.DropObjServerRpc(
                    _heldItem.NetworkObject, transform.position + transform.forward * 0.5f,
                    Vector3.down, 0f, default);
                ClearKitchenObj();
            }

            _currentTask = null;
            _substate = "idle";
            _execPhase = ExecPhase.None;
            _targetCounter = null;
            _carryTargetItem = null;
            _isDetouring = false;
            _detourPhase = DetourPhase.None;
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
            // 等空柜摆放：目标可放，或出现别的空闲空台
            if (_heldItem != null && _execPhase == ExecPhase.GotoDest)
            {
                if (_targetCounter != null && CanPlaceHeldItemOn(_targetCounter))
                    return true;
                if (FindNearestFreeCounter(transform.position) != null)
                    return true;
            }

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
                    // Check if processing is done (output KitchenObj or plate ingredient)
                    if (_targetCounter.HasKitchenObj())
                    {
                        var objOnCounter = _targetCounter.GetKitchenObj();
                        debugState = $"wait-chk {objOnCounter.objEnum} vs {_currentTask.outputType}";
                        if (_currentTask.outputType != 0 &&
                            (objOnCounter.objEnum == _currentTask.outputType
                             || (objOnCounter is Plate outPlate
                                 && outPlate.GetIngredients().Contains(_currentTask.outputType))))
                        {
                            Debug.Log($"[{chefName}] PROCESS output ready: {_currentTask.outputType} on {_targetCounter.name}");
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

            bool stageReady = currentStage.Value == _currentTask.outputType;
            bool plateReady = _targetCounter.HasKitchenObj()
                              && _targetCounter.GetKitchenObj() is Plate readyPlate
                              && readyPlate.GetIngredients().Contains(_currentTask.outputType);
            if ((stageReady || plateReady) && _targetCounter.HasKitchenObj())
            {
                Debug.Log($"[{chefName}] Process output ready: {currentStage.Value}, grabbing!");
                AIDebugLogger.LogState(chefName, "process grab", currentStage.Value.ToString(),
                    "output ready");
                if (_targetCounter is StoveCounter sc)
                    sc.OnCookingStageChange -= OnStoveStageChanged;
                if (_targetCounter is TimedFacilityCounter tfc)
                    tfc.OnCookingStageChange -= OnStoveStageChanged;
                PerformInteract(_targetCounter);

                // Deliver cooked output to staging. Plating is handled exclusively
                // by the order's ADD_TO_PLATE task.
                if (_heldItem != null)
                {
                    var dropTarget = FindNearestFreeCounter(transform.position);
                    if (dropTarget != null)
                    {
                        AIDebugLogger.Log(chefName, $"Delivering cooked {_heldItem.objEnum} to {dropTarget.name}");
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
        private KitchenObj FindItemAnywhere(KitchenObjEnum itemType, int orderId)
        {
            KitchenObj bestItem = null;
            float bestDist = float.MaxValue;

            // Check ground items (free-standing)
            foreach (var item in FindObjectsOfType<KitchenObj>())
            {
                if (item == null || item.objEnum != itemType) continue;
                if (!item.IsFree) continue;
                if (_aiManager?.Blackboard != null
                    && !_aiManager.Blackboard.CanUseItemForOrder(item, orderId))
                    continue;
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
                if (_aiManager?.Blackboard != null
                    && !_aiManager.Blackboard.CanUseItemForOrder(item, orderId))
                    continue;
                // Skip items on active processors
                if (counter is StoveCounter || counter is CuttingCounter
                    || counter is OvenCounter || counter is BlenderCounter) continue;
                // Skip items on PlateCounter, DeliveryCounter, TrashCounter
                if (counter is PlatesCounter || counter is DeliveryCounter) continue;
                float d = Vector3.Distance(transform.position, counter.transform.position);
                if (d < bestDist) { bestDist = d; bestItem = item; }
            }

            return bestItem;
        }

        /// <summary>Find a plate whose ingredient set contains the given type.</summary>
        private KitchenObj FindPlateHoldingIngredient(KitchenObjEnum itemType, int orderId)
        {
            KitchenObj best = null;
            float bestDist = float.MaxValue;
            foreach (var plate in FindObjectsOfType<Plate>())
            {
                if (plate == null || !plate.GetIngredients().Contains(itemType)) continue;
                if (plate.BoundOrderId != 0 && plate.BoundOrderId != orderId) continue;
                if (!(plate.IsFree || plate.GetHolder() is BaseCounter)) continue;
                if (plate.GetHolder() is OvenCounter or BlenderCounter) continue;
                float d = Vector3.Distance(transform.position, plate.transform.position);
                if (d < bestDist) { bestDist = d; best = plate; }
            }
            return best;
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

        /// <summary>
        /// Get the position the agent should face right now.
        /// During GotoItem phase: face the item/counter being picked up.
        /// Otherwise: face _targetCounter (the destination).
        /// </summary>
        private Vector3 GetFaceTargetPos()
        {
            if (_execPhase == ExecPhase.GotoItem && _carryTargetItem != null)
            {
                var holder = FindCounterHolding(_carryTargetItem);
                if (holder != null) return holder.transform.position;
                return _carryTargetItem.transform.position;
            }
            if (_targetCounter != null) return _targetCounter.transform.position;
            return transform.position + transform.forward; // fallback
        }

        /// <summary>Immediately snap to face the target counter (called on arrival).</summary>
        private void SnapFaceTarget()
        {
            Vector3 targetPos = GetFaceTargetPos();
            Vector3 dir = targetPos - transform.position;
            dir.y = 0;
            if (dir.magnitude < 0.01f) return;
            transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        /// <summary>Smoothly rotate to face the target counter.</summary>
        private void FaceTarget()
        {
            Vector3 targetPos = GetFaceTargetPos();
            Vector3 dir = targetPos - transform.position;
            dir.y = 0;
            if (dir.magnitude < 0.01f) return;
            Quaternion targetRot = Quaternion.LookRotation(dir.normalized);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 15f * Time.deltaTime);
        }

        /// <summary>Angle between current forward and direction to target (degrees).</summary>
        private float GetAngleToTarget()
        {
            Vector3 targetPos = GetFaceTargetPos();
            Vector3 dir = targetPos - transform.position;
            dir.y = 0;
            if (dir.magnitude < 0.01f) return 0f;
            return Vector3.Angle(transform.forward, dir.normalized);
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

            // Draw spawn point and detour ring
            if (_spawnPosition != Vector3.zero)
            {
                Vector3 spawn = _spawnPosition;
                spawn.y = transform.position.y;
                Gizmos.color = new Color(chefColor.r, chefColor.g, chefColor.b, 0.5f);
                Gizmos.DrawWireSphere(spawn, 0.25f);
                Gizmos.DrawWireSphere(spawn, _detourRadius);
            }

            if (_isDetouring && _detourPhase == DetourPhase.ToTransit)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawLine(transform.position, _ai.destination);
                Gizmos.DrawWireSphere(_ai.destination, 0.35f);
            }

            // Draw pre-generated idle wander points
            if (_idleWanderPoints.Count > 0)
            {
                Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.7f);
                foreach (var pt in _idleWanderPoints)
                {
                    Gizmos.DrawWireSphere(pt, 0.2f);
                }
            }

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
                        onNavMesh = nearest.node != null && Vector3.Distance(nearest.position, candidate) < 0.55f;
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
