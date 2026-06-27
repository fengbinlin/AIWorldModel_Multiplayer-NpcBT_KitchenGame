using System;
using Nico.Components;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    public class CuttingCounter : BaseCounter, IInteractAlternate
    {
        public int cuttingCount = 0;
        private ProgressBar _progressBar;
        public event Action OnCuttingEvent;
        public static event EventHandler<Vector3> OnAnyCut;

        protected override void Awake()
        {
            base.Awake();
            _progressBar = transform.Find("ProgressBarUI").GetComponent<ProgressBar>();
        }

        public override void Interact(Player.Player player)
        {
            //玩家持有物体，当前柜子没有物体 -> 放置物体
            if (player.HasKitchenObj() && !HasKitchenObj())
            {
                _ClearCountServerRpc();
                KitchenObjOperator.PutKitchenObj(player, this);
                return;
            }

            //玩家没有持有物体，当前柜子有物体 -> 拿起物体
            if (!player.HasKitchenObj() && HasKitchenObj())
            {
                _ClearCountServerRpc();
                KitchenObjOperator.PutKitchenObj(this, player);
                return;
            }

            if (CounterOperator.TryPlateOperator(player, this)) return;
        }

        [ServerRpc(RequireOwnership = false)]
        private void _ClearCountServerRpc()
        {
            _SetProgressClientRpc();
        }

        [ClientRpc]
        public void _SetProgressClientRpc()
        {
            cuttingCount = 0;
            _progressBar.SetProgress(0);
        }

        //交互逻辑 这里是切菜的逻辑
        public void InteractAlternate(Player.Player player)
        {
            if (!HasKitchenObj()) return;

            var process = DataTableManager.Sigleton.GetProcess(kitchenObj.objEnum, FacilityEnum.CuttingCounter);
            if (process == null) return;

            CuttingServerRpc(transform.position);
        }

        [ServerRpc(RequireOwnership = false)]
        public void CuttingServerRpc(Vector3 position)
        {
            CuttingClientRpc(position);
        }

        [ClientRpc]
        public void CuttingClientRpc(Vector3 position)
        {
            var process = DataTableManager.Sigleton.GetProcess(kitchenObj.objEnum, FacilityEnum.CuttingCounter);
            if (process == null) return;
            var maxCuttingCount = (int)process.processValue;

            //触发切菜事件
            ++cuttingCount;
            OnCuttingEvent?.Invoke();
            OnAnyCut?.Invoke(this, position);

            _progressBar.SetProgress((float)cuttingCount / maxCuttingCount);

            if (cuttingCount >= maxCuttingCount)
            {
                if (IsServer)
                {
                    KitchenObjOperator.DestroyKitchenObj(kitchenObj);
                    KitchenObjOperator.SpawnKitchenObjRpc(process.outputEnum, this);
                }

                cuttingCount = 0;
            }
        }
    }
}