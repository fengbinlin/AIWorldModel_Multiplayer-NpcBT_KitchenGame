using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Kitchen.AI;
using Kitchen.AI.Recording;
using Kitchen.Skin;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kitchen.Config
{
    /// <summary>
    /// Loads a rollout task from `--task path/to/task.json` before the first scene starts,
    /// then applies each category to the manager that owns it.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class KitchenTaskConfigManager : MonoBehaviour
    {
        private const string TaskArgument = "--task";
        private const string GetParamArgument = "--Getparam";

        public static KitchenTaskConfig Current { get; private set; }
        public static string SourcePath { get; private set; }
        public static bool HasTask => Current != null;

        private bool _sawPlayingState;
        private bool _quitScheduled;
        private double _episodeStartedAt = double.NaN;
        private readonly HashSet<int> _appliedSceneHandles = new();

#if UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX
        [DllImport("libc", EntryPoint = "_exit")]
        private static extern void UnixExit(int exitCode);
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Current = null;
            SourcePath = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (TryGetOptionalPathArgument(args, GetParamArgument, out string outputPath))
            {
                WriteDefaultConfig(outputPath);
                return;
            }

            if (!TryGetTaskPath(args, out string taskPath))
                return;

            try
            {
                SourcePath = Path.GetFullPath(taskPath);
                if (!File.Exists(SourcePath))
                    throw new FileNotFoundException("Task config does not exist", SourcePath);

                var config = new KitchenTaskConfig();
                JsonUtility.FromJsonOverwrite(File.ReadAllText(SourcePath), config);
                config.Normalize();
                ResolveRelativePaths(config, SourcePath);

                List<string> errors = config.Validate();
                if (errors.Count > 0)
                    throw new InvalidDataException(string.Join("; ", errors));

                Current = config;
                var go = new GameObject(nameof(KitchenTaskConfigManager));
                DontDestroyOnLoad(go);
                go.AddComponent<KitchenTaskConfigManager>();
                Debug.Log($"[KitchenTaskConfig] Loaded {SourcePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[KitchenTaskConfig] Failed to load --task: {e}");
                ExitImmediately(2);
            }
        }

        private static void WriteDefaultConfig(string outputPath)
        {
            try
            {
                var config = new KitchenTaskConfig();
                config.Normalize();
                string json = JsonUtility.ToJson(config, true);

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Console.Out.WriteLine(json);
                    Console.Out.Flush();
                }
                else
                {
                    string fullPath = Path.GetFullPath(outputPath);
                    string directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    File.WriteAllText(fullPath, json + Environment.NewLine);
                    Console.Error.WriteLine($"[KitchenTaskConfig] Wrote default config to {fullPath}");
                }

                ExitImmediately(0);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[KitchenTaskConfig] Failed to write defaults: {e}");
                ExitImmediately(2);
            }
        }

        private static void ExitImmediately(int exitCode)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(exitCode);
#elif UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX
            Console.Out.Flush();
            Console.Error.Flush();
            UnixExit(exitCode);
#else
            Environment.Exit(exitCode);
