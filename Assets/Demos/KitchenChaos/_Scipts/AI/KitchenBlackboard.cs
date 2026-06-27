using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// Facility type classification matching the HTML simulation.
    /// </summary>
    public enum FacilityType
    {
        Storage,        // ContainerCounter - spawns raw ingredients
        CuttingBoard,   // CuttingCounter - cuts ingredients
        FryingPan,      // StoveCounter - cooks ingredients
        AssemblyTable,  // ClearCounter - used for plate assembly
        PlatesCounter,  // PlatesCounter - spawns plates
        ServingCounter, // DeliveryCounter - completes orders
        TrashCan        // TrashCounter - disposes burned/waste items
    }

    /// <summary>
    /// Item processing stage (mirrors HTML simulation).
    /// </summary>
    public enum ItemStage
    {
        Raw,            // Fresh from ContainerCounter
        Intermediate,   // After cutting
        Cooked,         // After cooking
        Finished,       // Final dish (Burger, Salad)
        Plate           // Plate (container)
    }

    /// <summary>
    /// Tracked state of a single facility in the kitchen.
    /// </summary>
    public class FacilityState
    {
        public BaseCounter counter;
        public FacilityType type;
        public string state = "free"; // free | reserved | occupied
        public int reservedByAgent;
        public int occupiedByAgent;
        public float timer;

        // For ContainerCounter: which ingredient it provides
        public KitchenObjEnum providedIngredient;

        public Vector3 Center => counter != null
            ? counter.transform.position
            : Vector3.zero;

        public bool IsFree => state == "free";
        public bool IsReserved => state == "reserved";
        public bool IsOccupied => state == "occupied";
    }

    /// <summary>
    /// Tracked state of a single KitchenObj in the world.
    /// </summary>
    public class ItemState
    {
        public int id;
        public KitchenObj kitchenObj;
        public KitchenObjEnum itemType;
        public ItemStage stage;
        public int carriedByAgent;   // -1 if on ground
        public int reservedByTask;   // -1 if not reserved
        public int orderId;          // 0 if not bound to an order

        public Vector3 Position => kitchenObj != null && kitchenObj.IsFree
            ? kitchenObj.transform.position
            : kitchenObj != null
                ? kitchenObj.GetHolder()?.GetHoldTransform()?.position ?? Vector3.zero
                : Vector3.zero;

        public bool IsAvailable => carriedByAgent < 0 && reservedByTask < 0;
        public bool IsCarried => carriedByAgent >= 0;
    }

    /// <summary>
    /// Tracked state of a single AI chef.
    /// </summary>
    public class AgentState
    {
        public int agentId;
        public AIChefController controller;
        public string substate = "idle"; // idle | moving | working | waiting
        public KitchenTask currentTask;
        public int carryingItemId = -1;
        public Vector3 position;
        public float waitTimer;

        // Role specialization tracking
        public Dictionary<TaskType, int> roleCounts = new();
        public float stuckTimer; // For deadlock detection

        public bool IsIdle => currentTask == null
            || currentTask.status == "completed"
            || currentTask.status == "abandoned";
    }

    /// <summary>
    /// Represents one step in a recipe's preparation chain.
    /// Example: FetchMeat → CutMeat → FryMeat → Assemble → Serve
    /// </summary>
    public class RecipeStep
    {
        public string id;
        public string label;
        public TaskType taskType;
        public KitchenObjEnum? inputType;
        public KitchenObjEnum? outputType;
        public FacilityType requiredFacilityType;
    }

    /// <summary>
    /// Global blackboard — all shared state that the scheduler needs.
    /// Owned by KitchenAIManager.
    /// </summary>
    public class KitchenBlackboard
    {
        // ===== Facilities =====
        public List<FacilityState> facilities = new();

        // ===== Items =====
        public List<ItemState> items = new();

        // ===== Agents =====
        public List<AgentState> agents = new();

        // ===== Task Pool =====
        public List<KitchenTask> taskPool = new();

        // ===== Recipes =====
        public List<RecipeSo> allRecipes = new();
        public List<RecipeSo> activeOrders = new();

        // ===== Recipe Steps (pre-built) =====
        // recipeName → ordered list of steps
        public Dictionary<string, List<RecipeStep>> recipeStepChains = new();

        // ===== Reverse process lookup =====
        // output ingredient → process that produces it
        private Dictionary<KitchenObjEnum, KitchenProcessSo> _outputToProcess;

        // ===== Volatile Order IDs =====
        // ScriptableObject.GetHashCode() is stable, so when the same recipe re-enters
        // the queue, it gets the SAME hash and stale completedStepKeys block all steps.
        // We assign a fresh volatile ID each time a recipe (re)enters the queue.
        //
        // Key insight: multiple orders of the SAME recipe type (e.g., 3× tomato) can
        // coexist in the queue. They are the same RecipeSo instance, so we CANNOT use
        // RecipeSo as the key. Instead, we maintain activeOrderIds as a parallel list
        // to activeOrders, with one unique ID per queue position.
        // recipe → list of active order IDs, one per occurrence. Must stay stable
        // across cycles regardless of queue ordering.
        public Dictionary<RecipeSo, List<int>> recipeOrderIdLists = new();
        public List<int> activeOrderIds = new(); // parallel to activeOrders, one per order
        public int nextOrderId = 1;

        // Track when each order entered the queue for time-based urgency
        public Dictionary<int, float> orderEntryTimes = new();

        /// <summary>Get the volatile order ID for a recipe at a given index in activeOrders.</summary>
        public int GetOrderId(int activeOrderIndex) =>
            activeOrderIndex >= 0 && activeOrderIndex < activeOrderIds.Count
                ? activeOrderIds[activeOrderIndex]
                : 0;

        // ===== Order → Plate mapping =====
        // Track which Plate belongs to which order. The plate can be on
        // ANY ClearCounter — we find it by reference, not by counter.
        // Much simpler than the old "assembly counter" concept.
        public Dictionary<int, Plate> orderPlate = new();

        // ===== Config =====
        public const int MAX_OPEN_ITEMS = 2; // Max open items of each type

        // ===== Scoring Weights (from HTML simulation) =====
        public const float WEIGHT_DISTANCE = -0.08f;     // per 1 unit (≈ 8 per 100 units)
        public const float WEIGHT_FACILITY_WAIT = -0.4f; // per second
        public const float WEIGHT_ORDER_URGENCY = 1.5f;  // multiplier
        public const float WEIGHT_UNLOCK_VALUE = 2.5f;
        public const float WEIGHT_ROLE_BONUS = 0.04f;
        public const float WEIGHT_STOCK_BASE = 0.70f;
        public const float WEIGHT_FRESH_PICK = 3.0f;
        public const float WEIGHT_STALE_PICK = -8.0f;
        public const float WEIGHT_STALE_WORKSTATION_MULT = 4.0f;

        #region Initialization

        /// <summary>
        /// Scan the scene for all facilities and classify them.
        /// </summary>
        public void ScanFacilities()
        {
            facilities.Clear();
            var counters = Object.FindObjectsOfType<BaseCounter>();

            foreach (var c in counters)
            {
                var fs = new FacilityState { counter = c };

                if (c is ContainerCounter cc)
                {
                    fs.type = FacilityType.Storage;
                    fs.providedIngredient = cc.objEnum;
                }
                else if (c is CuttingCounter)
                {
                    fs.type = FacilityType.CuttingBoard;
                }
                else if (c is StoveCounter)
                {
                    fs.type = FacilityType.FryingPan;
                }
                else if (c is ClearCounter)
                {
                    fs.type = FacilityType.AssemblyTable;
                }
                else if (c is PlatesCounter)
                {
                    fs.type = FacilityType.PlatesCounter;
                }
                else if (c is DeliveryCounter)
                {
                    fs.type = FacilityType.ServingCounter;
                }
                else if (c is TrashCounter)
                {
                    fs.type = FacilityType.TrashCan;
                }
                else
                {
                    // Unknown — skip
                    continue;
                }

                facilities.Add(fs);
            }

            Debug.Log($"[Blackboard] Scanned {facilities.Count} facilities: " +
                      $"Storage={CountType(FacilityType.Storage)} " +
                      $"Cutting={CountType(FacilityType.CuttingBoard)} " +
                      $"Frying={CountType(FacilityType.FryingPan)} " +
                      $"Assembly={CountType(FacilityType.AssemblyTable)} " +
                      $"Plates={CountType(FacilityType.PlatesCounter)} " +
                      $"Serving={CountType(FacilityType.ServingCounter)}");
        }

        private int CountType(FacilityType t) => facilities.Count(f => f.type == t);

        /// <summary>
        /// Load all recipes and build step chains.
        /// </summary>
        public void LoadRecipes()
        {
            // Build reverse process lookup
            _outputToProcess = new Dictionary<KitchenObjEnum, KitchenProcessSo>();
            var processes = Resources.LoadAll<KitchenProcessSo>("So/Processes/");
            foreach (var p in processes)
            {
                if (!_outputToProcess.ContainsKey(p.outputEnum))
                    _outputToProcess[p.outputEnum] = p;
            }

            var loaded = Resources.LoadAll<RecipeSo>("So/Recipes/");
            allRecipes = new List<RecipeSo>(loaded);

            foreach (var recipe in allRecipes)
            {
                recipeStepChains[recipe.recipeName] = BuildRecipeSteps(recipe);
            }

            Debug.Log($"[Blackboard] Loaded {allRecipes.Count} recipes, {_outputToProcess.Count} process chains");
        }

        /// <summary>
        /// Reconstruct the step-by-step process chain for a recipe.
        /// For each ingredient, trace back through KitchenProcessSo to find the raw source.
        /// Then add plate-fetching, per-ingredient ADD_TO_PLATE, and SERVE steps.
        /// </summary>
        private List<RecipeStep> BuildRecipeSteps(RecipeSo recipe)
        {
            var steps = new List<RecipeStep>();
            var ingredientTypes = new List<KitchenObjEnum>();

            foreach (var ingredient in recipe.ingredients)
            {
                // Skip Plate — we fetch it separately
                if (ingredient == KitchenObjEnum.Plate) continue;

                // Trace back through processes to find raw source
                var chain = TraceIngredientChain(ingredient);

                // Determine the FINAL output (processed form) for ADD_TO_PLATE
                KitchenObjEnum finalForm = ingredient;
                foreach (var step in chain)
                {
                    if (step.taskType == TaskType.PROCESS && step.outputType.HasValue)
                        finalForm = step.outputType.Value;
                }
                ingredientTypes.Add(finalForm);

                steps.AddRange(chain);
            }

            // FETCH_PLATE: get plate from PlatesCounter, place on ClearCounter
            steps.Add(new RecipeStep
            {
                id = $"fetch_plate_{recipe.recipeName}",
                label = "拿盘子",
                taskType = TaskType.FETCH_PLATE,
                requiredFacilityType = FacilityType.AssemblyTable,
            });

            // ADD_TO_PLATE: one per ingredient (using the PROCESSED form)
            foreach (var ing in ingredientTypes)
            {
                steps.Add(new RecipeStep
                {
                    id = $"add_{ing}_{recipe.recipeName}",
                    label = $"加{ing}到盘子",
                    taskType = TaskType.ADD_TO_PLATE,
                    inputType = ing,
                    requiredFacilityType = FacilityType.AssemblyTable,
                });
            }

            // SERVE step
            steps.Add(new RecipeStep
            {
                id = $"serve_{recipe.recipeName}",
                label = $"出餐{recipe.recipeName}",
                taskType = TaskType.SERVE,
                requiredFacilityType = FacilityType.ServingCounter,
            });

            return steps;
        }

        /// <summary>
        /// Trace one ingredient back through the process chain.
        /// Returns FETCH + PROCESS steps needed to produce this ingredient.
        /// </summary>
        private List<RecipeStep> TraceIngredientChain(KitchenObjEnum finalIngredient)
        {
            var steps = new List<RecipeStep>();

            // Check if this is a raw ingredient (no process produces it)
            var producingProcess = FindProducingProcess(finalIngredient);
            if (producingProcess == null)
            {
                // Raw ingredient — just need FETCH
                steps.Add(new RecipeStep
                {
                    id = $"fetch_{finalIngredient}",
                    label = $"取{finalIngredient}",
                    taskType = TaskType.FETCH,
                    outputType = finalIngredient,
                    requiredFacilityType = FacilityType.Storage,
                });
                return steps;
            }

            // Trace back from the input
            var inputSteps = TraceIngredientChain(producingProcess.inputEnum);

            // Then the PROCESS step
            FacilityType facType = producingProcess.requiredFacility == FacilityEnum.CuttingCounter
                ? FacilityType.CuttingBoard
                : FacilityType.FryingPan;

            steps.AddRange(inputSteps);
            steps.Add(new RecipeStep
            {
                id = $"process_{producingProcess.inputEnum}_to_{producingProcess.outputEnum}",
                label = $"{producingProcess.inputEnum}→{producingProcess.outputEnum}",
                taskType = TaskType.PROCESS,
                inputType = producingProcess.inputEnum,
                outputType = producingProcess.outputEnum,
                requiredFacilityType = facType,
            });

            return steps;
        }

        private KitchenProcessSo FindProducingProcess(KitchenObjEnum output)
        {
            _outputToProcess.TryGetValue(output, out var process);
            return process;
        }

        #endregion

        #region Item Tracking

        /// <summary>
        /// Synchronize item tracking with the current scene state.
        /// Called each frame by KitchenAIManager.
        /// </summary>
        public void SyncItems()
        {
            var allObjs = Object.FindObjectsOfType<KitchenObj>();

            // Update existing and add new
            var seen = new HashSet<int>();
            foreach (var ko in allObjs)
            {
                if (ko == null || !ko.NetworkObject.IsSpawned) continue;

                var existing = items.Find(i => i.kitchenObj == ko);
                if (existing != null)
                {
                    seen.Add(existing.id);
                    UpdateItemState(existing);
                }
                else
                {
                    var newItem = CreateItemState(ko);
                    items.Add(newItem);
                    seen.Add(newItem.id);
                }
            }

            // Remove destroyed items
            items.RemoveAll(i => !seen.Contains(i.id));

            // Update carry states from agents
            foreach (var agent in agents)
            {
                agent.carryingItemId = -1;
                if (agent.controller != null && agent.controller.HeldItem != null)
                {
                    var itemState = items.Find(i => i.kitchenObj == agent.controller.HeldItem);
                    if (itemState != null)
                    {
                        agent.carryingItemId = itemState.id;
                        itemState.carriedByAgent = agent.agentId;
                    }
                }
            }

            // Mark items NOT carried by any agent
            var carriedIds = new HashSet<int>();
            foreach (var a in agents)
                if (a.carryingItemId >= 0)
                    carriedIds.Add(a.carryingItemId);

            foreach (var item in items)
                if (!carriedIds.Contains(item.id))
                    item.carriedByAgent = -1;
        }

        private ItemState CreateItemState(KitchenObj ko)
        {
            var item = new ItemState
            {
                id = ko.NetworkObjectId.GetHashCode(), // stable-ish ID
                kitchenObj = ko,
                itemType = ko.objEnum,
                stage = DetermineStage(ko.objEnum),
                carriedByAgent = -1,
                reservedByTask = -1,
                orderId = 0,
            };
            return item;
        }

        private void UpdateItemState(ItemState item)
        {
            if (item.kitchenObj == null) return;
            item.itemType = item.kitchenObj.objEnum;
            item.stage = DetermineStage(item.kitchenObj.objEnum);
        }

        /// <summary>
        /// Classify an ingredient's processing stage.
        /// </summary>
        public static ItemStage DetermineStage(KitchenObjEnum objEnum)
        {
            switch (objEnum)
            {
                // Raw ingredients
                case KitchenObjEnum.Bread:
                case KitchenObjEnum.Tomato:
                case KitchenObjEnum.Cabbage:
                case KitchenObjEnum.CheeseBlock:
                case KitchenObjEnum.MeatPattyUncooked:
                    return ItemStage.Raw;

                // Intermediate (after cutting)
                case KitchenObjEnum.TomatoSlices:
                case KitchenObjEnum.CabbageSlices:
                case KitchenObjEnum.CheeseSlices:
                    return ItemStage.Intermediate;

                // Cooked
                case KitchenObjEnum.MeatPattyCooked:
                    return ItemStage.Cooked;

                // Burned — still usable? No.
                case KitchenObjEnum.MeatPattyBurned:
                    return ItemStage.Cooked;

                // Plate
                case KitchenObjEnum.Plate:
                    return ItemStage.Plate;

                default:
                    return ItemStage.Raw;
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Find facilities of a given type.
        /// </summary>
        public List<FacilityState> GetFacilitiesOfType(FacilityType type)
        {
            return facilities.FindAll(f => f.type == type);
        }

        /// <summary>
        /// Find a free facility of a given type, preferring ones with more nearby items.
        /// </summary>
        public FacilityState BestFreeFacility(FacilityType type, Vector3 fromPos)
        {
            var candidates = facilities.FindAll(f => f.type == type && f.state == "free");
            if (candidates.Count == 0)
            {
                // Also consider reserved ones
                candidates = facilities.FindAll(f => f.type == type);
                if (candidates.Count == 0) return null;
                candidates.Sort((a, b) =>
                    Vector3.Distance(a.Center, fromPos).CompareTo(Vector3.Distance(b.Center, fromPos)));
                return candidates[0];
            }

            if (candidates.Count == 1) return candidates[0];

            // Prefer the facility with more items nearby
            candidates.Sort((a, b) =>
            {
                var countA = items.Count(i =>
                    i.IsAvailable &&
                    Vector3.Distance(i.Position, a.Center) < 3f);
                var countB = items.Count(i =>
                    i.IsAvailable &&
                    Vector3.Distance(i.Position, b.Center) < 3f);
                if (countA != countB) return countB.CompareTo(countA);
                return Vector3.Distance(a.Center, fromPos)
                    .CompareTo(Vector3.Distance(b.Center, fromPos));
            });
            return candidates[0];
        }

        /// <summary>
        /// Find a ContainerCounter that provides the given ingredient.
        /// </summary>
        public FacilityState FindStorageFor(KitchenObjEnum ingredient)
        {
            return facilities.Find(f =>
                f.type == FacilityType.Storage &&
                f.providedIngredient == ingredient);
        }

        /// <summary>
        /// Find available items of a given type.
        /// </summary>
        public List<ItemState> FindItemsOfType(KitchenObjEnum type, bool excludeReserved = true, int? forOrderId = null)
        {
            return items.FindAll(i =>
                i.itemType == type &&
                (!excludeReserved || i.reservedByTask < 0) &&
                i.kitchenObj != null &&
                (!forOrderId.HasValue || i.orderId == 0 || i.orderId == forOrderId.Value));
        }

        /// <summary>
        /// Tag an item as belonging to a specific order.
        /// </summary>
        public void TagItemForOrder(KitchenObj obj, int orderId)
        {
            var item = items.Find(i => i.kitchenObj == obj);
            if (item != null && item.orderId == 0)
            {
                item.orderId = orderId;
            }
        }

        /// <summary>
        /// Count how many active orders need a specific ingredient as input.
        /// </summary>
        public int CountOrdersNeeding(KitchenObjEnum ingredientType)
        {
            int count = 0;
            foreach (var recipe in activeOrders)
            {
                if (recipeStepChains.TryGetValue(recipe.recipeName, out var steps))
                {
                    bool needs = steps.Any(s =>
                        s.inputType == ingredientType ||
                        (s.taskType == TaskType.FETCH && s.outputType == ingredientType));
                    if (needs) count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Find an item of a given type near a facility.
        /// </summary>
        public ItemState FindItemAtFacility(KitchenObjEnum type, FacilityState facility)
        {
            return items.Find(i =>
                i.itemType == type &&
                i.kitchenObj != null &&
                !i.IsCarried &&
                Vector3.Distance(i.Position, facility.Center) < 3f);
        }

        /// <summary>
        /// Check if an item is at a storage facility.
        /// </summary>
        public bool IsItemAtStorage(ItemState item)
        {
            return facilities.Any(f =>
                f.type == FacilityType.Storage &&
                Vector3.Distance(item.Position, f.Center) < 3f);
        }

        /// <summary>
        /// Check if an item is at a non-storage facility.
        /// </summary>
        public bool IsItemAtNonStorageFacility(ItemState item)
        {
            return facilities.Any(f =>
                f.type != FacilityType.Storage &&
                f.type != FacilityType.ServingCounter &&
                Vector3.Distance(item.Position, f.Center) < 3f);
        }

        /// <summary>
        /// Assign a plate to an order. Called when FETCH_PLATE completes.
        /// </summary>
        public void AssignPlateToOrder(int orderId, Plate plate)
        {
            orderPlate[orderId] = plate;
        }

        /// <summary>
        /// Release the plate assignment for a completed/cancelled order.
        /// </summary>
        public void ReleaseOrderPlate(int orderId)
        {
            orderPlate.Remove(orderId);
        }

        /// <summary>
        /// Find the plate belonging to a specific order.
        /// </summary>
        public Plate FindPlateForOrder(int orderId)
        {
            orderPlate.TryGetValue(orderId, out var plate);
            if (plate == null) return null;
            // Verify the plate still exists (not destroyed)
            if (plate.gameObject == null) { orderPlate.Remove(orderId); return null; }
            return plate;
        }

        #endregion
    }
}
