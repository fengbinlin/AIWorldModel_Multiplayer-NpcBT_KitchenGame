using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// 开发用快速启动脚本。
    /// 放在你的 GameScene 副本中，按 Play 即可跳过大厅+角色选择，直接进入游戏。
    ///
    /// 场景要求：
    /// 1. 从 LobbyScene 复制 NetworkManager GameObject 到这个场景
    /// 2. 从 LobbyScene 复制 GameManager GameObject 到这个场景（自带 PlayerInput 组件）
    /// 3. 将这个脚本挂到任意 GameObject 上，并拖入 Player 预制体引用
    /// </summary>
    public class LocalPlayBootstrap : MonoBehaviour
    {
        [Header("拖入 Player 预制体（与 GameManager 上的相同）")]
        [SerializeField] private GameObject playerPrefab;

        private async void Start()
        {
            // 等待一帧，确保所有 Awake/Start 执行完毕
            await UniTask.NextFrame();
            await BootstrapSequence();
        }

        private async UniTask BootstrapSequence()
        {
            // ============================================================
            // Step 1: 初始化 Unity Services + 匿名认证
            // GameManager 的客户端连接回调需要 AuthenticationService.PlayerId
            // ============================================================
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetProfile($"dev_{Random.Range(0, 100000)}");
                await UnityServices.InitializeAsync(options);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            // ============================================================
            // Step 2: 启动 Host
            // GameManager.StartHost() 注册连接/断开回调，然后调用 NGO StartHost()
            // ============================================================
            var nm = NetworkManager.Singleton;
            var gm = GameManager.Instance;

            if (!nm.IsHost && !nm.IsServer && !nm.IsClient)
            {
                Debug.Log("[LocalPlayBootstrap] Starting Host...");
                gm.StartHost();
            }

            if (!nm.IsHost)
            {
                Debug.LogError("[LocalPlayBootstrap] 无法作为 Host 启动，请检查 NetworkManager 配置");
                return;
            }

            // 等待一帧让 Host 启动回调完毕（playerConfigs 填充等）
            await UniTask.NextFrame();

            if (!nm.IsServer)
            {
                Debug.LogError("[LocalPlayBootstrap] 不是 Server，无法生成玩家");
                return;
            }

            // ============================================================
            // Step 3: 模拟 OnLoadSceneCompleted 的逻辑
            // 正常流程：EnterGame() → NGO LoadScene → OnLoadSceneCompleted
            // 我们现在已经在 GameScene 中，所以直接执行关键步骤：
            //   A: 状态机推进到 WaitingToStart
            //   B: 为每个已连接客户端生成 Player
            // ============================================================
            Debug.Log("[LocalPlayBootstrap] 推进状态到 WaitingToStart 并生成玩家...");

            gm.ChangeStateClientRpc(GameStateEnum.WaitingToStart);

            foreach (var clientId in nm.ConnectedClientsIds)
            {
                var playerObj = Instantiate(playerPrefab);
                playerObj.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true);
            }

            Debug.Log("[LocalPlayBootstrap] 启动完成！游戏处于 WaitingToStart 状态，按 E 准备开始");
        }
    }
}