#endif
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Start()
        {
            if (Current == null || string.IsNullOrWhiteSpace(Current.run.scene))
                return;

            var active = SceneManager.GetActiveScene();
            if (!string.Equals(active.name, Current.run.scene, StringComparison.Ordinal))
                SceneManager.LoadScene(Current.run.scene, LoadSceneMode.Single);
            else
                ApplyToScene(active);
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            if (Current == null) return;
            if (!string.Equals(scene.name, Current.run.scene, StringComparison.Ordinal)) return;
            ApplyToScene(scene);
        }

        private void ApplyToScene(UnityEngine.SceneManagement.Scene scene)
        {
            if (!_appliedSceneHandles.Add(scene.handle)) return;

            try
            {
                ApplyRuntime(Current.run);
                AIDebugLogger.Configure(
                    Current.logging.enabled,
                    Current.logging.outputDirectory,
                    Current.logging.fileName);

                FindInScene<GameManager>(scene)?.ApplyTaskConfig(Current.episode, Current.run);
                FindInScene<PGCManager>(scene)?.ApplyTaskConfig(Current.world, Current.agents.count, Current.run.seed);
                FindInScene<KitchenAIManager>(scene)?.ApplyTaskConfig(Current.agents, Current.logging);
                FindInScene<DeliveryManager>(scene)?.ApplyTaskConfig(Current.orders);
                FindInScene<KitchenSessionRecorder>(scene)?.ApplyTaskConfig(Current.recording, Current.run, SourcePath);
                FindInScene<LocalPlayBootstrap>(scene)?.ApplyTaskConfig(
                    Current.run, Current.episode, Current.agents);
                FindInScene<SkinManager>(scene)?.ApplyTaskConfig(Current.appearance);
                ApplyNetwork(scene, Current.network);

                Debug.Log($"[KitchenTaskConfig] Applied task '{Current.run.taskId}' to scene '{scene.name}'.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[KitchenTaskConfig] Failed to apply task to scene '{scene.name}': {e}");
                Application.Quit(3);
            }
        }

        private void Update()
        {
            if (Current == null || !Current.run.quitOnGameOver || _quitScheduled)
                return;

            var game = GameManager.Instance;
            if (game == null) return;

            if (game.IsPlaying())
            {
                if (!_sawPlayingState)
                {
                    _sawPlayingState = true;
                    _episodeStartedAt = Time.timeAsDouble;
                }

                if (Current.episode.durationSeconds > 0f &&
                    Time.timeAsDouble - _episodeStartedAt >= Current.episode.durationSeconds)
                {
                    Debug.Log("[KitchenTaskConfig] Configured episode duration reached; exiting.");
                    _quitScheduled = true;
                    StartCoroutine(QuitAfterRecorderFlush());
                }
                return;
            }

            if (_sawPlayingState)
            {
                _quitScheduled = true;
                StartCoroutine(QuitAfterRecorderFlush());
            }
        }

        private static void ApplyRuntime(RunTaskConfig config)
        {
            Time.timeScale = config.timeScale;
            Application.targetFrameRate = config.targetFrameRate;
        }

        private static void ApplyNetwork(UnityEngine.SceneManagement.Scene scene, NetworkTaskConfig config)
        {
            var manager = FindInScene<NetworkManager>(scene);
            if (manager != null)
                manager.NetworkConfig.TickRate = (uint)config.tickRate;

            var transport = FindInScene<UnityTransport>(scene);
            if (transport != null)
                transport.SetConnectionData(config.address, (ushort)config.port, config.listenAddress);
        }

        private IEnumerator QuitAfterRecorderFlush()
        {
            yield return new WaitForEndOfFrame();
            var recorder = KitchenSessionRecorder.Instance;
            if (recorder != null && recorder.IsRecording)
                recorder.StopRecording(manual: false);

            if (recorder != null && !string.IsNullOrEmpty(recorder.SessionDirectory))
            {
                string marker = Path.Combine(recorder.SessionDirectory, "_SUCCESS");
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O") + "\n");
            }

            Debug.Log("[KitchenTaskConfig] Episode finished; exiting with code 0.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(0);
#endif
        }

        private static T FindInScene<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                var component = root.GetComponentInChildren<T>(true);
                if (component != null)
                    return component;
            }
            return null;
        }

        private static bool TryGetTaskPath(IReadOnlyList<string> args, out string taskPath)
        {
            taskPath = null;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg == TaskArgument && i + 1 < args.Count)
                {
                    taskPath = args[i + 1];
                    return !string.IsNullOrWhiteSpace(taskPath);
                }
                if (arg.StartsWith(TaskArgument + "=", StringComparison.Ordinal))
                {
                    taskPath = arg.Substring(TaskArgument.Length + 1);
                    return !string.IsNullOrWhiteSpace(taskPath);
                }
            }
            return false;
        }

        private static bool TryGetOptionalPathArgument(
            IReadOnlyList<string> args,
            string argumentName,
            out string outputPath)
        {
            outputPath = null;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, argumentName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        outputPath = args[i + 1];
                    return true;
                }

                string prefix = argumentName + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    outputPath = arg.Substring(prefix.Length);
                    return true;
                }
            }
            return false;
        }

        private static void ResolveRelativePaths(KitchenTaskConfig config, string taskPath)
        {
            string taskDirectory = Path.GetDirectoryName(taskPath) ?? Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(config.recording.outputDirectory) &&
                !Path.IsPathRooted(config.recording.outputDirectory))
            {
                config.recording.outputDirectory = Path.GetFullPath(
                    Path.Combine(taskDirectory, config.recording.outputDirectory));
            }

            if (string.IsNullOrWhiteSpace(config.logging.outputDirectory))
            {
                config.logging.outputDirectory = Path.Combine(
                    config.recording.outputDirectory,
                    "logs",
                    SafePathSegment(config.run.taskId),
                    $"worker_{SafePathSegment(config.run.workerId)}");
            }
            else if (!Path.IsPathRooted(config.logging.outputDirectory))
            {
                config.logging.outputDirectory = Path.GetFullPath(
                    Path.Combine(taskDirectory, config.logging.outputDirectory));
            }
        }

        private static string SafePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value.Replace(' ', '_');
        }
    }
}
