using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kitchen.AI;
using UnityEngine;

namespace Kitchen.PGC
{
    /// <summary>
    /// 根据本轮菜谱反推设施节点与 Mission 边。
    /// 规则：每道菜各算一遍完整链路；菜内同设施用几次算几次；
    /// 盘架 = ceil(菜品数/2)；
    /// 空台 = 盘架数 + 装盘食材暂存位 + 烤/搅后回盘暂存位；
    /// 相同菜食材暂存：首份全量，之后每份只叠加 ceil(食材数/2)。
    /// </summary>
    public static class RecipeFacilityInferencer
    {
        public static void Infer(
            IReadOnlyList<RecipeSo> recipes,
            out List<PGCFacilityNode> facilities,
            out List<PGCMissionEdge> edges,
            int chefCount = 4)
        {
            facilities = new List<PGCFacilityNode>();
            edges = new List<PGCMissionEdge>();
            if (recipes == null || recipes.Count == 0)
                return;

            var processes = Resources.LoadAll<KitchenProcessSo>("So/Processes/");
            var outputToProcess = new Dictionary<KitchenObjEnum, KitchenProcessSo>();
            foreach (var p in processes)
            {
                if (p != null && !outputToProcess.ContainsKey(p.outputEnum))
                    outputToProcess[p.outputEnum] = p;
            }

            int nextId = 0;
            var plateNodes = new List<PGCFacilityNode>();
            int plateCount = Mathf.Max(1, Mathf.CeilToInt(recipes.Count / 2f));
            for (int i = 0; i < plateCount; i++)
            {
                var plate = new PGCFacilityNode
                {
                    id = nextId++,
                    type = FacilityType.PlatesCounter,
                    recipeIndex = -1,
                    label = $"盘架#{i}",
                };
                plateNodes.Add(plate);
                facilities.Add(plate);
            }

            // 先扫一遍菜谱：装盘食材 / 是否烤搅，用于反推空台数量。
            // 相同菜：首份按全量食材计，之后每份只叠加 ceil(食材数/2)，不按同数量全加。
            var recipePlans = new List<(RecipeSo recipe, int recipeIndex, List<FacilityDemand> chain, RecipeStaging staging)>();
            var ingredientByRecipeKey = new Dictionary<string, (int ingredientCount, int copies)>();
            int postProcessSlots = 0;

            for (int r = 0; r < recipes.Count; r++)
            {
                var recipe = recipes[r];
                if (recipe == null) continue;

                var staging = AnalyzeRecipeStaging(recipe, outputToProcess);
                var chain = BuildFacilityChain(staging, outputToProcess);
                recipePlans.Add((recipe, r, chain, staging));

                string key = RecipeGroupKey(recipe);
                int k = staging.plateIngredients.Count;
                if (ingredientByRecipeKey.TryGetValue(key, out var g))
                    ingredientByRecipeKey[key] = (g.ingredientCount, g.copies + 1);
                else
                    ingredientByRecipeKey[key] = (k, 1);

                if (staging.hasPostProcess)
                    postProcessSlots += 1;
            }

            int plateIngredientSlots = 0;
            foreach (var g in ingredientByRecipeKey.Values)
            {
                int k = g.ingredientCount;
                int n = g.copies;
                // 首份全量，相同菜后续每份只加 ceil(k/2)
                plateIngredientSlots += k;
                if (n > 1)
                    plateIngredientSlots += (n - 1) * Mathf.Max(1, Mathf.CeilToInt(k / 2f));
            }

            // 空台反推：
            // 1) 盘架数 → 在制盘子落点（FETCH_PLATE / 装盘台）
            // 2) 装盘食材暂存（相同菜副本按食材/2叠加）
            // 3) 烤/搅菜 → 成品出炉后回盘前暂存
            int clearCount = Mathf.Max(1, plateCount + plateIngredientSlots + postProcessSlots);
            var clearNodes = new List<PGCFacilityNode>();
            for (int i = 0; i < clearCount; i++)
            {
                var clear = new PGCFacilityNode
                {
                    id = nextId++,
                    type = FacilityType.AssemblyTable,
                    recipeIndex = -1,
                    label = $"空台#{i}",
                };
                clearNodes.Add(clear);
                facilities.Add(clear);
            }

            var delivery = new PGCFacilityNode
            {
                id = nextId++,
                type = FacilityType.ServingCounter,
                recipeIndex = -1,
                label = "出餐口",
            };
            facilities.Add(delivery);

            var trash = new PGCFacilityNode
            {
                id = nextId++,
                type = FacilityType.TrashCan,
                recipeIndex = -1,
                label = "垃圾桶",
            };
            facilities.Add(trash);

            // 盘架 → 出餐；出餐 ↔ 垃圾桶
            foreach (var plate in plateNodes)
            {
                edges.Add(new PGCMissionEdge
                {
                    fromId = plate.id,
                    toId = delivery.id,
                    kind = "plates_delivery",
                });
            }

            edges.Add(new PGCMissionEdge
            {
                fromId = delivery.id,
                toId = trash.id,
                kind = "trash_delivery",
            });
            edges.Add(new PGCMissionEdge
            {
                fromId = trash.id,
                toId = delivery.id,
                kind = "trash_delivery",
            });

            // 空台 → 盘架（轮询），保证暂存台与装配区有通路
            if (plateNodes.Count > 0)
            {
                for (int i = 0; i < clearNodes.Count; i++)
                {
                    edges.Add(new PGCMissionEdge
                    {
                        fromId = clearNodes[i].id,
                        toId = plateNodes[i % plateNodes.Count].id,
                        kind = "staging",
                    });
                }
            }

            foreach (var plan in recipePlans)
            {
                var recipeNodes = new List<PGCFacilityNode>();
                int step = 0;
                foreach (var demand in plan.chain)
                {
                    var node = new PGCFacilityNode
                    {
                        id = nextId++,
                        type = demand.type,
                        hasIngredient = demand.hasIngredient,
                        ingredient = demand.ingredient,
                        recipeIndex = plan.recipeIndex,
                        stepIndex = step++,
                        label = demand.label,
                    };
                    recipeNodes.Add(node);
                    facilities.Add(node);
                }

                for (int i = 0; i < recipeNodes.Count - 1; i++)
                {
                    edges.Add(new PGCMissionEdge
                    {
                        fromId = recipeNodes[i].id,
                        toId = recipeNodes[i + 1].id,
                        kind = "mission",
                    });
                }

                if (recipeNodes.Count > 0 && plateNodes.Count > 0)
                {
                    var assignedPlate = plateNodes[plan.recipeIndex % plateNodes.Count];
                    edges.Add(new PGCMissionEdge
                    {
                        fromId = recipeNodes[recipeNodes.Count - 1].id,
                        toId = assignedPlate.id,
                        kind = "mission",
                    });
                }
            }

            LogSummary(
                recipes,
                facilities,
                edges,
                chefCount,
                clearCount,
                plateCount,
                plateIngredientSlots,
                postProcessSlots);
        }

