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
            if (DataTableManager.Sigleton.CanPlaceOnFacility(obj.objEnum, ProcessFacility))
                return true;
            return obj is Plate plate && plate.CanProcessOn(ProcessFacility);
        }

        private bool ShouldStartProcess(KitchenObj obj)
        {
            if (obj == null) return false;
            if (DataTableManager.Sigleton.CanProcess(obj.objEnum, ProcessFacility))
                return true;
            return obj is Plate plate && plate.CanProcessOn(ProcessFacility);
        }

        /// <summary>
        /// Extracts the single assembled item from an order plate and spawns
        /// that item directly on this facility. The plate remains empty and
        /// can be returned to an assembly counter while processing runs.
        /// </summary>
        public bool TryAcceptPlateContentsForOrder(Plate plate, int orderId)
        {
            if (!IsServer || plate == null || orderId == 0 || HasKitchenObj())
                return false;

            if (!plate.TryExtractIngredientForProcessingServer(
                    ProcessFacility,
                    orderId,
                    out var input))
                return false;

            KitchenObjOperator.SpawnKitchenObjForOrderRpc(
                input,
                this,
                orderId);
            return true;
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
            if (newKitchenObj != null && !isCooking && ShouldStartProcess(newKitchenObj))
                StartProcessServerRpc();
        }
        public override void ClearKitchenObj()
        {
            // `isCooking` is a client-facing visual flag. On a dedicated
            // server it is not set by StartProcessClientRpc, so cancellation
            // must be driven by the authoritative process token instead.
            if (IsServer)
                StopProcessOnServer();
            else if (isCooking)
                StopProcessServerRpc();
            base.ClearKitchenObj();
        }
        [ServerRpc(RequireOwnership = false)]
        private void StopProcessServerRpc()
        {
            StopProcessOnServer();
        }

        private void StopProcessOnServer()
        {
            // Only cancel — RunProcess owns dispose via its local CTS.
            // Process() destroys the input and ClearKitchenObj runs mid-loop;
            // nulling/disposing the field here used to NRE on the next while check.
            _cts?.Cancel();
            _cts = null;
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
            _cts?.Cancel();
            _cts = null;
        }
        private async UniTask RunProcess()
        {
            if (!IsServer) return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            StartProcessClientRpc();
            try
            {
                if (kitchenObj == null)
                    return;

                KitchenObjEnum stage = kitchenObj is Plate p && p.TryGetDeliverableItem(out var held)
                    ? held
                    : kitchenObj.objEnum;
                StageChangeClientRpc(stage);

                while (!cts.IsCancellationRequested)
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
                    while (Time.time - startTime < duration && !cts.IsCancellationRequested)
                    {
                        try
                        {
                            await UniTask.WaitForFixedUpdate(cancellationToken: cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        SetProgressClientRpc((Time.time - startTime) / duration);
                    }

                    if (cts.IsCancellationRequested) break;
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

                        // May ClearKitchenObj → cancel cts when the input is destroyed.
                        KitchenObjOperator.Process(kitchenObj, this, ProcessFacility);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[{ProcessFacility}] process failed: {e.Message}");
                        break;
                    }

                    if (cts.IsCancellationRequested) break;
                    if (kitchenObj != null)
                        StageChangeClientRpc(kitchenObj.objEnum);
                }
            }
            finally
            {
                cts.Dispose();
                if (ReferenceEquals(_cts, cts))
                    _cts = null;
                StopProcessClientRpc();
            }
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
