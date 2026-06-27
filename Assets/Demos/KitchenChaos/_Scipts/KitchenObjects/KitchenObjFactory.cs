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

        [ClientRpc]
        private void _SetHolderClientRpc(NetworkObjectReference holderRef, NetworkObjectReference objReference)
        {
            holderRef.TryGet(out NetworkObject holderObj);
            var holder = holderObj.GetComponent<ICanHoldKitchenObj>();
            objReference.TryGet(out NetworkObject obj);
            var kitchenObj = obj.GetComponent<KitchenObj>();

            if (holder.HasKitchenObj())
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
            _PutKitObjClientRpc(putterRef, recieverRef);
        }

        [ClientRpc]
        private void _PutKitObjClientRpc(NetworkObjectReference putterRef, NetworkObjectReference recieverRef)
        {
            putterRef.TryGet(out NetworkObject putterObj);
            recieverRef.TryGet(out NetworkObject recieverObj);
            var putter = putterObj.GetComponent<ICanHoldKitchenObj>();
            var reciever = recieverObj.GetComponent<ICanHoldKitchenObj>();

            var obj = putter.GetKitchenObj();
            putter.ClearKitchenObj();
            obj.SetHolder(reciever);
            reciever.SetKitchenObj(obj);
        }


        [ServerRpc(RequireOwnership = false)]
        public void DropObjServerRpc(NetworkObjectReference objRef, Vector3 dropPosition, Vector3 dropDirection, float dropForce, ServerRpcParams rpcParams = default)
        {
            // Server-side validation: only the holder can drop
            if (!objRef.TryGet(out NetworkObject obj)) return;
            var kitchenObj = obj.GetComponent<KitchenObj>();
            if (kitchenObj == null) return;

            var senderClientId = rpcParams.Receive.SenderClientId;
            var senderPlayerObj = NetworkManager.Singleton.ConnectedClients[senderClientId].PlayerObject;
            var holder = kitchenObj.GetHolder();
            if (holder == null || holder.GetNetworkObject() != senderPlayerObj) return;

            _DropObjClientRpc(objRef, dropPosition, dropDirection, dropForce);
        }

        [ClientRpc]
        private void _DropObjClientRpc(NetworkObjectReference objRef, Vector3 dropPosition, Vector3 dropDirection, float dropForce)
        {
            objRef.TryGet(out NetworkObject obj);
            if (obj == null) return;
            var kitchenObj = obj.GetComponent<KitchenObj>();
            kitchenObj.SetFree(dropPosition, dropDirection, dropForce);
        }

        [ServerRpc(RequireOwnership = false)]
        public void PickupObjServerRpc(NetworkObjectReference objRef, NetworkObjectReference holderRef, ServerRpcParams rpcParams = default)
        {
            // Server-side validation: object must be free, and holder must be the requesting player
            if (!objRef.TryGet(out NetworkObject obj)) return;
            if (!holderRef.TryGet(out NetworkObject holderObj)) return;
            var kitchenObj = obj.GetComponent<KitchenObj>();
            if (kitchenObj == null) return;

            var senderClientId = rpcParams.Receive.SenderClientId;
            var senderPlayerObj = NetworkManager.Singleton.ConnectedClients[senderClientId].PlayerObject;
            if (!kitchenObj.IsFree || holderObj != senderPlayerObj) return;

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
            kitchenObj.SetHeld(holder);
            holder.SetKitchenObj(kitchenObj);
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