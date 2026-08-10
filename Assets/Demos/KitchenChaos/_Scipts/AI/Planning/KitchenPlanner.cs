using System.Collections.Generic;
using System.Linq;
using Kitchen;

namespace Kitchen.AI
{
    /// <summary>
    /// Converts the existing reverse recipe chain into an order-scoped plan.
    /// This is deliberately pure data construction; world-state validation is
    /// handled by KitchenBlackboard so the plan can be regenerated safely.
    /// </summary>
    public static class KitchenPlanner
    {
        private static int _nextPlanTaskId = 1000000;

        public static KitchenPlan BuildPlan(
            KitchenBlackboard blackboard,
            int orderId,
            RecipeSo recipe)
        {
            var plan = new KitchenPlan
            {
                orderId = orderId,
                recipeName = recipe?.recipeName ?? string.Empty,
                requiredItem = recipe != null ? recipe.requiredItem : default,
            };

            if (blackboard == null || recipe == null)
                return plan;

            if (!blackboard.recipeStepChains.TryGetValue(recipe.recipeName, out var steps))
                return plan;

            var stepToTask = new Dictionary<string, KitchenPlanNode>();
            foreach (var step in steps)
            {
                if (step == null) continue;

                var node = new KitchenPlanNode
                {
                    taskId = _nextPlanTaskId++,
                    orderId = orderId,
                    stepId = step.id,
                    label = step.label,
                    legacyTaskType = step.taskType,
                    action = BuildAction(step),
                };

                foreach (var dependencyId in step.dependsOnStepIds ?? Enumerable.Empty<string>())
                {
                    if (stepToTask.TryGetValue(dependencyId, out var dependency))
                    {
                        node.dependencyTaskIds.Add(dependency.taskId);
                        node.preconditions.Add(new KitchenConditionSpec
                        {
                            type = KitchenConditionType.DependencyTasksCompleted,
                            orderId = orderId,
                            referencedTaskId = dependency.taskId,
                        });
                    }
                }

                if (step.inputType.HasValue)
                {
                    node.preconditions.Add(new KitchenConditionSpec
                    {
                        type = KitchenConditionType.ObjectExists,
                        orderId = orderId,
                        itemType = step.inputType.Value,
                    });
                }

                if (step.taskType == TaskType.SERVE)
                {
                    node.preconditions.Add(new KitchenConditionSpec
                    {
                        type = KitchenConditionType.PlateMatchesOrder,
                        orderId = orderId,
                        itemType = recipe.requiredItem,
                    });
                }

                if (step.taskType == TaskType.ADD_TO_PLATE && step.inputType.HasValue)
                {
                    node.postconditions.Add(new KitchenConditionSpec
                    {
                        type = KitchenConditionType.PlateContainsItem,
                        orderId = orderId,
                        itemType = step.inputType.Value,
                    });
                }
                else if (step.taskType == TaskType.PROCESS && step.outputType.HasValue)
                {
                    node.postconditions.Add(new KitchenConditionSpec
                    {
                        type = KitchenConditionType.ProcessOutputReady,
                        orderId = orderId,
                        itemType = step.outputType.Value,
                    });
                }

                plan.nodes.Add(node);
                stepToTask[step.id] = node;
            }

            return plan;
        }

        private static KitchenActionSpec BuildAction(RecipeStep step)
        {
            var action = new KitchenActionSpec
            {
                itemType = step.inputType ?? step.outputType ?? default,
                outputType = step.outputType ?? step.inputType ?? default,
                facilityType = step.requiredFacilityType,
            };

            switch (step.taskType)
            {
                case TaskType.FETCH:
                    action.type = KitchenActionType.SpawnFromStorage;
                    break;
                case TaskType.PROCESS:
                    action.type = KitchenActionType.Process;
                    break;
                case TaskType.FETCH_PLATE:
                    action.type = KitchenActionType.Transfer;
                    action.itemType = KitchenObjEnum.Plate;
                    break;
                case TaskType.ADD_TO_PLATE:
                    action.type = KitchenActionType.AddToPlate;
                    break;
                case TaskType.SERVE:
                    action.type = KitchenActionType.Serve;
                    action.itemType = KitchenObjEnum.Plate;
                    break;
                case TaskType.TRASH:
                    action.type = KitchenActionType.Trash;
                    break;
                default:
                    action.type = KitchenActionType.None;
                    break;
            }

            return action;
        }
    }
}
