using System.Collections.Generic;
using System.Linq;
using Nico.Exception;
using UnityEngine;

namespace Kitchen.UI
{
    public class RecipeManagerUI : MonoBehaviour
    {
        public Transform recipeUIContainer;
        public GameObject recipeUIPrefab;
        public List<RecipeIcon> recipeIcons = new();

        private void OnEnable()
        {
            TrySubscribe();
            RefreshFromDeliveryManager();
        }

        private void Start()
        {
            // DeliveryManager / first orders may appear after OnEnable.
            TrySubscribe();
            RefreshFromDeliveryManager();
        }

        private void OnDisable()
        {
            try
            {
                if (DeliveryManager.Instance == null) return;
                DeliveryManager.Instance.OnOrderFinished -= _OnOrderFinished;
                DeliveryManager.Instance.OnOrderAdded -= _OnOrderAdded;
                DeliveryManager.Instance.OnOrderSuccess -= _OnOrderSuccess;
                DeliveryManager.Instance.OnOrderFailed -= _OnOrderFailed;
            }
            catch (SingletonException)
            {
            }
        }

        private void TrySubscribe()
        {
            try
            {
                var deliveryManager = DeliveryManager.Instance;
                if (deliveryManager == null) return;

                deliveryManager.OnOrderFinished -= _OnOrderFinished;
                deliveryManager.OnOrderAdded -= _OnOrderAdded;
                deliveryManager.OnOrderSuccess -= _OnOrderSuccess;
                deliveryManager.OnOrderFailed -= _OnOrderFailed;

                deliveryManager.OnOrderFinished += _OnOrderFinished;
                deliveryManager.OnOrderAdded += _OnOrderAdded;
                deliveryManager.OnOrderSuccess += _OnOrderSuccess;
                deliveryManager.OnOrderFailed += _OnOrderFailed;
            }
            catch (SingletonException)
            {
            }
        }

        private void RefreshFromDeliveryManager()
        {
            try
            {
                var dm = DeliveryManager.Instance;
                if (dm == null) return;
                SetWaitingRecipes(dm.GetWaitingQueue());
            }
            catch (SingletonException)
            {
            }
        }

        private void _OnOrderFinished(object sender, RecipeSo e)
        {
            RefreshFromDeliveryManager();
        }

        private void _OnOrderSuccess(object sender, Vector3 e)
        {
            RefreshFromDeliveryManager();
        }

        private void _OnOrderFailed(object sender, Vector3 e)
        {
            RefreshFromDeliveryManager();
        }

        private void _OnOrderAdded(object sender, RecipeSo e)
        {
            RefreshFromDeliveryManager();
        }

        public void SetWaitingRecipes(ICollection<RecipeSo> recipes)
        {
            if (recipes == null) return;
            if (recipeUIPrefab == null || recipeUIContainer == null)
            {
                Debug.LogWarning("[RecipeManagerUI] Missing recipeUIPrefab or recipeUIContainer.");
                return;
            }

            for (int i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes.ElementAt(i);
                if (i < recipeIcons.Count)
                {
                    recipeIcons[i].gameObject.SetActive(true);
                    recipeIcons[i].SetRecipe(recipe);
                    continue;
                }

                var recipeUI = Instantiate(recipeUIPrefab, recipeUIContainer).GetComponent<RecipeIcon>();
                if (recipeUI == null)
                {
                    Debug.LogWarning("[RecipeManagerUI] recipeUIPrefab missing RecipeIcon.");
                    continue;
                }

                recipeIcons.Add(recipeUI);
                recipeUI.SetRecipe(recipe);
            }

            for (int i = recipes.Count; i < recipeIcons.Count; i++)
                recipeIcons[i].gameObject.SetActive(false);
        }
    }
}
