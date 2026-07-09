using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Records AI world-model training data without blocking the game loop.
    /// Capture is spread across multiple frames; PNG encode + disk I/O run on background threads.
    /// </summary>
    public class KitchenSessionRecorder : MonoBehaviour
    {
        public static KitchenSessionRecorder Instance { get; private set; }

        [Header("Recording")]
        [SerializeField] private bool _autoStartWhenPlaying = true;
        [SerializeField] private KeyCode _toggleKey = KeyCode.F9;
        [SerializeField] private float _captureFps = 10f;
        [SerializeField] private int _frameWidth = 256;
        [SerializeField] private int _frameHeight = 256;
        [SerializeField] private string _outputRootFolder = "KitchenTrainingRecordings";
        [Tooltip("Max background PNG writes queued. Drops frames if disk is slower than capture.")]
        [SerializeField] private int _maxPendingWrites = 64;

        [Header("Cameras")]
        [SerializeField] private Camera _globalCamera;

        private readonly List<ChefRecordingAgent> _agents = new();
        private RenderTexture _globalRt;
        private string _sessionDir;
        private string _framesJsonlPath;
        private float _captureTimer;
        private int _frameIndex;
        private bool _isRecording;
        private bool _manualStopRequested;
        private float _recordStartTime;

        // Spread one logical frame across multiple Unity frames to avoid Time.deltaTime spikes.
        private bool _hasPendingCapture;
        private PendingCapture _pending;
        private readonly object _jsonLock = new();

        public bool IsRecording => _isRecording;
        public string SessionDirectory => _sessionDir;
        public int PendingAsyncWrites => RecordingCameraUtility.PendingAsyncWrites;

        private struct PendingCapture
        {
            public int frameIndex;
            public string globalRelPath;
            public string globalAbsPath;
            public ChefRecordingFrame[] chefFrames;
            public int captureStep; // 0=global, 1..N=agents, N+1=finalize
        }

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

            if (!_isRecording) return;
            if (!CanRecord()) return;

            // Continue an in-progress multi-step capture first.
            if (_hasPendingCapture)
            {
                AdvancePendingCapture();
                return;
            }

            if (RecordingCameraUtility.PendingAsyncWrites >= _maxPendingWrites)
                return;

            _captureTimer += Time.unscaledDeltaTime;
            float interval = 1f / Mathf.Max(1f, _captureFps);
            if (_captureTimer < interval) return;
            _captureTimer -= interval;

            BeginCaptureFrame();
        }

        private void OnDestroy()
        {
            if (_isRecording)
                StopRecording();
            if (_globalRt != null)
                _globalRt.Release();
            if (Instance == this)
                Instance = null;
        }

        private bool CanRecord()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlaying())
                return false;
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
                return false;
            return true;
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
            _captureTimer = 0f;
            _recordStartTime = Time.time;
            _pending = default;
            _hasPendingCapture = false;
            _isRecording = true;

            foreach (var agent in _agents)
                agent.Initialize(_frameWidth, _frameHeight, _recordStartTime);

            var manifest = new RecordingSessionManifest
            {
                sessionId = $"session_{stamp}",
                sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                unityTimeStart = _recordStartTime,
                frameWidth = _frameWidth,
                frameHeight = _frameHeight,
                captureFps = _captureFps,
                chefCount = _agents.Count,
            };
            File.WriteAllText(Path.Combine(_sessionDir, "manifest.json"), JsonUtility.ToJson(manifest, true));

            Debug.Log($"[KitchenSessionRecorder] Recording started → {_sessionDir}");
        }

        [ContextMenu("Stop Recording")]
        public void StopRecording() => StopRecording(manual: true);

        public void StopRecording(bool manual)
        {
            if (!_isRecording) return;
            if (manual)
                _manualStopRequested = true;
            _isRecording = false;
            _hasPendingCapture = false;
            _pending = default;
            FlushPendingWrites(timeoutSeconds: 10f);
            Debug.Log($"[KitchenSessionRecorder] Recording stopped. {_frameIndex} frames saved to {_sessionDir}");
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

        private void BeginCaptureFrame()
        {
            foreach (var agent in _agents)
                agent.BeginFrame();

            var bb = KitchenAIManager.Instance?.Blackboard;
            _pending = new PendingCapture
            {
                frameIndex = _frameIndex,
                globalRelPath = $"global/frame_{_frameIndex:D6}.png",
                globalAbsPath = Path.Combine(_sessionDir, $"global/frame_{_frameIndex:D6}.png"),
                chefFrames = new ChefRecordingFrame[_agents.Count],
                captureStep = 0,
            };
            _hasPendingCapture = true;

            AdvancePendingCapture();
        }

        private void AdvancePendingCapture()
        {
            int step = _pending.captureStep;
            int agentCount = _agents.Count;

            if (step == 0)
            {
                if (_globalCamera != null)
                {
                    var pixels = RecordingCameraUtility.CapturePixels(_globalCamera, _globalRt);
                    RecordingCameraUtility.SavePixelsAsync(pixels, _frameWidth, _frameHeight, _pending.globalAbsPath);
                }
                _pending.captureStep = 1;
                return;
            }

            int agentIdx = step - 1;
            if (agentIdx < agentCount)
            {
                var agent = _agents[agentIdx];
                string rel = $"agent_{agent.Chef.agentId}/fp_{_pending.frameIndex:D6}.png";
                string abs = Path.Combine(_sessionDir, rel);
                agent.CaptureImageAsync(abs);
                _pending.chefFrames[agentIdx] = agent.CaptureFrame(_pending.frameIndex, rel);
                _pending.captureStep = step + 1;
                return;
            }

            FinalizeCaptureFrame();
            _hasPendingCapture = false;
            _pending = default;
            _frameIndex++;
        }

        private void FinalizeCaptureFrame()
        {
            var bb = KitchenAIManager.Instance?.Blackboard;
            float frameTime = Time.time - _recordStartTime;
            var frameData = new RecordingFrameData
            {
                frame = _pending.frameIndex,
                time = frameTime,
                globalImage = _pending.globalRelPath,
                chefs = _pending.chefFrames,
                world = KitchenWorldStateSerializer.Capture(bb),
            };

            // JSONL is small — write synchronously to preserve frame order and content alignment.
            lock (_jsonLock)
            {
                File.AppendAllText(_framesJsonlPath, JsonUtility.ToJson(frameData) + "\n");
            }
        }

        private void LateUpdate()
        {
            if (!_autoStartWhenPlaying || _isRecording || _manualStopRequested) return;
            if (CanRecord() && _agents.Count == 0)
                BindChefs();
            if (CanRecord() && _agents.Count > 0)
                StartRecording(manual: false);
        }
    }
}
