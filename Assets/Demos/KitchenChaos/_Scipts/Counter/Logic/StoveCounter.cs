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

        public override void Interact(Player.Player player)
        {
            //玩家持有物体，当前柜子没有物体 -> 放置物体（会自动开始烹饪）
            if (player.HasKitchenObj() && !HasKitchenObj())
            {
                KitchenObjOperator.PutKitchenObj(player, this);
                return;
            }

            //玩家没有持有物体，当前柜子有物体 -> 拿起物体（会自动停止烹饪）
            if (!player.HasKitchenObj() && HasKitchenObj())
            {
                KitchenObjOperator.PutKitchenObj(this, player);
                return;
            }

            if (!player.HasKitchenObj() || !HasKitchenObj()) return;
            //都有物体，尝试盘子操作
            CounterOperator.TryPlateOperator(player, this);
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
            if (isCooking) return; // 已经在烹饪中
            _Cooking().Forget();
        }

        private async UniTask _Cooking()
        {
            if (!IsServer)
                throw new Exception("只能在服务端执行 Cooking任务");

            _cookingCts = new CancellationTokenSource();
            _OnStartCookingClientRpc();
            _CookingStageChangeClientRpc(kitchenObj.objEnum);
            while (!_cookingCts.IsCancellationRequested)
            {
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

                KitchenObjOperator.Process(kitchenObj, this, FacilityEnum.StoveCounter);

                _CookingStageChangeClientRpc(kitchenObj.objEnum);
            }

            _CookingStageChangeClientRpc(kitchenObj.objEnum);
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
