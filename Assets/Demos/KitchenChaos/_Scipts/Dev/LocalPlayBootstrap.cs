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
    ///
    /// AI 模式：
    /// 4. 勾选 enableAIChefs，在场景中放置 KitchenAIManager + 4 个 AIChefController GameObject
    /// 5. Player 预制体仍会生成（用于网络/相机），但默认由 AI 接管
    /// </summary>
    public class LocalPlayBootstrap : MonoBehaviour
    {
        [Header("拖入 Player 预制体（与 GameManager 上的相同）")]
        [SerializeField] private GameObject playerPrefab;

        [Header("AI 模式")]
        [SerializeField] private bool enableAIChefs = false;
        [SerializeField] private bool disableHumanInput = true;

        [Header("跳过准备阶段")]
        [SerializeField] private bool autoReady = true;
        [SerializeField] private float autoReadyDelay = 1.5f;

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

            Debug.Log("[LocalPlayBootstrap] 启动完成！游戏处于 WaitingToStart 状态");

            // AI mode: initialize KitchenAIManager after everything is set up
            if (enableAIChefs)
            {
                await UniTask.NextFrame();
                var aiManager = FindObjectOfType<AI.KitchenAIManager>();
                if (aiManager != null)
                {
                    aiManager.Initialize();
                    Debug.Log("[LocalPlayBootstrap] AI 模式已激活 — KitchenAIManager 启动");
                }
                else
                {
                    Debug.LogWarning("[LocalPlayBootstrap] enableAIChefs=true 但场景中未找到 KitchenAIManager！");
                }

                // Disable human input on the local player if requested
                if (disableHumanInput)
                {
                    var playerInput = PlayerInput.Instance;
                    if (playerInput != null)
                    {
                        playerInput.Disable();
                        Debug.Log("[LocalPlayBootstrap] 人类玩家输入已禁用（AI 模式）");
                    }
                }
            }

            // Auto-ready: skip the "press E to ready" step
            if (autoReady)
            {
                await UniTask.Delay(System.TimeSpan.FromSeconds(autoReadyDelay));
                Debug.Log("[LocalPlayBootstrap] Auto-ready: triggering ready for local player...");
                gm.SetPlayerReadyServerRpc();
            }
        }
    }
}
