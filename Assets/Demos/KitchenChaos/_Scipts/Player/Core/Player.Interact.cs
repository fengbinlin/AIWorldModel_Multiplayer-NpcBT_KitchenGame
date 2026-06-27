using System;
using UnityEngine;

namespace Kitchen.Player
{
    public partial class Player
    {
        public event Action pause;

        private void OnPerformInteract()
        {
            if (!GameManager.Instance.IsPlaying()) return;

            // Priority 1: Interact with selected counter
            if (SelectedCounter != null)
            {
                SelectedCounter.Interact(this);
                return;
            }

            // Priority 2: Drop held item on ground
            if (HasKitchenObj())
            {
                DropOnGround();
                return;
            }

            // Priority 3: Pickup free item from ground
            var nearbyObj = TryFindNearbyFreeKitchenObj();
            if (nearbyObj != null)
            {
                PickupFromGround(nearbyObj);
            }
        }

        private void DropOnGround()
        {
            var dropPos = transform.position + transform.forward * 0.8f + Vector3.up * 0.5f;
            var dropDir = transform.forward;
            var obj = GetKitchenObj();
            ClearKitchenObj();
            KitchenObjFactory.Instance.DropObjServerRpc(
                obj.NetworkObject, dropPos, dropDir, data.dropForce);
        }

        private void PickupFromGround(KitchenObj kitchenObj)
        {
            KitchenObjFactory.Instance.PickupObjServerRpc(
                kitchenObj.NetworkObject, this.NetworkObject);
        }

        private void OnPerformInteractAlternate()
        {
            if (!GameManager.Instance.IsPlaying()) return;

            if (SelectedCounter == null) return;
            if (SelectedCounter.TryGetComponent(out IInteractAlternate interactAlternate))
            {
                interactAlternate.InteractAlternate(this);
            }
        }
    }
}