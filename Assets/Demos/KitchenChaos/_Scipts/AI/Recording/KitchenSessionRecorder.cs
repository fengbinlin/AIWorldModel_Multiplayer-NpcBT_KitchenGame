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
            EnableCaptureClock();
            _recordStartTime = Time.time;
            _isRecording = true;

            foreach (var agent in _agents)
            {
                agent.Initialize(_frameWidth, _frameHeight, _recordStartTime);
                agent.BeginFrame();
            }

            float simDt = GetSimDelta();
            var manifest = new RecordingSessionManifest
            {
                sessionId = $"session_{stamp}",
                sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                unityTimeStart = _recordStartTime,
                frameWidth = _frameWidth,
                frameHeight = _frameHeight,
                captureFps = 1f / simDt,
                chefCount = _agents.Count,
            };
            File.WriteAllText(Path.Combine(_sessionDir, "manifest.json"), JsonUtility.ToJson(manifest, true));

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
            FlushPendingWrites(timeoutSeconds: 10f);
            Debug.Log($"[KitchenSessionRecorder] Recording stopped. {_frameIndex} frames saved to {_sessionDir}");
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
        /// that just ran in Update. Does not spread across frames (that would desync under captureDeltaTime).
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

            var chefFrames = new ChefRecordingFrame[_agents.Count];
            for (int i = 0; i < _agents.Count; i++)
            {
                var agent = _agents[i];
                string rel = $"agent_{agent.Chef.agentId}/fp_{frame:D6}.png";
                string abs = Path.Combine(_sessionDir, rel);
                agent.CaptureImageAsync(abs);
                chefFrames[i] = agent.CaptureFrame(frame, rel);
                // Clear interaction latch after sampling so next Update can set it again.
                agent.BeginFrame();
            }

            var bb = KitchenAIManager.Instance?.Blackboard;
            float frameTime = Time.time - _recordStartTime;
            var frameData = new RecordingFrameData
            {
                frame = frame,
                time = frameTime,
                globalImage = globalRel,
                chefs = chefFrames,
                world = KitchenWorldStateSerializer.Capture(bb),
            };

            lock (_jsonLock)
            {
                File.AppendAllText(_framesJsonlPath, JsonUtility.ToJson(frameData) + "\n");
            }

            _frameIndex++;
        }
    }
}
