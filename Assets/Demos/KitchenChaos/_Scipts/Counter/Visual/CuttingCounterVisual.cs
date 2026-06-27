using System;
using UnityEngine;

namespace Kitchen
{
    public class CuttingCounterVisual : MonoBehaviour
    {
        private CuttingCounter _cuttingCounter;
        private Animator _animator;
        private static readonly int _isCuttingParam = Animator.StringToHash("isCutting");

        private void Awake()
        {
            _cuttingCounter = GetComponentInParent<CuttingCounter>();
            _animator = GetComponent<Animator>();
        }

        private void OnEnable()
        {
            _cuttingCounter.OnCuttingStart += _OnCuttingStart;
            _cuttingCounter.OnCuttingStop += _OnCuttingStop;
        }

        private void OnDisable()
        {
            _cuttingCounter.OnCuttingStart -= _OnCuttingStart;
            _cuttingCounter.OnCuttingStop -= _OnCuttingStop;
        }

        private void _OnCuttingStart()
        {
            _animator.SetBool(_isCuttingParam, true);
        }

        private void _OnCuttingStop()
        {
            _animator.SetBool(_isCuttingParam, false);
        }
    }
}