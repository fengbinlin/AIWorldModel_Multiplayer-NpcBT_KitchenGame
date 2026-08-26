using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Infinite plate dispenser. Visual stack is handled by <see cref="PlatesCounterVisual"/>.
    /// </summary>
    public class PlatesCounter : BaseCounter
    {
        public const int DisplayPlateCount = 5;

        private readonly KitchenObjEnum _kitchenObjEnum = KitchenObjEnum.Plate;

        public override void Interact(ICanHoldKitchenObj holder)
        {
            if (holder.HasKitchenObj()) return;
            KitchenObjOperator.SpawnKitchenObjRpc(_kitchenObjEnum, holder);
        }
    }
}
