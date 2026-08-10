using System;
using System.Collections.Generic;
using UnityEngine;
using Kitchen;

namespace Kitchen.AI
{
    public static class KitchenOrderIdentity
    {
        public static int ToRuntimeId(string orderCode)
        {
            if (string.IsNullOrEmpty(orderCode))
                return 0;

            int hash = Animator.StringToHash(orderCode);
            return hash == 0 ? 1 : hash;
        }
    }

    /// <summary>
    /// Low-level interaction represented by an AI task.
    /// Most kitchen interactions are a transfer of object A to object B,
    /// followed by an interaction with B.
    /// </summary>
    public enum KitchenActionType
    {
        None,
        Transfer,
        SpawnFromStorage,
        Process,
        AddToPlate,
        Assemble,
        Serve,
        DropToGround,
        Trash,
    }

    public enum KitchenConditionType
    {
        DependencyTasksCompleted,
        ObjectExists,
        ObjectBelongsToOrder,
        FacilityHasCapacity,
        FacilityContainsItem,
        PlateContainsItem,
        PlateMatchesOrder,
        ProcessOutputReady,
        GroundDropAvailable,
    }

    [Serializable]
    public sealed class KitchenConditionSpec
    {
        public KitchenConditionType type;
        public int orderId;
        public int referencedTaskId;
        public ulong objectId;
        public KitchenObjEnum itemType;
        public FacilityType facilityType;
        public bool negate;

        public KitchenConditionSpec Clone()
        {
            return new KitchenConditionSpec
            {
                type = type,
                orderId = orderId,
                referencedTaskId = referencedTaskId,
                objectId = objectId,
                itemType = itemType,
                facilityType = facilityType,
                negate = negate,
            };
        }
    }

    /// <summary>
    /// Runtime action descriptor. This is intentionally data-only; execution
    /// remains in AIChefController so the existing network-authoritative
    /// interaction path is preserved.
    /// </summary>
    [Serializable]
    public sealed class KitchenActionSpec
    {
        public KitchenActionType type;
        public ulong objectAId;
        public ulong objectBId;
        public KitchenObjEnum itemType;
        public KitchenObjEnum outputType;
        public FacilityType facilityType;
        public bool targetIsGround;
        public Vector3 groundPosition;
    }

    [Serializable]
    public sealed class KitchenPlanNode
    {
        public int taskId;
        public int orderId;
        public string stepId;
        public string label;
        public TaskType legacyTaskType;
        public KitchenActionSpec action = new();
        public List<int> dependencyTaskIds = new();
        public List<KitchenConditionSpec> preconditions = new();
        public List<KitchenConditionSpec> postconditions = new();
        public string status = "pending";
        public int retryCount;
        public int maxRetryCount = 3;
    }

    [Serializable]
    public sealed class KitchenPlan
    {
        public int orderId;
        public string recipeName;
        public KitchenObjEnum requiredItem;
        public List<KitchenPlanNode> nodes = new();
    }
}
