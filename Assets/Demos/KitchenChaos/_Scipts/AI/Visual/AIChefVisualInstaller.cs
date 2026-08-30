using UnityEngine;

namespace Kitchen.AI.Visual
{
    /// <summary>
    /// Attaches Layer-lab character presentation (Animancer + clip set) onto an AI chef
    /// after the visual skin is applied. Does not modify AI decision logic.
    /// </summary>
    public static class AIChefVisualInstaller
    {
        public const string DefaultAnimSetResourcePath = "So/AI/ChefVisualAnimSet_LayerLab";

        /// <summary>
        /// Local scale applied to the skinned character under PlayerVisual.
        /// AIPlayer root is often 0.7; 2.5 → ~1.75 world scale (readable in kitchen).
        /// </summary>
        public const float DefaultVisualLocalScale = 2.5f;

        public static AIChefVisualPresenter EnsureOn(GameObject chefRoot, ChefVisualAnimSet animSet = null)
        {
            if (chefRoot == null) return null;

            // CharacterSimple is static — no Animancer / Layer-lab clips.
            var skin = Kitchen.Skin.SkinManager.Instance;
            bool animEnabled = skin != null && skin.EnableCharacterAnimation;
            if (!animEnabled)
            {
                var existing = chefRoot.GetComponent<AIChefVisualPresenter>();
                if (existing != null)
                    Object.Destroy(existing);
                return null;
            }

            var chef = chefRoot.GetComponent<AIChefController>();
            if (chef == null)
                chef = chefRoot.GetComponentInChildren<AIChefController>();

            var presenter = chefRoot.GetComponent<AIChefVisualPresenter>();
            if (presenter == null)
                presenter = chefRoot.GetComponentInChildren<AIChefVisualPresenter>(true);
            if (presenter == null)
                presenter = chefRoot.AddComponent<AIChefVisualPresenter>();

            if (animSet == null)
                animSet = Resources.Load<ChefVisualAnimSet>(DefaultAnimSetResourcePath);
            presenter.Configure(animSet);

            // Prefer Animator on the skinned character under PlayerVisual.
            var animator = FindBestAnimator(chefRoot.transform);
            if (animator != null)
            {
                animator.runtimeAnimatorController = null;
                var animancer = animator.GetComponent<Animancer.AnimancerComponent>();
                if (animancer == null)
                    animancer = animator.gameObject.AddComponent<Animancer.AnimancerComponent>();
                animancer.Animator = animator;

                ApplyVisualScale(animator.transform, DefaultVisualLocalScale);
            }

            if (chef != null)
                presenter.Bind(chef);

            return presenter;
        }

        private static void ApplyVisualScale(Transform characterRoot, float localScale)
        {
            if (characterRoot == null) return;
            characterRoot.localScale = Vector3.one * localScale;
        }

        private static Animator FindBestAnimator(Transform root)
        {
            var playerVisual = root.Find("PlayerVisual");
            var searchRoot = playerVisual != null ? playerVisual : root;
            var animators = searchRoot.GetComponentsInChildren<Animator>(true);
            Animator best = null;
            int bestBones = -1;
            foreach (var a in animators)
            {
                if (a == null) continue;
                int bones = a.avatar != null && a.avatar.isValid ? 1 : 0;
                // Prefer the Layer-lab character (has many skinned meshes).
                int meshes = a.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                int score = bones * 1000 + meshes;
                if (score > bestBones)
                {
                    bestBones = score;
                    best = a;
                }
            }
            return best;
        }
    }
}
