using UnityEngine;

namespace Kitchen
{
    [CreateAssetMenu(fileName = "KitchenProcess", menuName = "ScriptableObjects/KitchenProcess", order = 1)]
    public class KitchenProcessSo : ScriptableObject
    {
        public KitchenObjEnum inputEnum;
        public KitchenObjEnum outputEnum;
        public FacilityEnum requiredFacility;
        [Tooltip("Cutting = number of cuts needed, Stove = cooking time in seconds")]
        public float processValue;
    }
}
