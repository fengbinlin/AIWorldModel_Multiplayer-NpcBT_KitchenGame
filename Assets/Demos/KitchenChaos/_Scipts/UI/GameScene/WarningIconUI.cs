using UnityEngine;
using Kitchen;

namespace Kitchen.UI
{
    public class WarningIconUI : MonoBehaviour
    {
        private ICookingFacility _cooking;
        [SerializeField] private GameObject warningIcon;

        private void Awake()
        {
            _cooking = GetComponentInParent<ICookingFacility>();
            if (warningIcon != null)
                warningIcon.SetActive(false);
        }

        private void OnEnable()
        {
            if (_cooking == null)
                _cooking = GetComponentInParent<ICookingFacility>();
            if (_cooking == null) return;
            _cooking.OnCookingStageChange += OnCookingStageChange;
        }

        private void OnDisable()
        {
            if (_cooking == null) return;
            _cooking.OnCookingStageChange -= OnCookingStageChange;
        }

        private void OnCookingStageChange(KitchenObjEnum? obj)
        {
            if (warningIcon == null) return;
            if (obj is null)
            {
                warningIcon.SetActive(false);
                return;
            }

            warningIcon.SetActive(KitchenObjOperator.WillBeBurned(obj.Value));
        }
    }
}

