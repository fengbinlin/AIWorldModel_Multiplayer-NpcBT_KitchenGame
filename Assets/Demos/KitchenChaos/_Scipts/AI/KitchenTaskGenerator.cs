using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kitchen;

namespace Kitchen.AI
{
    /// <summary>
    /// Generates exact order-bound tasks and assigns them in task-panel order.
    /// Pure logic — no MonoBehaviour. Called by KitchenAIManager each scheduling tick.
    ///
    /// This is a C# translation of the HTML simulation's core scheduling algorithm:
    ///   1. Generate all feasible tasks from current blackboard state
    ///   2. Score every (agent × task) pair across 8 dimensions
    ///   3. Greedy-assign: sort by score descending, reserve facilities/items, dispatch
    ///   4. Force-serve: idle agent + completed dish → immediate SERVE assignment
    /// </summary>
    public static class KitchenTaskGenerator
    {
        #region Task Generation

        /// <summary>
        /// Generate all candidate tasks from the current blackboard state.
        /// Returns a list of unassigned KitchenTasks.
        /// </summary>
        public static List<KitchenTask> GenerateAllTasks(KitchenBlackboard bb)
        {
            var tasks = new List<KitchenTask>();
            int skippedCompleted = 0, skippedActive = 0, skippedNoStorage = 0, skippedNoFacility = 0;

            // === Order-bound tasks ===
            // Use index-based iteration: multiple orders of the same recipe type
            // share the same RecipeSo instance, so we need independent order IDs
            // from the parallel activeOrderIds list.
            for (int orderIdx = 0; orderIdx < bb.activeOrders.Count; orderIdx++)
            {
                var order = bb.activeOrders[orderIdx];
                int orderId = bb.activeOrderIds[orderIdx];

                if (!bb.recipeStepChains.TryGetValue(order.recipeName, out var steps))
                {
                    Debug.LogWarning($"[GenTasks] No step chain for recipe: {order.recipeName}");
                    continue;
                }

                // Pure world-state task generation — no completedStepKeys.
                // Each step checks: "Does the output already exist?" and "Do inputs exist?"
                // The todolist is global — every cycle re-evaluates from scratch.
                foreach (var step in steps)
                {
                    bb.EnsureOrderPlan(orderId, order);
                    if (bb.GetStepState(orderId, step.id) == "completed")
                    {
                        skippedCompleted++;
                        continue;
                    }
                    if (!bb.AreStepDependenciesSatisfied(orderId, step))
                        continue;
                    // Don't duplicate an active task for this step+order
                    bool alreadyActive = bb.agents.Any(a =>
                        a.currentTask != null &&
                        a.currentTask.stepId == step.id &&
                        a.currentTask.orderId == orderId &&
                        a.currentTask.status != "completed");
                    if (alreadyActive) { skippedActive++; continue; }

                    // Output checks are exact-order only.
                    if (step.taskType == TaskType.FETCH && step.outputType.HasValue)
                    {
                        bool existsForOrder = bb.FindItemsOfType(
                                step.outputType.Value,
                                excludeReserved: false,
                                forOrderId: orderId)
                            .Any();
                        if (existsForOrder) { skippedCompleted++; continue; }
                    }
                    if (step.taskType == TaskType.FETCH_PLATE)
                    {
                        if (bb.FindPlateForOrder(orderId) != null) { skippedCompleted++; continue; }
                    }
                    if (step.taskType == TaskType.PROCESS && step.outputType.HasValue)
                    {
                        bool outputExists = bb.FindItemsOfType(
                                step.outputType.Value,
                                excludeReserved: false,
                                forOrderId: orderId)
                            .Any()
                            || OrderPlateContains(bb, orderId, step.outputType.Value);
                        if (outputExists) { skippedCompleted++; continue; }
                    }
                    if (step.taskType == TaskType.ADD_TO_PLATE)
                    {
                        var plate = bb.FindPlateForOrder(orderId);
                        // Assembled / finished dish already on plate — skip further adds.
                        if (plate != null && plate.TryGetDeliverableItem(out var held))
                        {
                            if (held == order.requiredItem || held == step.inputType)
                                { skippedCompleted++; continue; }
                            // e.g. PizzaUnbaked / TomatoSalad after assembly, before oven/blender
                            if (PlateAssemblyMatcher.FindAssemblyProducing(held) != null)
                                { skippedCompleted++; continue; }
                        }
                        if (plate != null && step.inputType.HasValue
                            && plate.GetIngredients().Contains(step.inputType.Value))
                            { skippedCompleted++; continue; }
                    }

                    // === INPUT EXISTENCE CHECK ===
                    if (step.taskType == TaskType.PROCESS && step.inputType.HasValue)
                    {
                        var inputs = bb.FindItemsOfType(step.inputType.Value, excludeReserved: true, forOrderId: orderId);
                        if (inputs.Count == 0
                            && !OrderPlateContains(bb, orderId, step.inputType.Value))
                            continue;
                    }
                    if (step.taskType == TaskType.ADD_TO_PLATE && step.inputType.HasValue)
                    {
                        // Assembly uses ANY available ingredient — plates are shared, don't lock by orderId
                        var inputs = bb.FindItemsOfType(
                            step.inputType.Value,
                            excludeReserved: true,
                            forOrderId: orderId);
                        if (inputs.Count == 0) continue;
                    }

                    switch (step.taskType)
                    {
                        case TaskType.FETCH:
                            GenerateFetchTasks(tasks, bb, order, orderId, step, ref skippedNoStorage);
                            break;
                        case TaskType.FETCH_PLATE:
                            GenerateFetchPlateTask(tasks, bb, order, orderId, step);
                            break;
                        case TaskType.PROCESS:
                            GenerateProcessTasks(tasks, bb, order, orderId, step, ref skippedNoFacility);
                            break;
                        case TaskType.ADD_TO_PLATE:
                            GenerateAddToPlateTask(tasks, bb, order, orderId, step);
                            break;
                        case TaskType.SERVE:
                            GenerateServeTask(tasks, bb, order, orderId, step, null);
                            break;
                    }
                }
            }

            // === Cleanup tasks: clear burned/waste items blocking facilities ===
            GenerateTrashTasks(tasks, bb);

            foreach (var task in tasks)
                ApplyPlanMetadata(task, bb);

            // Remove duplicates
            tasks = tasks
                .GroupBy(t => $"{t.type}_{t.stepId}_{t.targetItem?.GetHashCode()}")
                .Select(g => g.First())
                .ToList();

            if (tasks.Count == 0 && bb.activeOrders.Count > 0)
            {
                AIDebugLogger.LogWarning("Scheduler", $"0 tasks for {bb.activeOrders.Count} orders! " +
                    $"skippedCompleted={skippedCompleted} skippedActive={skippedActive} " +
                    $"skippedNoStorage={skippedNoStorage} skippedNoFacility={skippedNoFacility}");
            }

            return tasks;
        }

