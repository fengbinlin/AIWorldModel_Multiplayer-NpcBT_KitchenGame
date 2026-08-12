using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 煎锅柜视觉：挂在 *_Visual 上，订阅父级 ICookingFacility，开关火焰与粒子。
    /// </summary>
    public class StoveCounterVisual : MonoBehaviour
    {
        private ICookingFacility _cooking;
        private GameObject _stoveOnVisual;
        private GameObject _sizzlingParticles;

        private void Awake()
        {
            _cooking = GetComponentInParent<ICookingFacility>();
            _stoveOnVisual = FindChildByName("StoveOnVisual");
            _sizzlingParticles = FindChildByName("SizzlingParticles");
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
        }

        private void OnStartCooking()
        {
            if (_stoveOnVisual != null) _stoveOnVisual.SetActive(true);
            if (_sizzlingParticles != null) _sizzlingParticles.SetActive(true);
        }

        private void OnStopCooking()
        {
            if (_stoveOnVisual != null) _stoveOnVisual.SetActive(false);
            if (_sizzlingParticles != null) _sizzlingParticles.SetActive(false);
        }

        private GameObject FindChildByName(string childName)
        {
            var direct = transform.Find(childName);
            if (direct != null) return direct.gameObject;

            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name == childName)
                    return t.gameObject;
            }

            return null;
        }
    }
}
