using UnityEngine;

namespace Kitchen.UI
{
    /// <summary>
    /// Plate ingredient icon strip — permanently disabled; 3D stack visuals replace it.
    /// </summary>
    public class PlateIconsUI : MonoBehaviour
    {
        private void Awake()
        {
            gameObject.SetActive(false);
            enabled = false;
        }
    }
}
