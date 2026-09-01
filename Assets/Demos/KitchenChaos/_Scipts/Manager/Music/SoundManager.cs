using System;
using Cysharp.Threading.Tasks;
using Nico.Design;
using UnityEngine;

namespace Kitchen.Music
{
    public class SoundManager : SceneSingleton<SoundManager>
    {
        [SerializeField] AudioClipData audioClipData;
        private DeliveryManager _deliveryManager;
        private GameManager _gameManager;
        private Player.Player _localPlayer;

        protected override void OnEnable()
        {
            base.OnEnable();
            _deliveryManager = DeliveryManager.Instance;
            _deliveryManager.OnOrderSuccess += _OnOrderSuccess;
            _deliveryManager.OnOrderFailed += _OnOrderFailed;
            CuttingCounter.OnAnyCut += _CuttingCounter_OnAnyCut;

            Player.Player.OnAnyPickUpSomeThing += _On_PickUpSomeThing;
            if (Player.Player.LocalInstance != null)
            {
                _localPlayer = Player.Player.LocalInstance;
                _localPlayer.MoveController.onMoving += _Player_OnMoving;
            }
            else
            {
                Player.Player.OnAnyPlayerSpawned += _OnAnyPlayerSpawned;
            }


            BaseCounter.OnAnyObjPlaceOnCounter += _BaseCounter_OnAnyObjPlaceOnCounter;
            TrashCounter.OnAnyObjTrashed += _TrashCounter_OnAnyObjTrashed;
            _gameManager = GameManager.Instance;
            _gameManager.OnCountDownChange += _OnCountDownChanged;
        }

        private void _OnAnyPlayerSpawned()
        {
            if (Player.Player.LocalInstance != null)
            {
                _localPlayer = Player.Player.LocalInstance;
                _localPlayer.MoveController.onMoving += _Player_OnMoving;
                Player.Player.OnAnyPlayerSpawned -= _OnAnyPlayerSpawned;
            }
        }


        private void OnDisable()
        {
            if (_deliveryManager != null)
            {
                _deliveryManager.OnOrderSuccess -= _OnOrderSuccess;
                _deliveryManager.OnOrderFailed -= _OnOrderFailed;
            }


            CuttingCounter.OnAnyCut -= _CuttingCounter_OnAnyCut;
            Player.Player.OnAnyPickUpSomeThing -= _On_PickUpSomeThing;
            if (_localPlayer != null)
                _localPlayer.MoveController.onMoving -= _Player_OnMoving;

            BaseCounter.OnAnyObjPlaceOnCounter -= _BaseCounter_OnAnyObjPlaceOnCounter;
            TrashCounter.OnAnyObjTrashed -= _TrashCounter_OnAnyObjTrashed;

            if (_gameManager != null)
                _gameManager.OnCountDownChange -= _OnCountDownChanged;
        }


        #region Play Sound

        private void _PlaySound(AudioClip audioClip, Vector3 position)
        {
            AudioSource.PlayClipAtPoint(audioClip, position, _volume);
        }

        private void _PlaySound(AudioClip[] audioClips, Vector3 position)
        {
            var audioClip = audioClips[UnityEngine.Random.Range(0, audioClips.Length)];
            _PlaySound(audioClip, position);
        }

        #endregion

        #region Events

        private void _OnCountDownChanged(int obj)
        {
            _PlaySound(audioClipData.warning, transform.position);
        }

        private bool _canPlayMovingSound = true;
        private readonly float _movingSoundInterval = 0.2f;

        private void _Player_OnMoving(Vector3 position)
        {
            //TODO 由于玩家每帧都在动 会导致生成的声音数量太多 听起来卡卡的
            //设置一个interval 只有计数到interval才会播放声音
            if (_canPlayMovingSound)
            {
                _PlaySound(audioClipData.footStep, position);
                //开启计时器
                _canPlayMovingSound = false;
                UniTask.Delay(TimeSpan.FromSeconds(_movingSoundInterval)).ContinueWith(() =>
                {
                    _canPlayMovingSound = true;
                }).Forget();
            }
        }

        private void _OnOrderSuccess(object sender, Vector3 position)
        {
            _PlaySound(audioClipData.deliverySuccess, position);
        }

        private void _OnOrderFailed(object sender, Vector3 position)
        {
            _PlaySound(audioClipData.deliveryFail, position);
        }


        private void _TrashCounter_OnAnyObjTrashed(Vector3 position)
        {
            _PlaySound(audioClipData.trash, position);
        }

        private void _BaseCounter_OnAnyObjPlaceOnCounter(object sender, Vector3 position)
        {
            _PlaySound(audioClipData.drop, position);
        }

        private void _On_PickUpSomeThing(Vector3 position)
        {
            _PlaySound(audioClipData.pickUp, position);
        }

        private void _CuttingCounter_OnAnyCut(object sender, Vector3 position)
        {
            _PlaySound(audioClipData.chop, position);
        }

        #endregion

        #region 音量大小

        private float _volume = 1f;

        public void ChangeVolume(float volume)
        {
            if (volume > 1)
            {
                volume = 0;
            }

            if (volume < 0)
            {
                volume = 0;
            }

            _volume = volume;
        }

        public float GetVolume()
        {
            return _volume;
        }

        #endregion

        public void PlayWarning(Vector3 position)
        {
            _PlaySound(audioClipData.warning, position);
        }
    }
}
