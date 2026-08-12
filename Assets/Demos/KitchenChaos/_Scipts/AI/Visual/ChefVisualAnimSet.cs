using UnityEngine;

namespace Kitchen.AI.Visual
{
    /// <summary>
    /// Animation clips for AI chef presentation (Layer lab / any humanoid set).
    /// </summary>
    [CreateAssetMenu(menuName = "Kitchen/AI/Chef Visual Anim Set", fileName = "ChefVisualAnimSet")]
    public class ChefVisualAnimSet : ScriptableObject
    {
        [Header("Locomotion")]
        public AnimationClip idle;
        public AnimationClip walk;
        public AnimationClip run;

        [Header("Interactions (one-shot)")]
        public AnimationClip pickup;
        public AnimationClip putDown;

        [Header("Stationary task")]
        [Tooltip("Loop while cutting / actively working.")]
        public AnimationClip work;
        [Tooltip("Loop while waiting on a cooker.")]
        public AnimationClip wait;
    }
}
