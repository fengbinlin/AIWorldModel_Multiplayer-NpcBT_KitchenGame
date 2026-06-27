using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// Real-time on-screen display showing:
    ///   1. Each agent's current task + substate (top-left)
    ///   2. Active orders with completed/total steps (top-right)
    ///   3. Recent log entries scrollable (bottom)
    /// Toggle with F3.
    /// </summary>
    public class ScreenDebugDisplay : MonoBehaviour
    {
        [Header("Display")]
        [SerializeField] private int _fontSize = 13;
        [SerializeField] private int _logLines = 12;
        [SerializeField] private Color _bgColor = new Color(0.08f, 0.08f, 0.18f, 0.82f);

        private bool _show = true;
        private readonly Queue<string> _logEntries = new();
        private Texture2D _bgTex;
        private GUIStyle _labelStyle;
        private GUIStyle _boldStyle;
        private GUIStyle _smallStyle;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _bgTex = MakeTex(1, 1, _bgColor);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F3))
                _show = !_show;
        }

        private void OnEnable()
        {
            Application.logMessageReceived += OnLogMessage;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= OnLogMessage;
        }

        private void OnLogMessage(string logString, string stackTrace, LogType type)
        {
            // Only capture AI-related logs
            if (!logString.StartsWith("[") && !logString.Contains("AI_Chef") &&
                !logString.Contains("Scheduler") && !logString.Contains("GenTasks") &&
                !logString.Contains("Blackboard") && !logString.Contains("GreedyAssign") &&
                !logString.Contains("Assign") && !logString.Contains("ABANDON"))
                return;

            string prefix = type switch
            {
                LogType.Error => "🔴",
                LogType.Warning => "🟡",
                _ => "  "
            };

            // Trim long messages
            if (logString.Length > 120)
                logString = logString.Substring(0, 117) + "...";

            _logEntries.Enqueue($"{prefix} {logString}");
            while (_logEntries.Count > 80)
                _logEntries.Dequeue();
        }

        private void InitStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = _fontSize,
                normal = { textColor = new Color(0.9f, 0.9f, 0.9f) },
                wordWrap = false,
                richText = true,
                padding = new RectOffset(4, 4, 1, 1)
            };

            _boldStyle = new GUIStyle(_labelStyle)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.yellow }
            };

            _smallStyle = new GUIStyle(_labelStyle)
            {
                fontSize = _fontSize - 2,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) }
            };
        }

        private void OnGUI()
        {
            if (!_show) return;
            InitStyles();

            var manager = KitchenAIManager.Instance;
            if (manager == null || manager.Blackboard == null)
            {
                GUI.Label(new Rect(10, 10, 400, 30), "KitchenAIManager not running", _labelStyle);
                return;
            }

            var bb = manager.Blackboard;

            // === LEFT PANEL: Agent Status ===
            DrawPanel(10, 10, 380, 30 + bb.agents.Count * 26,
                "👨‍🍳 Agent Status",
                () =>
                {
                    foreach (var agent in bb.agents)
                    {
                        string stateIcon = agent.substate switch
                        {
                            "idle" => "💤",
                            "moving" => "🏃",
                            "working" => "🔧",
                            "waiting" => "⏳",
                            "interacting" => "🤝",
                            _ => "❓"
                        };

                        string taskLabel = agent.currentTask?.label ?? "<color=#888>—</color>";
                        string agentName = agent.controller?.chefName ?? $"Agent{agent.agentId}";
                        Color agentColor = agent.controller?.chefColor ?? Color.white;
                        string colorHex = ColorUtility.ToHtmlStringRGB(agentColor);

                        GUILayout.Label(
                            $"<color=#{colorHex}>●</color> {agentName} {stateIcon} {taskLabel}",
                            _labelStyle, GUILayout.Height(22));
                    }
                });

            // === RIGHT PANEL: Active Orders ===
            var orderLines = new List<string>();
            foreach (var order in bb.activeOrders)
            {
                int orderId = bb.activeOrderIds[bb.activeOrders.IndexOf(order)];
                if (bb.recipeStepChains.TryGetValue(order.recipeName, out var steps))
                {
                    // Count completed steps (output exists)
                    int done = 0;
                    foreach (var step in steps)
                    {
                        if (step.taskType == TaskType.FETCH_PLATE)
                        {
                            if (bb.FindPlateForOrder(orderId) != null) done++;
                        }
                        else if (step.taskType == TaskType.ADD_TO_PLATE && step.inputType.HasValue)
                        {
                            var plate = bb.FindPlateForOrder(orderId);
                            if (plate != null && plate.GetIngredients().Contains(step.inputType.Value)) done++;
                        }
                        else if (step.outputType.HasValue)
                        {
                            int avail = bb.FindItemsOfType(step.outputType.Value, excludeReserved: true)
                                .Count(i => !i.IsCarried && i.kitchenObj != null &&
                                    (i.kitchenObj.IsFree || i.kitchenObj.GetHolder() is BaseCounter));
                            if (avail > 0) done++;
                        }
                    }
                    int total = steps.Count;
                    float pct = total > 0 ? (float)done / total : 0f;
                    string bar = new string('█', Mathf.RoundToInt(pct * 10)).PadRight(10, '░');
                    orderLines.Add($"<color=#f39c12>{order.recipeName}</color> #{orderId} [{bar}] {done}/{total}");
                }
            }

            DrawPanel(Screen.width - 320, 10, 310, 30 + orderLines.Count * 22,
                "📋 Orders",
                () =>
                {
                    foreach (var line in orderLines)
                        GUILayout.Label(line, _labelStyle, GUILayout.Height(20));
                });

            // === BOTTOM PANEL: Task Assignments ===
            var taskLines = new List<string>();
            foreach (var agent in bb.agents)
            {
                if (agent.currentTask != null)
                {
                    string typeTag = agent.currentTask.type switch
                    {
                        TaskType.FETCH => "<color=#3498db>取</color>",
                        TaskType.FETCH_PLATE => "<color=#2ecc71>碟</color>",
                        TaskType.PROCESS => "<color=#f39c12>切/煎</color>",
                        TaskType.ADD_TO_PLATE => "<color=#e67e22>装</color>",
                        TaskType.SERVE => "<color=#e74c3c>送</color>",
                        TaskType.TRASH => "<color=#888>🗑</color>",
                        _ => "?"
                    };
                    string agentName = agent.controller?.chefName ?? $"A{agent.agentId}";
                    taskLines.Add($"{typeTag} <b>{agentName}</b> → {agent.currentTask.label}");
                }
            }

            // Also show unassigned pending tasks from task pool
            foreach (var task in bb.taskPool)
            {
                if (task.status != "assigned" && task.status != "executing" && task.status != "completed")
                {
                    string typeTag = task.type switch
                    {
                        TaskType.FETCH => "<color=#3498db>取</color>",
                        TaskType.PROCESS => "<color=#f39c12>切/煎</color>",
                        TaskType.ADD_TO_PLATE => "<color=#e67e22>装</color>",
                        _ => "?"
                    };
                    taskLines.Add($"{typeTag} <color=#888>待分配</color> → {task.label}");
                }
            }

            DrawPanel(10, Screen.height - 140, Mathf.Min(700, Screen.width - 20), 30 + Mathf.Min(taskLines.Count, 6) * 22,
                "📝 Active Tasks",
                () =>
                {
                    int show = Mathf.Min(taskLines.Count, 6);
                    for (int i = 0; i < show; i++)
                        GUILayout.Label(taskLines[i], _labelStyle, GUILayout.Height(20));
                    if (taskLines.Count > 6)
                        GUILayout.Label($"... +{taskLines.Count - 6} more", _smallStyle);
                });

            // === LOG PANEL: Bottom-right ===
            DrawPanel(720, Screen.height - 240, Mathf.Min(Screen.width - 730, 500), 240,
                $"📄 Log ({_logEntries.Count})",
                () =>
                {
                    var entries = _logEntries.Reverse().Take(_logLines).Reverse();
                    foreach (var entry in entries)
                        GUILayout.Label(entry, _smallStyle, GUILayout.Height(17));
                });
        }

        private void DrawPanel(float x, float y, float w, float h, string title, System.Action drawContent)
        {
            var rect = new Rect(x, y, w, h);
            GUI.Box(rect, "", new GUIStyle(GUI.skin.box) { normal = { background = _bgTex } });

            GUILayout.BeginArea(new Rect(x + 6, y + 4, w - 12, h - 8));
            GUILayout.Label($"<b>{title}</b>", _boldStyle ?? new GUIStyle(GUI.skin.label)
            {
                fontSize = _fontSize,
                normal = { textColor = Color.yellow },
                richText = true
            });
            drawContent();
            GUILayout.EndArea();
        }

        private Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pix);
            tex.Apply();
            return tex;
        }
    }
}
