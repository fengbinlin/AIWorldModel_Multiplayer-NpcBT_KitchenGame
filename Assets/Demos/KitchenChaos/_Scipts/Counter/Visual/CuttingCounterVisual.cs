using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 切菜柜视觉：挂在 *_Visual 上，订阅父级 CuttingCounter 的切剁事件驱动 Animator。
    /// </summary>
    public class CuttingCounterVisual : MonoBehaviour
    {
        private CuttingCounter _cuttingCounter;
        private Animator _animator;
        private static readonly int IsCuttingParam = Animator.StringToHash("isCutting");

        private void Awake()
        {
            _cuttingCounter = GetComponentInParent<CuttingCounter>();
            _animator = GetComponent<Animator>();
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);
        }

        private void OnEnable()
        {
            if (_cuttingCounter == null)
                _cuttingCounter = GetComponentInParent<CuttingCounter>();
            if (_cuttingCounter == null) return;

            _cuttingCounter.OnCuttingStart += OnCuttingStart;
            _cuttingCounter.OnCuttingStop += OnCuttingStop;
        }

        private void OnDisable()
        {
            if (_cuttingCounter == null) return;
            _cuttingCounter.OnCuttingStart -= OnCuttingStart;
            _cuttingCounter.OnCuttingStop -= OnCuttingStop;
        }

        private void OnCuttingStart()
        {
            if (_animator != null)
                _animator.SetBool(IsCuttingParam, true);
        }

        private void OnCuttingStop()
        {
            if (_animator != null)
                _animator.SetBool(IsCuttingParam, false);
        }
    }
}
