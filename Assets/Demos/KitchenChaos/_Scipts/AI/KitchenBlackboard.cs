using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kitchen;

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
        Oven,           // OvenCounter - bakes
        Blender,        // BlenderCounter - blends shakes
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
        public ulong objectId;
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
        public List<string> dependsOnStepIds = new();
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

        // PGC-derived walkable positions that are legal fallback locations
        // when all usable counters are occupied.
        public List<Vector3> groundDropPositions = new();

        // ===== Task Pool =====
        public List<KitchenTask> taskPool = new();

        // ===== Recipes =====
        public List<RecipeSo> allRecipes = new();
        public List<RecipeSo> activeOrders = new();
        public List<string> activeOrderCodes = new();

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
        public List<int> activeOrderIds = new();

        // Track when each order entered the queue for time-based urgency
        public Dictionary<int, string> orderCodeById = new();

        /// <summary>Get the volatile order ID for a recipe at a given index in activeOrders.</summary>

        // ===== Order → Plate mapping =====
        // Track which Plate belongs to which order. The plate can be on
        // ANY ClearCounter — we find it by reference, not by counter.
        // Much simpler than the old "assembly counter" concept.
        public Dictionary<int, Plate> orderPlate = new();

        // Per-order explicit plan state. The legacy recipeStepChains remains
        // available while the planner migration is incremental.
        public Dictionary<int, KitchenPlan> orderPlans = new();
        public Dictionary<int, Dictionary<string, string>> orderStepStates = new();

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
                // PGC 墙占位（空柜顶墙）不参与 AI 设施
                if (c != null && c.name.StartsWith("Wall_"))
                    continue;

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
                else if (c is OvenCounter)
                {
                    fs.type = FacilityType.Oven;
                }
                else if (c is BlenderCounter)
                {
                    fs.type = FacilityType.Blender;
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

        public void SetGroundDropPositions(IEnumerable<Vector3> positions)
        {
            groundDropPositions.Clear();
            if (positions == null) return;
            foreach (var position in positions)
            {
                var p = position;
                p.y = 0f;
                groundDropPositions.Add(p);
            }
        }

        public bool TryGetNearestGroundDropPosition(Vector3 from, out Vector3 position)
        {
            position = Vector3.zero;
            if (groundDropPositions.Count == 0) return false;

            float bestDistance = float.MaxValue;
            foreach (var candidate in groundDropPositions)
            {
                bool occupied = items.Any(i =>
                    i?.kitchenObj != null &&
                    i.carriedByAgent < 0 &&
                    Vector3.Distance(i.Position, candidate) < 0.45f);
                if (occupied) continue;

                float distance = Vector3.Distance(from, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    position = candidate;
                }
            }

            return bestDistance < float.MaxValue;
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
                var chain = BuildRecipeSteps(recipe);
                AttachStepDependencies(chain);
                recipeStepChains[recipe.recipeName] = chain;
            }

            Debug.Log($"[Blackboard] Loaded {allRecipes.Count} recipes, {_outputToProcess.Count} process chains");
        }

        /// <summary>
        /// Adds explicit Item-to-Item dependencies to the legacy recipe chain.
        /// The chain is still generated by the existing reverse lookup, but
        /// execution is now gated by these dependencies.
        /// </summary>
        private static void AttachStepDependencies(List<RecipeStep> steps)
        {
            if (steps == null || steps.Count == 0) return;

            var fetchPlate = steps.FirstOrDefault(s => s.taskType == TaskType.FETCH_PLATE);
            var addSteps = steps.Where(s => s.taskType == TaskType.ADD_TO_PLATE).ToList();
            var postProcess = steps.LastOrDefault(s =>
                s.taskType == TaskType.PROCESS &&
                (s.requiredFacilityType == FacilityType.Oven ||
                 s.requiredFacilityType == FacilityType.Blender));

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (step == null) continue;
                step.dependsOnStepIds ??= new List<string>();

                if (step.taskType == TaskType.PROCESS && step.inputType.HasValue)
                {
                    var inputProducer = steps
                        .Take(i)
                        .LastOrDefault(s => s.outputType == step.inputType);
                    if (inputProducer != null && !step.dependsOnStepIds.Contains(inputProducer.id))
                        step.dependsOnStepIds.Add(inputProducer.id);
                }

                if (step.taskType == TaskType.ADD_TO_PLATE)
                {
                    if (fetchPlate != null && !step.dependsOnStepIds.Contains(fetchPlate.id))
                        step.dependsOnStepIds.Add(fetchPlate.id);

                    var ingredientProducer = steps
                        .Take(i)
                        .LastOrDefault(s => s.outputType == step.inputType);
                    if (ingredientProducer != null && !step.dependsOnStepIds.Contains(ingredientProducer.id))
                        step.dependsOnStepIds.Add(ingredientProducer.id);
                }
            }

            if (postProcess != null)
            {
                foreach (var add in addSteps)
                    if (!postProcess.dependsOnStepIds.Contains(add.id))
                        postProcess.dependsOnStepIds.Add(add.id);
            }

            var serve = steps.LastOrDefault(s => s.taskType == TaskType.SERVE);
            if (serve != null)
            {
                if (postProcess != null)
                {
                    if (!serve.dependsOnStepIds.Contains(postProcess.id))
                        serve.dependsOnStepIds.Add(postProcess.id);
                }
                else
                {
                    foreach (var add in addSteps)
                        if (!serve.dependsOnStepIds.Contains(add.id))
                            serve.dependsOnStepIds.Add(add.id);
                }
            }
        }

        /// <summary>
        /// Build steps for an order that requires a single <see cref="RecipeSo.requiredItem"/>.
        /// Expands plate-assembly inputs and facility process chains as needed.
        /// </summary>
        private List<RecipeStep> BuildRecipeSteps(RecipeSo recipe)
        {
            var steps = new List<RecipeStep>();
            var required = recipe.requiredItem;

            // Post-assembly facility step (oven / blender) if required item is produced that way.
            var postProcess = FindProducingProcess(required);
            KitchenObjEnum plateTarget = required;
            FacilityEnum? postFacility = null;

            if (postProcess != null
                && (postProcess.requiredFacility == FacilityEnum.OvenCounter
                    || postProcess.requiredFacility == FacilityEnum.BlenderCounter))
            {
                plateTarget = postProcess.inputEnum;
                postFacility = postProcess.requiredFacility;
            }

            var assembly = PlateAssemblyMatcher.FindAssemblyProducing(plateTarget);
            var toAddOnPlate = new List<KitchenObjEnum>();

            if (assembly != null && assembly.inputs != null)
            {
                foreach (var input in assembly.inputs)
                {
                    steps.AddRange(TraceIngredientChain(input));
                    toAddOnPlate.Add(input);
                }
            }
            else
            {
                // Direct item on plate (e.g. raw tomato, chopped fish, steak).
                steps.AddRange(TraceIngredientChain(plateTarget));
                toAddOnPlate.Add(plateTarget);
            }

            steps.Add(new RecipeStep
            {
                id = $"fetch_plate_{recipe.recipeName}",
                label = "拿盘子",
                taskType = TaskType.FETCH_PLATE,
                requiredFacilityType = FacilityType.AssemblyTable,
            });

            foreach (var ing in toAddOnPlate)
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

            if (postFacility.HasValue && postProcess != null)
            {
                steps.Add(new RecipeStep
                {
                    id = $"process_{postProcess.inputEnum}_to_{postProcess.outputEnum}",
                    label = $"{postProcess.inputEnum}→{postProcess.outputEnum}",
                    taskType = TaskType.PROCESS,
                    inputType = postProcess.inputEnum,
                    outputType = postProcess.outputEnum,
                    requiredFacilityType = FacilityToType(postFacility.Value),
                });
            }

            steps.Add(new RecipeStep
            {
                id = $"serve_{recipe.recipeName}",
                label = $"出餐{recipe.recipeName}",
                taskType = TaskType.SERVE,
                requiredFacilityType = FacilityType.ServingCounter,
            });

            return steps;
        }

        private static FacilityType FacilityToType(FacilityEnum f)
        {
            return f switch
            {
                FacilityEnum.CuttingCounter => FacilityType.CuttingBoard,
                FacilityEnum.StoveCounter => FacilityType.FryingPan,
                FacilityEnum.OvenCounter => FacilityType.Oven,
                FacilityEnum.BlenderCounter => FacilityType.Blender,
                _ => FacilityType.FryingPan,
            };
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

            FacilityType facType = FacilityToType(producingProcess.requiredFacility);

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
                objectId = ko.RuntimeObjectId,
                kitchenObj = ko,
                itemType = ko.objEnum,
                stage = DetermineStage(ko.objEnum),
                carriedByAgent = -1,
                reservedByTask = -1,
                orderId = ko.BoundOrderId,
            };
            return item;
        }

        private void UpdateItemState(ItemState item)
        {
            if (item.kitchenObj == null) return;
            item.itemType = item.kitchenObj.objEnum;
            item.objectId = item.kitchenObj.RuntimeObjectId;
            item.stage = DetermineStage(item.kitchenObj.objEnum);
            item.orderId = item.kitchenObj.BoundOrderId;
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
                (!forOrderId.HasValue || i.orderId == forOrderId.Value));
        }

        /// <summary>
        /// Tag an item as belonging to a specific order.
        /// </summary>
        public void TagItemForOrder(KitchenObj obj, int orderId)
        {
            var item = items.Find(i => i.kitchenObj == obj);
            if (item != null
                && item.orderId == orderId
                && obj != null
                && obj.BoundOrderId == orderId)
                return;

            if (obj != null && obj.IsServer && obj.BindToOrder(orderId))
            {
                if (item != null)
                    item.orderId = orderId;
            }
        }

        public bool CanUseItemForOrder(KitchenObj obj, int orderId)
        {
            if (obj == null) return false;
            if (orderId == 0) return false;
            var item = items.Find(i => i.kitchenObj == obj);
            return item != null && item.orderId == orderId
                && obj.BoundOrderId == orderId;
        }

        public bool TryClaimItemForOrder(KitchenObj obj, int orderId)
        {
            if (!CanUseItemForOrder(obj, orderId)) return false;
            TagItemForOrder(obj, orderId);
            return true;
        }

        public ItemState FindItemByObjectId(ulong objectId)
        {
            if (objectId == 0UL) return null;
            return items.Find(i => i != null && i.objectId == objectId);
        }

        public void EnsureOrderPlan(int orderId, RecipeSo recipe)
        {
            if (orderId == 0 || recipe == null) return;
            if (!orderPlans.ContainsKey(orderId))
            {
                orderPlans[orderId] = KitchenPlanner.BuildPlan(this, orderId, recipe);
            }

            if (!orderStepStates.ContainsKey(orderId))
                orderStepStates[orderId] = new Dictionary<string, string>();
        }

        public KitchenPlan GetOrderPlan(int orderId)
        {
            orderPlans.TryGetValue(orderId, out var plan);
            return plan;
        }

        public string GetStepState(int orderId, string stepId)
        {
            if (orderStepStates.TryGetValue(orderId, out var states)
                && states.TryGetValue(stepId, out var state))
                return state;
            return "pending";
        }

        public void SetStepState(int orderId, string stepId, string state)
        {
            if (orderId == 0 || string.IsNullOrEmpty(stepId)) return;
            if (!orderStepStates.TryGetValue(orderId, out var states))
            {
                states = new Dictionary<string, string>();
                orderStepStates[orderId] = states;
            }
            states[stepId] = state;
        }

        public bool AreStepDependenciesSatisfied(int orderId, RecipeStep step)
        {
            if (step == null) return false;

            foreach (var dependencyId in step.dependsOnStepIds ?? new List<string>())
            {
                if (GetStepState(orderId, dependencyId) == "completed")
                    continue;

                var plan = GetOrderPlan(orderId);
                var node = plan?.nodes?.Find(n => n.stepId == dependencyId);
                if (node != null && IsPlanNodeSatisfied(orderId, node))
                {
                    SetStepState(orderId, dependencyId, "completed");
                    continue;
                }

                return false;
            }

            var currentPlan = GetOrderPlan(orderId);
            var currentNode = currentPlan?.nodes?.Find(n => n.stepId == step.id);
            if (currentNode?.preconditions == null) return true;

            return currentNode.preconditions.All(condition =>
                EvaluateCondition(orderId, condition));
        }

        public bool EvaluateCondition(int orderId, KitchenConditionSpec condition)
        {
            if (condition == null) return true;

            bool result;
            switch (condition.type)
            {
                case KitchenConditionType.DependencyTasksCompleted:
                {
                    var plan = GetOrderPlan(orderId);
                    var dependency = plan?.nodes?.Find(n => n.taskId == condition.referencedTaskId);
                    result = dependency != null &&
                             (dependency.status == "completed" ||
                              IsPlanNodeSatisfied(orderId, dependency));
                    break;
                }
                case KitchenConditionType.ObjectExists:
                    result = FindItemsOfType(condition.itemType, excludeReserved: false)
                        .Any(i => i.orderId == orderId)
                        || (orderPlate.TryGetValue(orderId, out var objectPlate)
                            && objectPlate != null
                            && objectPlate.GetIngredients().Contains(condition.itemType));
                    break;
                case KitchenConditionType.ObjectBelongsToOrder:
                    result = FindItemByObjectId(condition.objectId)?.orderId == orderId;
                    break;
                case KitchenConditionType.FacilityHasCapacity:
                    result = facilities.Any(f => f.type == condition.facilityType &&
                        (f.counter is ClearCounter && !f.counter.HasKitchenObj()
                         || f.type == FacilityType.PlatesCounter
                         || f.type == FacilityType.Storage));
                    break;
                case KitchenConditionType.FacilityContainsItem:
                    result = facilities.Any(f => f.type == condition.facilityType
                        && f.counter != null
                        && f.counter.HasKitchenObj()
                        && f.counter.GetKitchenObj().objEnum == condition.itemType);
                    break;
                case KitchenConditionType.PlateContainsItem:
                    result = orderPlate.TryGetValue(orderId, out var plate)
                        && plate != null
                        && plate.GetIngredients().Contains(condition.itemType);
                    break;
                case KitchenConditionType.PlateMatchesOrder:
                    result = orderPlate.TryGetValue(orderId, out var matchingPlate)
                        && matchingPlate != null
                        && matchingPlate.TryGetDeliverableItem(out var delivered)
                        && delivered == condition.itemType;
                    break;
                case KitchenConditionType.ProcessOutputReady:
                    result = FindItemsOfType(condition.itemType, excludeReserved: false)
                        .Any(i => i.orderId == orderId)
                        || (orderPlate.TryGetValue(orderId, out var processedPlate)
                            && processedPlate != null
                            && processedPlate.GetIngredients().Contains(condition.itemType));
                    break;
                case KitchenConditionType.GroundDropAvailable:
                    result = TryGetNearestGroundDropPosition(Vector3.zero, out _);
                    break;
                default:
                    result = false;
                    break;
            }

            return condition.negate ? !result : result;
        }

        private bool IsPlanNodeSatisfied(int orderId, KitchenPlanNode node)
        {
            if (node == null) return false;

            if (node.legacyTaskType == TaskType.FETCH_PLATE)
                return FindPlateForOrder(orderId) != null;

            if (node.legacyTaskType == TaskType.ADD_TO_PLATE)
            {
                var plate = FindPlateForOrder(orderId);
                return plate != null && node.action.itemType != 0
                    && plate.GetIngredients().Contains(node.action.itemType);
            }

            var expectedType = node.action.outputType != 0
                ? node.action.outputType
                : node.action.itemType;

            if (expectedType != 0 && node.action.type == KitchenActionType.Process)
            {
                if (FindItemsOfType(expectedType, excludeReserved: false)
                    .Any(i => i.orderId == orderId))
                    return true;

                return orderPlate.TryGetValue(orderId, out var processedPlate)
                    && processedPlate != null
                    && processedPlate.GetIngredients().Contains(expectedType);
            }

            if (expectedType != 0)
            {
                return FindItemsOfType(expectedType, excludeReserved: false)
                    .Any(i => i.orderId == orderId);
            }

            return GetStepState(orderId, node.stepId) == "completed";
        }

        /// <summary>
        /// Find an item of a given type near a facility.
        /// </summary>
        public ItemState FindItemAtFacility(
            KitchenObjEnum type,
            FacilityState facility,
            int? forOrderId = null)
        {
            return items.Find(i =>
                i.itemType == type &&
                i.kitchenObj != null &&
                !i.IsCarried &&
                (!forOrderId.HasValue || i.orderId == forOrderId.Value) &&
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
            if (plate == null || orderId == 0) return;
            if (plate.BoundOrderId != 0 && plate.BoundOrderId != orderId)
                return;
            if (plate.IsServer)
                plate.BindToOrder(orderId);
            orderPlate[orderId] = plate;
            TagItemForOrder(plate, orderId);
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
            if (plate.gameObject == null
                || (plate.BoundOrderId != 0 && plate.BoundOrderId != orderId))
            {
                orderPlate.Remove(orderId);
                return null;
            }
            return plate;
        }

        #endregion
    }
}
