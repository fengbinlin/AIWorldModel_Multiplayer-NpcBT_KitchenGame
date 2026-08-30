using System;
using UnityEngine;

namespace Kitchen.Skin
{
    /// <summary>
    /// Lives on CharacterSimple. Owns body material, eye exclusion, tint, and no-animation.
    /// Does not read SkinCatalog character entries — fully decoupled from catalog SO.
    /// Optional: subscribe to <see cref="SkinManager.CharacterTintRequested"/> when tint
    /// is broadcast globally; normally the host calls <see cref="SetTint"/> directly.
    /// </summary>
    [DisallowMultipleComponent]
    public class CharacterSkinVisual : MonoBehaviour
    {
        public const string DefaultMaterialResourcePath = "So/Skin/SkinAsset/Characters/lilToonMaterialDemo 1";

        [Header("Materials")]
        [SerializeField] private Material bodyMaterial;
        [Tooltip("Keep-black features (eyes/brows). Null = leave existing mats, force black tint.")]
        [SerializeField] private Material keepBlackMaterial;
        [SerializeField] private Color keepBlackColor = Color.black;

        [Header("Keep-black parts")]
        [Tooltip("Renderer names that stay black (eyes, square brows). Exact match, case-insensitive.")]
        [SerializeField] private string[] keepBlackObjectNames =
        {
            "Sphere",
            "Sphere (1)",
            "Cube",
        };

        [Header("Animation")]
        [SerializeField] private bool disableAnimation = true;

        [Header("SkinManager subscription")]
        [Tooltip("When on, listens to SkinManager.CharacterTintRequested for this host.")]
        [SerializeField] private bool subscribeToSkinManagerTint;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int MainColorId = Shader.PropertyToID("_MainColor");

        private bool _prepared;
        private bool _subscribed;
        private Color _currentTint = Color.white;

        public Color CurrentTint => _currentTint;

        private void Awake()
        {
            Prepare();
            if (subscribeToSkinManagerTint)
                SubscribeTint();
        }

        private void OnDestroy() => UnsubscribeTint();

        /// <summary>Apply body/eye materials and disable anim. Safe to call more than once.</summary>
        public void Prepare()
        {
            if (_prepared) return;

            if (bodyMaterial == null)
                bodyMaterial = Resources.Load<Material>(DefaultMaterialResourcePath);

            if (disableAnimation)
                DisableAnimation();

            ApplyMaterials();
            _prepared = true;
        }

        public void SetTint(Color color)
        {
            Prepare();
            _currentTint = color;

            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer is SpriteRenderer) continue;
                if (IsKeepBlackRenderer(renderer)) continue;

                var mats = renderer.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    SetLilToonColor(mats[i], color);
                }
                renderer.materials = mats;
            }
        }

        public void SetSubscribeToSkinManagerTint(bool enabled)
        {
            subscribeToSkinManagerTint = enabled;
            if (enabled) SubscribeTint();
            else UnsubscribeTint();
        }

        private void ApplyMaterials()
        {
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer is SpriteRenderer) continue;

                if (IsKeepBlackRenderer(renderer))
                {
                    if (keepBlackMaterial != null)
                        AssignShared(renderer, keepBlackMaterial);
                    else
                        ForceKeepBlack(renderer);
                    continue;
                }

                if (bodyMaterial != null)
                    AssignShared(renderer, bodyMaterial);
            }
        }

        private void ForceKeepBlack(Renderer renderer)
        {
            var mats = renderer.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                SetLilToonColor(mats[i], keepBlackColor);
            }
            renderer.materials = mats;
        }

        private static void AssignShared(Renderer renderer, Material mat)
        {
            int count = Mathf.Max(1, renderer.sharedMaterials.Length);
            var slots = new Material[count];
            for (int i = 0; i < count; i++)
                slots[i] = mat;
            renderer.sharedMaterials = slots;
        }

        private bool IsKeepBlackRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            string name = renderer.gameObject.name;
            if (keepBlackObjectNames != null)
            {
                for (int i = 0; i < keepBlackObjectNames.Length; i++)
                {
                    if (string.Equals(name, keepBlackObjectNames[i], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Fallback: Unity primitive eyes / square brow.
            return name.StartsWith("Sphere", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("Cube", StringComparison.OrdinalIgnoreCase);
        }

        private void DisableAnimation()
        {
            foreach (var animator in GetComponentsInChildren<Animator>(true))
            {
                if (animator == null) continue;
                animator.runtimeAnimatorController = null;
                animator.enabled = false;
                animator.cullingMode = AnimatorCullingMode.CullCompletely;
            }

            foreach (var animancer in GetComponentsInChildren<Animancer.AnimancerComponent>(true))
            {
                if (animancer != null)
                    Destroy(animancer);
            }
        }

        private void SubscribeTint()
        {
            if (_subscribed) return;
            var mgr = SkinManager.Instance;
            if (mgr == null) return;
            mgr.CharacterTintRequested += OnTintRequested;
            _subscribed = true;
        }

        private void UnsubscribeTint()
        {
            if (!_subscribed) return;
            var mgr = SkinManager.Instance;
            if (mgr != null)
                mgr.CharacterTintRequested -= OnTintRequested;
            _subscribed = false;
        }

        private void OnTintRequested(GameObject hostRoot, Color color)
        {
            if (hostRoot == null) return;
            // Accept tint if this visual is under the host.
            if (!transform.IsChildOf(hostRoot.transform) && hostRoot != gameObject)
                return;
            SetTint(color);
        }

        private static void SetLilToonColor(Material mat, Color color)
        {
            if (mat.HasProperty(ColorId))
                mat.SetColor(ColorId, color);
            if (mat.HasProperty(BaseColorId))
                mat.SetColor(BaseColorId, color);
            if (mat.HasProperty(MainColorId))
                mat.SetColor(MainColorId, color);
            if (!mat.HasProperty(ColorId) && !mat.HasProperty(BaseColorId))
                mat.color = color;
        }
    }
}
