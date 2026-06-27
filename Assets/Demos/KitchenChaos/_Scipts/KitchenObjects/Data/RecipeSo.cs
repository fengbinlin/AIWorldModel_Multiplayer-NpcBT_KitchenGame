using UnityEngine;

namespace Kitchen
{
    [CreateAssetMenu(fileName = "Recipe", menuName = "ScriptableObjects/Recipe", order = 2)]
    public class RecipeSo : ScriptableObject
    {
        public string recipeName;
        public KitchenObjEnum[] ingredients;
    }
}
