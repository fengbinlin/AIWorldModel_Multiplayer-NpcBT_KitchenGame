using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nico.Components;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    public class StoveCounter : BaseCounter, ICookingFacility
    {
        private CancellationTokenSource _cookingCts;
        public event Action OnStartCooking;
        public event Action OnStopCooking;
        public event Action<KitchenObjEnum?> OnCookingStageChange;
        private ProgressBar _progressBarUI;

        public bool isCooking;

        protected override void Awake()
        {
            base.Awake();
            _progressBarUI = transform.Find("ProgressBarUI").GetComponent<ProgressBar>();
            if (_progressBarUI != null)
                Kitchen.UI.KitchenUiVisibilitySetup.ApplyTo(_progressBarUI.gameObject);
        }

        public override void Interact(ICanHoldKitchenObj holder)
        {
            //玩家持有物体，当前柜子没有物体 -> 放置物体（会自动开始烹饪）
            //可加工原料，或本设施已完成的成品（熟了可放回，但不会再烧）
            if (holder.HasKitchenObj() && !HasKitchenObj())
            {
                if (!DataTableManager.Sigleton.CanPlaceOnFacility(
                        holder.GetKitchenObj().objEnum, FacilityEnum.StoveCounter))
                    return;
                KitchenObjOperator.PutKitchenObj(holder, this);
                return;
            }

            //玩家没有持有物体，当前柜子有物体 -> 拿起物体（会自动停止烹饪）
            if (!holder.HasKitchenObj() && HasKitchenObj())
            {
                KitchenObjOperator.PutKitchenObj(this, holder);
                return;
            }

            if (!holder.HasKitchenObj() || !HasKitchenObj()) return;
            //都有物体，尝试盘子操作
            CounterOperator.TryPlateOperator(holder, this);
        }

        /// <summary>
        /// 当食材被放到锅上时，如果可烹饪则自动开始。
        /// </summary>
        public override void SetKitchenObj(KitchenObj newKitchenObj)
        {
            base.SetKitchenObj(newKitchenObj);

            if (newKitchenObj != null && !isCooking
                && DataTableManager.Sigleton.CanProcess(newKitchenObj.objEnum, FacilityEnum.StoveCounter))
            {
                StartCookingServerRpc();
            }
        }

        /// <summary>
        /// 当食材从锅里被拿走/销毁时，自动停止烹饪。
        /// </summary>
        public override void ClearKitchenObj()
        {
            // `isCooking` is synchronized through a ClientRpc and is not
            // authoritative on a dedicated server. Use the server coroutine
            // as the source of truth so taking food off the pan really stops
            // the burn timer.
            if (IsServer)
                StopCookingOnServer();
            else if (isCooking)
                _StopCookingServerRpc();
            base.ClearKitchenObj();
        }

        [ServerRpc(RequireOwnership = false)]
        private void _StopCookingServerRpc()
        {
            StopCookingOnServer();
        }

        private void StopCookingOnServer()
        {
            _cookingCts?.Cancel();
            _cookingCts?.Dispose();
            _cookingCts = null;
            _StopCookingClientRpc();
        }

        [ClientRpc]
        private void _StopCookingClientRpc()
        {
            ApplyCookingStoppedClient(clearStage: true);
        }

        [ServerRpc(RequireOwnership = false)]
        private void StartCookingServerRpc()
        {
            // Cancel previous cooking if any
            CancelCooking();
            _Cooking().Forget();
        }

        private void CancelCooking()
        {
            if (_cookingCts != null && !_cookingCts.IsCancellationRequested)
            {
                _cookingCts.Cancel();
                _cookingCts.Dispose();
            }
            _cookingCts = null;
        }

        private async UniTask _Cooking()
        {
            if (!IsServer)
                throw new Exception("只能在服务端执行 Cooking任务");

            _cookingCts = new CancellationTokenSource();
            _OnStartCookingClientRpc();
            if (kitchenObj == null) { _OnStopCookingClientRpc(true); return; }
            _CookingStageChangeClientRpc(kitchenObj.objEnum);
            while (_cookingCts != null && !_cookingCts.IsCancellationRequested)
            {
                if (kitchenObj == null) break;
                if (DataTableManager.Sigleton == null) break;
                var process = DataTableManager.Sigleton.GetProcess(kitchenObj.objEnum, FacilityEnum.StoveCounter);
                if (process == null) break;

                float cookTime = process.processValue;
                var startTime = Time.time;
                while (_cookingCts != null
                       && Time.time - startTime < cookTime
                       && !_cookingCts.IsCancellationRequested)
                {
                    var cookingToken = _cookingCts.Token;
                    await UniTask.WaitForFixedUpdate(cancellationToken: cookingToken);
                    _SetProgressClientRpc((Time.time - startTime) / cookTime);
                }

                if (_cookingCts == null || _cookingCts.IsCancellationRequested) break;
                if (kitchenObj == null || kitchenObj.NetworkObject == null || !kitchenObj.NetworkObject.IsSpawned) break;

                // Save the objEnum BEFORE Process (Process destroys the object)
                var currentObjEnum = kitchenObj.objEnum;

                try
                {
                    KitchenObjOperator.Process(kitchenObj, this, FacilityEnum.StoveCounter);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[StoveCounter] Cooking process failed: {e.Message}");
                    break;
                }

                // kitchenObj may have been replaced by Process — check new value
                if (kitchenObj != null)
                    _CookingStageChangeClientRpc(kitchenObj.objEnum);
                else
                    Debug.LogWarning($"[StoveCounter] kitchenObj became null after processing {currentObjEnum}");
            }

            // Item gone (picked up / destroyed): clear stage UI.
            // Item still on stove (e.g. burned terminal): keep last stage for visuals, still stop bar/audio.
            _OnStopCookingClientRpc(kitchenObj == null);
        }

        [ClientRpc]
        private void _SetProgressClientRpc(float progress)
        {
            _progressBarUI?.SetProgress(progress);
        }

        [ClientRpc]
        private void _OnStartCookingClientRpc()
        {
            isCooking = true;
            OnStartCooking?.Invoke();
        }

        [ClientRpc]
        private void _OnStopCookingClientRpc(bool clearStage)
        {
            ApplyCookingStoppedClient(clearStage);
        }

        /// <summary>All stop paths must hide progress; clear stage when pan is empty.</summary>
        private void ApplyCookingStoppedClient(bool clearStage)
        {
            isCooking = false;
            OnStopCooking?.Invoke();
            if (clearStage)
                OnCookingStageChange?.Invoke(null);
            _progressBarUI?.Hide();
        }

        [ClientRpc]
        private void _CookingStageChangeClientRpc(KitchenObjEnum kitchenObjEnum)
        {
            OnCookingStageChange?.Invoke(kitchenObjEnum);
        }
    }
}
