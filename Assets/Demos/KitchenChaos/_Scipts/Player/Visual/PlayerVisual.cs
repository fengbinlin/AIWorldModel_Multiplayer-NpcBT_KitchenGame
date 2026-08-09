using UnityEngine;

namespace Kitchen.Visual
{
    public class PlayerVisual : MonoBehaviour
    {
        [SerializeField] private MeshRenderer headRenderer;
        [SerializeField] private MeshRenderer bodyRenderer;
        [SerializeField] private Material material;

        private void Awake()
        {
            EnsureRenderers();
            if (headRenderer == null) return;
            material = new Material(headRenderer.sharedMaterial);
            headRenderer.material = material;
            if (bodyRenderer != null)
                bodyRenderer.material = material;
        }

        public void SetColor(Color color)
        {
            if (material == null)
            {
                EnsureRenderers();
                if (headRenderer == null) return;
                material = new Material(headRenderer.sharedMaterial);
                headRenderer.material = material;
                if (bodyRenderer != null)
                    bodyRenderer.material = material;
            }
            material.color = color;
        }

        /// <summary>皮肤替换 VisualPrefab 后重新绑定 Head/Body 渲染器。</summary>
        public void RebindFromHierarchy()
        {
            headRenderer = null;
            bodyRenderer = null;
            EnsureRenderers();
            if (headRenderer == null) return;
            material = new Material(headRenderer.sharedMaterial);
            headRenderer.material = material;
            if (bodyRenderer != null)
                bodyRenderer.material = material;
        }

        private void EnsureRenderers()
        {
            if (headRenderer != null && bodyRenderer != null) return;

            var visualRoot = transform.Find("VisualPrefab") ?? transform;
            foreach (var r in visualRoot.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = r.name.ToLowerInvariant();
                if (headRenderer == null && n.Contains("head"))
                    headRenderer = r;
                else if (bodyRenderer == null && n.Contains("body"))
                    bodyRenderer = r;
            }

            if (headRenderer == null || bodyRenderer == null)
            {
                var all = visualRoot.GetComponentsInChildren<MeshRenderer>(true);
                if (headRenderer == null && all.Length > 0) headRenderer = all[0];
                if (bodyRenderer == null && all.Length > 1) bodyRenderer = all[1];
                else if (bodyRenderer == null) bodyRenderer = headRenderer;
            }
        }
    }
}
