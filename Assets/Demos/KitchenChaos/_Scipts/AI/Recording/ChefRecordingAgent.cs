using Pathfinding;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Per-chef recording component: first-person camera + input reverse-engineering.
    /// Auto-bound by <see cref="KitchenSessionRecorder"/>.
    /// Uses the prefab child named <c>AICamera</c> (must have a <see cref="Camera"/>).
    ///
    /// IDM / causal action timing:
    ///   CaptureState() snapshots state at the current sim frame.
    ///   ConsumeTransitionAction() reverse-engineers the action that moved prev→current;
    ///   the recorder writes that action back onto the previous frame
    ///   (state_i + action_i => state_(i+1)).
    /// </summary>
    [DisallowMultipleComponent]
    public class ChefRecordingAgent : MonoBehaviour
    {
        public const string AiCameraObjectName = "AICamera";

        private AIChefController _chef;
        private IAstarAI _ai;
        private Camera _fpCamera;
        private RenderTexture _renderTexture;
        private bool _interactThisFrame;
        private int _frameWidth;
        private int _frameHeight;
        private float _recordStartTime;

        private bool _hasPrevPose;
        private Vector3 _prevPosition;
        private float _prevYaw;
        private float _prevPitch;

        public AIChefController Chef => _chef;
        public Camera FirstPersonCamera => _fpCamera;

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
            _hasPrevPose = false;
            _interactThisFrame = false;
            _prevPosition = transform.position;
            SampleViewAngles(out _prevYaw, out _prevPitch);
        }

        public void BeginFrame()
        {
            _interactThisFrame = false;
        }

        private void OnChefInteraction()
        {
            _interactThisFrame = true;
        }

        /// <summary>
        /// Snapshot observation / state at the current sim frame.
        /// Action fields are left zero; the recorder fills them later from the next transition.
        /// </summary>
        public PlayerRecordingFrame CaptureState(string imageRelativePath)
        {
            SampleViewAngles(out float yaw, out float pitch);
            var pos = transform.position;
            var task = _chef?.CurrentTask;
            return new PlayerRecordingFrame
            {
                playerId = _chef != null ? _chef.agentId : -1,
                playerName = _chef != null ? _chef.chefName : name,
                fpImage = imageRelativePath,
                keyW = false,
                keyA = false,
                keyS = false,
                keyD = false,
                keyE = false,
                moveX = 0f,
                moveZ = 0f,
                mouseX = 0f,
                mouseY = 0f,
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
                camera_info = KitchenCameraInfoUtility.Capture(
                    _fpCamera, _frameWidth, _frameHeight, "fp"),
            };
        }

        /// <summary>
        /// Reverse-engineer the action that took the previous captured pose to the current pose.
        /// Uses previous view yaw as the move basis (action taken at state_i).
        /// Advances the internal previous-pose markers afterward.
        /// </summary>
        public ChefKeyboardInput ConsumeTransitionAction()
        {
            SampleViewAngles(out float yaw, out float pitch);
            Vector3 pos = transform.position;

            if (!_hasPrevPose)
            {
                _prevPosition = pos;
                _prevYaw = yaw;
                _prevPitch = pitch;
                _hasPrevPose = true;
                _interactThisFrame = false;
                return default;
            }

            float mouseX = Mathf.DeltaAngle(_prevYaw, yaw);
            float mouseY = Mathf.DeltaAngle(_prevPitch, pitch);
            float maxSpeed = _ai != null ? Mathf.Max(_ai.maxSpeed, 0.01f) : 1f;
            float dt = Time.deltaTime > 0f ? Time.deltaTime : 0.016f;

            Vector3 velocity = _ai != null ? _ai.velocity : Vector3.zero;
            ChefKeyboardInput input;
            if (velocity.sqrMagnitude < 0.01f)
            {
                input = KitchenInputEncoder.EncodeFirstPersonFromDelta(
                    pos - _prevPosition,
                    dt,
                    _prevYaw,
                    maxSpeed,
                    mouseX,
                    mouseY,
                    _interactThisFrame);
            }
            else
            {
                // Move basis = yaw at action time (previous frame / state_i).
                input = KitchenInputEncoder.EncodeFirstPerson(
                    velocity, _prevYaw, maxSpeed, mouseX, mouseY, _interactThisFrame);
            }

            _prevPosition = pos;
            _prevYaw = yaw;
            _prevPitch = pitch;
            _interactThisFrame = false;
            return input;
        }

        public static void ApplyAction(PlayerRecordingFrame player, ChefKeyboardInput input)
        {
            if (player == null) return;
            player.keyW = input.W;
            player.keyA = input.A;
            player.keyS = input.S;
            player.keyD = input.D;
            player.keyE = input.E;
            player.moveX = input.moveX;
            player.moveZ = input.moveZ;
            player.mouseX = input.mouseX;
            player.mouseY = input.mouseY;
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
