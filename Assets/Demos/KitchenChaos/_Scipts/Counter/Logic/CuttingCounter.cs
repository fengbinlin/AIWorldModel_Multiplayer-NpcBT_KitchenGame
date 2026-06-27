using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nico.Components;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Kitchen
{
    public class CuttingCounter : BaseCounter
    {
        private ProgressBar _progressBar;
        private CancellationTokenSource _cuttingCts;
        private bool _isCutting;
        private float _cuttingProgress; // 0 to processValue
        public event Action OnCuttingStart;
        public event Action OnCuttingStop;
        public static event EventHandler<Vector3> OnAnyCut;

        protected override void Awake()
        {
            base.Awake();
            _progressBar = transform.Find("ProgressBarUI").GetComponent<ProgressBar>();
        }

        private void OnEnable()
        {
            var input = PlayerInput.Instance;
            input.Player.InteractAlternate.started += OnInteractAlternateStarted;
            input.Player.InteractAlternate.canceled += OnInteractAlternateCanceled;
        }

        private void OnDisable()
        {
            if (PlayerInput.Instance != null)
            {
                var input = PlayerInput.Instance;
                input.Player.InteractAlternate.started -= OnInteractAlternateStarted;
                input.Player.InteractAlternate.canceled -= OnInteractAlternateCanceled;
            }
        }

        private void OnInteractAlternateStarted(InputAction.CallbackContext ctx)
        {
            if (!GameManager.Instance.IsPlaying()) return;

            var localPlayer = Player.Player.LocalInstance;
            if (localPlayer == null) return;
            if (localPlayer.SelectCounterController.SelectedCounter != this) return;
            if (!HasKitchenObj()) return;
            if (!DataTableManager.Sigleton.CanProcess(kitchenObj.objEnum, FacilityEnum.CuttingCounter)) return;

            StartCuttingServerRpc();
        }

        private void OnInteractAlternateCanceled(InputAction.CallbackContext ctx)
        {
            if (_isCutting)
            {
                StopCuttingServerRpc();
            }
        }

        public override void Interact(Player.Player player)
        {
            //玩家持有物体，当前柜子没有物体 -> 放置物体
            if (player.HasKitchenObj() && !HasKitchenObj())
            {
                //只有能被CuttingCounter处理的食材才允许放置
                if (!DataTableManager.Sigleton.CanProcess(player.GetKitchenObj().objEnum, FacilityEnum.CuttingCounter))
                    return;
                _ClearCuttingStateServerRpc();
                KitchenObjOperator.PutKitchenObj(player, this);
                return;
            }

            //玩家没有持有物体，当前柜子有物体 -> 拿起物体
            if (!player.HasKitchenObj() && HasKitchenObj())
            {
                _ClearCuttingStateServerRpc();
                KitchenObjOperator.PutKitchenObj(this, player);
                return;
            }

            if (CounterOperator.TryPlateOperator(player, this)) return;
        }

        public override void ClearKitchenObj()
        {
            if (_isCutting)
            {
                StopCuttingServerRpc();
                _isCutting = false;
            }
            base.ClearKitchenObj();
        }

        [ServerRpc(RequireOwnership = false)]
        private void StartCuttingServerRpc()
        {
            if (_isCutting) return;
            _CuttingRoutine().Forget();
        }

        [ServerRpc(RequireOwnership = false)]
        private void StopCuttingServerRpc()
        {
            _cuttingCts?.Cancel();
            _StopCuttingClientRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void _ClearCuttingStateServerRpc()
        {
            _cuttingCts?.Cancel();
            _ClearCuttingStateClientRpc();
        }

        [ClientRpc]
        private void _ClearCuttingStateClientRpc()
        {
            _cuttingProgress = 0;
            _progressBar.SetProgress(0);
        }

        [ClientRpc]
        private void _StopCuttingClientRpc()
        {
            _isCutting = false;
            OnCuttingStop?.Invoke();
        }

        private async UniTask _CuttingRoutine()
        {
            if (!IsServer) return;

            var process = DataTableManager.Sigleton.GetProcess(kitchenObj.objEnum, FacilityEnum.CuttingCounter);
            if (process == null)
            {
                Debug.LogWarning("[CuttingCounter] No process found for cutting, aborting.");
                return;
            }

            _cuttingCts = new CancellationTokenSource();
            _isCutting = true;
            _OnStartCuttingClientRpc();

            float maxTime = process.processValue;
            Debug.Log($"[CuttingCounter] Cutting started, maxTime={maxTime}");

            while (_cuttingProgress < maxTime)
            {
                if (_cuttingCts.IsCancellationRequested)
                {
                    Debug.Log("[CuttingCounter] Cutting cancelled mid-way.");
                    break;
                }

                _cuttingProgress += Time.deltaTime;
                _SetProgressClientRpc(_cuttingProgress / maxTime);
                await UniTask.Yield();
            }

            if (_cuttingProgress >= maxTime)
            {
                Debug.Log("[CuttingCounter] Cutting complete, transforming ingredient.");
                KitchenObjOperator.DestroyKitchenObj(kitchenObj);
                KitchenObjOperator.SpawnKitchenObjRpc(process.outputEnum, this);
                _OnCutCompleteClientRpc(transform.position);
                _ClearCuttingStateClientRpc();
            }

            _isCutting = false;
        }

        [ClientRpc]
        private void _SetProgressClientRpc(float progress)
        {
            _progressBar.SetProgress(progress);
        }

        [ClientRpc]
        private void _OnStartCuttingClientRpc()
        {
            _isCutting = true;
            OnCuttingStart?.Invoke();
        }

        [ClientRpc]
        private void _OnCutCompleteClientRpc(Vector3 position)
        {
            OnAnyCut?.Invoke(this, position);
        }
    }
}
