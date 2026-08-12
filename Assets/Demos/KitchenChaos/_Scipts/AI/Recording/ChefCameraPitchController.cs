using UnityEngine;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// Smooth pitch-only bias of the chef's <c>AICamera</c> toward a task interest point.
    /// Yaw/roll stay locked to the body (local Y/Z = 0). Recording reverse-engineering
    /// picks up pitch via <see cref="ChefRecordingAgent"/> mouseY / rotX.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public class ChefCameraPitchController : MonoBehaviour
    {
        [Tooltip("Resting local pitch (degrees). Positive = look down (Unity).")]
        [SerializeField] private float _defaultPitch = 14.806f;

        [Tooltip("Max deviation from default pitch toward interest (degrees).")]
        [SerializeField] private float _maxBiasDegrees = 22.5f;

        [SerializeField] private float _smoothTime = 0.14f;
        [SerializeField] private float _maxDegreesPerSecond = 140f;

        [Tooltip("Minimum forward depth used when computing elevation (avoids near/side spikes).")]
        [SerializeField] private float _minForwardDepth = 0.35f;

        private AIChefController _chef;
        private Transform _cameraTransform;
        private float _currentPitch;
        private float _pitchVelocity;
        private bool _capturedDefault;

        public float CurrentPitch => _currentPitch;
        public float DefaultPitch => _defaultPitch;

        public void Bind(AIChefController chef, Transform cameraTransform)
        {
            _chef = chef;
            _cameraTransform = cameraTransform;
            if (_cameraTransform == null) return;

            _currentPitch = KitchenInputEncoder.NormalizePitch(
                _cameraTransform.localEulerAngles.x);
            if (!_capturedDefault)
            {
                _defaultPitch = _currentPitch;
                _capturedDefault = true;
            }

            ApplyLocalPitch(_currentPitch);
        }

        private void LateUpdate()
        {
            if (_cameraTransform == null || _chef == null) return;
            Tick(Time.deltaTime);
        }

        /// <summary>Advance pitch; safe to call from the recorder before a capture.</summary>
        public void Tick(float deltaTime)
        {
            if (_cameraTransform == null || _chef == null) return;

            float targetPitch = _defaultPitch;
            if (_chef.TryGetLookInterestWorldPoint(out Vector3 interest))
            {
                float desired = ComputeDesiredPitch(interest);
                targetPitch = Mathf.Clamp(
                    desired,
                    _defaultPitch - _maxBiasDegrees,
                    _defaultPitch + _maxBiasDegrees);
            }

            float dt = deltaTime > 0f ? deltaTime : Time.deltaTime;
            if (dt <= 0f) dt = 0.016f;

            _currentPitch = Mathf.SmoothDampAngle(
                _currentPitch,
                targetPitch,
                ref _pitchVelocity,
                _smoothTime,
                _maxDegreesPerSecond,
                dt);

            ApplyLocalPitch(_currentPitch);
        }

        private float ComputeDesiredPitch(Vector3 interestWorld)
        {
            Vector3 to = interestWorld - _cameraTransform.position;

            Vector3 bodyFwd = _chef.transform.forward;
            bodyFwd.y = 0f;
            if (bodyFwd.sqrMagnitude < 1e-6f)
                bodyFwd = Vector3.forward;
            bodyFwd.Normalize();

            // Pitch-only: ignore sideways offset; only elevation vs forward depth.
            float forwardDepth = Vector3.Dot(to, bodyFwd);
            float depth = Mathf.Max(forwardDepth, _minForwardDepth);
            float elevationDeg = Mathf.Atan2(to.y, depth) * Mathf.Rad2Deg;

            // Unity local X+: look down. Target above ⇒ negative pitch.
            return -elevationDeg;
        }

        private void ApplyLocalPitch(float pitchDegrees)
        {
            // Hard lock: never introduce local yaw/roll.
            _cameraTransform.localRotation = Quaternion.Euler(pitchDegrees, 0f, 0f);
        }
    }
}
