using System;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    public class ContainerCounter : BaseCounter
    {
        private KitchenObjSo _kitchenObjSo;
        public KitchenObjEnum objEnum;
        public event Action OnInteractEvent;
        public SpriteRenderer re;

        protected override void Awake()
        {
            base.Awake();
            RebindObjectSprite();
        }

        private void Start()
        {
            _kitchenObjSo = DataTableManager.Sigleton.GetKitchenObjSo(objEnum);
            RebindObjectSprite();
            if (re != null && _kitchenObjSo != null)
                re.sprite = _kitchenObjSo.sprite;
        }

        /// <summary>
        /// 换肤会 Destroy 旧 Visual，预制体上序列化的 ObjectSprite 引用会失效，需重新查找。
        /// </summary>
        private void RebindObjectSprite()
        {
            if (re != null) return;

            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != "ObjectSprite") continue;
                re = t.GetComponent<SpriteRenderer>();
                if (re != null) return;
            }
        }

        public override void Interact(ICanHoldKitchenObj holder)
        {
            Debug.Log(holder + "尝试获取道具" + $"它是否有道具:{holder.HasKitchenObj()}");
            if (holder.HasKitchenObj())
            {
                Debug.Log("获取失败");
                return;
            }

            Debug.Log("请求生成道具");
            KitchenObjOperator.SpawnKitchenObjRpc(_kitchenObjSo.kitchenObjEnum, holder);
            _OnInteractServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void _OnInteractServerRpc()
        {
            _OnInteractClientRpc();
        }

        [ClientRpc]
        private void _OnInteractClientRpc()
        {
            Debug.Log("交互事件触发");
            OnInteractEvent?.Invoke();
        }
    }
}
