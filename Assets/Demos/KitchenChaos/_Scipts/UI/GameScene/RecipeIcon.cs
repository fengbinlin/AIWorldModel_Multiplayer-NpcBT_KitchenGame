using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Kitchen.UI
{
    /// <summary>
    /// One order card: recipe name + a single icon for the final deliverable dish
    /// (<see cref="RecipeSo.requiredItem"/>). Intermediate ingredients are not shown.
    /// </summary>
    public class RecipeIcon : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _textMeshPro;
        [SerializeField] private Transform iconContainer;
        [SerializeField] private GameObject iconPrefab;

        private Image _dishIcon;

        private void Awake()
        {
            if (_textMeshPro == null)
                _textMeshPro = GetComponentInChildren<TextMeshProUGUI>();
            EnsureSingleDishIconSlot();
        }

        public void SetRecipe(RecipeSo recipeData)
        {
            if (recipeData == null) return;

            if (_textMeshPro == null)
                _textMeshPro = GetComponentInChildren<TextMeshProUGUI>();
            if (_textMeshPro != null)
                _textMeshPro.text = recipeData.recipeName;

            EnsureSingleDishIconSlot();
            if (_dishIcon == null) return;

            KitchenObjSo so = null;
            try
            {
                if (DataTableManager.Sigleton != null)
                    so = DataTableManager.Sigleton.GetKitchenObjSo(recipeData.requiredItem);
            }
            catch
            {
                // missing SO
            }

            _dishIcon.sprite = so != null ? so.sprite : null;
            _dishIcon.enabled = _dishIcon.sprite != null;
            _dishIcon.gameObject.SetActive(true);
        }

        /// <summary>
        /// Keep exactly one icon under the container — the finished dish only.
        /// </summary>
        private void EnsureSingleDishIconSlot()
        {
            if (iconContainer == null) return;

            // Remove leftover multi-ingredient icons from older UI versions / pooling.
            for (int i = iconContainer.childCount - 1; i >= 0; i--)
            {
                var child = iconContainer.GetChild(i);
                if (_dishIcon != null && child == _dishIcon.transform)
                    continue;
                Destroy(child.gameObject);
            }

            if (_dishIcon != null) return;

            if (iconContainer.childCount > 0)
            {
                _dishIcon = iconContainer.GetChild(0).GetComponent<Image>();
                if (_dishIcon != null) return;
            }

            if (iconPrefab == null) return;
            var go = Instantiate(iconPrefab, iconContainer);
            go.name = "DishIcon";
            _dishIcon = go.GetComponent<Image>();
        }
    }
}
