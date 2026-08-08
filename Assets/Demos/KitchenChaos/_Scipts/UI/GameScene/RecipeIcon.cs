using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Kitchen.UI
{
    public class RecipeIcon : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _textMeshPro;
        [SerializeField] private Transform iconContainer;
        [SerializeField] private GameObject iconPrefab;
        private List<GameObject> _icons = new();

        private void Awake()
        {
            _textMeshPro = GetComponentInChildren<TextMeshProUGUI>();
        }

        public void SetRecipe(RecipeSo recipeData)
        {
            if (recipeData == null) return;
            _textMeshPro.text = recipeData.recipeName;

            // Orders are a single required item now.
            var items = new[] { recipeData.requiredItem };
            for (int i = 0; i < items.Length; i++)
            {
                var objEnum = items[i];
                KitchenObjSo so = null;
                try
                {
                    so = DataTableManager.Sigleton.GetKitchenObjSo(objEnum);
                }
                catch
                {
                    // missing SO
                }

                if (i < _icons.Count)
                {
                    _icons[i].gameObject.SetActive(true);
                    if (so != null)
                        _icons[i].GetComponent<Image>().sprite = so.sprite;
                    continue;
                }

                var icon = Instantiate(iconPrefab, iconContainer);
                if (so != null)
                    icon.GetComponent<Image>().sprite = so.sprite;
                _icons.Add(icon);
            }

            for (int i = items.Length; i < _icons.Count; i++)
                _icons[i].gameObject.SetActive(false);
        }
    }
}
