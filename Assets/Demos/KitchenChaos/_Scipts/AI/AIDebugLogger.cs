using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// File-based debug logger for the AI Chef system.
    /// Writes structured logs to a file in persistentDataPath so they can be
    /// copied and shared for debugging.
    ///
    /// Usage:
    ///   AIDebugLogger.Log("Scheduler", "Assigning task...");
    ///   AIDebugLogger.LogState("Agent1", "moving", "→ CuttingCounter(3)");
    ///   AIDebugLogger.LogError("Agent2", "Stuck! Abandoning task");
    ///
    /// Log file location: {persistentDataPath}/ai_debug_log.txt
    /// Previous session's log is moved to ai_debug_log_prev.txt on startup.
    /// </summary>
    public static class AIDebugLogger
    {
        private static StringBuilder _buffer = new StringBuilder(65536);
        private static string _logPath;
        private static bool _initialized;
        private static int _frameCount;
        private static float _sessionStartTime;

        // How often to flush to disk (in seconds)
        private const float FLUSH_INTERVAL = 1.0f;
        private static float _lastFlushTime;

        // Max lines before forcing a flush
        private const int MAX_BUFFER_LINES = 200;

        // Track warnings/errors for summary
        private static int _warningCount;
        private static int _errorCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void AutoInit()
        {
            Init();
        }

        public static void Init()
        {
            if (_initialized) return;

            // Write to project root (one level above Assets/)
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            _logPath = Path.Combine(projectRoot, "ai_debug_log.txt");

            // Rotate previous log
            string prevPath = Path.Combine(projectRoot, "ai_debug_log_prev.txt");
            try
            {
                if (File.Exists(_logPath))
                {
                    if (File.Exists(prevPath)) File.Delete(prevPath);
                    File.Move(_logPath, prevPath);
                }
            }
            catch { /* ignore file errors */ }

            _buffer.Clear();
            _sessionStartTime = Time.time;
            _warningCount = 0;
            _errorCount = 0;

            _initialized = true;

            WriteHeader();
            Debug.Log($"[AIDebugLogger] Log file: {_logPath}");
        }

        private static void WriteHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine("══════════════════════════════════════════════════");
            sb.AppendLine($"  AI Chef Debug Log");
            sb.AppendLine($"  Session: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"  Platform: {Application.platform}");
            sb.AppendLine($"  Unity: {Application.unityVersion}");
            sb.AppendLine("══════════════════════════════════════════════════");
            sb.AppendLine();
            AppendToBuffer(sb.ToString());
            FlushToDisk();
        }

        #region Public API

        /// <summary>
        /// Log a general message with a category tag.
        /// </summary>
        public static void Log(string category, string message)
        {
            EnsureInit();
            AppendLine($"[{FormatTime()}] [{category}] {message}");
        }

        /// <summary>
        /// Log a state transition for an agent or facility.
        /// </summary>
        public static void LogState(string entity, string oldState, string newState, string detail = "")
        {
            EnsureInit();
            string detailStr = string.IsNullOrEmpty(detail) ? "" : $" | {detail}";
            AppendLine($"[{FormatTime()}] [STATE] {entity}: {oldState} → {newState}{detailStr}");
        }

        /// <summary>
        /// Log a task assignment with full score breakdown.
        /// </summary>
        public static void LogAssignment(int agentId, string agentName, KitchenTask task)
        {
            EnsureInit();
            var sb = new StringBuilder();
            sb.Append($"[{FormatTime()}] [ASSIGN] Agent#{agentId}({agentName}) ← {task.label}");
            sb.Append($" type={task.type} score={task.score:F2}");
            if (task.targetFacility != null)
                sb.Append($" facility={task.targetFacility.name}");
            if (task.targetItem != null)
                sb.Append($" item={task.targetItem.objEnum}@{task.targetItem.transform.position:F0}");
            if (task.scoreDetail != null)
            {
                var d = task.scoreDetail;
                sb.Append($" | d={d.distance:F2} w={d.facilityWait:F2} u={d.orderUrgency:F2} ul={d.unlockValue:F2} r={d.roleBonus:F2} f={d.freshPickBonus:F2} s={d.stalePickPenalty:F2}");
            }
            AppendLine(sb.ToString());
        }

        /// <summary>
        /// Log a task completion.
        /// </summary>
        public static void LogTaskComplete(int agentId, string agentName, KitchenTask task, string result)
        {
            EnsureInit();
            string taskLabel = task?.label ?? "null";
            AppendLine($"[{FormatTime()}] [COMPLETE] Agent#{agentId}({agentName}) {taskLabel}: {result}");
        }

        /// <summary>
        /// Log a task abandonment (timeout, stuck, etc).
        /// </summary>
        public static void LogTaskAbandon(int agentId, string agentName, KitchenTask task, string reason)
        {
            EnsureInit();
            string taskLabel = task?.label ?? "null";
            AppendLine($"[{FormatTime()}] [ABANDON] Agent#{agentId}({agentName}) {taskLabel}: {reason}");
        }

        /// <summary>
        /// Log a facility reservation/release.
        /// </summary>
        public static void LogFacility(string facilityName, string action, string detail = "")
        {
            EnsureInit();
            string detailStr = string.IsNullOrEmpty(detail) ? "" : $" | {detail}";
            AppendLine($"[{FormatTime()}] [FACILITY] {facilityName}: {action}{detailStr}");
        }

        /// <summary>
        /// Log an item reservation/release.
        /// </summary>
        public static void LogItem(string itemType, string action, string detail = "")
        {
            EnsureInit();
            string detailStr = string.IsNullOrEmpty(detail) ? "" : $" | {detail}";
            AppendLine($"[{FormatTime()}] [ITEM] {itemType}: {action}{detailStr}");
        }

        /// <summary>
        /// Log the scheduler cycle start with summary stats.
        /// </summary>
        public static void LogSchedulerCycle(int cycleNum, int agentCount, int taskCount, int orderCount)
        {
            EnsureInit();
            AppendLine($"[{FormatTime()}] ═══ SCHEDULER CYCLE #{cycleNum} ═══ agents={agentCount} tasks={taskCount} orders={orderCount}");
        }

        /// <summary>
        /// Log a warning.
        /// </summary>
        public static void LogWarning(string category, string message)
        {
            EnsureInit();
            _warningCount++;
            AppendLine($"[{FormatTime()}] [WARN] [{category}] {message}");
        }

        /// <summary>
        /// Log an error.
        /// </summary>
        public static void LogError(string category, string message)
        {
            EnsureInit();
            _errorCount++;
            AppendLine($"[{FormatTime()}] [ERROR] [{category}] {message}");
        }

        /// <summary>
        /// Log deadlock detection and resolution.
        /// </summary>
        public static void LogDeadlock(string action, List<AgentState> agents)
        {
            EnsureInit();
            var sb = new StringBuilder();
            sb.Append($"[{FormatTime()}] [DEADLOCK] {action}");
            foreach (var a in agents)
            {
                sb.Append($" | Agent#{a.agentId}: {a.substate} task={a.currentTask?.label ?? "none"}");
            }
            AppendLine(sb.ToString());
        }

        /// <summary>
        /// Log blackboard snapshot for debugging.
        /// </summary>
        public static void LogBlackboardSnapshot(KitchenBlackboard bb)
        {
            EnsureInit();
            var sb = new StringBuilder();
            sb.AppendLine($"[{FormatTime()}] ═══ BLACKBOARD SNAPSHOT ═══");

            sb.AppendLine($"  Facilities ({bb.facilities.Count}):");
            foreach (var f in bb.facilities)
            {
                string marker = f.state != "free" ? $" [{f.state}]" : "";
                sb.AppendLine($"    {f.counter?.name ?? "?"} type={f.type}{marker} reservedBy={f.reservedByAgent}");
            }

            sb.AppendLine($"  Items ({bb.items.Count}):");
            foreach (var i in bb.items)
            {
                string carried = i.carriedByAgent >= 0 ? $" carriedBy={i.carriedByAgent}" : "";
                string reserved = i.reservedByTask >= 0 ? $" reservedByTask={i.reservedByTask}" : "";
                sb.AppendLine($"    {i.itemType} stage={i.stage}{carried}{reserved}");
            }

            sb.AppendLine($"  Agents ({bb.agents.Count}):");
            foreach (var a in bb.agents)
            {
                sb.AppendLine($"    Agent#{a.agentId} substate={a.substate} pos=({a.position.x:F1},{a.position.z:F1}) task={a.currentTask?.label ?? "none"}");
            }

            sb.AppendLine($"  Orders: {bb.activeOrders.Count}");

            AppendToBuffer(sb.ToString());
        }

        /// <summary>
        /// Flush the buffer to disk. Called automatically every FLUSH_INTERVAL seconds.
        /// </summary>
        public static void FlushToDisk()
        {
            if (_buffer.Length == 0) return;
            try
            {
                File.AppendAllText(_logPath, _buffer.ToString());
                _buffer.Clear();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[AIDebugLogger] Failed to flush: {e.Message}");
            }
        }

        /// <summary>
        /// Called each frame by KitchenAIManager to handle periodic flush.
        /// </summary>
        public static void Update()
        {
            if (!_initialized) return;

            _frameCount++;
            float timeSinceFlush = Time.time - _lastFlushTime;

            // Flush periodically or when buffer is large
            int lineCount = _buffer.ToString().Split('\n').Length;
            if (timeSinceFlush >= FLUSH_INTERVAL || lineCount >= MAX_BUFFER_LINES)
            {
                FlushToDisk();
                _lastFlushTime = Time.time;
            }
        }

        /// <summary>
        /// Get the log file path for display.
        /// </summary>
        public static string GetLogPath() => _logPath;

        /// <summary>
        /// Get stats for the current session.
        /// </summary>
        public static string GetStats()
        {
            return $"Frames: {_frameCount}, Warnings: {_warningCount}, Errors: {_errorCount}, Buffer: {_buffer.Length} chars";
        }

        #endregion

        #region Internal

        private static void EnsureInit()
        {
            if (!_initialized) Init();
        }

        private static string FormatTime()
        {
            float elapsed = Time.time - _sessionStartTime;
            int minutes = (int)(elapsed / 60);
            int seconds = (int)(elapsed % 60);
            int millis = (int)((elapsed % 1) * 1000);
            return $"{minutes:D2}:{seconds:D2}.{millis:D3}";
        }

        private static void AppendLine(string line)
        {
            _buffer.AppendLine(line);
        }

        private static void AppendToBuffer(string text)
        {
            _buffer.Append(text);
        }

        #endregion
    }
}
