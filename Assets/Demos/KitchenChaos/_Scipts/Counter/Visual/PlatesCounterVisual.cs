using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Static decorative stack of plates on the counter. Plates are infinite, so the
    /// visual never changes when players or AI take one.
    /// </summary>
    public class PlatesCounterVisual : MonoBehaviour
    {
        private const string DefaultPlateResourcePath =
            "So/Skin/SkinAsset/Ingredients/Skin_Plate_Visual";

        [SerializeField] private float plateOffset = 0.1f;
        [SerializeField] private int displayCount = PlatesCounter.DisplayPlateCount;
        [SerializeField] private GameObject platePrefab;
        [SerializeField] private Transform spawnPoint;

        private readonly List<GameObject> _plates = new();
        private bool _built;

        private void Start()
        {
            TryBuildStack();
        }

        /// <summary>Called after skin swap so the stack is rebuilt on the new visual root.</summary>
        public void TryBuildStack()
        {
            if (_built) return;
            EnsureReferences();
            if (platePrefab == null || spawnPoint == null) return;

            HideLegacyIndicators();
            ClearPlates();

            for (int i = 0; i < displayCount; i++)
            {
                var plate = StripToVisualOnly(Instantiate(platePrefab, spawnPoint));
                plate.transform.localPosition = new Vector3(0f, plateOffset * i, 0f);
                plate.transform.localRotation = Quaternion.identity;
                _plates.Add(plate);
            }

            _built = true;
        }

        private void EnsureReferences()
        {
            if (spawnPoint == null)
            {
                var existing = transform.Find("PlateSpawnPoint");
                if (existing != null)
                {
                    spawnPoint = existing;
                }
                else
                {
                    var go = new GameObject("PlateSpawnPoint");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = new Vector3(0f, 1.268f, 0f);
                    spawnPoint = go.transform;
                }
            }

            if (platePrefab == null)
                platePrefab = Resources.Load<GameObject>(DefaultPlateResourcePath);
        }

        private void HideLegacyIndicators()
        {
            var circle = transform.Find("CircleSprite");
            if (circle != null)
                circle.gameObject.SetActive(false);
        }

        private void ClearPlates()
        {
            for (int i = 0; i < _plates.Count; i++)
            {
                if (_plates[i] != null)
                    Destroy(_plates[i]);
            }

            _plates.Clear();
        }

        private static GameObject StripToVisualOnly(GameObject go)
        {
            if (go == null) return null;

            foreach (var nb in go.GetComponentsInChildren<NetworkBehaviour>(true))
                Destroy(nb);
            foreach (var no in go.GetComponentsInChildren<NetworkObject>(true))
                Destroy(no);
            foreach (var ko in go.GetComponentsInChildren<KitchenObj>(true))
                Destroy(ko);
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                Destroy(col);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                Destroy(rb);

            return go;
        }
    }
}
