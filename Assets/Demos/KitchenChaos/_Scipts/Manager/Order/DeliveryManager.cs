using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nico.MVC;
using Nico.Network;
using Unity.Netcode;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Kitchen
{
    public class DeliveryManager : NetSingleton<DeliveryManager>
    {
        private const string _recipeSoDir = "So/Recipes/";

        private List<RecipeSo> _allRecipes;
        private readonly List<RecipeSo> _waitingQueue = new();
        private CancellationTokenSource _orderGenerateCts;

        public float spawnTime = 5f;
        public float spawnTimeRange = 2f;
        public int maxOrderCount = 3;
        private bool _isGeneratingOrder = false;
        public event EventHandler<RecipeSo> OnOrderAdded;
        public event EventHandler<RecipeSo> OnOrderFinished;
        public event EventHandler<Vector3> OnOrderSuccess;
        public event EventHandler<Vector3> OnOrderFailed;

        protected override void Awake()
        {
            base.Awake();
            _Init();
        }


        private void _Init()
        {
            var recipes = Resources.LoadAll<RecipeSo>(_recipeSoDir);
            _allRecipes = new List<RecipeSo>(recipes);
        }


        private void _OnGameStateChange(GameState arg1, GameState arg2)
        {
            if (!IsServer) return; //只有服务器端才会生成订单
            if (arg2 is ReadyToStartState)
            {
                //当切换到ReadyToStartState时，开始生成订单
                _GenerateOrder().Forget();
            }
        }

        /// <summary>
        /// 由服务器调用,在所有 客户端 上生成订单
        /// Tag 之所以只传递一个idx,是因为参数会在网络传输中被序列化,如果传递整个RecipeSo,会导致序列化失败 或者 开销过大
        /// </summary>
        /// <param name="recipeDataIdx"></param>
        [ClientRpc]
        private void SpawnOrderClientRpc(int recipeIdx)
        {
            var recipe = _allRecipes[recipeIdx];
            _waitingQueue.Add(recipe);
            OnOrderAdded?.Invoke(this, recipe);
        }

        private async UniTask _GenerateOrder()
        {
            _isGeneratingOrder = true;
            _orderGenerateCts = new CancellationTokenSource();
            while (!_orderGenerateCts.IsCancellationRequested)
            {
                //等待指定时间
                var randomFloat = UnityEngine.Random.Range(-spawnTimeRange, spawnTimeRange);
                await UniTask.Delay(TimeSpan.FromSeconds(spawnTime + randomFloat),
                    cancellationToken: _orderGenerateCts.Token);
                if (_waitingQueue.Count < maxOrderCount)
                {
                    var randomIndex = UnityEngine.Random.Range(0, _allRecipes.Count);
                    SpawnOrderClientRpc(randomIndex);
                    //如果在这里添加订单,则只会生成服务端的订单 客户端的订单需要通过网络同步来实现
                    //如果在Rpc中添加 则会在所有客户端上生成订单
                }
                else
                {
                    //订单已满
                    _isGeneratingOrder = false;
                    return;
                }
            }

            _isGeneratingOrder = false;
        }

        public bool TryDeliverOrder(Vector3 position, HashSet<KitchenObjEnum> ingredients)
        {
            //检查是否有匹配的订单
            int target = _CheckIngredients(ingredients);
            if (target == -1)
            {
                _FailedOrderServerRpc(position);
                return false;
            }

            Debug.Log("订单完成!!");
            _CompleteOrderServerRpc(target, position);

            return true;
        }

        [ClientRpc]
        private void _FailedOrderClientRpc(Vector3 position)
        {
            OnOrderFailed?.Invoke(this, position);
        }

        [ServerRpc(RequireOwnership = false)]
        private void _FailedOrderServerRpc(Vector3 position)
        {
            _FailedOrderClientRpc(position);
        }

        [ServerRpc(RequireOwnership = false)]
        private void _CompleteOrderServerRpc(int recipeDataIdx, Vector3 position)
        {
            _CompleteOrderClientRpc(recipeDataIdx, position);
            //尝试重新启动订单生成任务
            if (!_isGeneratingOrder)
            {
                _GenerateOrder().Forget();
            }
        }

        [ClientRpc]
        private void _CompleteOrderClientRpc(int recipeDataIdx, Vector3 position)
        {
            Debug.Log("订单完成!!");
            ++ModelManager.Get<CompletedOrderModel>().orderCount;

            _waitingQueue.RemoveAt(recipeDataIdx);
            OnOrderSuccess?.Invoke(this, position);
        }

        private int _CheckIngredients(HashSet<KitchenObjEnum> ingredients)
        {
            int target = -1;
            for (int i = 0; i < _waitingQueue.Count; i++)
            {
                var order = _waitingQueue[i];
                if (order.ingredients.Length != ingredients.Count)
                    continue;
                Debug.Log($"{string.Join(",", order.ingredients)} VS {string.Join(",", ingredients)}");
                bool flag = order.ingredients.All(ingredients.Contains);

                //如果有一个不相同则跳过
                if (!flag) continue;
                //有符合的订单 -> 跳出检查
                target = i;
                break;
            }

            return target;
        }

        public ICollection<RecipeSo> GetWaitingQueue()
        {
            return _waitingQueue;
        }

        #region 脚本 激活 取消激活

        protected override void OnEnable()
        {
            base.OnEnable();
            GameManager.Instance.stateMachine.onStateChange += _OnGameStateChange;
        }

        private void OnDisable()
        {
            _orderGenerateCts?.Cancel();

            if (GameManager.Instance != null)
            {
                GameManager.Instance.stateMachine.onStateChange -= _OnGameStateChange;
            }
        }

        #endregion
    }
}