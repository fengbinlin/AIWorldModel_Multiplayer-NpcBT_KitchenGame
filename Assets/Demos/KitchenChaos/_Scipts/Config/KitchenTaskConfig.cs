using System;
using System.Collections.Generic;

namespace Kitchen.Config
{
    /// <summary>
    /// JSON DTO loaded by KitchenTaskConfigManager. Values mirror the rollout-relevant
    /// Inspector settings in NPC_PGC.unity and are grouped by responsibility.
    /// </summary>
    [Serializable]
    public sealed class KitchenTaskConfig
    {
        public int version = 1;
        public RunTaskConfig run = new();
        public EpisodeTaskConfig episode = new();
        public WorldTaskConfig world = new();
        public AgentTaskConfig agents = new();
        public OrderTaskConfig orders = new();
        public RecordingTaskConfig recording = new();
        public NetworkTaskConfig network = new();
        public AppearanceTaskConfig appearance = new();
        public LoggingTaskConfig logging = new();

        public void Normalize()
        {
            run ??= new RunTaskConfig();
            episode ??= new EpisodeTaskConfig();
            world ??= new WorldTaskConfig();
            agents ??= new AgentTaskConfig();
            orders ??= new OrderTaskConfig();
            recording ??= new RecordingTaskConfig();
            network ??= new NetworkTaskConfig();
            appearance ??= new AppearanceTaskConfig();
            logging ??= new LoggingTaskConfig();
            world.recipeNames ??= Array.Empty<string>();
            agents.colors ??= Array.Empty<string>();
        }

        public List<string> Validate()
        {
            Normalize();
            var errors = new List<string>();

            if (version != 1) errors.Add($"version must be 1, got {version}");
            if (string.IsNullOrWhiteSpace(run.taskId)) errors.Add("run.taskId must not be empty");
            if (string.IsNullOrWhiteSpace(run.workerId)) errors.Add("run.workerId must not be empty");
            if (string.IsNullOrWhiteSpace(run.scene)) errors.Add("run.scene must not be empty");
            if (run.timeScale <= 0f) errors.Add("run.timeScale must be > 0");
            if (run.targetFrameRate == 0 || run.targetFrameRate < -1)
                errors.Add("run.targetFrameRate must be -1 or > 0");
            if (episode.durationSeconds < 0f) errors.Add("episode.durationSeconds must be >= 0");
            if (episode.readyCountdownSeconds < 0) errors.Add("episode.readyCountdownSeconds must be >= 0");
            if (episode.maxPlayers < 1) errors.Add("episode.maxPlayers must be >= 1");
            if (episode.autoReadyDelaySeconds < 0f) errors.Add("episode.autoReadyDelaySeconds must be >= 0");

            if (world.width < 4 || world.height < 4) errors.Add("world.width and world.height must be >= 4");
            if (world.dilateKernel < 0) errors.Add("world.dilateKernel must be >= 0");
            if (world.pathRandomness < 0f) errors.Add("world.pathRandomness must be >= 0");
            if (world.extraEdges < 0) errors.Add("world.extraEdges must be >= 0");
            if (world.cellSize <= 0f) errors.Add("world.cellSize must be > 0");
            if (world.randomRecipeCount < 1) errors.Add("world.randomRecipeCount must be >= 1");

            if (agents.count < 1) errors.Add("agents.count must be >= 1");
            if (agents.scheduleIntervalSeconds <= 0f) errors.Add("agents.scheduleIntervalSeconds must be > 0");
            if (agents.moveSpeed <= 0f) errors.Add("agents.moveSpeed must be > 0");
            if (agents.interactionRange <= 0f) errors.Add("agents.interactionRange must be > 0");
            if (agents.arrivalThreshold <= 0f || agents.arrivalThreshold > 1f)
                errors.Add("agents.arrivalThreshold must be in (0, 1]");
            if (agents.stuckTimeoutSeconds <= 0f) errors.Add("agents.stuckTimeoutSeconds must be > 0");
            if (agents.radius <= 0f) errors.Add("agents.radius must be > 0");
            if (agents.rvoBasePriority < 0f || agents.rvoBasePriority > 1f)
                errors.Add("agents.rvoBasePriority must be in [0, 1]");
            if (agents.approachOffset < 0f) errors.Add("agents.approachOffset must be >= 0");
            if (agents.interactAnimationHoldSeconds < 0f ||
                agents.postInteractAnimationHoldSeconds < 0f ||
                agents.minimumWorkAnimationSeconds < 0f ||
                agents.minimumWaitAnimationSeconds < 0f)
                errors.Add("agents animation timing values must be >= 0");
            if (agents.idleWanderPointCount < 1) errors.Add("agents.idleWanderPointCount must be >= 1");
            if (agents.wanderRadius < 0f || agents.wanderIntervalSeconds < 0f ||
                agents.wanderNavmeshTolerance < 0f)
                errors.Add("agents wander radius/interval/navmesh tolerance must be >= 0");
            if (agents.detourRadius < 0f || agents.detourAngleStep <= 0f)
                errors.Add("agents.detourRadius must be >= 0 and detourAngleStep must be > 0");

            if (orders.spawnIntervalSeconds < 0f || orders.spawnJitterSeconds < 0f)
                errors.Add("orders spawn interval/jitter must be >= 0");
            if (orders.spawnIntervalSeconds < orders.spawnJitterSeconds)
                errors.Add("orders.spawnIntervalSeconds must be >= orders.spawnJitterSeconds");
            if (orders.maxActive < 1) errors.Add("orders.maxActive must be >= 1");

            if (recording.enabled)
            {
                if (recording.captureFps <= 0f) errors.Add("recording.captureFps must be > 0");
                if (recording.width < 1 || recording.height < 1)
                    errors.Add("recording.width and recording.height must be >= 1");
                if (string.IsNullOrWhiteSpace(recording.outputDirectory))
                    errors.Add("recording.outputDirectory must not be empty");
                if (recording.maxPendingWrites < 1) errors.Add("recording.maxPendingWrites must be >= 1");
                if (!recording.captureGlobalCamera && !recording.captureAgentCameras)
                    errors.Add("recording must enable at least one camera source");
            }

            if (network.port < 1 || network.port > 65535) errors.Add("network.port must be in [1, 65535]");
            if (network.tickRate < 1) errors.Add("network.tickRate must be >= 1");
            if (string.IsNullOrWhiteSpace(network.address)) errors.Add("network.address must not be empty");
            if (string.IsNullOrWhiteSpace(network.listenAddress)) errors.Add("network.listenAddress must not be empty");
            if (string.IsNullOrWhiteSpace(logging.fileName)) errors.Add("logging.fileName must not be empty");
            if (PathHasDirectory(logging.fileName)) errors.Add("logging.fileName must be a file name, not a path");
            return errors;
        }

