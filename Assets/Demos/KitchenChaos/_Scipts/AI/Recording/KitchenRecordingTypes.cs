using System;
using Kitchen.PGC;

namespace Kitchen.AI.Recording
{
    /// <summary>
    /// First-person controls reverse-engineered from AI behavior:
    /// WASD in view-yaw local space, mouse XY = look deltas (degrees).
    /// moveX = strafe (A/D), moveZ = forward (W/S).
    /// </summary>
    [Serializable]
    public struct ChefKeyboardInput
    {
        public bool W;
        public bool A;
        public bool S;
        public bool D;
        public bool E;
        public float moveX;
        public float moveZ;
        public float mouseX;
        public float mouseY;
    }

    [Serializable]
    public class CameraIntrinsic
    {
        public float fx;
        public float fy;
        public float cx;
        public float cy;
        public int width;
        public int height;
        public float fovY;
    }

    /// <summary>
    /// Camera pose in world space (global). matrix = localToWorld, row-major 4x4.
    /// </summary>
    [Serializable]
    public class CameraExtrinsic
    {
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;
        public float[] matrix;
    }

    [Serializable]
    public class CameraInfo
    {
        public string name;
        public CameraIntrinsic @int;
        public CameraExtrinsic ext;
    }

    [Serializable]
    public class SceneFacilityInfo
    {
        public string name;
        public string facilityType;
        public float posX;
        public float posY;
        public float posZ;
        public float rotY;
        public float sizeX;
        public float sizeY;
        public float sizeZ;
    }

    [Serializable]
    public class SceneSpawnPointInfo
    {
        public float posX;
        public float posY;
        public float posZ;
    }

    [Serializable]
    public class Scene3DInfo
    {
        public string coordinateSystem;
        public SceneFacilityInfo[] facilities;
        public SceneSpawnPointInfo[] spawnPoints;
        public SceneSpawnPointInfo[] groundDropPoints;
    }

    /// <summary>World-space axis-aligned bounds assembled from all child colliders.</summary>
    [Serializable]
    public class ActorAabbSnapshot
    {
        public bool valid;
        public float centerX;
        public float centerY;
        public float centerZ;
        public float sizeX;
        public float sizeY;
        public float sizeZ;
        public float minX;
        public float minY;
        public float minZ;
        public float maxX;
        public float maxY;
        public float maxZ;
    }

    /// <summary>
    /// A physical gameplay actor. Static map actors live in the manifest; moving
    /// agents and items are captured on every frame.
    /// </summary>
    [Serializable]
    public class GameActorSnapshot
    {
        public string actorId;
        public string category;
        public string actorType;
        public string name;
        public int layoutId;
        public int gridX;
        public int gridY;
        public bool active;
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;
        public float rotW;
        public float scaleX;
        public float scaleY;
        public float scaleZ;
        public ActorAabbSnapshot aabb;
        public string holderActorId;
    }

    /// <summary>One concrete agent-to-actor interaction observed during this frame.</summary>
    [Serializable]
    public class InteractionSnapshot
    {
        public string sourceActorId;
        public string targetActorId;
        public string targetCategory;
        public string targetType;
        public string targetName;
        public string interactionType;
        public bool stateChanged;
        public int taskId;
        public string taskType;
        public string taskLabel;
        public string heldItemBefore;
        public string heldItemAfter;
        public string targetItemBefore;
        public string targetItemAfter;
    }

    [Serializable]
    public class RecordingSessionManifest
    {
        public int schemaVersion;
        public string schemaName;
        public string sessionId;
        public string taskId;
        public string workerId;
        public int seed;
        public string gameName;
        public string sceneName;
        public string unityVersion;
        public string applicationVersion;
        public string createdUtc;
        public string completedUtc;
        public string status;
        public string taskFile;
        public string framesFile;
        public float unityTimeStart;
        public int frameWidth;
        public int frameHeight;
        public float captureFps;
        public int playerCount;
        public int totalFrames;
        public string task_description;
        public string[] round_recipes;
        public Scene3DInfo scene_3d_info;
        public GameActorSnapshot[] map_actors;
        public PGCLayoutResult pgc_layout;
        public WorldStateSnapshot initial_world;
        public WorldStateSnapshot final_world;
    }

    /// <summary>
    /// Per-player observation + action on one recorded frame.
    /// Action fields (keys / move / mouse) are the control applied at this state
    /// to reach the next frame: state_i + action_i => state_(i+1).
    /// Last frame has zeroed actions (no successor).
    /// </summary>
    [Serializable]
    public class PlayerRecordingFrame
    {
        public int playerId;
        public string playerName;
        public string fpImage;
        public bool keyW;
        public bool keyA;
        public bool keyS;
        public bool keyD;
        public bool keyE;
        /// <summary>Local strafe at state_i view yaw (-1..1, A/D).</summary>
        public float moveX;
        /// <summary>Local forward at state_i view yaw (-1..1, W/S).</summary>
        public float moveZ;
        /// <summary>Look yaw delta action_i (degrees, + = turn right).</summary>
        public float mouseX;
        /// <summary>Look pitch delta action_i (degrees, + = look up).</summary>
        public float mouseY;
        public float posX;
        public float posY;
        public float posZ;
        /// <summary>View pitch (signed degrees).</summary>
        public float rotX;
        /// <summary>View yaw (degrees).</summary>
        public float rotY;
        public float captureTime;
        public string substate;
        public string taskType;
        public string taskLabel;
        public string heldItem;
        /// <summary>First-person camera: int + world-space ext (localToWorld).</summary>
        public CameraInfo camera_info;
    }

    [Serializable]
    public class FacilitySnapshot
    {
        public string name;
        public string facilityType;
        public string state;
        public float posX;
        public float posY;
        public float posZ;
        public string counterItem;
        public int reservedByAgent;
        public int occupiedByAgent;
        public float timer;
        public string providedIngredient;
    }

    [Serializable]
    public class ItemSnapshot
    {
        public int id;
        public string objectId;
        public string itemType;
        public string stage;
        public float posX;
        public float posY;
        public float posZ;
        public int carriedByAgent;
        public int reservedByTask;
        public int exclusiveDelivererAgentId;
        public int orderId;
        public string holderName;
    }

    [Serializable]
    public class OrderSnapshot
    {
        public int orderId;
        public string orderCode;
        public string recipeName;
        public string[] ingredients;
    }

    [Serializable]
    public class TaskSnapshot
    {
        public int taskId;
        public string actionType;
        public string objectAId;
        public string objectBId;
        public string taskType;
        public string label;
        public string status;
        public int assignedAgentId;
        public int orderId;
        public int[] dependencyTaskIds;
        public string[] preconditions;
        public string[] postconditions;
    }

    [Serializable]
    public class WorldStateSnapshot
    {
        public FacilitySnapshot[] facilities;
        public ItemSnapshot[] items;
        public OrderSnapshot[] orders;
        public TaskSnapshot[] tasks;
    }

    [Serializable]
    public class RecordingFrameData
    {
        public int frame;
        public float time;
        public string globalImage;
        /// <summary>Global / third-person camera for this frame (world-space ext).</summary>
        public CameraInfo camera_info;
        public PlayerRecordingFrame[] players;
        public GameActorSnapshot[] actors;
        public InteractionSnapshot[] interactions;
        public WorldStateSnapshot world;
    }
}
