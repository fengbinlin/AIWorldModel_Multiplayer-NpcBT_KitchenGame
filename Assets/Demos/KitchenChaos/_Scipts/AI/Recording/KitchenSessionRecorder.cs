using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Records AI training data with a locked simulation clock.
    /// While recording, <see cref="Time.captureDeltaTime"/> is set to 1/captureFps so every
    /// Unity frame advances exactly one sim step (A→B takes N frames regardless of wall-clock FPS).
    /// One recording frame is captured per sim frame in LateUpdate (after AI movement).
    /// PNG encode + disk I/O stay on background threads.
    /// </summary>
    public class KitchenSessionRecorder : MonoBehaviour
    {
        public static KitchenSessionRecorder Instance { get; private set; }

        [Header("Recording")]
        [SerializeField] private bool _autoStartWhenPlaying = true;
        [SerializeField] private KeyCode _toggleKey = KeyCode.F9;
        [SerializeField] private float _captureFps = 60f;
        [SerializeField] private int _frameWidth = 256;
        [SerializeField] private int _frameHeight = 256;
        [SerializeField] private string _outputRootFolder = "KitchenTrainingRecordings";
        [Tooltip("Max background PNG writes queued. Blocks (freezes sim via waiting) until under limit.")]
        [SerializeField] private int _maxPendingWrites = 64;

        [Header("Cameras")]
        [SerializeField] private Camera _globalCamera;

        private readonly List<ChefRecordingAgent> _agents = new();
        private RenderTexture _globalRt;
        private string _sessionDir;
        private string _framesJsonlPath;
        private int _frameIndex;
        private bool _isRecording;
        private bool _manualStopRequested;
        private float _recordStartTime;
        private bool _captureClockActive;
        private float _savedFixedDeltaTime;
        private readonly object _jsonLock = new();
        private RecordingSessionManifest _manifest;
        /// <summary>
        /// Previous state frame held until the next capture computes action_i
        /// (state_i + action_i => state_(i+1)); action is written back here before flush.
        /// </summary>
        private RecordingFrameData _pendingFrame;
        private bool _hasPendingFrame;

        public bool IsRecording => _isRecording;
        public string SessionDirectory => _sessionDir;
        public int PendingAsyncWrites => RecordingCameraUtility.PendingAsyncWrites;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (_globalCamera == null)
                _globalCamera = Camera.main;

            _globalRt = RecordingCameraUtility.CreateRenderTexture(_frameWidth, _frameHeight);
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey))
            {
                if (_isRecording)
                    StopRecording(manual: true);
                else
                    StartRecording(manual: true);
            }
        }

        private void LateUpdate()
        {
            if (!_isRecording)
            {
                TryAutoStart();
                return;
            }

            if (!CanRecord())
            {
                StopRecording(manual: false);
                return;
            }

            // One Unity frame == one locked sim step == one recording sample.
            CaptureSimFrame();
        }

        private void OnDestroy()
        {
            if (_isRecording)
                StopRecording();
            else
                DisableCaptureClock();

            if (_globalRt != null)
                _globalRt.Release();
            if (Instance == this)
                Instance = null;
        }

        private void OnApplicationQuit()
        {
            DisableCaptureClock();
        }

        private bool CanRecord()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlaying())
                return false;
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
                return false;
            return true;
        }

        private void TryAutoStart()
        {
            if (!_autoStartWhenPlaying || _manualStopRequested) return;
            if (CanRecord() && _agents.Count == 0)
                BindChefs();
            if (CanRecord() && _agents.Count > 0)
                StartRecording(manual: false);
        }

        [ContextMenu("Start Recording")]
        public void StartRecording() => StartRecording(manual: true);

        public void StartRecording(bool manual)
        {
            if (_isRecording) return;
            if (manual)
                _manualStopRequested = false;

            BindChefs();
            if (_agents.Count == 0)
            {
                Debug.LogWarning("[KitchenSessionRecorder] No AI chefs found to record.");
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _sessionDir = Path.Combine(Application.dataPath, "..", _outputRootFolder, $"session_{stamp}");
            _sessionDir = Path.GetFullPath(_sessionDir);
            Directory.CreateDirectory(_sessionDir);
            Directory.CreateDirectory(Path.Combine(_sessionDir, "global"));
            foreach (var agent in _agents)
                Directory.CreateDirectory(Path.Combine(_sessionDir, $"agent_{agent.Chef.agentId}"));

            _framesJsonlPath = Path.Combine(_sessionDir, "frames.jsonl");
            _frameIndex = 0;
            _pendingFrame = null;
            _hasPendingFrame = false;
            EnableCaptureClock();
            _recordStartTime = Time.time;
            _isRecording = true;

            foreach (var agent in _agents)
            {
                agent.Initialize(_frameWidth, _frameHeight, _recordStartTime);
                agent.BeginFrame();
            }

            float simDt = GetSimDelta();
            _manifest = new RecordingSessionManifest
            {
                sessionId = $"session_{stamp}",
                gameName = KitchenCameraInfoUtility.DefaultGameName,
                unityTimeStart = _recordStartTime,
                frameWidth = _frameWidth,
                frameHeight = _frameHeight,
                captureFps = 1f / simDt,
                playerCount = _agents.Count,
                totalFrames = 0,
                task_description = KitchenCameraInfoUtility.DefaultTaskDescription,
            };
            WriteManifest();

            Debug.Log($"[KitchenSessionRecorder] Recording started (simDt={simDt:F4}s, " +
                      $"captureDeltaTime locked) → {_sessionDir}");
        }

        [ContextMenu("Stop Recording")]
        public void StopRecording() => StopRecording(manual: true);

        public void StopRecording(bool manual)
        {
            if (!_isRecording) return;
            if (manual)
                _manualStopRequested = true;
            _isRecording = false;
            DisableCaptureClock();

            // Last state has no outgoing action (no state_(i+1)); flush with zero action fields.
            FlushPendingFrame();
            FlushPendingWrites(timeoutSeconds: 10f);

            if (_manifest != null)
            {
                _manifest.totalFrames = _frameIndex;
                WriteManifest();
            }

            Debug.Log($"[KitchenSessionRecorder] Recording stopped. {_frameIndex} frames saved to {_sessionDir}");
        }

        private void FlushPendingFrame()
        {
            if (!_hasPendingFrame || _pendingFrame == null) return;
            lock (_jsonLock)
            {
                File.AppendAllText(_framesJsonlPath, JsonUtility.ToJson(_pendingFrame) + "\n");
            }
            _pendingFrame = null;
            _hasPendingFrame = false;
        }

        private void WriteManifest()
        {
            if (_manifest == null || string.IsNullOrEmpty(_sessionDir)) return;
            File.WriteAllText(
                Path.Combine(_sessionDir, "manifest.json"),
                JsonUtility.ToJson(_manifest, true));
        }

        private float GetSimDelta()
        {
            float fps = Mathf.Max(1f, _captureFps);
            return 1f / fps;
        }

        private void EnableCaptureClock()
        {
            if (_captureClockActive) return;

            float simDt = GetSimDelta();
            _savedFixedDeltaTime = Time.fixedDeltaTime;
            // Lock scaled delta for Update-driven systems (AIPath, AI timers, cutting, etc.).
            Time.captureDeltaTime = simDt;
            // Keep FixedUpdate in lockstep if anything uses it.
            Time.fixedDeltaTime = simDt;
            _captureClockActive = true;
        }

        private void DisableCaptureClock()
        {
            if (!_captureClockActive) return;

            Time.captureDeltaTime = 0f;
            Time.fixedDeltaTime = _savedFixedDeltaTime > 0f ? _savedFixedDeltaTime : 0.02f;
            _captureClockActive = false;
        }

        private static void FlushPendingWrites(float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (RecordingCameraUtility.PendingAsyncWrites > 0 && Time.realtimeSinceStartup < deadline)
                Thread.Sleep(10);
        }

        private void BindChefs()
        {
            _agents.Clear();

            var manager = KitchenAIManager.Instance;
            if (manager != null)
            {
                foreach (var chef in manager.GetChefs())
                {
                    if (chef == null) continue;
                    var agent = chef.GetComponent<ChefRecordingAgent>();
                    if (agent == null)
                        agent = chef.gameObject.AddComponent<ChefRecordingAgent>();
                    _agents.Add(agent);
                }
            }

            if (_agents.Count == 0)
            {
                foreach (var chef in FindObjectsOfType<AIChefController>())
                {
                    var agent = chef.GetComponent<ChefRecordingAgent>();
                    if (agent == null)
                        agent = chef.gameObject.AddComponent<ChefRecordingAgent>();
                    _agents.Add(agent);
                }
            }
        }

        /// <summary>
        /// Capture global + all agent views in this LateUpdate so labels align with the sim step
        /// that just ran in Update.
        ///
        /// Action timing (IDM): transition action computed at state_(i+1) is written back onto
        /// the pending state_i frame before it is flushed to disk
        /// (state_i + action_i => state_(i+1)).
        /// </summary>
        private void CaptureSimFrame()
        {
            // Never skip a sim frame: Update already advanced time. Wait on wall clock only.
            if (RecordingCameraUtility.PendingAsyncWrites >= _maxPendingWrites)
                FlushPendingWrites(timeoutSeconds: 30f);

            int frame = _frameIndex;
            string globalRel = $"global/frame_{frame:D6}.png";
            string globalAbs = Path.Combine(_sessionDir, globalRel);

            if (_globalCamera != null)
            {
                var pixels = RecordingCameraUtility.CapturePixels(_globalCamera, _globalRt);
                RecordingCameraUtility.SavePixelsAsync(pixels, _frameWidth, _frameHeight, globalAbs);
            }

            var playerFrames = new PlayerRecordingFrame[_agents.Count];
            var transitionActions = new ChefKeyboardInput[_agents.Count];
            for (int i = 0; i < _agents.Count; i++)
            {
                var agent = _agents[i];
                string rel = $"agent_{agent.Chef.agentId}/fp_{frame:D6}.png";
                string abs = Path.Combine(_sessionDir, rel);
                agent.CaptureImageAsync(abs);
                playerFrames[i] = agent.CaptureState(rel);
                // Action that moved prev→current; belongs on the previous frame.
                transitionActions[i] = agent.ConsumeTransitionAction();
                agent.BeginFrame();
            }

            // Write back action_i onto pending state_i, then flush state_i.
            if (_hasPendingFrame && _pendingFrame?.players != null)
            {
                int n = Mathf.Min(_pendingFrame.players.Length, transitionActions.Length);
                for (int i = 0; i < n; i++)
                    ChefRecordingAgent.ApplyAction(_pendingFrame.players[i], transitionActions[i]);
                FlushPendingFrame();
            }

            var bb = KitchenAIManager.Instance?.Blackboard;
            var spawnPositions = KitchenAIManager.Instance != null
                ? KitchenAIManager.Instance.GetSpawnPositions()
                : (IReadOnlyList<Vector3>)System.Array.Empty<Vector3>();
            var groundDropPositions = KitchenAIManager.Instance?.Blackboard?.groundDropPositions
                ?? (IReadOnlyList<Vector3>)System.Array.Empty<Vector3>();
            float frameTime = Time.time - _recordStartTime;
            _pendingFrame = new RecordingFrameData
            {
                frame = frame,
                time = frameTime,
                globalImage = globalRel,
                camera_info = KitchenCameraInfoUtility.Capture(
                    _globalCamera, _frameWidth, _frameHeight, "global"),
                scene_3d_info = KitchenWorldStateSerializer.CaptureScene3D(
                    bb, spawnPositions, groundDropPositions),
                players = playerFrames,
                world = KitchenWorldStateSerializer.Capture(bb),
            };
            _hasPendingFrame = true;

            _frameIndex++;
        }
    }
}
