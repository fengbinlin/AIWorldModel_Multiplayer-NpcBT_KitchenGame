using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 食材货箱视觉：挂在 *_Visual 上，订阅父级 ContainerCounter.OnInteractEvent，播放开门 Animator。
    /// 在 Start 订阅（不用 OnEnable），避免 Awake 里换肤 Destroy/Instantiate 同帧把订阅弄丢。
    /// </summary>
    public class ContainerCounterVisual : MonoBehaviour
    {
        private ContainerCounter _containerCounter;
        private Animator _animator;
        private static readonly int OpenParam = Animator.StringToHash("open");
        private bool _subscribed;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);
        }

        private void Start()
        {
            TrySubscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        /// <summary>换肤 Instantiation 后可由 Skin 管线再调一次，保证订阅落在最终 Visual 上。</summary>
        public void TrySubscribe()
        {
            if (_subscribed) return;

            if (_containerCounter == null)
                _containerCounter = GetComponentInParent<ContainerCounter>();
            if (_animator == null)
            {
                _animator = GetComponent<Animator>();
                if (_animator == null)
                    _animator = GetComponentInChildren<Animator>(true);
            }

            if (_containerCounter == null) return;

            _containerCounter.OnInteractEvent += OnInteractEvent;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _containerCounter == null) return;
            _containerCounter.OnInteractEvent -= OnInteractEvent;
            _subscribed = false;
        }

        private void OnInteractEvent()
        {
            if (_animator != null)
                _animator.SetTrigger(OpenParam);
        }
    }
}
