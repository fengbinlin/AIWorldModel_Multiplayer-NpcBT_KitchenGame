using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Order definition: deliver a single kitchen item (usually an assembled / finished item).
    /// </summary>
    [CreateAssetMenu(fileName = "Recipe", menuName = "ScriptableObjects/Recipe", order = 2)]
    public class RecipeSo : ScriptableObject
    {
        public string recipeName;
        public KitchenObjEnum requiredItem;
    }
}