        private static void ApplyPlanMetadata(KitchenTask task, KitchenBlackboard bb)
        {
            if (task == null || bb == null || task.orderId == 0) return;
            var plan = bb.GetOrderPlan(task.orderId);
            var node = plan?.nodes?.Find(n => n.stepId == task.stepId);
            if (node == null) return;

            task.actionType = node.action.type;
            task.objectAId = node.action.objectAId;
            task.objectBId = node.action.objectBId;
            task.targetIsGround = node.action.targetIsGround;
            task.groundPosition = node.action.groundPosition;
            task.dependencyTaskIds = new List<int>(node.dependencyTaskIds);
            task.preconditions = node.preconditions
                .Select(c => c.Clone())
                .ToList();
            task.postconditions = node.postconditions
                .Select(c => c.Clone())
                .ToList();

            // Bind concrete runtime objects to the transfer action whenever
            // the legacy generator has already resolved them.
            if (task.targetItem != null)
                task.objectAId = task.targetItem.RuntimeObjectId;
            if (task.targetFacility != null)
                task.objectBId = task.targetFacility.NetworkObject != null
                    ? task.targetFacility.NetworkObject.NetworkObjectId
                    : 0UL;
        }

        #endregion

        #region Per-Task-Type Generators

        private static void GenerateFetchTasks(List<KitchenTask> tasks, KitchenBlackboard bb,
            RecipeSo order, int orderId, RecipeStep step, ref int skippedNoStorage)
        {
            if (!step.outputType.HasValue) return;

            var ingredient = step.outputType.Value;
            // Prefer exact container; fall back to any storage (AI will spawn the needed type).
            var storage = bb.FindStorageFor(ingredient)
                          ?? bb.facilities.FirstOrDefault(f => f.type == FacilityType.Storage);
            if (storage == null) { skippedNoStorage++; return; }

            if (bb.FindItemsOfType(
                    ingredient,
                    excludeReserved: false,
                    forOrderId: orderId)
                .Any())
                return;

            // Check this order doesn't already have a FETCH in progress for this ingredient.
            // We check orderId so different orders CAN fetch the same ingredient simultaneously
            // (e.g., salad needs Tomato→slice, tomato order needs whole Tomato — no conflict).
            bool alreadyFetching = bb.agents.Any(a =>
                a.currentTask != null &&
                a.currentTask.type == TaskType.FETCH &&
                a.currentTask.outputType == ingredient &&
                a.currentTask.orderId == orderId);
            if (alreadyFetching) return;

            var task = KitchenTask.Create(TaskType.FETCH, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetFacility = storage.counter;
            task.outputType = ingredient;
            task.duration = 1.0f; // 1 second to fetch
            tasks.Add(task);
        }

        private static void GenerateProcessTasks(List<KitchenTask> tasks, KitchenBlackboard bb,
            RecipeSo order, int orderId, RecipeStep step, ref int skippedNoFacility)
        {
            if (!step.outputType.HasValue || !step.inputType.HasValue) return;

            var facilityType = step.requiredFacilityType;

            // Prefer a facility that already has the input (KitchenObj or plate ingredient)
            FacilityState facility = null;
            foreach (var f in bb.facilities)
            {
                if (f.type != facilityType) continue;
                if (f.counter == null || !f.counter.HasKitchenObj()) continue;
                var onFac = f.counter.GetKitchenObj();
                if ((onFac.objEnum == step.inputType.Value
                        && onFac.BoundOrderId == orderId)
                    || (onFac is Plate pl
                        && pl.BoundOrderId == orderId
                        && pl.GetIngredients().Contains(step.inputType.Value)))
                {
                    facility = f;
                    break;
                }
            }

            // Fallback to a physically empty facility only
            if (facility == null)
                facility = bb.BestFreeFacility(facilityType, Vector3.zero);

            if (facility == null)
            {
                // All facilities of this type are busy — do not spawn a PROCESS that
                // sends agents to an occupied cooker (causes park/reassign pacing loops).
                skippedNoFacility++;
                return;
            }

            // Double-check: never target a counter that already holds a foreign item
            if (facility.counter.HasKitchenObj())
            {
                var onFac = facility.counter.GetKitchenObj();
                bool ours = (onFac.objEnum == step.inputType.Value && onFac.BoundOrderId == orderId)
                    || (onFac.objEnum == step.outputType.Value && onFac.BoundOrderId == orderId)
                    || (onFac is Plate pl
                        && pl.BoundOrderId == orderId
                        && (pl.GetIngredients().Contains(step.inputType.Value)
                            || pl.GetIngredients().Contains(step.outputType.Value)));
                if (!ours)
                {
                    skippedNoFacility++;
                    return;
                }
            }

            // Input may be a free KitchenObj or sitting as a plate ingredient (assembled dish).
            var inputItems = bb.FindItemsOfType(
                step.inputType.Value,
                excludeReserved: true,
                forOrderId: orderId);
            bool onOrderPlate = OrderPlateContains(bb, orderId, step.inputType.Value);
            if (inputItems.Count == 0 && !onOrderPlate) return;

            if (bb.FindItemsOfType(
                    step.outputType.Value,
                    excludeReserved: false,
                    forOrderId: orderId)
                .Any()
                || OrderPlateContains(bb, orderId, step.outputType.Value))
                return;

            // For PROCESS, the item needs to be at the facility or being carried there
            var itemAtFac = bb.FindItemAtFacility(
                step.inputType.Value,
                facility,
                orderId);
            bool isBeingCarried = bb.agents.Any(a =>
                a.currentTask != null &&
                a.currentTask.type == TaskType.ADD_TO_PLATE &&
                a.currentTask.itemType == step.inputType.Value &&
                a.currentTask.orderId == orderId); // Only if same order, not cross-order

            // Also check if any item of this type exists (even if not at facility)
            if (!isBeingCarried && itemAtFac == null)
            {
                var freeItem = inputItems.FirstOrDefault(i => i.IsAvailable);
                bool plateHasInput = onOrderPlate
                    || FacilityHoldsOrderPlateWith(
                        bb,
                        facility,
                        step.inputType.Value,
                        orderId);
                if (freeItem == null && !plateHasInput) return;
            }

            // Determine duration based on facility type
            float duration;
            if (facilityType == FacilityType.CuttingBoard)
                duration = 2.0f;
            else if (facilityType == FacilityType.FryingPan)
                duration = 3.0f;
            else
                duration = 1.5f;

            var task = KitchenTask.Create(TaskType.PROCESS, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetFacility = facility.counter;
            task.itemType = step.inputType.Value;
            task.outputType = step.outputType.Value;
            task.duration = duration;
            tasks.Add(task);
        }

        private static void GenerateFetchPlateTask(List<KitchenTask> tasks, KitchenBlackboard bb,
            RecipeSo order, int orderId, RecipeStep step)
        {

            // Check if this order already has a plate at its assembly counter
            var existingPlate = bb.FindPlateForOrder(orderId);
            if (existingPlate != null) return;

            // Find PlatesCounter with the most plates available,
            // accounting for agents already en route to fetch plates.
            FacilityState bestPlatesCounter = null;
            int bestAvailable = -1;
            foreach (var f in bb.facilities)
            {
                if (f.type != FacilityType.PlatesCounter) continue;
                var pc = f.counter as PlatesCounter;
                if (pc == null) continue;
                int reserved = bb.agents.Count(a =>
                    a.currentTask != null &&
                    a.currentTask.type == TaskType.FETCH_PLATE &&
                    a.currentTask.targetFacility == f.counter &&
                    a.currentTask.status != "completed" &&
                    a.currentTask.status != "abandoned");
                int available = pc.plateCount - reserved;
                if (available > bestAvailable)
                {
                    bestAvailable = available;
                    bestPlatesCounter = f;
                }
            }
            if (bestPlatesCounter == null || bestAvailable <= 0) return;

            // Find any free ClearCounter as drop target
            var dropTarget = bb.facilities
                .Where(f => f.type == FacilityType.AssemblyTable && f.state == "free")
                .OrderBy(f => Vector3.Distance(f.Center, Vector3.zero))
                .FirstOrDefault();
            if (dropTarget == null) return;

            var task = KitchenTask.Create(TaskType.FETCH_PLATE, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetFacility = bestPlatesCounter.counter; // source: PlatesCounter with most plates
            task.destFacility = dropTarget.counter;       // destination: any free ClearCounter
            task.outputType = KitchenObjEnum.Plate;
            task.duration = 1.0f;
            tasks.Add(task);
        }

        private static void GenerateAddToPlateTask(List<KitchenTask> tasks, KitchenBlackboard bb,
            RecipeSo order, int orderId, RecipeStep step)
        {
            if (!step.inputType.HasValue) return;

            // Find this order's plate on its dedicated assembly counter
            var plateOnCounter = bb.FindPlateForOrder(orderId);
            if (plateOnCounter == null) return;

            // Assembly uses ANY available ingredient — don't filter by orderId
            var ingredient = bb.FindItemsOfType(
                    step.inputType.Value,
                    excludeReserved: true,
                    forOrderId: orderId)
                .FirstOrDefault(i => i.IsAvailable && !i.IsCarried
                    && i.orderId == orderId);
            if (ingredient == null) return;

            // Find which counter holds this order's plate
            BaseCounter plateCounter = null;
            var holder = plateOnCounter.GetHolder();
            if (holder is BaseCounter bc) plateCounter = bc;
            if (plateCounter == null) return;

            var task = KitchenTask.Create(TaskType.ADD_TO_PLATE, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetItem = ingredient.kitchenObj;  // pick up this ingredient
            task.itemType = step.inputType.Value;
            task.targetFacility = plateCounter;       // add to THIS order's plate
            task.duration = 0.5f;
            tasks.Add(task);
        }

        private static void GenerateServeTask(List<KitchenTask> tasks, KitchenBlackboard bb,
            RecipeSo order, int orderId, RecipeStep step, HashSet<string> completedSteps)
        {
            var servingCounter = bb.facilities.FirstOrDefault(f => f.type == FacilityType.ServingCounter);
            if (servingCounter == null) return;

            // Plate must hold exactly the order's required item (after assembly / final process).
            var required = order.requiredItem;

            Plate matchingPlate = bb.FindPlateForOrder(orderId);
            if (matchingPlate != null)
            {
                if (!matchingPlate.TryGetDeliverableItem(out var item) || item != required)
                    matchingPlate = null;
            }
            if (matchingPlate == null)
            {
                var allPlates = Object.FindObjectsOfType<Plate>();
                foreach (var p in allPlates)
                {
                    if (p.BoundOrderId != 0 && p.BoundOrderId != orderId)
                        continue;
                    bool accessible = p.IsFree || p.GetHolder() is BaseCounter;
                    if (!accessible) continue;
                    if (p.TryGetDeliverableItem(out var item) && item == required)
                    {
                        matchingPlate = p;
                        bb.AssignPlateToOrder(orderId, matchingPlate);
                        break;
                    }
                }
            }
            if (matchingPlate == null)
            {
                AIDebugLogger.Log("Scheduler", $"SERVE #{orderId} {order.recipeName}: no plate with [{required}]");
                return;
            }

            bool onCounter = matchingPlate.GetHolder() is BaseCounter;
            if (!matchingPlate.IsFree && !onCounter) return;

            var plateIngredients = matchingPlate.GetIngredients();
            if (plateIngredients.Count == 0) return;
            if (!matchingPlate.TryGetDeliverableItem(out var delivered) || delivered != required) return;

            var task = KitchenTask.Create(TaskType.SERVE, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetFacility = servingCounter.counter;
            task.targetItem = matchingPlate;
            task.itemType = KitchenObjEnum.Plate;
            task.duration = 0.8f;
            tasks.Add(task);
        }

        #if false
        private static void GenerateStockTasks(List<KitchenTask> tasks, KitchenBlackboard bb)
        {
            // Open prep: pre-make intermediate ingredients when idle
            foreach (var kvp in bb.recipeStepChains)
            {
                foreach (var step in kvp.Value)
                {
                    if (step.taskType == TaskType.FETCH && step.outputType.HasValue)
                    {
                        var ingredient = step.outputType.Value;
                        int count = bb.items.Count(i =>
                            i.itemType == ingredient && i.orderId == 0 && i.IsAvailable);
                        if (count < KitchenBlackboard.MAX_OPEN_ITEMS)
                        {
                            var storage = bb.FindStorageFor(ingredient);
                            if (storage == null) continue;

                            bool alreadyDoing = bb.agents.Any(a =>
                                a.currentTask != null &&
                                a.currentTask.type == TaskType.FETCH &&
                                a.currentTask.outputType == ingredient &&
                                a.currentTask.isStockTask);
                            if (alreadyDoing) continue;

                            var task = KitchenTask.Create(TaskType.FETCH, $"备货: {step.label}");
                            task.isStockTask = true;
                            task.targetFacility = storage.counter;
                            task.outputType = ingredient;
                            task.duration = 1.0f;
                            tasks.Add(task);
                        }
                    }
                    else if (step.taskType == TaskType.PROCESS && step.inputType.HasValue && step.outputType.HasValue)
                    {
                        int outCount = bb.items.Count(i =>
                            i.itemType == step.outputType.Value && i.orderId == 0 && i.IsAvailable);
                        if (outCount < KitchenBlackboard.MAX_OPEN_ITEMS)
                        {
                            var inputItems = bb.FindItemsOfType(step.inputType.Value)
                                .Where(i => i.orderId == 0 && i.IsAvailable).ToList();
                            if (inputItems.Count == 0) continue;

                            var facility = bb.BestFreeFacility(step.requiredFacilityType,
                                inputItems[0].Position);
                            if (facility == null) continue;

                            var task = KitchenTask.Create(TaskType.PROCESS,
                                $"备货: {step.label} → {step.outputType.Value}");
                            task.isStockTask = true;
                            task.targetFacility = facility.counter;
                            task.itemType = step.inputType.Value;
                            task.outputType = step.outputType.Value;
                            task.duration = step.requiredFacilityType == FacilityType.CuttingBoard ? 2.0f : 3.0f;
                            tasks.Add(task);
                        }
                    }
                }
            }
        }
        #endif

        /// <summary>
        /// Generate TRASH tasks: take burned/waste items blocking facilities to the TrashCounter.
        /// Only generated when there are idle agents and limited order tasks, to avoid
        /// interfering with normal work. Rate-limited to one cleanup task per cycle.
        /// </summary>
        private static void GenerateTrashTasks(List<KitchenTask> tasks, KitchenBlackboard bb)
        {
            var trashCounter = bb.facilities.FirstOrDefault(f => f.type == FacilityType.TrashCan);
            if (trashCounter == null) return;

            // Always check for burned items on StoveCounters — they block ALL cooking.
            // This runs even when other tasks exist, because a blocked stove prevents
            // PROCESS tasks from executing, which blocks ADD_TO_PLATE → SERVE.
            foreach (var fac in bb.facilities)
            {
                if (fac.type != FacilityType.FryingPan) continue;
                if (!fac.counter.HasKitchenObj()) continue;

                var item = fac.counter.GetKitchenObj();
                bool isBurned = PlateAssemblyMatcher.IsBurnedWaste(item.objEnum);
                if (!isBurned) continue;

                // Skip if another agent is already trashing or processing this item
                bool alreadyDealing = bb.agents.Any(a =>
                    a.currentTask != null &&
                    a.currentTask.targetItem == item);
                if (alreadyDealing) continue;

                var task = KitchenTask.Create(TaskType.TRASH, $"🗑 清理 {item.objEnum} from {fac.counter.name}");
                task.targetItem = item;
                task.itemType = item.objEnum;
                task.targetFacility = trashCounter.counter;
                task.duration = 0.5f;
                task.isCleanupTask = true;
                tasks.Add(task);
                return; // One per cycle to avoid flooding
            }

            // Burned items on ClearCounters: only cleanup when system is congested
            int totalClear = bb.facilities.Count(f => f.type == FacilityType.AssemblyTable);
            int occupiedClear = bb.facilities.Count(f =>
                f.type == FacilityType.AssemblyTable && f.counter.HasKitchenObj());
            if (totalClear > 0 && occupiedClear > totalClear / 2 && tasks.Count < 3)
            {
                foreach (var fac in bb.facilities)
                {
                    if (fac.type != FacilityType.AssemblyTable) continue;
                    if (!fac.counter.HasKitchenObj()) continue;

                    var item = fac.counter.GetKitchenObj();
                    bool isBurned = PlateAssemblyMatcher.IsBurnedWaste(item.objEnum);
                    if (!isBurned) continue;

                    bool alreadyTrashing = bb.agents.Any(a =>
                        a.currentTask != null &&
                        a.currentTask.type == TaskType.TRASH &&
                        a.currentTask.targetItem == item);
                    if (alreadyTrashing) continue;

                    var task = KitchenTask.Create(TaskType.TRASH, $"🗑 清理 {item.objEnum} from {fac.counter.name}");
                    task.targetItem = item;
                    task.itemType = item.objEnum;
                    task.targetFacility = trashCounter.counter;
                    task.duration = 0.5f;
                    task.isCleanupTask = true;
                    tasks.Add(task);
                    return;
                }
            }

            // === Waste plates: plates with ingredients on non-order counters ===
            var activePlates = new HashSet<Plate>(bb.orderPlate.Values);
            int wastePlatesFound = 0;
            foreach (var fac in bb.facilities)
            {
                if (fac.type != FacilityType.AssemblyTable) continue;
                if (!fac.counter.HasKitchenObj()) continue;
                var item = fac.counter.GetKitchenObj();
                if (!(item is Plate wastePlate)) continue;
                if (activePlates.Contains(wastePlate)) continue; // Still belongs to an active order

                var plateIngredients = wastePlate.GetIngredients();
                if (plateIngredients.Count == 0) continue; // Empty plate = just FETCH_PLATE result

                // Found a waste plate — generate TRASH task
                bool alreadyTrashing = bb.agents.Any(a =>
                    a.currentTask != null &&
                    a.currentTask.type == TaskType.TRASH &&
                    a.currentTask.targetItem == wastePlate);
                if (alreadyTrashing) continue;

                var task = KitchenTask.Create(TaskType.TRASH, $"🗑 丢弃废盘 ({string.Join(",", plateIngredients)}) from {fac.counter.name}");
                task.targetItem = wastePlate;
                task.itemType = KitchenObjEnum.Plate;
                task.targetFacility = trashCounter.counter;
                task.duration = 0.5f;
                task.isCleanupTask = true;
                tasks.Add(task);
                wastePlatesFound++;
                if (wastePlatesFound >= 1) break; // One per cycle
            }
        }

        private static bool FacilityHoldsOrderPlateWith(
            KitchenBlackboard bb,
            FacilityState facility,
            KitchenObjEnum type,
            int orderId)
        {
            if (facility?.counter == null || !facility.counter.HasKitchenObj())
                return false;
            return facility.counter.GetKitchenObj() is Plate plate
                   && plate.BoundOrderId == orderId
                   && plate.GetIngredients().Contains(type);
        }

        private static bool OrderPlateContains(
            KitchenBlackboard bb,
            int orderId,
            KitchenObjEnum type)
        {
            var plate = bb.FindPlateForOrder(orderId);
            return plate != null && plate.GetIngredients().Contains(type);
        }

        #endregion

        #if false
        #region Removed Utility Scoring

        /// <summary>
        /// Score a (agent, task) pair across all dimensions.
        /// Mirrors the HTML simulation's scoreTask() function.
        /// </summary>
        public static ScoreDetail ScoreTask(AgentState agent, KitchenTask task, KitchenBlackboard bb)
        {
            var detail = new ScoreDetail();

            // === Distance cost ===
            float distance = 0f;
            Vector3 targetPos = Vector3.zero;

            if ((task.type == TaskType.ADD_TO_PLATE || task.type == TaskType.FETCH_PLATE)
                && task.targetItem != null)
            {
                targetPos = task.targetItem.transform.position;
            }
            else if (task.targetFacility != null)
            {
                targetPos = task.targetFacility.transform.position;
            }

            if (targetPos != Vector3.zero)
            {
                distance = Vector3.Distance(agent.position, targetPos);
                detail.distance = (distance / 100f) * KitchenBlackboard.WEIGHT_DISTANCE;
            }

            // === Facility wait cost ===
            if (task.targetFacility != null)
            {
                var fac = bb.facilities.Find(f => f.counter == task.targetFacility);
                if (fac != null)
                {
                    if (fac.state == "occupied")
                        detail.facilityWait = fac.timer * KitchenBlackboard.WEIGHT_FACILITY_WAIT;
                    else if (fac.state == "reserved")
                        detail.facilityWait = 1.5f * KitchenBlackboard.WEIGHT_FACILITY_WAIT;
                }
            }

            // === Order urgency (time-based) ===
            // Orders waiting longer get exponentially higher priority.
            // This ensures old orders (e.g., salad sitting with ready ingredients)
            // don't get starved by newer orders.
            if (task.orderId != 0)
            {
                int idx = bb.activeOrderIds.IndexOf(task.orderId);
                var order = idx >= 0 ? bb.activeOrders[idx] : null;
                if (order != null)
                {
                    float waitTime = 0f;
                    if (bb.orderEntryTimes.TryGetValue(task.orderId, out float entryTime))
                        waitTime = Mathf.Max(0f, Time.time - entryTime);

                    // Linear urgency with cap: each second of waiting adds 1.0 to urgency.
                    // Capped at 30 to prevent infinite runaway but still strongly prioritize old orders.
                    // At 0s: 3.75, at 10s: 18.75, at 30s: 48.75
                    float waitBonus = Mathf.Min(waitTime * 1.0f, 30f);
                    detail.orderUrgency = (2.5f + waitBonus) * KitchenBlackboard.WEIGHT_ORDER_URGENCY;
                }
            }

            // === Unlock value (progress-based) ===
            if (task.stepId != null)
            {
                foreach (var kvp in bb.recipeStepChains)
                {
                    var steps = kvp.Value;
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (steps[i].id == task.stepId)
                        {
                            float progress = (i + 1f) / steps.Count;
                            detail.unlockValue = progress * 5.0f * KitchenBlackboard.WEIGHT_UNLOCK_VALUE;
                            break;
                        }
                    }
                }
            }

            // === Stock base value ===
            if (task.isStockTask)
                detail.stockBase = KitchenBlackboard.WEIGHT_STOCK_BASE;

            // === Role bonus ===
            if (agent.roleCounts.TryGetValue(task.type, out int similarCount))
                detail.roleBonus = Mathf.Min(similarCount * KitchenBlackboard.WEIGHT_ROLE_BONUS, 0.25f);

            // === Pickup source bonus/penalty ===
            if ((task.type == TaskType.ADD_TO_PLATE || task.type == TaskType.PROCESS)
                && task.targetItem != null)
            {
                var itemState = bb.items.Find(i => i.kitchenObj == task.targetItem);
                if (itemState != null)
                {
                    if (bb.IsItemAtStorage(itemState))
                    {
                        detail.freshPickBonus = KitchenBlackboard.WEIGHT_FRESH_PICK;
                    }
                    else if (bb.IsItemAtNonStorageFacility(itemState))
                    {
                        detail.stalePickPenalty = KitchenBlackboard.WEIGHT_STALE_PICK;
                    }
                }
            }

            // === Assembly / serve bonus ===
            if (task.type == TaskType.ADD_TO_PLATE)
                detail.unlockValue += 3.0f;
            if (task.type == TaskType.SERVE)
                detail.unlockValue += 10.0f;
            // PROCESS with output already ready on stove — grab before it burns
            if (task.type == TaskType.PROCESS && task.targetFacility != null)
            {
                var fac = bb.facilities.Find(f => f.counter == task.targetFacility);
                if (fac != null && fac.type == FacilityType.FryingPan
                    && task.targetFacility.HasKitchenObj()
                    && task.targetFacility.GetKitchenObj().objEnum == task.outputType)
                    detail.unlockValue += 5.0f;
            }

            // === Total ===
            detail.total = detail.distance + detail.facilityWait + detail.orderUrgency +
                           detail.unlockValue + detail.roleBonus + detail.stockBase +
                           detail.freshPickBonus + detail.stalePickPenalty;

            return detail;
        }

        #endregion
        #endif

        #region Task Panel Assignment

        /// <summary>
        /// Assign the best (agent, task) pairs using greedy matching.
        /// Returns the list of assignments made.
        /// </summary>
        public class Assignment
        {
            public AgentState agent;
            public KitchenTask task;
        }

        /// <summary>
        /// Deterministic assignment in task-panel order. No utility score,
        /// distance ranking, urgency ranking, role bonus, or force-serve path.
        /// </summary>
        public static List<Assignment> AssignInPanelOrder(
            List<AgentState> idleAgents,
            List<KitchenTask> taskPool,
            KitchenBlackboard bb)
        {
            var assignments = new List<Assignment>();
            if (idleAgents.Count == 0 || taskPool.Count == 0)
                return assignments;

            var activeTaskIds = new HashSet<int>(
                bb.agents
                    .Where(a => a.currentTask != null
                        && a.currentTask.status != "completed"
                        && a.currentTask.status != "abandoned")
                    .Select(a => a.currentTask.id));

            // Ready-to-serve dishes must go first. Keep the original panel
            // order within each priority group so preparation remains
            // deterministic while completed orders are not starved.
            var availableTasks = taskPool
                .Select((task, index) => new { task, index })
                .Where(x => !activeTaskIds.Contains(x.task.id))
                .OrderByDescending(x => x.task.type == TaskType.SERVE)
                .ThenBy(x => x.index)
                .Select(x => x.task)
                .ToList();
            var remainingAgents = new Queue<AgentState>(idleAgents);
            var assignedTaskIds = new HashSet<int>();

            foreach (var task in availableTasks)
            {
                if (remainingAgents.Count == 0)
                    break;

                if (task.targetItem != null)
                {
                    var itemState = bb.items.Find(i => i.kitchenObj == task.targetItem);
                    if (itemState == null
                        || itemState.orderId != task.orderId
                        || (itemState.reservedByTask >= 0
                            && itemState.reservedByTask != task.id))
                    {
                        if (task.type == TaskType.SERVE)
                            continue;
                        break;
                    }
                }

                var agent = remainingAgents.Peek();

                bool skipFacilityReserve = task.type == TaskType.FETCH_PLATE
                    || task.type == TaskType.ADD_TO_PLATE
                    || task.type == TaskType.TRASH;
                if (task.targetFacility != null && !skipFacilityReserve)
                {
                    var facility = bb.facilities.Find(
                        f => f.counter == task.targetFacility);
                    if (facility != null
                        && facility.state == "reserved"
                        && facility.reservedByAgent != agent.agentId
                        && facility.reservedByAgent != -1)
                    {
                        if (task.type == TaskType.SERVE)
                            continue;
                        break;
                    }

                    if (facility != null)
                    {
                        facility.state = "reserved";
                        facility.reservedByAgent = agent.agentId;
                    }
                }

                if (task.targetItem != null)
                {
                    var itemState = bb.items.Find(i => i.kitchenObj == task.targetItem);
                    if (itemState != null)
                    {
                        itemState.reservedByTask = task.id;
                        task.reservedItemIds.Add(itemState.id);
                    }
                }

                task.status = "assigned";
                task.assignedAgentId = agent.agentId;
                remainingAgents.Dequeue();
                agent.currentTask = task;
                agent.substate = "moving";

                assignedTaskIds.Add(task.id);
                assignments.Add(new Assignment
                {
                    agent = agent,
                    task = task,
                });
            }

            bb.taskPool = availableTasks
                .Where(t => !assignedTaskIds.Contains(t.id))
                .ToList();
            return assignments;
        }

        #if false
        public static List<Assignment> GreedyAssign(List<AgentState> idleAgents,
            List<KitchenTask> taskPool, KitchenBlackboard bb)
        {
            var assignments = new List<Assignment>();

            if (idleAgents.Count == 0 || taskPool.Count == 0)
                return assignments;

            // Remove tasks already assigned or executing
            var activeTaskIds = new HashSet<int>();
            foreach (var a in bb.agents)
            {
                if (a.currentTask != null && a.currentTask.status != "completed")
                    activeTaskIds.Add(a.currentTask.id);
            }
            var availableTasks = taskPool.Where(t => !activeTaskIds.Contains(t.id)).ToList();
            if (availableTasks.Count == 0) return assignments;

            // Score all (agent, task) pairs
            var scored = new List<(AgentState agent, KitchenTask task, ScoreDetail detail)>();
            int skippedItemReserved = 0;
            foreach (var agent in idleAgents)
            {
                agent.position = agent.controller != null
                    ? agent.controller.transform.position
                    : agent.position;

                foreach (var task in availableTasks)
                {
                    // Skip if task's item is reserved by another task
                    if (task.targetItem != null)
                    {
                        var itemState = bb.items.Find(i => i.kitchenObj == task.targetItem);
                        if (itemState != null && itemState.reservedByTask >= 0 &&
                            itemState.reservedByTask != task.id)
                        {
                            skippedItemReserved++;
                            continue;
                        }
                    }

                    // NOTE: Do NOT filter by facility "occupied" state.
                    // "Occupied" = has item, which is NORMAL for ADD_TO_PLATE/PLATE/PROCESS.
                    // The AI handles occupied facilities at runtime via counter.Interact().
                    // Only block if reserved by another agent (handled later).

                    var detail = ScoreTask(agent, task, bb);
                    scored.Add((agent, task, detail));
                }
            }

            if (scored.Count == 0 && availableTasks.Count > 0)
            {
                Debug.LogWarning($"[GreedyAssign] All {availableTasks.Count} tasks filtered out! " +
                    $"skippedItemReserved={skippedItemReserved}");
            }

            // Sort by score descending
            scored.Sort((a, b) => b.detail.total.CompareTo(a.detail.total));

            // Greedy assignment
            var assignedAgents = new HashSet<int>();
            var assignedTasks = new HashSet<int>();

            foreach (var (agent, task, detail) in scored)
            {
                if (assignedAgents.Contains(agent.agentId)) continue;
                if (assignedTasks.Contains(task.id)) continue;

                // Double-check: facility is not reserved by another agent
                // reservedByAgent == -1 means order-level reservation (assembly counter) — any agent can use
                if (task.targetFacility != null)
                {
                    var fac = bb.facilities.Find(f => f.counter == task.targetFacility);
                    if (fac != null && fac.state == "reserved"
                        && fac.reservedByAgent != agent.agentId
                        && fac.reservedByAgent != -1)
                    {
                        continue;
                    }
                }

                // Reserve facility (skip for tasks that just drop/pass through)
                bool skipFacilityReserve = task.type == TaskType.FETCH_PLATE
                    || task.type == TaskType.ADD_TO_PLATE
                    || task.type == TaskType.TRASH;
                if (task.targetFacility != null && !skipFacilityReserve)
                {
                    var fac = bb.facilities.Find(f => f.counter == task.targetFacility);
                    if (fac != null)
                    {
                        if (fac.state == "reserved" && fac.reservedByAgent != agent.agentId
                            && fac.reservedByAgent != -1)
                            continue;
                        // Occupied is OK for PROCESS tasks (input item is already on it)
                        // Occupied is OK for FETCH tasks (the AI will handle delivery at runtime)
                        // Only block if occupied by a DIFFERENT task's target
                        // → just allow it; the AI controller checks HasKitchenObj at runtime
                        fac.state = "reserved";
                        fac.reservedByAgent = agent.agentId;
                    }
                }

                // Reserve target item
                if (task.targetItem != null)
                {
                    var itemState = bb.items.Find(i => i.kitchenObj == task.targetItem);
                    if (itemState != null && itemState.reservedByTask < 0)
                    {
                        itemState.reservedByTask = task.id;
                        task.reservedItemIds.Add(itemState.id);
                    }
                }

                // Assign
                task.status = "assigned";
                task.assignedAgentId = agent.agentId;
                task.score = detail.total;
                task.scoreDetail = detail;
                agent.currentTask = task;
                agent.substate = "moving";

                assignedAgents.Add(agent.agentId);
                assignedTasks.Add(task.id);
                assignments.Add(new Assignment { agent = agent, task = task, scoreDetail = detail });

                Debug.Log($"[Assign] {agent.agentId}:{agent.controller?.name} ← {task.label} " +
                          $"score={detail.total:F2} ({detail})");
            }

            // === Force-serve: idle agents + unserved finished dishes ===
            var stillIdle = idleAgents.Where(a => !assignedAgents.Contains(a.agentId)).ToList();
            if (stillIdle.Count > 0)
            {
                var finishedTypes = new[] { KitchenObjEnum.Plate }; // Plates with ingredients
                foreach (var fType in finishedTypes)
                {
                    var unserved = bb.items.Where(i =>
                        i.itemType == fType &&
                        i.orderId != 0 &&
                        i.IsAvailable).ToList();

                    foreach (var dish in unserved)
                    {
                        if (stillIdle.Count == 0) break;

                        // Find nearest idle agent
                        stillIdle.Sort((a, b) =>
                            Vector3.Distance(a.position, dish.Position)
                                .CompareTo(Vector3.Distance(b.position, dish.Position)));
                        var agent = stillIdle[0];
                        stillIdle.RemoveAt(0);

                        var servingCounter = bb.facilities
                            .FirstOrDefault(f => f.type == FacilityType.ServingCounter);
                        if (servingCounter == null || servingCounter.state == "occupied") continue;

                        var serveTask = KitchenTask.Create(TaskType.SERVE,
                            $"🚀 强制出餐: {dish.itemType} #{dish.orderId}");
                        serveTask.orderId = dish.orderId;
                        serveTask.targetFacility = servingCounter.counter;
                        serveTask.targetItem = dish.kitchenObj;
                        serveTask.itemType = dish.itemType;
                        serveTask.duration = 0.8f;
                        serveTask.status = "assigned";
                        serveTask.assignedAgentId = agent.agentId;
                        serveTask.score = 99f;
                        serveTask.scoreDetail = new ScoreDetail
                        {
                            orderUrgency = 99f,
                            total = 99f
                        };

                        // Reserve item
                        dish.reservedByTask = serveTask.id;
                        serveTask.reservedItemIds.Add(dish.id);

                        // Reserve facility
                        if (servingCounter.state == "free" ||
                            (servingCounter.state == "reserved" && servingCounter.reservedByAgent == agent.agentId))
                        {
                            servingCounter.state = "reserved";
                            servingCounter.reservedByAgent = agent.agentId;
                        }

                        agent.currentTask = serveTask;
                        agent.substate = "moving";
                        assignedAgents.Add(agent.agentId);

                        Debug.Log($"[ForceServe] {agent.agentId}:{agent.controller?.name} ← {serveTask.label}");
                    }

                    if (stillIdle.Count == 0) break;
                }
            }

            // Remove assigned tasks from pool
            bb.taskPool = availableTasks.Where(t => !assignedTasks.Contains(t.id)).ToList();

            return assignments;
        }
        #endif

        /// <summary>
        /// Release all reservations held by a task.
        /// </summary>
        public static void ReleaseReservations(KitchenTask task, KitchenBlackboard bb)
        {
            if (task.targetFacility != null)
            {
                var fac = bb.facilities.Find(f => f.counter == task.targetFacility);
                if (fac != null && fac.reservedByAgent == task.assignedAgentId)
                {
                    fac.state = "free";
                    fac.reservedByAgent = -1;
                }
            }

            foreach (var itemId in task.reservedItemIds)
            {
                var item = bb.items.Find(i => i.id == itemId);
                if (item != null && item.reservedByTask == task.id)
                    item.reservedByTask = -1;
            }
            task.reservedItemIds.Clear();
        }

        #endregion
    }
}