        private struct FacilityDemand
        {
            public FacilityType type;
            public KitchenObjEnum ingredient;
            public bool hasIngredient;
            public string label;
        }

        private struct RecipeStaging
        {
            public List<KitchenObjEnum> plateIngredients;
            public bool hasPostProcess;
            public KitchenProcessSo postProcess;
            public KitchenObjEnum plateTarget;
        }

        private static string RecipeGroupKey(RecipeSo recipe)
        {
            if (recipe == null) return string.Empty;
            if (!string.IsNullOrEmpty(recipe.recipeName))
                return recipe.recipeName;
            return recipe.name;
        }

        /// <summary>
        /// 解析一道菜：装盘食材列表、是否需要烤/搅后处理。
        /// </summary>
        private static RecipeStaging AnalyzeRecipeStaging(
            RecipeSo recipe,
            Dictionary<KitchenObjEnum, KitchenProcessSo> outputToProcess)
        {
            var staging = new RecipeStaging
            {
                plateIngredients = new List<KitchenObjEnum>(),
                plateTarget = recipe.requiredItem,
            };

            var required = recipe.requiredItem;
            if (outputToProcess.TryGetValue(required, out var maybePost)
                && (maybePost.requiredFacility == FacilityEnum.OvenCounter
                    || maybePost.requiredFacility == FacilityEnum.BlenderCounter))
            {
                staging.hasPostProcess = true;
                staging.postProcess = maybePost;
                staging.plateTarget = maybePost.inputEnum;
            }

            var assembly = PlateAssemblyMatcher.FindAssemblyProducing(staging.plateTarget);
            if (assembly != null && assembly.inputs != null && assembly.inputs.Length > 0)
            {
                foreach (var input in assembly.inputs)
                    staging.plateIngredients.Add(input);
            }
            else
            {
                staging.plateIngredients.Add(staging.plateTarget);
            }

            return staging;
        }

