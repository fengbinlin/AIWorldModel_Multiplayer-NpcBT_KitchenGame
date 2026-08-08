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
        /// </summary>
        public static bool TryMatch(HashSet<KitchenObjEnum> ingredients, out KitchenObjEnum output, out PlateAssemblySo rule)
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
