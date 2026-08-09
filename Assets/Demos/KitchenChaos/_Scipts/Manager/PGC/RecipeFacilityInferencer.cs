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
    /// 盘架 = ceil(菜品数/2)；空台不在此阶段生成。
    /// </summary>
    public static class RecipeFacilityInferencer
    {
        public static void Infer(
            IReadOnlyList<RecipeSo> recipes,
            out List<PGCFacilityNode> facilities,
            out List<PGCMissionEdge> edges)
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

            for (int r = 0; r < recipes.Count; r++)
            {
                var recipe = recipes[r];
                if (recipe == null) continue;

                var chain = BuildFacilityChain(recipe, outputToProcess);
                var recipeNodes = new List<PGCFacilityNode>();
                int step = 0;
                foreach (var demand in chain)
                {
                    var node = new PGCFacilityNode
                    {
                        id = nextId++,
                        type = demand.type,
                        hasIngredient = demand.hasIngredient,
                        ingredient = demand.ingredient,
                        recipeIndex = r,
                        stepIndex = step++,
                        label = demand.label,
                    };
                    recipeNodes.Add(node);
                    facilities.Add(node);
                }

                // 菜品工序链
                for (int i = 0; i < recipeNodes.Count - 1; i++)
                {
                    edges.Add(new PGCMissionEdge
                    {
                        fromId = recipeNodes[i].id,
                        toId = recipeNodes[i + 1].id,
                        kind = "mission",
                    });
                }

                // 链尾 → 轮询盘架 →（盘架已连出餐）
                if (recipeNodes.Count > 0 && plateNodes.Count > 0)
                {
                    var assignedPlate = plateNodes[r % plateNodes.Count];
                    edges.Add(new PGCMissionEdge
                    {
                        fromId = recipeNodes[recipeNodes.Count - 1].id,
                        toId = assignedPlate.id,
                        kind = "mission",
                    });
                }
            }

            LogSummary(recipes, facilities, edges);
        }

        private struct FacilityDemand
        {
            public FacilityType type;
            public KitchenObjEnum ingredient;
            public bool hasIngredient;
            public string label;
        }

        /// <summary>
        /// 与 Blackboard.BuildRecipeSteps 对齐：原料柜 / 加工柜按链路逐步累加（不含盘架出餐）。
        /// </summary>
        private static List<FacilityDemand> BuildFacilityChain(
            RecipeSo recipe,
            Dictionary<KitchenObjEnum, KitchenProcessSo> outputToProcess)
        {
            var demands = new List<FacilityDemand>();
            var required = recipe.requiredItem;

            KitchenProcessSo postProcess = null;
            KitchenObjEnum plateTarget = required;
            if (outputToProcess.TryGetValue(required, out var maybePost)
                && (maybePost.requiredFacility == FacilityEnum.OvenCounter
                    || maybePost.requiredFacility == FacilityEnum.BlenderCounter))
            {
                postProcess = maybePost;
                plateTarget = maybePost.inputEnum;
            }

            var assembly = PlateAssemblyMatcher.FindAssemblyProducing(plateTarget);
            var toAddOnPlate = new List<KitchenObjEnum>();
            if (assembly != null && assembly.inputs != null && assembly.inputs.Length > 0)
            {
                foreach (var input in assembly.inputs)
                    toAddOnPlate.Add(input);
            }
            else
            {
                toAddOnPlate.Add(plateTarget);
            }

            foreach (var ing in toAddOnPlate)
                AppendIngredientChain(ing, outputToProcess, demands);

            if (postProcess != null)
            {
                demands.Add(new FacilityDemand
                {
                    type = FacilityToType(postProcess.requiredFacility),
                    label = $"{FacilityToType(postProcess.requiredFacility)}:{postProcess.inputEnum}→{postProcess.outputEnum}",
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
            List<PGCMissionEdge> edges)
        {
            var sb = new StringBuilder();
            sb.Append("[PGC-Infer] recipes=").Append(recipes.Count);
            sb.Append(" facilities=").Append(facilities.Count);
            sb.Append(" edges=").Append(edges.Count).Append('\n');
            foreach (var g in facilities.GroupBy(f => f.type))
                sb.Append("  ").Append(g.Key).Append('=').Append(g.Count()).Append('\n');
            Debug.Log(sb.ToString());
        }
    }
}