        /// <summary>
        /// 与 Blackboard.BuildRecipeSteps 对齐：原料柜 / 加工柜按链路逐步累加（不含盘架出餐）。
        /// </summary>
        private static List<FacilityDemand> BuildFacilityChain(
            RecipeStaging staging,
            Dictionary<KitchenObjEnum, KitchenProcessSo> outputToProcess)
        {
            var demands = new List<FacilityDemand>();

            foreach (var ing in staging.plateIngredients)
                AppendIngredientChain(ing, outputToProcess, demands);

            if (staging.hasPostProcess && staging.postProcess != null)
            {
                demands.Add(new FacilityDemand
                {
                    type = FacilityToType(staging.postProcess.requiredFacility),
                    label =
                        $"{FacilityToType(staging.postProcess.requiredFacility)}:" +
                        $"{staging.postProcess.inputEnum}→{staging.postProcess.outputEnum}",
                });
            }

            return demands;
        }

        private static void AppendIngredientChain(
            KitchenObjEnum finalIngredient,
            Dictionary<KitchenObjEnum, KitchenProcessSo> outputToProcess,
            List<FacilityDemand> demands)
        {
            if (!outputToProcess.TryGetValue(finalIngredient, out var producing) || producing == null)
            {
                demands.Add(new FacilityDemand
                {
                    type = FacilityType.Storage,
                    hasIngredient = true,
                    ingredient = finalIngredient,
                    label = $"原料柜:{finalIngredient}",
                });
                return;
            }

            AppendIngredientChain(producing.inputEnum, outputToProcess, demands);
            demands.Add(new FacilityDemand
            {
                type = FacilityToType(producing.requiredFacility),
                label = $"{FacilityToType(producing.requiredFacility)}:{producing.inputEnum}→{producing.outputEnum}",
            });
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

        private static void LogSummary(
            IReadOnlyList<RecipeSo> recipes,
            List<PGCFacilityNode> facilities,
            List<PGCMissionEdge> edges,
            int chefCount,
            int clearCount,
            int plateCount,
            int plateIngredientSlots,
            int postProcessSlots)
        {
            var sb = new StringBuilder();
            sb.Append("[PGC-Infer] recipes=").Append(recipes.Count);
            sb.Append(" chefs=").Append(chefCount);
            sb.Append(" plates=").Append(plateCount);
            sb.Append(" plateIngredients=").Append(plateIngredientSlots);
            sb.Append(" (same-recipe extras use ceil(k/2))");
            sb.Append(" postProcess=").Append(postProcessSlots);
            sb.Append(" requiredClears=").Append(clearCount);
            sb.Append(" (=plates + ingredients + post)");
            sb.Append(" facilities=").Append(facilities.Count);
            sb.Append(" edges=").Append(edges.Count).Append('\n');
            foreach (var g in facilities.GroupBy(f => f.type))
                sb.Append("  ").Append(g.Key).Append('=').Append(g.Count()).Append('\n');
            Debug.Log(sb.ToString());
        }
    }
}
