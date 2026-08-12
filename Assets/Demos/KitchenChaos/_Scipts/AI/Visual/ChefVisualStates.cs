using Animancer;
using Animancer.FSM;
using UnityEngine;

namespace Kitchen.AI.Visual
{
    public abstract class ChefVisualState : IState
    {
        protected readonly AIChefVisualPresenter Presenter;

        protected ChefVisualState(AIChefVisualPresenter presenter)
        {
            Presenter = presenter;
        }

        public virtual bool CanEnterState => true;
        public virtual bool CanExitState => true;

        public abstract void OnEnterState();
        public virtual void OnExitState() { }
    }

    public sealed class ChefVisualIdleState : ChefVisualState
    {
        public ChefVisualIdleState(AIChefVisualPresenter p) : base(p) { }

        public override void OnEnterState()
        {
            Presenter.PlayClip(Presenter.AnimSet != null ? Presenter.AnimSet.idle : null);
        }
    }

    public sealed class ChefVisualMoveState : ChefVisualState
    {
        public ChefVisualMoveState(AIChefVisualPresenter p) : base(p) { }

        public override void OnEnterState() => RefreshClip();

        public void RefreshClip()
        {
            var set = Presenter.AnimSet;
            if (set == null) return;
            bool run = Presenter.PreferRun;
            Presenter.NotifyMoveClipKind(run);
            var clip = run
                ? (set.run != null ? set.run : set.walk)
                : (set.walk != null ? set.walk : set.run);
            Presenter.PlayClip(clip);
        }
    }

    public sealed class ChefVisualWorkState : ChefVisualState
    {
        public ChefVisualWorkState(AIChefVisualPresenter p) : base(p) { }

        public override void OnEnterState()
        {
            var set = Presenter.AnimSet;
            var clip = set != null ? (set.work != null ? set.work : set.idle) : null;
            Presenter.PlayClip(clip);
        }
    }

    public sealed class ChefVisualWaitState : ChefVisualState
    {
        private AnimancerState _playing;
        private AnimationClip _clip;

        public ChefVisualWaitState(AIChefVisualPresenter p) : base(p) { }

        public override void OnEnterState()
        {
            var set = Presenter.AnimSet;
            _clip = set != null
                ? (set.wait != null ? set.wait : set.idle)
                : null;
            _playing = Presenter.PlayClip(_clip);
            if (_playing == null) return;
            if (_playing.IsLooping)
            {
                // Dance_3 etc.: suppress Animancer end spam so the loop keeps playing.
                var events = _playing.Events(this);
                events.NormalizedEndTime = float.NaN;
                events.OnEnd = null;
            }
            else
            {
                _playing.Events(this).OnEnd = OnClipEnd;
            }
        }

        private void OnClipEnd()
        {
            if (Presenter.ResolveModeFromChef() == ChefVisualMode.Wait)
            {
                _playing = Presenter.PlayClip(_clip);
                if (_playing != null)
                    _playing.Events(this).OnEnd = OnClipEnd;
            }
        }

        public override void OnExitState()
        {
            if (_playing != null)
                _playing.Events(this).OnEnd = null;
            _playing = null;
            _clip = null;
        }
    }

    public sealed class ChefVisualInteractState : ChefVisualState
    {
        private AnimancerState _playing;
        private AnimationClip _activeClip;

        public ChefVisualInteractState(AIChefVisualPresenter p) : base(p) { }

        public override void OnEnterState()
        {
            var set = Presenter.AnimSet;
            // Box take / facility interact → Emoji_Aghast (pickup & putDown both mapped).
            _activeClip = set != null
                ? (set.pickup != null ? set.pickup : set.putDown)
                : null;
            _playing = Presenter.PlayClip(_activeClip, 0.05f);
            if (_playing == null) return;

            _playing.Events(this).OnEnd = OnClipEnd;
        }

        private void OnClipEnd()
        {
            if (Presenter.ResolveModeFromChef() == ChefVisualMode.Interact)
            {
                _playing = Presenter.PlayClip(_activeClip, 0.05f);
                if (_playing != null)
                    _playing.Events(this).OnEnd = OnClipEnd;
            }
            else
            {
                Presenter.StateMachine.ForceSetState(Presenter.ResolveModeFromChef());
            }
        }

        public override void OnExitState()
        {
            if (_playing != null)
                _playing.Events(this).OnEnd = null;
            _playing = null;
            _activeClip = null;
        }
    }
}
