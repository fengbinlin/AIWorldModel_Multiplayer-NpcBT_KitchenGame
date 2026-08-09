using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// 挂在柜子 Visual 部件上，声明对应的固定槽位。
    /// 未挂时 CounterSkinApplier 会按常见子物体名自动推断。
    /// </summary>
    [DisallowMultipleComponent]
    public class CounterPartSlotMarker : MonoBehaviour
    {
        public CounterPartSlot slot = CounterPartSlot.CounterBody;
    }
}
