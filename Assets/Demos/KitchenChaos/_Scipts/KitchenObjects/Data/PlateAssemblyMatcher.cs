using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Loads plate assembly recipes and matches exact ingredient sets.
    /// </summary>
    public static class PlateAssemblyMatcher
    {
        private const string Dir = "So/Assemblies/";
        private static List<PlateAssemblySo> _assemblies;
        private static bool _loaded;

        public static IReadOnlyList<PlateAssemblySo> All
        {
            get
            {
                EnsureLoaded();
                return _assemblies;
            }
        }

        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            var loaded = Resources.LoadAll<PlateAssemblySo>(Dir);
            _assemblies = new List<PlateAssemblySo>(loaded);
            _loaded = true;
        }

        /// <summary>
        /// If <paramref name="ingredients"/> exactly matches an assembly recipe, returns its output.
        /// When <paramref name="requiredOutput"/> is set, only that assembly may match — this
        /// prevents partial sets (e.g. tomato + cream) from assembling into a smaller salad
        /// while a larger order-bound salad is still being built.
        /// </summary>
        public static bool TryMatch(
            HashSet<KitchenObjEnum> ingredients,
            out KitchenObjEnum output,
            out PlateAssemblySo rule,
            KitchenObjEnum? requiredOutput = null)
        {
            EnsureLoaded();
            output = default;
            rule = null;
            if (ingredients == null || ingredients.Count == 0 || _assemblies == null)
                return false;

            foreach (var a in _assemblies)
            {
                if (a == null || a.inputs == null || a.inputs.Length == 0)
                    continue;
                if (requiredOutput.HasValue && a.output != requiredOutput.Value)
                    continue;
                if (a.inputs.Length != ingredients.Count)
                    continue;
                if (a.inputs.All(ingredients.Contains))
                {
                    output = a.output;
                    rule = a;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Plate assembly target for an order's required item. Blender/oven finals use their
        /// pre-process assembled input (e.g. shake → salad before blending).
        /// </summary>
        public static KitchenObjEnum ResolvePlateAssemblyTarget(KitchenObjEnum requiredItem)
        {
            var processes = Resources.LoadAll<KitchenProcessSo>("So/Processes/");
            foreach (var process in processes)
            {
                if (process == null || process.outputEnum != requiredItem)
                    continue;
                if (process.requiredFacility == FacilityEnum.OvenCounter
                    || process.requiredFacility == FacilityEnum.BlenderCounter)
                    return process.inputEnum;
            }

            return requiredItem;
        }

        /// <summary>Find assembly whose output is the given item (for AI planning).</summary>
        public static PlateAssemblySo FindAssemblyProducing(KitchenObjEnum output)
        {
            EnsureLoaded();
            return _assemblies?.FirstOrDefault(a => a != null && a.output == output);
        }

        public static bool IsBurnedWaste(KitchenObjEnum item)
        {
            return item == KitchenObjEnum.MeatPattyBurned
                   || item == KitchenObjEnum.ChickenBurned;
        }
    }
}
