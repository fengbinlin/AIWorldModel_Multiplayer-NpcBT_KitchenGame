using System.Collections.Generic;
using System.Linq;
using Kitchen;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// Opportunistic chaining: look-ahead divert during FETCH, claim after postconditions,
    /// and holder exclusivity for PROCESS / ADD.
    /// Spec: docs/superpowers/specs/2026-08-30-ai-opportunistic-task-chaining-design.md
    /// </summary>
    public static class KitchenTaskContinuation
    {
        static int _nextChainSessionId = 1;

        public static int BeginOrGetChainSession(AgentState agent)
        {
            if (agent == null) return 0;
            if (agent.chainSessionId == 0)
                agent.chainSessionId = _nextChainSessionId++;
            return agent.chainSessionId;
        }

        public static void ClearChainSession(AgentState agent)
        {
            if (agent != null)
                agent.chainSessionId = 0;
        }

        public static void TagExclusiveDeliverer(
            KitchenBlackboard bb,
            KitchenObj placed,
            int agentId)
        {
            if (bb == null || placed == null || agentId < 0)
                return;

            var item = bb.items.Find(i => i.kitchenObj == placed);
            if (item != null)
                item.exclusiveDelivererAgentId = agentId;
        }

        public static void ClearExclusiveDelivererForAgent(KitchenBlackboard bb, int agentId)
        {
            if (bb?.items == null || agentId < 0)
                return;

            foreach (var item in bb.items)
            {
                if (item.exclusiveDelivererAgentId == agentId)
                    item.exclusiveDelivererAgentId = -1;
            }
        }

        /// <summary>
        /// Mid-FETCH look-ahead: next PROCESS will consume held type and a process
        /// facility is free. Does not claim PROCESS.
        /// </summary>
        public static bool TryLookAheadProcessSink(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask fetchTask,
            KitchenObj held,
            out BaseCounter sink,
            out FacilityType facilityType)
        {
            sink = null;
            facilityType = default;
            if (bb == null || agent == null || fetchTask == null || held == null)
                return false;
            if (fetchTask.type != TaskType.FETCH || fetchTask.orderId == 0)
                return false;

            var recipe = bb.FindRecipeForOrder(fetchTask.orderId);
            if (recipe == null || !bb.TryGetRecipeSteps(recipe.recipeName, out var steps))
                return false;

            var processStep = FindConsumingProcessStep(steps, fetchTask.stepId, held.objEnum);
            if (processStep == null)
                return false;

            var facility = bb.FindBestAvailableFacility(
                processStep.requiredFacilityType,
                agent.position,
                agent.agentId);
            if (facility?.counter == null || facility.counter.HasKitchenObj())
                return false;

            sink = facility.counter;
            facilityType = processStep.requiredFacilityType;
            return true;
        }

        /// <summary>
        /// Look-ahead: order plate exists and held item is an ADD input for the recipe.
        /// </summary>
        public static bool TryLookAheadPlateSink(
            KitchenBlackboard bb,
            KitchenTask currentTask,
            KitchenObj held,
            out BaseCounter plateCounter)
        {
            plateCounter = null;
            if (bb == null || currentTask == null || held == null || currentTask.orderId == 0)
                return false;

            var plate = bb.FindPlateForOrder(currentTask.orderId);
            if (plate == null)
                return false;
            if (plate.GetHolder() is not BaseCounter counter || counter == null)
                return false;

            var recipe = bb.FindRecipeForOrder(currentTask.orderId);
            if (recipe == null || !bb.TryGetRecipeSteps(recipe.recipeName, out var steps))
                return false;

            if (!steps.Any(s =>
                    s.taskType == TaskType.ADD_TO_PLATE
                    && s.inputType.HasValue
                    && s.inputType.Value == held.objEnum))
                return false;

            // Skip if already on plate
            if (plate.GetIngredients().Contains(held.objEnum))
                return false;

            plateCounter = counter;
            return true;
        }

        /// <summary>
        /// After a unit task's postcondition is true: regenerate candidates and
        /// atomically assign the next PROCESS/ADD to this agent.
        /// </summary>
        public static bool TryClaimContinuation(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask justCompleted,
            BaseCounter preferredSink,
            out KitchenTask next)
        {
            next = null;
            if (bb == null || agent == null || justCompleted == null || justCompleted.orderId == 0)
                return false;

            bb.SyncItems();
            var fresh = KitchenTaskGenerator.GenerateAllTasks(bb);
            next = FindBestContinuation(bb, agent, justCompleted, preferredSink, fresh);
            if (next == null)
                next = TryBuildHeldAddTask(bb, agent, justCompleted);

            if (next == null)
                return false;

            bool skipFacilityReserve = next.type == TaskType.ADD_TO_PLATE
                || next.type == TaskType.FETCH_PLATE
                || next.type == TaskType.TRASH;

            if (!KitchenTaskGenerator.TryReserveTaskFacilitiesPublic(
                    next, agent, bb, skipFacilityReserve))
                return false;

            BeginOrGetChainSession(agent);
            next.status = "assigned";
            next.assignedAgentId = agent.agentId;
            agent.currentTask = next;
            agent.substate = "moving";

            ClearDelivererForTaskInput(bb, next);
            int claimedId = next.id;
            bb.taskPool = fresh.Where(t => t.id != claimedId).ToList();
            return true;
        }

        /// <summary>
        /// Hard exclusivity while someone <b>holds</b> the input, or an exclusive
        /// deliverer is <b>idle</b> and can take the task. If the deliverer is busy
        /// with unrelated work, do not block other idle agents (avoids stuck ADD).
        /// </summary>
        public static int FindExclusiveHolderAgentId(KitchenBlackboard bb, KitchenTask task)
        {
            if (bb == null || task == null)
                return -1;
            if (task.type != TaskType.PROCESS && task.type != TaskType.ADD_TO_PLATE)
                return -1;

            // KitchenObjEnum.Tomato == 0 — do not treat default as "no type".
            var need = task.itemType;

            foreach (var a in bb.agents)
            {
                if (a?.controller == null)
                    continue;
                var held = a.controller.HeldItem;
                if (held != null
                    && held.objEnum == need
                    && (task.orderId == 0 || held.BoundOrderId == task.orderId))
                    return a.agentId;
            }

            foreach (var item in bb.items)
            {
                if (item.itemType != need)
                    continue;
                if (task.orderId != 0 && item.orderId != task.orderId)
                    continue;
                if (item.exclusiveDelivererAgentId < 0)
                    continue;

                var deliverer = bb.agents.Find(a => a.agentId == item.exclusiveDelivererAgentId);
                // Only exclusive while deliverer is idle and free to take the follow-up.
                if (deliverer != null && deliverer.IsIdle)
                    return deliverer.agentId;
            }

            return -1;
        }

        static RecipeStep FindConsumingProcessStep(
            List<RecipeStep> steps,
            string fetchStepId,
            KitchenObjEnum heldType)
        {
            if (steps == null)
                return null;

            if (!string.IsNullOrEmpty(fetchStepId))
            {
                var dep = steps.FirstOrDefault(s =>
                    s.taskType == TaskType.PROCESS
                    && s.inputType.HasValue
                    && s.inputType.Value == heldType
                    && s.dependsOnStepIds != null
                    && s.dependsOnStepIds.Contains(fetchStepId));
                if (dep != null)
                    return dep;
            }

            return steps.FirstOrDefault(s =>
                s.taskType == TaskType.PROCESS
                && s.inputType.HasValue
                && s.inputType.Value == heldType);
        }

        static KitchenTask FindBestContinuation(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask from,
            BaseCounter preferredSink,
            List<KitchenTask> pool)
        {
            if (pool == null || pool.Count == 0)
                return null;

            var processCandidates = pool
                .Where(t =>
                    t.type == TaskType.PROCESS
                    && t.orderId == from.orderId
                    && InputReadyForProcess(bb, agent, t, preferredSink))
                .ToList();

            if (processCandidates.Count > 0)
            {
                // Prefer PROCESS whose input is already on the sink we just used.
                if (preferredSink != null && preferredSink.HasKitchenObj())
                {
                    var onItem = preferredSink.GetKitchenObj().objEnum;
                    var matchItem = processCandidates.FirstOrDefault(t => t.itemType == onItem);
                    if (matchItem != null)
                        return matchItem;
                }

                if (preferredSink != null)
                {
                    var onSink = processCandidates.FirstOrDefault(t =>
                        t.targetFacility == preferredSink);
                    if (onSink != null)
                        return onSink;
                }

                return processCandidates[0];
            }

            return pool.FirstOrDefault(t =>
                t.type == TaskType.ADD_TO_PLATE
                && t.orderId == from.orderId
                && InputReadyForAdd(agent, t));
        }

        static bool InputReadyForProcess(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask t,
            BaseCounter preferredSink)
        {
            var held = agent.controller?.HeldItem;
            if (held != null
                && held.objEnum == t.itemType
                && (t.orderId == 0 || held.BoundOrderId == t.orderId))
                return true;

            if (preferredSink != null
                && preferredSink.HasKitchenObj()
                && preferredSink.GetKitchenObj().objEnum == t.itemType
                && (t.orderId == 0
                    || preferredSink.GetKitchenObj().BoundOrderId == t.orderId))
                return true;

            if (t.targetFacility != null
                && t.targetFacility.HasKitchenObj()
                && t.targetFacility.GetKitchenObj().objEnum == t.itemType
                && (t.orderId == 0
                    || t.targetFacility.GetKitchenObj().BoundOrderId == t.orderId))
                return true;

            return bb.FindItemsOfType(t.itemType, excludeReserved: false, forOrderId: t.orderId)
                .Any(i => i.exclusiveDelivererAgentId == agent.agentId || !i.IsCarried);
        }

        static bool InputReadyForAdd(AgentState agent, KitchenTask t)
        {
            var held = agent.controller?.HeldItem;
            return held != null
                   && held.objEnum == t.itemType
                   && (t.orderId == 0 || held.BoundOrderId == t.orderId);
        }

        /// <summary>
        /// GenerateAllTasks skips ADD while the ingredient is carried; build one for claim.
        /// </summary>
        static KitchenTask TryBuildHeldAddTask(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask from)
        {
            var held = agent.controller?.HeldItem;
            if (held == null || from.orderId == 0)
                return null;

            var plate = bb.FindPlateForOrder(from.orderId);
            if (plate == null || plate.GetIngredients().Contains(held.objEnum))
                return null;
            if (plate.GetHolder() is not BaseCounter plateCounter)
                return null;

            var recipe = bb.FindRecipeForOrder(from.orderId);
            if (recipe == null || !bb.TryGetRecipeSteps(recipe.recipeName, out var steps))
                return null;

            var addStep = steps.FirstOrDefault(s =>
                s.taskType == TaskType.ADD_TO_PLATE
                && s.inputType.HasValue
                && s.inputType.Value == held.objEnum);
            if (addStep == null)
                return null;

            var task = KitchenTask.Create(
                TaskType.ADD_TO_PLATE,
                $"{recipe.recipeName}: {addStep.label}");
            task.stepId = addStep.id;
            task.orderId = from.orderId;
            task.targetItem = held;
            task.itemType = held.objEnum;
            task.targetFacility = plateCounter;
            task.duration = 0.5f;
            return task;
        }

        static void ClearDelivererForTaskInput(KitchenBlackboard bb, KitchenTask t)
        {
            if (bb?.items == null || t == null)
                return;

            foreach (var item in bb.items)
            {
                if (item.orderId == t.orderId && item.itemType == t.itemType)
                    item.exclusiveDelivererAgentId = -1;
            }
        }
    }
}
