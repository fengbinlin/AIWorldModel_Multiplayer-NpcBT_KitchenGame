namespace Kitchen
{
    /// <summary>
    /// All kitchen item types (raw, intermediate, assembled, burn waste).
    /// Orders reference a single required item; plate assembly may convert sets into one item.
    /// </summary>
    public enum KitchenObjEnum
    {
        // --- Legacy / base (values preserved) ---
        Tomato = 0,
        TomatoSlices = 1,
        CheeseBlock = 2,
        Bread = 3,                 // 汉堡皮
        Cabbage = 4,
        MeatPattyUncooked = 5,     // 牛肉
        CabbageSlices = 6,
        MeatPattyCooked = 7,       // 煮熟牛肉 / 牛排
        CheeseSlices = 8,
        MeatPattyBurned = 9,
        Plate = 10,

        // --- New meats / seafood ---
        ChickenRaw = 11,
        ChickenCooked = 12,        // 煮熟鸡腿 / 鸡排
        ChickenBurned = 13,
        Fish = 14,
        FishSlices = 15,           // 切碎鱼 = 可下单「切碎鱼肉」
        Shrimp = 16,
        ShrimpSlices = 17,

        // --- Assembly ingredients ---
        RiceBall = 18,
        Nori = 19,                 // 紫菜
        Cream = 20,
        PizzaDough = 21,

        // --- Assembled / facility outputs ---
        PizzaUnbaked = 22,         // 番茄鸡腿披萨（未烤）
        PizzaBaked = 23,           // 番茄鸡腿披萨
        ChickenBurger = 24,
        BeefBurger = 25,
        FishSushi = 26,
        ShrimpSushi = 27,
        TomatoSalad = 28,          // 番茄沙拉 = 未搅拌番茄奶昔
        CabbageSalad = 29,
        TomatoCabbageSalad = 30,
        TomatoShake = 31,
        CabbageShake = 32,
        TomatoCabbageShake = 33,
    }

    public enum KitchenObjStateEnum
    {
        Idle,
        Frying,
        Fried,
        Burned,
    }
}
