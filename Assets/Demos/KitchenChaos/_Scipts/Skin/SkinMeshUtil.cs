using UnityEngine;

namespace Kitchen.Skin
{
    public static class SkinMeshUtil
    {
        public static void ApplyMeshMaterial(GameObject target, Mesh mesh, Material material)
        {
            if (target == null) return;

            var filter = target.GetComponent<MeshFilter>();
            if (filter == null) filter = target.GetComponentInChildren<MeshFilter>(true);
            if (filter != null && mesh != null)
                filter.sharedMesh = mesh;

            var renderer = target.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = target.GetComponentInChildren<MeshRenderer>(true);
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;
        }

        public static void ApplyMeshMaterialToFirstMesh(Transform root, Mesh mesh, Material material)
        {
            if (root == null) return;
            var filter = root.GetComponentInChildren<MeshFilter>(true);
            if (filter == null) return;
            if (mesh != null) filter.sharedMesh = mesh;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;
        }

        /// <summary>
        /// 替换逻辑根下旧的 Visual 子物体为新预制体实例。
        /// 优先替换名含 "_Visual" 的子物体；否则替换以 visualRootHint 命名的节点。
        /// </summary>
        public static GameObject ReplaceVisualChild(Transform logicRoot, GameObject visualPrefab, string keepName = null)
        {
            if (logicRoot == null || visualPrefab == null) return null;

            Transform old = null;
            for (int i = 0; i < logicRoot.childCount; i++)
            {
                var c = logicRoot.GetChild(i);
                if (c.name.Contains("_Visual") || c.name.EndsWith("Visual"))
                {
                    old = c;
                    break;
                }
            }

            string name = keepName ?? visualPrefab.name;
            Vector3 localPos = Vector3.zero;
            Quaternion localRot = Quaternion.identity;
            Vector3 localScale = Vector3.one;
            int sibling = logicRoot.childCount;

            if (old != null)
            {
                name = string.IsNullOrEmpty(keepName) ? old.name : keepName;
                localPos = old.localPosition;
                localRot = old.localRotation;
                localScale = old.localScale;
                sibling = old.GetSiblingIndex();
                Object.Destroy(old.gameObject);
            }

            var inst = Object.Instantiate(visualPrefab, logicRoot);
            inst.name = name;
            inst.transform.localPosition = localPos;
            inst.transform.localRotation = localRot;
            inst.transform.localScale = localScale;
            inst.transform.SetSiblingIndex(Mathf.Clamp(sibling, 0, logicRoot.childCount - 1));
            return inst;
        }
    }
}
