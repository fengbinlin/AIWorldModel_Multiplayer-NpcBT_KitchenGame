using Kitchen;
using UnityEngine;

namespace Kitchen
{
    public static class KitchenObjOperator
    {
        public static void SpawnKitchenObjRpc(KitchenObjEnum objEnum, ICanHoldKitchenObj holder)
        {
            KitchenObjFactory.Instance.SpawnKitObjServerRpc(objEnum, holder.GetNetworkObject());
        }

        public static void ExchangeKitchenObj(ICanHoldKitchenObj holder1, ICanHoldKitchenObj holder2)
        {
            bool h1 = holder1.HasKitchenObj();
            bool h2 = holder2.HasKitchenObj();
            if (!h1 && !h2) return;

            if (h1 && h2)
            {
                var obj1 = holder1.GetKitchenObj();
                var obj2 = holder2.GetKitchenObj();
                holder1.SetKitchenObj(obj2);
                obj2.SetHolder(holder1);
                holder2.SetKitchenObj(obj1);
                obj1.SetHolder(holder2);
                return;
            }

            if (h1)
            {
                var obj1 = holder1.GetKitchenObj();
                holder1.ClearKitchenObj();
                holder2.SetKitchenObj(obj1);
                obj1.SetHolder(holder2);
                return;
            }

            {
                var obj2 = holder2.GetKitchenObj();
                holder2.ClearKitchenObj();
                holder1.SetKitchenObj(obj2);
                obj2.SetHolder(holder1);
            }
        }

        public static void PutKitchenObj(ICanHoldKitchenObj putter, ICanHoldKitchenObj reciever)
        {
            KitchenObjFactory.Instance.PutKitObjServerRpc(putter.GetNetworkObject(), reciever.GetNetworkObject());
        }

        /// <summary>
        /// Processes an ingredient on a given facility: destroys the old object
        /// and spawns the output defined by the matching KitchenProcessSo.
        /// </summary>
        public static void Process(KitchenObj oldObj, ICanHoldKitchenObj holder, FacilityEnum facility)
        {
            var process = DataTableManager.Sigleton.GetProcess(oldObj.objEnum, facility);
            if (process == null) return;
            DestroyKitchenObj(oldObj);
            SpawnKitchenObjRpc(process.outputEnum, holder);
        }

        public static void PutToPlate(KitchenObj kitchenObj, Plate plate)
        {
            if (plate.TryAddIngredient(kitchenObj))
            {
                DestroyKitchenObj(kitchenObj);
            }
        }

        /// <summary>
        /// Returns true if this ingredient is at risk of burning when cooked on the stove —
        /// i.e. it has a StoveCounter process whose output has no further process (terminal/burned state).
        /// </summary>
        public static bool WillBeBurned(KitchenObjEnum objEnum)
        {
            var process = DataTableManager.Sigleton.GetProcess(objEnum, FacilityEnum.StoveCounter);
            if (process == null) return false;
            return !DataTableManager.Sigleton.CanProcess(process.outputEnum, FacilityEnum.StoveCounter);
        }

        public static void DestroyKitchenObj(KitchenObj kitchenObj)
        {
            if (kitchenObj == null)
                return;
            KitchenObjFactory.Instance.DestroyServerRpc(kitchenObj.NetworkObject);
        }
    }
}
