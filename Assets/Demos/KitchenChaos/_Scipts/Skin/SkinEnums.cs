namespace Kitchen.Skin
{
    /// <summary>
    /// Controls where an object's skin id is resolved from.
    /// Global uses the catalog default, Kind uses the per-kind override,
    /// and Specific uses the id configured on the object.
    /// </summary>
    public enum SkinIdGranularity
    {
        Global = 0,
        Kind = 1,
        Specific = 2,
        Manager = 3,
    }

    public enum SkinConfigSource
    {
        InlineThenCatalog = 0,
        CatalogThenInline = 1,
        InlineOnly = 2,
        CatalogOnly = 3,
    }

    public enum SkinPreferMode
    {
        PreferMeshMaterial = 0,
        PreferPrefab = 1,
    }

    public enum SkinResolvedMode
    {
        None = 0,
        MeshMaterial = 1,
        Prefab = 2,
    }

    /// <summary>柜子视觉部件槽（固定枚举）。</summary>
    public enum CounterPartSlot
    {
        CounterBody = 0,
        Knife = 1,
        CuttingBoard = 2,
        Pot = 3,
        StoveBurner = 4,
        TrashBin = 5,
        ContainerDoor = 6,
        Extra1 = 7,
        Extra2 = 8,
    }

    public enum SkinCounterKind
    {
        Clear = 0,
        Container = 1,
        Cutting = 2,
        Stove = 3,
        Oven = 4,
        Blender = 5,
        Plates = 6,
        Delivery = 7,
        Trash = 8,
        Wall = 9,
    }

    public enum SkinCharacterKind
    {
        Player = 0,
        AIPlayer = 1,
    }
}
