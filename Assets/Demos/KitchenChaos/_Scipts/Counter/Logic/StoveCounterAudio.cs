using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Kitchen.Music;
using UnityEngine;

namespace Kitchen
{
    public class StoveCounterAudio : MonoBehaviour
    {
        [SerializeField] private AudioSource _cookingAudioSource;
        private ICookingFacility _cooking;

        private void Awake()
        {
            if (_cookingAudioSource == null)
                _cookingAudioSource = GetComponent<AudioSource>();
            _cooking = GetComponentInParent<ICookingFacility>();
        }

        private void OnEnable()
        {
            if (_cooking == null)
                _cooking = GetComponentInParent<ICookingFacility>();
            if (_cooking == null) return;
            _cooking.OnStartCooking += _OnStartCooking;
            _cooking.OnStopCooking += _OnStopCooking;
            _cooking.OnCookingStageChange += _OnCookingStageChange;
        }

        private bool _isPlayingWarningSound;
        private CancellationTokenSource _playingWarningSoundCts;
        private float _warningSoundInterval = 0.5f;

        private void _OnCookingStageChange(KitchenObjEnum? obj)
        {
            if (obj is null)
            {
                _playingWarningSoundCts?.Cancel();
                return;
            }

            if (KitchenObjOperator.WillBeBurned(obj.Value))
            {
                if (!_isPlayingWarningSound)
                    _PlayingWarningTileStop().Forget();
            }
            else
            {
                _playingWarningSoundCts?.Cancel();
            }
        }

        private async UniTask _PlayingWarningTileStop()
        {
            _playingWarningSoundCts = new CancellationTokenSource();
            _isPlayingWarningSound = true;
            while (_playingWarningSoundCts.IsCancellationRequested == false)
            {
                if (SoundManager.Instance != null)
                    SoundManager.Instance.PlayWarning(transform.position);
                await UniTask.Delay(TimeSpan.FromSeconds(_warningSoundInterval),
                    cancellationToken: _playingWarningSoundCts.Token);
            }

            _isPlayingWarningSound = false;
        }

        private void OnDisable()
        {
            if (_cooking == null) return;
            _cooking.OnStartCooking -= _OnStartCooking;
            _cooking.OnStopCooking -= _OnStopCooking;
            _cooking.OnCookingStageChange -= _OnCookingStageChange;
        }

        private void _OnStopCooking()
        {
            if (_cookingAudioSource != null)
                _cookingAudioSource.Stop();
        }

        private void _OnStartCooking()
        {
            if (_cookingAudioSource != null)
                _cookingAudioSource.Play();
        }
    }
}