        private static bool PathHasDirectory(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0);
        }
    }

    [Serializable]
    public sealed class RunTaskConfig
    {
        public string taskId = "kitchen-rollout";
        public string workerId = "0";
        public string scene = "NPC_PGC";
        public int seed = 42;
        public float timeScale = 1f;
        public int targetFrameRate = -1;
        public bool quitOnGameOver = true;
        public bool initializeUnityServices = false;
    }

    [Serializable]
    public sealed class EpisodeTaskConfig
    {
        public float durationSeconds = 60f;
        public int readyCountdownSeconds = 1;
        public int maxPlayers = 4;
        public bool autoReady = true;
        public float autoReadyDelaySeconds = 0f;
    }

    [Serializable]
    public sealed class WorldTaskConfig
    {
        public bool useRandomRecipes = true;
        public int randomRecipeCount = 3;
        public string[] recipeNames = Array.Empty<string>();
        public int width = 16;
        public int height = 10;
        public int dilateKernel = 0;
        public float pathRandomness = 1.4f;
        public int extraEdges = 1;
        public float cellSize = 1.5f;
        public int layoutSeed = 42;
        public bool randomizeLayoutSeed = true;
        public bool destroyExistingCounters = true;
        public bool networkSpawnCounters = true;
    }

    [Serializable]
    public sealed class AgentTaskConfig
    {
        public bool enabled = true;
        public int count = 4;
        public float scheduleIntervalSeconds = 0.2f;
        public float moveSpeed = 4.2f;
        public float interactionRange = 2.5f;
        public float arrivalThreshold = 0.4f;
        public float stuckTimeoutSeconds = 8f;
        public float radius = 0.7f;
        public float rvoBasePriority = 0.5f;
        public float approachOffset = 1.2f;
        public string[] colors = Array.Empty<string>();
        public float interactAnimationHoldSeconds = 1.35f;
        public float postInteractAnimationHoldSeconds = 0.85f;
        public float minimumWorkAnimationSeconds = 2.8f;
        public float minimumWaitAnimationSeconds = 1.6f;
        public bool enableWander = true;
        public int idleWanderPointCount = 12;
        public float wanderRadius = 5f;
        public float wanderIntervalSeconds = 3f;
        public float wanderNavmeshTolerance = 0.5f;
        public float detourRadius = 2.5f;
        public float detourAngleStep = 60f;
    }

    [Serializable]
    public sealed class OrderTaskConfig
    {
        public float spawnIntervalSeconds = 2f;
        public float spawnJitterSeconds = 1f;
        public int maxActive = 3;
    }

    [Serializable]
    public sealed class RecordingTaskConfig
    {
        public bool enabled = true;
        public bool autoStart = true;
        public float captureFps = 60f;
        public int width = 1920;
        public int height = 1080;
        public string outputDirectory = "KitchenTrainingRecordings";
        public int maxPendingWrites = 64;
        public bool captureGlobalCamera = true;
        public bool captureAgentCameras = true;
    }

    [Serializable]
    public sealed class NetworkTaskConfig
    {
        public string address = "127.0.0.1";
        public string listenAddress = "0.0.0.0";
        public int port = 7777;
        public int tickRate = 30;
    }

    [Serializable]
    public sealed class AppearanceTaskConfig
    {
        public bool overrideGlobalSkin = false;
        public int globalSkinId = 0;
        public bool enableCharacterAnimation = false;
    }

    [Serializable]
    public sealed class LoggingTaskConfig
    {
        public bool enabled = true;
        public string outputDirectory = "";
        public string fileName = "ai_debug.log";
    }
}
