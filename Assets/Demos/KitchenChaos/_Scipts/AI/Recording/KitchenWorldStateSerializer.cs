using System.Collections.Generic;
using System.Linq;
using Kitchen;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    public static class KitchenWorldStateSerializer
    {
        public static WorldStateSnapshot Capture(KitchenBlackboard bb)
        {
            if (bb == null)
                return new WorldStateSnapshot();

            var snapshot = new WorldStateSnapshot
            {
                facilities = CaptureFacilities(bb),
                items = CaptureItems(bb),
                orders = CaptureOrders(bb),
                tasks = CaptureTasks(bb),
            };
            return snapshot;
        }

        private static FacilitySnapshot[] CaptureFacilities(KitchenBlackboard bb)
        {
            var list = new List<FacilitySnapshot>();
            foreach (var f in bb.facilities)
            {
                if (f?.counter == null) continue;
                var pos = f.counter.transform.position;
                list.Add(new FacilitySnapshot
                {
                    name = f.counter.name,
                    facilityType = f.type.ToString(),
                    state = f.state,
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    counterItem = f.counter.HasKitchenObj()
                        ? f.counter.GetKitchenObj().objEnum.ToString()
                        : "",
                    reservedByAgent = f.reservedByAgent,
                    occupiedByAgent = f.occupiedByAgent,
                });
            }
            return list.ToArray();
        }

        private static ItemSnapshot[] CaptureItems(KitchenBlackboard bb)
        {
            var list = new List<ItemSnapshot>();
            foreach (var i in bb.items)
            {
                if (i == null) continue;
                var pos = i.Position;
                list.Add(new ItemSnapshot
                {
                    id = i.id,
                    itemType = i.itemType.ToString(),
                    stage = i.stage.ToString(),
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    carriedByAgent = i.carriedByAgent,
                    orderId = i.orderId,
                });
            }
            return list.ToArray();
        }

        private static OrderSnapshot[] CaptureOrders(KitchenBlackboard bb)
        {
            var list = new List<OrderSnapshot>();
            for (int i = 0; i < bb.activeOrders.Count; i++)
            {
                var recipe = bb.activeOrders[i];
                if (recipe == null) continue;
                int orderId = i < bb.activeOrderIds.Count ? bb.activeOrderIds[i] : 0;
                list.Add(new OrderSnapshot
                {
                    orderId = orderId,
                    recipeName = recipe.recipeName,
                    ingredients = recipe.ingredients?.Select(ing => ing.ToString()).ToArray() ?? new string[0],
                });
            }
            return list.ToArray();
        }

        private static TaskSnapshot[] CaptureTasks(KitchenBlackboard bb)
        {
            var list = new List<TaskSnapshot>();
            foreach (var t in bb.taskPool)
            {
                if (t == null) continue;
                list.Add(new TaskSnapshot
                {
                    taskId = t.id,
                    taskType = t.type.ToString(),
                    label = t.label,
                    status = t.status,
                    assignedAgentId = t.assignedAgentId,
                    orderId = t.orderId,
                });
            }
            return list.ToArray();
        }
    }
}
