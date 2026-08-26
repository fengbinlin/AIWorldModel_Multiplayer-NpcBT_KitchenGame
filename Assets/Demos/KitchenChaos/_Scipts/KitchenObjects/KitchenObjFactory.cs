using System;
using System.Collections.Generic;
using Nico.Network;
using Nico.Network.Singleton;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 用于生成和销毁 KitchenObj 的工厂
    /// </summary>
    internal class KitchenObjFactory : NetSingleton<KitchenObjFactory>
    {
        [ServerRpc(RequireOwnership = false)]
        public void SpawnKitObjServerRpc(KitchenObjEnum kitchenObjEnum, NetworkObjectReference holderRef)
        {
            var so = DataTableManager.Sigleton.GetKitchenObjSo(kitchenObjEnum);

            var obj = Instantiate(so.prefab).GetComponent<KitchenObj>(); //生成 KitObj 并且获取对应脚本
            obj.EnsurePhysicsComponents(); // 确保 Rigidbody + NetworkTransform 存在（Awake中已调用，此处二次确保）
            var netObj = obj.GetComponent<NetworkObject>(); //获取物体网络组件
            netObj.Spawn(true); //在网络上生成这个物体 生成的物体会在所有客户端生成

            //
            _SetHolderClientRpc(holderRef, netObj);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnKitObjForOrderServerRpc(
            KitchenObjEnum kitchenObjEnum,
            NetworkObjectReference holderRef,
            int orderId)
        {
            if (orderId == 0) return;

            var so = DataTableManager.Sigleton.GetKitchenObjSo(kitchenObjEnum);
            var obj = Instantiate(so.prefab).GetComponent<KitchenObj>();
            obj.EnsurePhysicsComponents();
            var netObj = obj.GetComponent<NetworkObject>();
            netObj.Spawn(true);
            obj.BindToOrder(orderId);

            // Keep the authoritative server-side holder in sync as well as
            // the clients. This is required when a timed facility starts
            // processing a newly spawned standalone pizza.
            if (holderRef.TryGet(out NetworkObject holderObj))
            {
                var holder = holderObj.GetComponent<ICanHoldKitchenObj>();
                if (holder != null)
                {
                    obj.SetHolder(holder);
                    holder.SetKitchenObj(obj);
                }
            }

            _SetHolderClientRpc(holderRef, netObj);
        }

        [ClientRpc]
        private void _SetHolderClientRpc(NetworkObjectReference holderRef, NetworkObjectReference objReference)
        {
            holderRef.TryGet(out NetworkObject holderObj);
            var holder = holderObj.GetComponent<ICanHoldKitchenObj>();
            objReference.TryGet(out NetworkObject obj);
            var kitchenObj = obj.GetComponent<KitchenObj>();

            if (holder.HasKitchenObj() && holder.GetKitchenObj() != kitchenObj)
            {
                Debug.LogWarning(
                    $"{holder}] already has:{holder.GetKitchenObj()}" +
                    $" it will be replaced by {kitchenObj}"
                );
                // var oldObj = holder.GetKitchenObj();
                // oldObj.SetHolder(null);
                // oldObj.gameObject.SetActive(false);
            }


            kitchenObj.SetHolder(holder);
            holder.SetKitchenObj(kitchenObj);
        }


        [ServerRpc(RequireOwnership = false)]
        public void PutKitObjServerRpc(NetworkObjectReference putterRef, NetworkObjectReference recieverRef)
        {
            if (!ApplyPutKitObj(putterRef, recieverRef))
                return;

            _PutKitObjClientRpc(putterRef, recieverRef);
        }

        private static bool ApplyPutKitObj(
            NetworkObjectReference putterRef,
            NetworkObjectReference recieverRef)
        {
            if (!putterRef.TryGet(out NetworkObject putterObj)) return false;
            if (!recieverRef.TryGet(out NetworkObject recieverObj)) return false;

            var putter = putterObj.GetComponent<ICanHoldKitchenObj>();
            var reciever = recieverObj.GetComponent<ICanHoldKitchenObj>();
            if (putter == null || reciever == null) return false;

            var obj = putter.GetKitchenObj();
            if (obj == null) return false;

            putter.ClearKitchenObj();
            obj.SetHolder(reciever);
            reciever.SetKitchenObj(obj);
            return true;
        }

        [ClientRpc]
        private void _PutKitObjClientRpc(NetworkObjectReference putterRef, NetworkObjectReference recieverRef)
        {
            if (IsServer) return;
            ApplyPutKitObj(putterRef, recieverRef);
        }


        [ServerRpc(RequireOwnership = false)]
        public void DropObjServerRpc(NetworkObjectReference objRef, Vector3 dropPosition, Vector3 dropDirection, float dropForce, ServerRpcParams rpcParams = default)
        {
            // Server-side validation: only the holder can drop
            if (!objRef.TryGet(out NetworkObject obj)) return;
            var kitchenObj = obj.GetComponent<KitchenObj>();
            if (kitchenObj == null) return;

            var senderClientId = rpcParams.Receive.SenderClientId;
            var holder = kitchenObj.GetHolder();
            if (holder == null) return;

            // Allow if: holder is player object OR holder is owned by the sender (AI chefs)
            var senderPlayerObj = NetworkManager.Singleton.ConnectedClients[senderClientId].PlayerObject;
            var holderNetObj = holder.GetNetworkObject();
            bool isValid = holderNetObj == senderPlayerObj ||
                           holderNetObj.OwnerClientId == senderClientId;
            if (!isValid) return;

            // The server is authoritative for IsFree, holder references and
            // physics. A ClientRpc alone leaves dedicated-server state stale,
            // so AI cannot find the dropped object again.
            kitchenObj.SetFree(dropPosition, dropDirection, dropForce);
            _DropObjClientRpc(objRef, dropPosition, dropDirection, dropForce);
        }

        [ClientRpc]
        private void _DropObjClientRpc(NetworkObjectReference objRef, Vector3 dropPosition, Vector3 dropDirection, float dropForce)
        {
            objRef.TryGet(out NetworkObject obj);
            if (obj == null) return;
            var kitchenObj = obj.GetComponent<KitchenObj>();
            if (kitchenObj != null && !kitchenObj.IsServer)
                kitchenObj.SetFree(dropPosition, dropDirection, dropForce);
        }

        [ServerRpc(RequireOwnership = false)]
        public void PickupObjServerRpc(NetworkObjectReference objRef, NetworkObjectReference holderRef, ServerRpcParams rpcParams = default)
        {
            // Server-side validation: object must be free, and holder must be a valid entity
            if (!objRef.TryGet(out NetworkObject obj)) return;
            if (!holderRef.TryGet(out NetworkObject holderObj)) return;
            var kitchenObj = obj.GetComponent<KitchenObj>();
            if (kitchenObj == null) return;

            if (!kitchenObj.IsFree) return;

            // Allow if: holder is player object OR holder is owned by the sender (AI chefs)
            var senderClientId = rpcParams.Receive.SenderClientId;
            var senderPlayerObj = NetworkManager.Singleton.ConnectedClients[senderClientId].PlayerObject;
            bool isValid = holderObj == senderPlayerObj ||
                           holderObj.OwnerClientId == senderClientId;
            if (!isValid) return;

            // Keep the authoritative server-side holder in sync immediately.
            var holder = holderObj.GetComponent<ICanHoldKitchenObj>();
            if (holder == null) return;
            kitchenObj.SetHeld(holder);
            holder.SetKitchenObj(kitchenObj);
            _PickupObjClientRpc(objRef, holderRef);
        }

        [ClientRpc]
        private void _PickupObjClientRpc(NetworkObjectReference objRef, NetworkObjectReference holderRef)
        {
            objRef.TryGet(out NetworkObject obj);
            holderRef.TryGet(out NetworkObject holderObj);
            if (obj == null || holderObj == null) return;

            var kitchenObj = obj.GetComponent<KitchenObj>();
            var holder = holderObj.GetComponent<ICanHoldKitchenObj>();
            if (kitchenObj != null && holder != null && !kitchenObj.IsServer)
            {
                kitchenObj.SetHeld(holder);
                holder.SetKitchenObj(kitchenObj);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void DestroyServerRpc(NetworkObjectReference objRef)
        {
            objRef.TryGet(out NetworkObject obj);
            _ClearHolderClientRpc(objRef); //清空持有者 这个需要在所有客户端执行  先清空持有者再销毁物体
            Destroy(obj.gameObject); //销毁物体
        }

        [ClientRpc]
        private void _ClearHolderClientRpc(NetworkObjectReference objRef)
        {
            objRef.TryGet(out NetworkObject obj);
            obj.GetComponent<KitchenObj>().ClearHolder();
        }
    }
}
