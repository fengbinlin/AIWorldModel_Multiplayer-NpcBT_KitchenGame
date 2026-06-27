using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nico.Components;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    public class StoveCounter : BaseCounter
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
        }

        public override void Interact(ICanHoldKitchenObj holder)
        {
            //玩家持有物体，当前柜子没有物体 -> 放置物体（会自动开始烹饪）
            //只有能被StoveCounter处理的食材才允许放置
            if (holder.HasKitchenObj() && !HasKitchenObj())
            {
                if (!DataTableManager.Sigleton.CanProcess(holder.GetKitchenObj().objEnum, FacilityEnum.StoveCounter))
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
            if (isCooking)
            {
                _StopCookingServerRpc();
            }
            base.ClearKitchenObj();
        }

        [ServerRpc(RequireOwnership = false)]
        private void _StopCookingServerRpc()
        {
            _cookingCts?.Cancel();
            _StopCookingClientRpc();
        }

        [ClientRpc]
        private void _StopCookingClientRpc()
        {
            isCooking = false;
            OnStopCooking?.Invoke();
            OnCookingStageChange?.Invoke(null);
            _progressBarUI.Hide();
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
            if (kitchenObj == null) { _OnStopCookingClientRpc(); return; }
            _CookingStageChangeClientRpc(kitchenObj.objEnum);
            while (!_cookingCts.IsCancellationRequested)
            {
                if (kitchenObj == null) break;
                var process = DataTableManager.Sigleton.GetProcess(kitchenObj.objEnum, FacilityEnum.StoveCounter);
                if (process == null) break;

                float cookTime = process.processValue;
                var startTime = Time.time;
                while (Time.time - startTime < cookTime && !_cookingCts.IsCancellationRequested)
                {
                    await UniTask.WaitForFixedUpdate(cancellationToken: _cookingCts.Token);
                    _SetProgressClientRpc((Time.time - startTime) / cookTime);
                }

                if (_cookingCts.IsCancellationRequested) break;
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

            if (kitchenObj != null)
            {
                try { _CookingStageChangeClientRpc(kitchenObj.objEnum); }
                catch (System.Exception) { /* item may be gone */ }
            }
            _OnStopCookingClientRpc();
        }

        [ClientRpc]
        private void _SetProgressClientRpc(float progress)
        {
            _progressBarUI.SetProgress(progress);
        }

        [ClientRpc]
        private void _OnStartCookingClientRpc()
        {
            isCooking = true;
            OnStartCooking?.Invoke();
        }

        [ClientRpc]
        private void _OnStopCookingClientRpc()
        {
            isCooking = false;
            OnStopCooking?.Invoke();
        }

        [ClientRpc]
        private void _CookingStageChangeClientRpc(KitchenObjEnum kitchenObjEnum)
        {
            OnCookingStageChange?.Invoke(kitchenObjEnum);
        }
    }
}
