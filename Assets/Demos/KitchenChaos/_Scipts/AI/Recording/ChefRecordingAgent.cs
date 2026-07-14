using Pathfinding;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Per-chef recording component: first-person camera + input reverse-engineering.
    /// Auto-bound by <see cref="KitchenSessionRecorder"/>.
    /// Uses the prefab child named <c>AICamera</c> (must have a <see cref="Camera"/>).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChefRecordingAgent : MonoBehaviour
    {
        public const string AiCameraObjectName = "AICamera";

        private AIChefController _chef;
        private IAstarAI _ai;
        private Camera _fpCamera;
        private RenderTexture _renderTexture;
        private Vector3 _lastPosition;
        private bool _interactThisFrame;
        private int _frameWidth;
        private int _frameHeight;
        private float _recordStartTime;

        private bool _hasPrevView;
        private float _lastYaw;
        private float _lastPitch;

        public AIChefController Chef => _chef;

        private void Awake()
        {
            _chef = GetComponent<AIChefController>();
            _ai = GetComponent<IAstarAI>();
            ResolveFirstPersonCamera();
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
            _hasPrevView = false;
            SampleViewAngles(out _lastYaw, out _lastPitch);
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
            SampleViewAngles(out float yaw, out float pitch);
            float mouseX = 0f;
            float mouseY = 0f;
            if (_hasPrevView)
            {
                mouseX = Mathf.DeltaAngle(_lastYaw, yaw);
                mouseY = Mathf.DeltaAngle(_lastPitch, pitch);
            }
            _lastYaw = yaw;
            _lastPitch = pitch;
            _hasPrevView = true;

            Vector3 velocity = _ai != null ? _ai.velocity : Vector3.zero;
            float maxSpeed = _ai != null ? Mathf.Max(_ai.maxSpeed, 0.01f) : 1f;

            var input = KitchenInputEncoder.EncodeFirstPerson(
                velocity, yaw, maxSpeed, mouseX, mouseY, _interactThisFrame);

            if (velocity.sqrMagnitude < 0.01f)
            {
                float dt = Time.deltaTime > 0f ? Time.deltaTime : 0.016f;
                input = KitchenInputEncoder.EncodeFirstPersonFromDelta(
                    transform.position - _lastPosition,
                    dt,
                    yaw,
                    maxSpeed,
                    mouseX,
                    mouseY,
                    _interactThisFrame);
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
                mouseX = input.mouseX,
                mouseY = input.mouseY,
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z,
                rotX = pitch,
                rotY = yaw,
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
            var pixels = RecordingCameraUtility.CapturePixels(_fpCamera, _renderTexture);
            RecordingCameraUtility.SavePixelsAsync(pixels, _frameWidth, _frameHeight, absolutePath);
        }

        private void SampleViewAngles(out float yaw, out float pitch)
        {
            if (_fpCamera != null)
            {
                yaw = _fpCamera.transform.eulerAngles.y;
                pitch = KitchenInputEncoder.NormalizePitch(_fpCamera.transform.localEulerAngles.x);
                return;
            }

            yaw = transform.eulerAngles.y;
            pitch = 0f;
        }

        private void ResolveFirstPersonCamera()
        {
            var camTransform = FindChildRecursive(transform, AiCameraObjectName);
            if (camTransform == null)
            {
                Debug.LogError(
                    $"[{name}] ChefRecordingAgent requires a child GameObject named '{AiCameraObjectName}'.",
                    this);
                return;
            }

            _fpCamera = camTransform.GetComponent<Camera>();
            if (_fpCamera == null)
            {
                Debug.LogError(
                    $"[{name}] '{AiCameraObjectName}' is missing a Camera component.",
                    camTransform);
                return;
            }

            // Recording only — keep it out of the game view.
            _fpCamera.enabled = false;

            var listener = _fpCamera.GetComponent<AudioListener>();
            if (listener != null)
                listener.enabled = false;
        }

        private static Transform FindChildRecursive(Transform root, string objectName)
        {
            if (root.name == objectName)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChildRecursive(root.GetChild(i), objectName);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
