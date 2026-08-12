using Nico.Components;
using UnityEngine;

namespace Kitchen.UI
{
    public class StoveFlashingBarUI : MonoBehaviour
    {
        private ICookingFacility _cooking;
        private Animator _animator;
        private readonly int _animParamHash = Animator.StringToHash("flashing");

        private void Awake()
        {
            // StoveCounter / TimedFacilityCounter both implement ICookingFacility
            _cooking = GetComponentInParent<ICookingFacility>();
            _animator = GetComponent<Animator>();
        }

        private void OnEnable()
        {
            if (_cooking == null)
                _cooking = GetComponentInParent<ICookingFacility>();
            if (_cooking == null) return;
            _cooking.OnCookingStageChange += OnCookingStageChange;
            _cooking.OnStopCooking += OnStopCooking;
        }

        private void OnDisable()
        {
            if (_cooking == null) return;
            _cooking.OnCookingStageChange -= OnCookingStageChange;
            _cooking.OnStopCooking -= OnStopCooking;
        }

        private void OnCookingStageChange(KitchenObjEnum? obj)
        {
            if (_animator == null) return;
            if (obj is null)
            {
                _animator.SetBool(_animParamHash, false);
                return;
            }

            _animator.SetBool(_animParamHash, KitchenObjOperator.WillBeBurned(obj.Value));
        }

        private void OnStopCooking()
        {
            if (_animator == null) return;
            // ProgressBar may deactivate this GO; ensure flashing is off when cooking stops.
            _animator.SetBool(_animParamHash, false);
        }
    }
}
