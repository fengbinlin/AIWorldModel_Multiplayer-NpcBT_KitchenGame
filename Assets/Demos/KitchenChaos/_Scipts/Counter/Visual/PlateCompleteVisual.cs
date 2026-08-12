using System.Collections.Generic;
using Kitchen.Skin;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Spawns stacked ingredient visuals on the plate (add order = bottom → top).
    /// Uses the same skin prefab scales as held/counter items (no extra plate shrink).
    /// </summary>
    public class PlateCompleteVisual : MonoBehaviour
    {
        [Header("Stacking")]
        [Tooltip("Extra air gap between layers after each item's own height.")]
        [SerializeField] private float layerGap = 0.05f;

        [Tooltip("Minimum Y step even if a mesh is very flat.")]
        [SerializeField] private float minLayerStep = 0.16f;

        [SerializeField] private Transform stackRoot;
        [SerializeField] private Vector3 stackBaseLocalPosition = new Vector3(0f, 0.05f, 0f);

        private Plate _plate;
        private readonly List<GameObject> _spawned = new();

        private void Awake()
        {
            _plate = GetComponentInParent<Plate>();
            EnsureStackRoot();
            HideLegacyStaticVisuals();
        }

        private void OnEnable()
        {
            if (_plate == null)
                _plate = GetComponentInParent<Plate>();
            if (_plate == null) return;
            _plate.onContentsChanged += RefreshVisuals;
            RefreshVisuals();
        }

        private void OnDisable()
        {
            if (_plate == null) return;
            _plate.onContentsChanged -= RefreshVisuals;
        }

        private void EnsureStackRoot()
        {
            if (stackRoot != null) return;
            var existing = transform.Find("IngredientStack");
            if (existing != null)
            {
                stackRoot = existing;
                return;
            }

            var go = new GameObject("IngredientStack");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            stackRoot = go.transform;
        }

        private void HideLegacyStaticVisuals()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (stackRoot != null && child == stackRoot) continue;
                child.gameObject.SetActive(false);
            }
        }

        private void RefreshVisuals()
        {
            ClearSpawned();
            if (_plate == null) return;

            var ordered = _plate.GetIngredientsOrdered();
            if (ordered == null || ordered.Count == 0) return;

            float cursorY = stackBaseLocalPosition.y;
            for (int i = 0; i < ordered.Count; i++)
            {
                var kind = ordered[i];
                var visual = SpawnIngredientVisual(kind);
                if (visual == null) continue;

                visual.name = $"Stack_{kind}";
                visual.transform.SetParent(stackRoot, false);
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localPosition = new Vector3(
                    stackBaseLocalPosition.x, cursorY, stackBaseLocalPosition.z);

                float height = MeasureHeightInStackRoot(visual);
                float step = Mathf.Max(minLayerStep, height + layerGap);
                cursorY += step;

                _spawned.Add(visual);
            }
        }

        private void ClearSpawned()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    Destroy(_spawned[i]);
            }
            _spawned.Clear();
        }

        private float MeasureHeightInStackRoot(GameObject visual)
        {
            if (!TryGetBoundsIn(stackRoot, visual, out var b))
                return 0f;
            return b.size.y;
        }

        private static bool TryGetBoundsIn(Transform space, GameObject go, out Bounds localBounds)
        {
            localBounds = default;
            if (space == null || go == null) return false;

            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0)
                return false;

            bool has = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                EncapsulateWorldBoundsAsLocal(space, r.bounds, ref localBounds, ref has);
            }

            return has;
        }

        private static void EncapsulateWorldBoundsAsLocal(
            Transform space, Bounds worldBounds, ref Bounds localBounds, ref bool has)
        {
            Vector3 c = worldBounds.center;
            Vector3 e = worldBounds.extents;
            for (int ix = -1; ix <= 1; ix += 2)
            for (int iy = -1; iy <= 1; iy += 2)
            for (int iz = -1; iz <= 1; iz += 2)
            {
                var world = c + new Vector3(e.x * ix, e.y * iy, e.z * iz);
                var local = space.InverseTransformPoint(world);
                if (!has)
                {
                    localBounds = new Bounds(local, Vector3.zero);
                    has = true;
                }
                else
                {
                    localBounds.Encapsulate(local);
                }
            }
        }

        private GameObject SpawnIngredientVisual(KitchenObjEnum kind)
        {
            var mgr = SkinManager.Instance ?? SkinManager.EnsureExists();
            if (mgr.TryGetIngredientEntry(kind, out var entry, out _))
            {
                bool hasMm = entry.meshMaterial != null && entry.meshMaterial.HasAny;
                bool hasPrefab = entry.visualPrefab != null;
                var mode = SkinCatalogSo.ResolveMode(hasMm, hasPrefab, entry.preferMode);

                if (mode == SkinResolvedMode.Prefab && entry.visualPrefab != null)
                    return StripToVisualOnly(Instantiate(entry.visualPrefab));

                if (mode == SkinResolvedMode.MeshMaterial && entry.meshMaterial != null)
                    return CreateMeshVisual(kind, entry.meshMaterial);
            }

            if (DataTableManager.Sigleton == null) return null;
            var so = DataTableManager.Sigleton.GetKitchenObjSo(kind);
            if (so == null || so.prefab == null) return null;

            var root = Instantiate(so.prefab);
            StripToVisualOnly(root);
            var visualChild = FindVisualChild(root.transform);
            if (visualChild != null && visualChild != root.transform)
            {
                visualChild.SetParent(null, true);
                Destroy(root);
                return visualChild.gameObject;
            }

            return root;
        }

        private static GameObject CreateMeshVisual(KitchenObjEnum kind, SkinMeshMaterial mm)
        {
            var go = new GameObject($"Mesh_{kind}");
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            if (mm.mesh != null)
                filter.sharedMesh = mm.mesh;
            if (mm.material != null)
                renderer.sharedMaterial = mm.material;
            return go;
        }

        private static Transform FindVisualChild(Transform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name.Contains("_Visual") || c.name.EndsWith("Visual") || c.name.Contains("mesh"))
                    return c;
            }
            return root.childCount > 0 ? root.GetChild(0) : root;
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
