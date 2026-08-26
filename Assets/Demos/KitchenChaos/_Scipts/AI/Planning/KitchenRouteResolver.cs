using System.Collections.Generic;
using System.Linq;
using Kitchen;

namespace Kitchen.AI
{
    /// <summary>
    /// How a carried item should be delivered after FETCH / PROCESS.
    /// Default keeps the legacy clear-counter staging behaviour.
    /// </summary>
    public enum KitchenDeliveryIntent
    {
        Default,
        BypassToProcessFacility,
        BypassToPlateAssembly,
    }

    public struct KitchenRouteDecision
    {
        public KitchenDeliveryIntent Intent;
        public FacilityType? TargetFacilityType;
    }

    /// <summary>
    /// Analyses recipe step chains and resolves delivery routes that can skip
    /// unnecessary clear-counter staging when the next explicit step already
    /// knows the real destination (process facility or order plate counter).
    /// </summary>
    public static class KitchenRouteResolver
    {
        public static bool TryResolveFetchRoute(
            KitchenBlackboard bb,
            RecipeSo order,
            int orderId,
            RecipeStep fetchStep,
            out KitchenRouteDecision route,
            out BaseCounter destination,
            int forAgentId = -1)
        {
            route = default;
            destination = null;
            if (bb == null || order == null || fetchStep == null || orderId == 0)
                return false;
            if (fetchStep.taskType != TaskType.FETCH || !fetchStep.outputType.HasValue)
                return false;
            if (!bb.TryGetRecipeSteps(order.recipeName, out var steps))
                return false;

            var processStep = FindConsumingProcessStep(steps, fetchStep);
            if (processStep == null)
                return false;

            var facility = bb.FindBestAvailableFacility(
                processStep.requiredFacilityType,
                fetchStep.outputType.HasValue
                    ? bb.FindStorageFor(fetchStep.outputType.Value)?.Center ?? default
                    : default,
                forAgentId);
            if (facility?.counter == null || !IsProcessFacility(facility.counter))
                return false;

            route = new KitchenRouteDecision
            {
                Intent = KitchenDeliveryIntent.BypassToProcessFacility,
                TargetFacilityType = processStep.requiredFacilityType,
            };
            destination = facility.counter;
            return true;
        }

        public static bool TryResolveProcessOutputRoute(
            KitchenBlackboard bb,
            RecipeSo order,
            int orderId,
            RecipeStep processStep,
            out KitchenRouteDecision route,
            out BaseCounter destination)
        {
            route = default;
            destination = null;
            if (bb == null || order == null || processStep == null || orderId == 0)
                return false;
            if (processStep.taskType != TaskType.PROCESS || !processStep.outputType.HasValue)
                return false;
            if (!bb.TryGetRecipeSteps(order.recipeName, out var steps))
                return false;

            var addStep = FindConsumingAddStep(steps, processStep);
            if (addStep == null)
                return false;

            var plate = bb.FindPlateForOrder(orderId);
            if (plate == null)
                return false;
            if (plate.GetHolder() is not BaseCounter plateCounter || plateCounter == null)
                return false;

            route = new KitchenRouteDecision
            {
                Intent = KitchenDeliveryIntent.BypassToPlateAssembly,
            };
            destination = plateCounter;
            return true;
        }

        public static bool TryResolveFetchRouteForTask(
            KitchenBlackboard bb,
            KitchenTask task,
            out KitchenRouteDecision route,
            out BaseCounter destination,
            int forAgentId = -1)
        {
            route = default;
            destination = null;
            if (task == null || task.type != TaskType.FETCH || task.orderId == 0)
                return false;

            var recipe = bb?.FindRecipeForOrder(task.orderId);
            if (recipe == null || !bb.TryGetRecipeStep(recipe.recipeName, task.stepId, out var step))
                return false;

            return TryResolveFetchRoute(
                bb,
                recipe,
                task.orderId,
                step,
                out route,
                out destination,
                forAgentId);
        }

        public static bool TryResolveProcessOutputRouteForTask(
            KitchenBlackboard bb,
            KitchenTask task,
            out KitchenRouteDecision route,
            out BaseCounter destination)
        {
            route = default;
            destination = null;
            if (task == null || task.type != TaskType.PROCESS || task.orderId == 0)
                return false;

            var recipe = bb?.FindRecipeForOrder(task.orderId);
            if (recipe == null || !bb.TryGetRecipeStep(recipe.recipeName, task.stepId, out var step))
                return false;

            return TryResolveProcessOutputRoute(bb, recipe, task.orderId, step, out route, out destination);
        }

        private static RecipeStep FindConsumingProcessStep(
            IReadOnlyList<RecipeStep> steps,
            RecipeStep fetchStep)
        {
            if (fetchStep.outputType == null)
                return null;

            foreach (var step in steps)
            {
                if (step == null || step.taskType != TaskType.PROCESS)
                    continue;
                if (step.inputType != fetchStep.outputType)
                    continue;
                if (step.dependsOnStepIds != null && step.dependsOnStepIds.Contains(fetchStep.id))
                    return step;
            }

            int fetchIdx = -1;
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] == fetchStep)
                {
                    fetchIdx = i;
                    break;
                }
            }

            if (fetchIdx < 0)
                return null;

            for (int i = fetchIdx + 1; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step == null)
                    continue;
                if (step.taskType == TaskType.FETCH_PLATE || step.taskType == TaskType.ADD_TO_PLATE)
                    break;
                if (step.taskType == TaskType.PROCESS && step.inputType == fetchStep.outputType)
                    return step;
            }

            return null;
        }

        private static RecipeStep FindConsumingAddStep(
            IReadOnlyList<RecipeStep> steps,
            RecipeStep processStep)
        {
            if (processStep.outputType == null)
                return null;

            foreach (var step in steps)
            {
                if (step == null || step.taskType != TaskType.ADD_TO_PLATE)
                    continue;
                if (step.inputType != processStep.outputType)
                    continue;
                if (step.dependsOnStepIds != null && step.dependsOnStepIds.Contains(processStep.id))
                    return step;
            }

            return steps.FirstOrDefault(step =>
                step != null
                && step.taskType == TaskType.ADD_TO_PLATE
                && step.inputType == processStep.outputType);
        }

        private static bool IsProcessFacility(BaseCounter counter)
        {
            return counter is CuttingCounter
                   || counter is StoveCounter
                   || counter is TimedFacilityCounter;
        }
    }
}
