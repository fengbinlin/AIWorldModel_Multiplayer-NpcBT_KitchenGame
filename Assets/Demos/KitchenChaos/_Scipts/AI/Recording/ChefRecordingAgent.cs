using Pathfinding;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Per-chef recording component: first-person camera + input reverse-engineering.
    /// Auto-bound by <see cref="KitchenSessionRecorder"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class ChefRecordingAgent : MonoBehaviour
    {
        [Header("First-Person Camera")]
        [SerializeField] private float _eyeHeight = 1.6f;
        [SerializeField] private float _eyeForward = 0.15f;
        [SerializeField] private float _fpFov = 70f;

        private AIChefController _chef;
        private IAstarAI _ai;
        private Camera _fpCamera;
        private RenderTexture _renderTexture;
        private Vector3 _lastPosition;
        private bool _interactThisFrame;
        private int _frameWidth;
        private int _frameHeight;
        private float _recordStartTime;

        public AIChefController Chef => _chef;

        private void Awake()
        {
            _chef = GetComponent<AIChefController>();
            _ai = GetComponent<IAstarAI>();
            SetupFirstPersonCamera();
        }

        private void OnEnable()
        {
            if (_chef != null)
                _chef.OnInteractionPerformed += OnChefInteraction;
        }

        private void OnDisable()
        {
            if (_chef != null)
                _chef.OnInteractionPerformed -= OnChefInteraction;
        }

        private void OnDestroy()
        {
            if (_fpCamera != null)
                Destroy(_fpCamera.gameObject);
            if (_renderTexture != null)
                _renderTexture.Release();
        }

        public void Initialize(int frameWidth, int frameHeight, float recordStartTime)
        {
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _recordStartTime = recordStartTime;
            if (_renderTexture != null)
                _renderTexture.Release();
            _renderTexture = RecordingCameraUtility.CreateRenderTexture(frameWidth, frameHeight);
            if (_fpCamera != null)
                _fpCamera.targetTexture = null;
            _lastPosition = transform.position;
        }

        public void BeginFrame()
        {
            _interactThisFrame = false;
        }

        private void OnChefInteraction()
        {
            _interactThisFrame = true;
        }

        public ChefRecordingFrame CaptureFrame(int frameIndex, string imageRelativePath)
        {
            Vector3 velocity = _ai != null ? _ai.velocity : Vector3.zero;
            var input = KitchenInputEncoder.Encode(velocity, _interactThisFrame);
            if (velocity.sqrMagnitude < 0.01f)
            {
                float dt = Time.deltaTime > 0f ? Time.deltaTime : 0.016f;
                input = KitchenInputEncoder.EncodeFromDelta(transform.position - _lastPosition, dt, _interactThisFrame);
            }
            _lastPosition = transform.position;

            var pos = transform.position;
            var task = _chef?.CurrentTask;
            return new ChefRecordingFrame
            {
                agentId = _chef != null ? _chef.agentId : -1,
                chefName = _chef != null ? _chef.chefName : name,
                fpImage = imageRelativePath,
                keyW = input.W,
                keyA = input.A,
                keyS = input.S,
                keyD = input.D,
                keyE = input.E,
                moveX = input.moveX,
                moveZ = input.moveZ,
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z,
                rotY = transform.eulerAngles.y,
                captureTime = Time.time - _recordStartTime,
                substate = _chef != null ? _chef.Substate : "unknown",
                taskType = task != null ? task.type.ToString() : "",
                taskLabel = task != null ? task.label : "",
                heldItem = _chef?.HeldItem != null ? _chef.HeldItem.objEnum.ToString() : "",
            };
        }

        public void CaptureImageAsync(string absolutePath)
        {
            if (_fpCamera == null || _renderTexture == null) return;
            SyncCameraTransform();
            var pixels = RecordingCameraUtility.CapturePixels(_fpCamera, _renderTexture);
            RecordingCameraUtility.SavePixelsAsync(pixels, _frameWidth, _frameHeight, absolutePath);
        }

        private void SetupFirstPersonCamera()
        {
            var camGo = new GameObject("ChefRecordingCamera");
            camGo.transform.SetParent(transform, false);
            camGo.transform.localPosition = new Vector3(0f, _eyeHeight, _eyeForward);
            camGo.transform.localRotation = Quaternion.identity;

            _fpCamera = camGo.AddComponent<Camera>();
            _fpCamera.enabled = false;
            _fpCamera.fieldOfView = _fpFov;
            _fpCamera.nearClipPlane = 0.1f;
            _fpCamera.farClipPlane = 80f;
            _fpCamera.clearFlags = CameraClearFlags.Skybox;
            _fpCamera.depth = -10;

            var listener = camGo.GetComponent<AudioListener>();
            if (listener != null)
                Destroy(listener);
        }

        private void SyncCameraTransform()
        {
            if (_fpCamera == null) return;
            _fpCamera.transform.localPosition = new Vector3(0f, _eyeHeight, _eyeForward);
            _fpCamera.transform.rotation = transform.rotation;
        }
    }
}
