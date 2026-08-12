using Animancer;
using Animancer.FSM;
using UnityEngine;

namespace Kitchen.AI.Visual
{
    /// <summary>
    /// High-level visual modes driven by <see cref="AIChefController"/> without
    /// embedding animation logic inside the AI FSM.
    /// </summary>
    public enum ChefVisualMode
    {
        Idle,
        Move,
        Interact,
        Work,
        Wait,
    }

    /// <summary>
    /// Animancer presentation layer. Lives on the AI root (or visual child),
    /// binds to <see cref="AIChefController"/> at runtime, and plays Layer-lab clips.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]
    public class AIChefVisualPresenter : MonoBehaviour
    {
        [SerializeField] private AnimancerComponent _animancer;
        [SerializeField] private ChefVisualAnimSet _animSet;
        [SerializeField] private float _fadeDuration = 0.08f;
        [SerializeField] private float _runNormalizedSpeed = 2f; // keep Action_Walk only (never prefer run)
        [SerializeField] private float _moveSpeedEpsilon = 0.08f;

        private AIChefController _chef;
        private readonly StateMachine<ChefVisualMode, ChefVisualState>.WithDefault _stateMachine = new();
        private ChefVisualIdleState _idle;
        private ChefVisualMoveState _move;
        private ChefVisualInteractState _interact;
        private ChefVisualWorkState _work;
        private ChefVisualWaitState _wait;
        private bool _bound;
        private bool _playingRun;

        public AnimancerComponent Animancer => _animancer;
        public ChefVisualAnimSet AnimSet => _animSet;
        public float FadeDuration => _fadeDuration;
        public AIChefController Chef => _chef;
        public StateMachine<ChefVisualMode, ChefVisualState>.WithDefault StateMachine => _stateMachine;

        public void Configure(ChefVisualAnimSet animSet)
        {
            if (animSet != null)
                _animSet = animSet;
        }

        public void Bind(AIChefController chef)
        {
            Unbind();
            _chef = chef;
            if (_chef == null) return;

            EnsureAnimancer();
            EnsureStates();

            _chef.OnInteractionPerformed += OnChefInteracted;
            _bound = true;

            _stateMachine.DefaultKey = ChefVisualMode.Idle;
            _stateMachine.ForceSetState(ResolveModeFromChef());
        }

        public void Unbind()
        {
            if (_chef != null)
                _chef.OnInteractionPerformed -= OnChefInteracted;
            _chef = null;
            _bound = false;
        }

        private void OnDestroy() => Unbind();

        private void LateUpdate()
        {
            if (!_bound || _chef == null || _animSet == null || _animancer == null)
                return;

            var desired = ResolveModeFromChef();
            if (desired != _stateMachine.CurrentKey)
            {
                _stateMachine.TrySetState(desired);
                return;
            }

            if (desired == ChefVisualMode.Move)
            {
                bool run = PreferRun;
                if (run != _playingRun)
                {
                    _playingRun = run;
                    _move.RefreshClip();
                }
            }
        }

        private void OnChefInteracted()
        {
            // Restart interact clip at the moment the logic actually performs Interact.
            if (_animSet == null) return;
            if (_stateMachine.CurrentKey == ChefVisualMode.Interact)
                _stateMachine.TryResetState(ChefVisualMode.Interact);
        }

        public ChefVisualMode ResolveModeFromChef()
        {
            if (_chef == null) return ChefVisualMode.Idle;

            string sub = _chef.Substate;
            if (sub == "interacting" || sub == "postInteract")
                return ChefVisualMode.Interact;
            if (sub == "working")
                return ChefVisualMode.Work;
            if (sub == "waiting")
                return ChefVisualMode.Wait;

            float speed = GetPlanarSpeed();
            if (sub == "moving" || speed > _moveSpeedEpsilon)
                return ChefVisualMode.Move;

            return ChefVisualMode.Idle;
        }

        public float GetPlanarSpeed()
        {
            if (_chef == null) return 0f;
            var ai = _chef.GetComponent<Pathfinding.IAstarAI>();
            if (ai == null) return 0f;
            var v = ai.velocity;
            v.y = 0f;
            return v.magnitude;
        }

        public bool PreferRun =>
            _chef != null
            && _chef.moveSpeed > 0.01f
            && GetPlanarSpeed() / _chef.moveSpeed >= _runNormalizedSpeed;

        public AnimancerState PlayClip(AnimationClip clip, float fade = -1f)
        {
            if (_animancer == null || clip == null) return null;
            float f = fade >= 0f ? fade : _fadeDuration;
            return _animancer.Play(clip, f);
        }

        private void EnsureAnimancer()
        {
            if (_animancer == null)
                _animancer = GetComponentInChildren<AnimancerComponent>(true);
            if (_animancer != null) return;

            var animator = GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                Debug.LogWarning($"[{name}] AIChefVisualPresenter: no Animator under visual.", this);
                return;
            }

            animator.runtimeAnimatorController = null;
            _animancer = animator.gameObject.GetComponent<AnimancerComponent>();
            if (_animancer == null)
                _animancer = animator.gameObject.AddComponent<AnimancerComponent>();
            _animancer.Animator = animator;
        }

        private void EnsureStates()
        {
            if (_idle != null) return;
            _idle = new ChefVisualIdleState(this);
            _move = new ChefVisualMoveState(this);
            _interact = new ChefVisualInteractState(this);
            _work = new ChefVisualWorkState(this);
            _wait = new ChefVisualWaitState(this);

            _stateMachine.Add(ChefVisualMode.Idle, _idle);
            _stateMachine.Add(ChefVisualMode.Move, _move);
            _stateMachine.Add(ChefVisualMode.Interact, _interact);
            _stateMachine.Add(ChefVisualMode.Work, _work);
            _stateMachine.Add(ChefVisualMode.Wait, _wait);
        }

        public void NotifyMoveClipKind(bool run) => _playingRun = run;
    }
}
