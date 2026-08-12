using System.Collections.Generic;
using UnityEngine;

namespace Kitchen.UI
{
    public class PlateIconsUI : MonoBehaviour
    {
        public GameObject iconPrefab;
        private Plate _plate;
        private readonly List<KitchenObjIcon> _icons = new List<KitchenObjIcon>();

        private void Awake()
        {
            _plate = GetComponentInParent<Plate>();
        }

        private void OnEnable()
        {
            if (_plate == null) return;
            _plate.onIngredientAdded += OnIngredientAdded;
            _plate.onContentsChanged += RefreshIcons;
            RefreshIcons();
        }

        private void OnDisable()
        {
            if (_plate == null) return;
            _plate.onIngredientAdded -= OnIngredientAdded;
            _plate.onContentsChanged -= RefreshIcons;
        }

        private void OnIngredientAdded(object sender, KitchenObjEnum e)
        {
            // Full refresh handles assembly collapse / extract-to-oven empty plate.
            RefreshIcons();
        }

        private void RefreshIcons()
        {
            if (_plate == null || iconPrefab == null) return;

            for (int i = _icons.Count - 1; i >= 0; i--)
            {
                if (_icons[i] != null)
                    Destroy(_icons[i].gameObject);
            }
            _icons.Clear();

            var ingredients = _plate.GetIngredientsOrdered();
            if (ingredients == null || ingredients.Count == 0)
                return;

            foreach (var ingredient in ingredients)
            {
                var obj = Instantiate(iconPrefab, transform);
                var icon = obj.GetComponent<KitchenObjIcon>();
                if (icon == null)
                {
                    Destroy(obj);
                    continue;
                }

                _icons.Add(icon);
                var dataSo = DataTableManager.Sigleton.GetKitchenObjSo(ingredient);
                if (dataSo != null)
                    icon.SetData(dataSo);
            }
        }
    }
}
