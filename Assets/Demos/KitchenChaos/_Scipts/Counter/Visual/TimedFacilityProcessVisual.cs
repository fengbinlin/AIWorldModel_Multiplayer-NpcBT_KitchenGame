using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 烤箱 / 搅拌器等计时设施视觉：挂在 *_Visual 上，订阅父级 ICookingFacility，
    /// 加工中仅对 tripo 模型子物体做缩放波动。
    /// </summary>
    public class TimedFacilityProcessVisual : MonoBehaviour
    {
        [SerializeField] private Transform pulseTarget;
        [SerializeField] private float pulseAmplitude = 0.06f;
        [SerializeField] private float pulseSpeed = 6f;

        private ICookingFacility _cooking;
        private Vector3 _baseScale;
        private bool _processing;

        private void Awake()
        {
            _cooking = GetComponentInParent<ICookingFacility>();
            if (pulseTarget == null)
                pulseTarget = FindTripoChild();
            if (pulseTarget != null)
                _baseScale = pulseTarget.localScale;
        }

        private void OnEnable()
        {
            if (_cooking == null)
                _cooking = GetComponentInParent<ICookingFacility>();
            if (_cooking == null) return;

            _cooking.OnStartCooking += OnStartCooking;
            _cooking.OnStopCooking += OnStopCooking;
        }

        private void OnDisable()
        {
            if (_cooking == null) return;
            _cooking.OnStartCooking -= OnStartCooking;
            _cooking.OnStopCooking -= OnStopCooking;
            StopPulse();
        }

        private void Update()
        {
            if (!_processing || pulseTarget == null) return;

            float wobble = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmplitude;
            pulseTarget.localScale = _baseScale * wobble;
        }

        private void OnStartCooking()
        {
            if (pulseTarget == null)
                pulseTarget = FindTripoChild();
            if (pulseTarget == null) return;

            _baseScale = pulseTarget.localScale;
            _processing = true;
        }

        private void OnStopCooking()
        {
            StopPulse();
        }

        private void StopPulse()
        {
            _processing = false;
            if (pulseTarget != null)
                pulseTarget.localScale = _baseScale;
        }

        private Transform FindTripoChild()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == transform) continue;
                if (t.name.IndexOf("tripo", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }

            return null;
        }
    }
}
