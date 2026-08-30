using System;
using Kitchen.Skin;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    public class ContainerCounter : BaseCounter
    {
        private KitchenObjSo _kitchenObjSo;
        public KitchenObjEnum objEnum;
        public event Action OnInteractEvent;

        /// <summary>Legacy icon renderer; hidden when sample mesh is used.</summary>
        public SpriteRenderer re;

        private GameObject _sampleDisplay;
        private const string SampleName = "IngredientSampleDisplay";

        protected override void Awake()
        {
            base.Awake();
            HideObjectSprite();
        }

        private void Start()
        {
            _kitchenObjSo = DataTableManager.Sigleton != null
                ? DataTableManager.Sigleton.GetKitchenObjSo(objEnum)
                : null;
            RefreshSampleDisplay();
        }

        /// <summary>
        /// Rebuild the TopPoint sample mesh for <see cref="objEnum"/>.
        /// Call after skin swap or after PGC assigns ingredient type.
        /// </summary>
        public void RefreshSampleDisplay()
        {
            HideObjectSprite();
            ClearSampleDisplay();

            if (DataTableManager.Sigleton != null)
                _kitchenObjSo = DataTableManager.Sigleton.GetKitchenObjSo(objEnum);

            var anchor = GetHoldTransform();
            if (anchor == null)
                anchor = transform.Find("TopPoint");
            if (anchor == null)
                return;

            var sample = SpawnSampleVisual(objEnum);
            if (sample == null)
                return;

            sample.name = SampleName;
            sample.transform.SetParent(anchor, false);
            sample.transform.localPosition = Vector3.zero;
            sample.transform.localRotation = Quaternion.identity;
            sample.transform.localScale = Vector3.one;
            _sampleDisplay = sample;
        }

        private void ClearSampleDisplay()
        {
            if (_sampleDisplay != null)
            {
                Destroy(_sampleDisplay);
                _sampleDisplay = null;
            }

            var anchor = GetHoldTransform() ?? transform.Find("TopPoint");
            if (anchor == null) return;
            for (int i = anchor.childCount - 1; i >= 0; i--)
            {
                var child = anchor.GetChild(i);
                if (child != null && child.name == SampleName)
                    Destroy(child.gameObject);
            }
        }

        private void HideObjectSprite()
        {
            RebindObjectSprite();
            if (re != null)
                re.enabled = false;

            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != "ObjectSprite") continue;
                t.gameObject.SetActive(false);
                var sr = t.GetComponent<SpriteRenderer>();
                if (sr != null) sr.enabled = false;
            }
        }

        /// <summary>
        /// 换肤会 Destroy 旧 Visual，预制体上序列化的 ObjectSprite 引用会失效，需重新查找。
        /// </summary>
        private void RebindObjectSprite()
        {
            if (re != null) return;

            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != "ObjectSprite") continue;
                re = t.GetComponent<SpriteRenderer>();
                if (re != null) return;
            }
        }

        private static GameObject SpawnSampleVisual(KitchenObjEnum kind)
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

        public override void Interact(ICanHoldKitchenObj holder)
        {
            Debug.Log(holder + "尝试获取道具" + $"它是否有道具:{holder.HasKitchenObj()}");
            if (holder.HasKitchenObj())
            {
                Debug.Log("获取失败");
                return;
            }

            if (_kitchenObjSo == null && DataTableManager.Sigleton != null)
                _kitchenObjSo = DataTableManager.Sigleton.GetKitchenObjSo(objEnum);
            if (_kitchenObjSo == null)
            {
                Debug.LogWarning($"[ContainerCounter] No KitchenObjSo for {objEnum}");
                return;
            }

            Debug.Log("请求生成道具");
            KitchenObjOperator.SpawnKitchenObjRpc(_kitchenObjSo.kitchenObjEnum, holder);
            _OnInteractServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void _OnInteractServerRpc()
        {
            _OnInteractClientRpc();
        }

        [ClientRpc]
        private void _OnInteractClientRpc()
        {
            Debug.Log("交互事件触发");
            OnInteractEvent?.Invoke();
        }
    }
}
