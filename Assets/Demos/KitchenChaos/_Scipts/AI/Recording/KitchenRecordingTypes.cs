using System;

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
    public class RecordingSessionManifest
    {
        public string sessionId;
        public string sceneName;
        public float unityTimeStart;
        public int frameWidth;
        public int frameHeight;
        public float captureFps;
        public int chefCount;
    }

    [Serializable]
    public class ChefRecordingFrame
    {
        public int agentId;
        public string chefName;
        public string fpImage;
        public bool keyW;
        public bool keyA;
        public bool keyS;
        public bool keyD;
        public bool keyE;
        /// <summary>Local strafe axis relative to view yaw (-1..1, A/D).</summary>
        public float moveX;
        /// <summary>Local forward axis relative to view yaw (-1..1, W/S).</summary>
        public float moveZ;
        /// <summary>Look yaw delta this frame (degrees, + = turn right).</summary>
        public float mouseX;
        /// <summary>Look pitch delta this frame (degrees, + = look up).</summary>
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
    }

    [Serializable]
    public class ItemSnapshot
    {
        public int id;
        public string itemType;
        public string stage;
        public float posX;
        public float posY;
        public float posZ;
        public int carriedByAgent;
        public int orderId;
    }

    [Serializable]
    public class OrderSnapshot
    {
        public int orderId;
        public string recipeName;
        public string[] ingredients;
    }

    [Serializable]
    public class TaskSnapshot
    {
        public int taskId;
        public string taskType;
        public string label;
        public string status;
        public int assignedAgentId;
        public int orderId;
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
        public ChefRecordingFrame[] chefs;
        public WorldStateSnapshot world;
    }
}
