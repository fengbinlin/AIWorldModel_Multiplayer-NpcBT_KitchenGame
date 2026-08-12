namespace Kitchen
{
    /// <summary>
    /// Shared real-world-ish footprints relative to Bread (= 1).
    /// Used by plate stacking and skin scale alignment so held / counter / plate stay consistent.
    /// </summary>
    public static class IngredientVisualProportion
    {
        public static float GetRelativeFootprint(KitchenObjEnum kind)
        {
            switch (kind)
            {
                case KitchenObjEnum.Bread:
                case KitchenObjEnum.PizzaDough:
                case KitchenObjEnum.PizzaUnbaked:
                case KitchenObjEnum.PizzaBaked:
                case KitchenObjEnum.BeefBurger:
                case KitchenObjEnum.ChickenBurger:
                    return 1.00f;

                case KitchenObjEnum.CabbageSlices:
                case KitchenObjEnum.Nori:
                    return 0.92f;

                case KitchenObjEnum.MeatPattyUncooked:
                case KitchenObjEnum.MeatPattyCooked:
                case KitchenObjEnum.MeatPattyBurned:
                case KitchenObjEnum.ChickenRaw:
                case KitchenObjEnum.ChickenCooked:
                case KitchenObjEnum.ChickenBurned:
                    return 0.82f;

                case KitchenObjEnum.CheeseSlices:
                    return 0.72f;

                case KitchenObjEnum.TomatoSlices:
                    return 0.55f;

                case KitchenObjEnum.FishSlices:
                    return 0.55f;

                case KitchenObjEnum.ShrimpSlices:
                    return 0.42f;

                case KitchenObjEnum.Tomato:
                    return 0.62f;

                case KitchenObjEnum.Cabbage:
                    return 0.70f;

                case KitchenObjEnum.CheeseBlock:
                    return 0.70f;

                case KitchenObjEnum.Fish:
                    return 0.65f;

                case KitchenObjEnum.Shrimp:
                    return 0.38f;

                case KitchenObjEnum.RiceBall:
                case KitchenObjEnum.FishSushi:
                case KitchenObjEnum.ShrimpSushi:
                    return 0.50f;

                case KitchenObjEnum.Cream:
                    return 0.32f;

                case KitchenObjEnum.TomatoSalad:
                case KitchenObjEnum.CabbageSalad:
                case KitchenObjEnum.TomatoCabbageSalad:
                case KitchenObjEnum.TomatoShake:
                case KitchenObjEnum.CabbageShake:
                case KitchenObjEnum.TomatoCabbageShake:
                    return 0.72f;

                case KitchenObjEnum.Plate:
                    return 1.15f;

                default:
                    return 0.75f;
            }
        }

        /// <summary>Map Skin_*_Visual prefab stem → relative footprint.</summary>
        public static float GetRelativeFootprintBySkinName(string prefabFileName)
        {
            if (string.IsNullOrEmpty(prefabFileName))
                return 0.75f;

            var name = prefabFileName
                .Replace("Skin_", "")
                .Replace("_Visual", "")
                .Replace(".prefab", "");

            if (System.Enum.TryParse(name, out KitchenObjEnum kind))
                return GetRelativeFootprint(kind);

            // Prefab names that don't match enum exactly
            return name switch
            {
                "CabbageSliced" => GetRelativeFootprint(KitchenObjEnum.CabbageSlices),
                _ => 0.75f
            };
        }
    }
}
