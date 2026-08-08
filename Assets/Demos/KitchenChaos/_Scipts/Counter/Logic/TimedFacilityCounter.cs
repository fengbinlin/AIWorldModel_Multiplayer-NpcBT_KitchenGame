using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nico.Components;
using Unity.Netcode;
using UnityEngine;
namespace Kitchen
{
    /// <summary>
    /// Generic timed processor (oven / blender). Facility type selects KitchenProcessSo rows.
    /// Accepts either a raw KitchenObj or a Plate holding exactly one processable ingredient.
    /// </summary>
    public abstract class TimedFacilityCounter : BaseCounter, ICookingFacility
    {
        protected abstract FacilityEnum ProcessFacility { get; }
        private CancellationTokenSource _cts;
        public event Action OnStartCooking;
        public event Action OnStopCooking;
        public event Action<KitchenObjEnum?> OnCookingStageChange;
        private ProgressBar _progressBarUI;
        public bool isCooking;
        protected override void Awake()
        {
            base.Awake();
            var bar = transform.Find("ProgressBarUI");
            if (bar != null)
                _progressBarUI = bar.GetComponent<ProgressBar>();
        }
        private bool CanAccept(KitchenObj obj)
        {
            if (obj == null) return false;
            if (DataTableManager.Sigleton.CanProcess(obj.objEnum, ProcessFacility))
                return true;
            return obj is Plate plate && plate.CanProcessOn(ProcessFacility);
        }
        public override void Interact(ICanHoldKitchenObj holder)
        {
            if (holder.HasKitchenObj() && !HasKitchenObj())
            {
                if (!CanAccept(holder.GetKitchenObj()))
                    return;
                KitchenObjOperator.PutKitchenObj(holder, this);
                return;
            }
            if (!holder.HasKitchenObj() && HasKitchenObj())
            {
                KitchenObjOperator.PutKitchenObj(this, holder);
                return;
            }
            if (!holder.HasKitchenObj() || !HasKitchenObj()) return;
            CounterOperator.TryPlateOperator(holder, this);
        }
        public override void SetKitchenObj(KitchenObj newKitchenObj)
        {
            base.SetKitchenObj(newKitchenObj);
            if (newKitchenObj != null && !isCooking && CanAccept(newKitchenObj))
                StartProcessServerRpc();
        }
        public override void ClearKitchenObj()
        {
            if (isCooking)
                StopProcessServerRpc();
            base.ClearKitchenObj();
        }
        [ServerRpc(RequireOwnership = false)]
        private void StopProcessServerRpc()
        {
            _cts?.Cancel();
            StopProcessClientRpc();
        }
        [ClientRpc]
        private void StopProcessClientRpc()
        {
            isCooking = false;
            OnStopCooking?.Invoke();
            OnCookingStageChange?.Invoke(null);
            _progressBarUI?.Hide();
        }
        [ServerRpc(RequireOwnership = false)]
        private void StartProcessServerRpc()
        {
            CancelProcess();
            RunProcess().Forget();
        }
        private void CancelProcess()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            _cts = null;
        }
        private async UniTask RunProcess()
        {
            if (!IsServer) return;
            _cts = new CancellationTokenSource();
            StartProcessClientRpc();
            if (kitchenObj == null)
            {
                StopProcessClientRpc();
                return;
            }
            KitchenObjEnum stage = kitchenObj is Plate p && p.TryGetDeliverableItem(out var held)
                ? held
                : kitchenObj.objEnum;
            StageChangeClientRpc(stage);
            while (!_cts.IsCancellationRequested)
            {
                if (kitchenObj == null) break;
                KitchenProcessSo process = null;
                Plate plate = kitchenObj as Plate;
                if (plate != null && plate.TryGetDeliverableItem(out var plateItem))
                    process = DataTableManager.Sigleton.GetProcess(plateItem, ProcessFacility);
                else
                    process = DataTableManager.Sigleton.GetProcess(kitchenObj.objEnum, ProcessFacility);
                if (process == null) break;
                float duration = process.processValue;
                var startTime = Time.time;
                while (Time.time - startTime < duration && !_cts.IsCancellationRequested)
                {
                    await UniTask.WaitForFixedUpdate(cancellationToken: _cts.Token);
                    SetProgressClientRpc((Time.time - startTime) / duration);
                }
                if (_cts.IsCancellationRequested) break;
                if (kitchenObj == null || kitchenObj.NetworkObject == null || !kitchenObj.NetworkObject.IsSpawned)
                    break;
                try
                {
                    if (plate != null)
                    {
                        plate.ApplyProcessServer(ProcessFacility);
                        if (plate.TryGetDeliverableItem(out var after))
                            StageChangeClientRpc(after);
                        break;
                    }
                    KitchenObjOperator.Process(kitchenObj, this, ProcessFacility);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[{ProcessFacility}] process failed: {e.Message}");
                    break;
                }
                if (kitchenObj != null)
                    StageChangeClientRpc(kitchenObj.objEnum);
            }
            StopProcessClientRpc();
        }
        [ClientRpc]
        private void SetProgressClientRpc(float progress)
        {
            _progressBarUI?.SetProgress(progress);
        }
        [ClientRpc]
        private void StartProcessClientRpc()
        {
            isCooking = true;
            OnStartCooking?.Invoke();
        }
        [ClientRpc]
        private void StageChangeClientRpc(KitchenObjEnum kitchenObjEnum)
        {
            OnCookingStageChange?.Invoke(kitchenObjEnum);
        }
    }

}
