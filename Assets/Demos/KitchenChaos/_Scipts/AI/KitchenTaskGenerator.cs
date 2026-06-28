using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// Generates candidate tasks, scores them, and performs greedy assignment.
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
                    // Don't duplicate an active task for this step+order
                    bool alreadyActive = bb.agents.Any(a =>
                        a.currentTask != null &&
                        a.currentTask.stepId == step.id &&
                        a.currentTask.orderId == orderId &&
                        a.currentTask.status != "completed");
                    if (alreadyActive) { skippedActive++; continue; }

                    // === OUTPUT-EXISTS CHECK: count ALL items regardless of orderId ===
                    // (processed items are public stock once produced, not tied to their original order)
                    if (step.taskType == TaskType.FETCH && step.outputType.HasValue)
                    {
                        bool fetchNeeded = true;
                        int rawAvailable = bb.FindItemsOfType(step.outputType.Value, excludeReserved: true) // NO forOrderId
                            .Count(i => !i.IsCarried && (i.kitchenObj.IsFree || i.kitchenObj.GetHolder() is BaseCounter));
                        int rawNeeded = CountOrdersNeeding(bb, step.outputType.Value);
                        if (rawNeeded > 0 && rawAvailable >= rawNeeded) { fetchNeeded = false; }
                        else
                        {
                            foreach (var s in steps)
                            {
                                if (s.taskType == TaskType.PROCESS && s.inputType == step.outputType && s.outputType.HasValue)
                                {
                                    int processedAvail = bb.FindItemsOfType(s.outputType.Value, excludeReserved: true) // NO forOrderId
                                        .Count(i => !i.IsCarried && (i.kitchenObj.IsFree || i.kitchenObj.GetHolder() is BaseCounter));
                                    int processedNeeded = CountOrdersNeeding(bb, s.outputType.Value);
                                    if (processedNeeded > 0 && processedAvail >= processedNeeded)
                                        { fetchNeeded = false; break; }
                                }
                            }
                        }
                        if (!fetchNeeded) { skippedCompleted++; continue; }
                    }
                    if (step.taskType == TaskType.FETCH_PLATE)
                    {
                        if (bb.FindPlateForOrder(orderId) != null) { skippedCompleted++; continue; }
                    }
                    if (step.taskType == TaskType.PROCESS && step.outputType.HasValue)
                    {
                        int available = bb.FindItemsOfType(step.outputType.Value, excludeReserved: true)
                            .Count(i => !i.IsCarried && (i.kitchenObj.IsFree || i.kitchenObj.GetHolder() is BaseCounter));
                        int needed = CountOrdersNeeding(bb, step.outputType.Value);
                        if (needed > 0 && available >= needed) { skippedCompleted++; continue; }
                    }
                    if (step.taskType == TaskType.ADD_TO_PLATE)
                    {
                        var plate = bb.FindPlateForOrder(orderId);
                        if (plate != null && plate.GetIngredients().Contains(step.inputType!.Value))
                            { skippedCompleted++; continue; } // Already on the plate
                    }

                    // === INPUT EXISTENCE CHECK ===
                    if (step.taskType == TaskType.PROCESS && step.inputType.HasValue)
                    {
                        var inputs = bb.FindItemsOfType(step.inputType.Value, excludeReserved: true, forOrderId: orderId);
                        if (inputs.Count == 0) continue;
                    }
                    if (step.taskType == TaskType.ADD_TO_PLATE && step.inputType.HasValue)
                    {
                        // Assembly uses ANY available ingredient — plates are shared, don't lock by orderId
                        var inputs = bb.FindItemsOfType(step.inputType.Value, excludeReserved: true);
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

            // === Open preparation (stock) tasks ===
            // DISABLED: stock tasks flood the system and cause deadlocks.
            // TODO: re-enable with proper rate-limiting and step tracking.
            // GenerateStockTasks(tasks, bb);

            // === Cleanup tasks: clear burned/waste items blocking facilities ===
            GenerateTrashTasks(tasks, bb);

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

        #endregion

        #region Per-Task-Type Generators

        private static void GenerateFetchTasks(List<KitchenTask> tasks, KitchenBlackboard bb,
            RecipeSo order, int orderId, RecipeStep step, ref int skippedNoStorage)
        {
            if (!step.outputType.HasValue) return;

            var ingredient = step.outputType.Value;
            var storage = bb.FindStorageFor(ingredient);
            if (storage == null) { skippedNoStorage++; return; }

            // Count items already in world AND active FETCH/PROCESS tasks in flight.
            // This prevents overproduction when agents are still en route.
            int totalAvail = bb.items.Count(i =>
                i.itemType == ingredient && !i.IsCarried && i.kitchenObj != null);
            int activeFetchCount = bb.agents.Count(a =>
                a.currentTask != null &&
                a.currentTask.outputType == ingredient &&
                (a.currentTask.type == TaskType.FETCH || a.currentTask.type == TaskType.PROCESS) &&
                a.currentTask.status != "completed" && a.currentTask.status != "abandoned");
            int totalNeeded = CountOrdersNeeding(bb, ingredient);
            if (totalNeeded > 0 && totalAvail + activeFetchCount >= totalNeeded) return;

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

            // Prefer a facility that already has the input item on it (even if occupied)
            FacilityState facility = null;
            foreach (var f in bb.facilities)
            {
                if (f.type != facilityType) continue;
                if (f.counter == null) continue;
                if (f.counter.HasKitchenObj() &&
                    f.counter.GetKitchenObj().objEnum == step.inputType.Value)
                {
                    facility = f;
                    break;
                }
            }

            // Fallback to best free facility
            if (facility == null)
                facility = bb.BestFreeFacility(facilityType, Vector3.zero);

            if (facility == null)
            {
                skippedNoFacility++;
                return;
            }

            // Check if input item exists
            var inputItems = bb.FindItemsOfType(step.inputType.Value);
            if (inputItems.Count == 0) return;

            // Don't produce more than ALL orders need combined.
            // Include active FETCH/PROCESS to avoid overproduction during transit.
            int totalAvail = bb.items.Count(i =>
                i.itemType == step.outputType.Value && !i.IsCarried && i.kitchenObj != null);
            int activeProdCount = bb.agents.Count(a =>
                a.currentTask != null &&
                a.currentTask.outputType == step.outputType.Value &&
                (a.currentTask.type == TaskType.FETCH || a.currentTask.type == TaskType.PROCESS) &&
                a.currentTask.status != "completed" && a.currentTask.status != "abandoned");
            int totalNeeded = CountOrdersNeeding(bb, step.outputType.Value);
            if (totalNeeded > 0 && totalAvail + activeProdCount >= totalNeeded) return;

            // For PROCESS, the item needs to be at the facility or being carried there
            var itemAtFac = bb.FindItemAtFacility(step.inputType.Value, facility);
            bool isBeingCarried = bb.agents.Any(a =>
                a.currentTask != null &&
                a.currentTask.type == TaskType.ADD_TO_PLATE &&
                a.currentTask.itemType == step.inputType.Value &&
                a.currentTask.orderId == orderId); // Only if same order, not cross-order

            // Also check if any item of this type exists (even if not at facility)
            if (!isBeingCarried && itemAtFac == null)
            {
                // Generate anyway — the AI may self-fetch
                // But only if there's a free item available
                var freeItem = inputItems.FirstOrDefault(i => i.IsAvailable);
                if (freeItem == null) return;
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

            // Find PlatesCounter
            var platesCounter = bb.facilities.FirstOrDefault(f => f.type == FacilityType.PlatesCounter);
            if (platesCounter == null) return;

            // Find any free ClearCounter as drop target
            var dropTarget = bb.facilities
                .Where(f => f.type == FacilityType.AssemblyTable && f.state == "free")
                .OrderBy(f => Vector3.Distance(f.Center, Vector3.zero))
                .FirstOrDefault();
            if (dropTarget == null) return;

            var task = KitchenTask.Create(TaskType.FETCH_PLATE, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetFacility = platesCounter.counter; // source: PlatesCounter
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
            var ingredient = bb.FindItemsOfType(step.inputType.Value, excludeReserved: true)
                .FirstOrDefault(i => i.IsAvailable && !i.IsCarried);
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

            // Collect required ingredients from step chain
            var requiredIngredients = new List<KitchenObjEnum>();
            if (bb.recipeStepChains.TryGetValue(order.recipeName, out var steps))
            {
                foreach (var s in steps)
                {
                    if (s.taskType == TaskType.ADD_TO_PLATE && s.inputType.HasValue)
                        requiredIngredients.Add(s.inputType.Value);
                }
            }
            if (requiredIngredients.Count == 0)
            {
                requiredIngredients = order.ingredients
                    .Where(i => i != KitchenObjEnum.Plate).ToList();
            }

            // Find a plate with all required ingredients for this order
            Plate matchingPlate = bb.FindPlateForOrder(orderId);
            if (matchingPlate != null)
            {
                var plateIngs = matchingPlate.GetIngredients();
                if (!requiredIngredients.All(ri => plateIngs.Contains(ri)))
                    matchingPlate = null; // Order's plate doesn't have all ingredients — search globally
            }
            if (matchingPlate == null)
            {
                // Global search: ANY plate with exactly the right ingredients
                var allPlates = Object.FindObjectsOfType<Plate>();
                foreach (var p in allPlates)
                {
                    bool accessible = p.IsFree || p.GetHolder() is BaseCounter;
                    if (!accessible) continue;
                    var ings = p.GetIngredients();
                    if (ings.Count == 0) continue;
                    if (requiredIngredients.All(ri => ings.Contains(ri)))
                        { matchingPlate = p; bb.AssignPlateToOrder(orderId, matchingPlate); break; }
                }
            }
            if (matchingPlate == null)
            {
                AIDebugLogger.Log("Scheduler", $"SERVE #{orderId} {order.recipeName}: no plate with [{string.Join(",", requiredIngredients)}]");
                return;
            }

            // Verify the plate is accessible (on a counter or ground, not carried)
            bool onCounter = matchingPlate.GetHolder() is BaseCounter;
            if (!matchingPlate.IsFree && !onCounter) return;

            var plateIngredients = matchingPlate.GetIngredients();
            if (plateIngredients.Count == 0) return;

            if (!requiredIngredients.All(ri => plateIngredients.Contains(ri))) return;

            var task = KitchenTask.Create(TaskType.SERVE, $"{order.recipeName}: {step.label}");
            task.stepId = step.id;
            task.orderId = orderId;
            task.targetFacility = servingCounter.counter;
            task.targetItem = matchingPlate;
            task.itemType = KitchenObjEnum.Plate;
            task.duration = 0.8f;
            tasks.Add(task);
        }

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
                bool isBurned = item.objEnum == KitchenObjEnum.MeatPattyBurned;
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
                task.isStockTask = true;
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
                    bool isBurned = item.objEnum == KitchenObjEnum.MeatPattyBurned;
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
                    task.isStockTask = true;
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
                task.isStockTask = true;
                tasks.Add(task);
                wastePlatesFound++;
                if (wastePlatesFound >= 1) break; // One per cycle
            }
        }

        /// <summary>Count how many active orders need a given output type (any step).</summary>
        private static int CountOrdersNeeding(KitchenBlackboard bb, KitchenObjEnum outputType)
        {
            int count = 0;
            foreach (var order in bb.activeOrders)
            {
                if (bb.recipeStepChains.TryGetValue(order.recipeName, out var steps))
                {
                    if (steps.Any(s => s.outputType == outputType))
                        count++;
                }
            }
            return count;
        }

        #endregion

        #region Scoring

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

        #region Greedy Assignment

        /// <summary>
        /// Assign the best (agent, task) pairs using greedy matching.
        /// Returns the list of assignments made.
        /// </summary>
        public class Assignment
        {
            public AgentState agent;
            public KitchenTask task;
            public ScoreDetail scoreDetail;
        }

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
