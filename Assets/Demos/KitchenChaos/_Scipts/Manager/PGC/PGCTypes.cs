using System;
using System.Collections.Generic;
using Kitchen.AI;
using UnityEngine;

namespace Kitchen.PGC
{
    /// <summary>PGC 规划出的单个设施节点（布局前）。</summary>
    [Serializable]
    public class PGCFacilityNode
    {
        public int id;
        public FacilityType type;
        /// <summary>仅 Storage 有意义：该柜子提供的原料。</summary>
        public KitchenObjEnum ingredient;
        public bool hasIngredient;
        /// <summary>所属菜品在本轮池中的下标；共享设施为 -1。</summary>
        public int recipeIndex = -1;
        public int stepIndex;
        public string label;
        /// <summary>墙占位（用空柜顶替），不参与 AI 设施扫描。</summary>
        public bool isWallPlaceholder;

        // 布局结果（细格总坐标，含外围墙偏移）
        public int gridX;
        public int gridY;
        public Vector3 worldPos;
        /// <summary>Y 轴朝向（已按 90° 吸附，面向地图中心）。</summary>
        public float yawDegrees;
    }

    /// <summary>Mission 有向关系（用于铺通路）。</summary>
    [Serializable]
    public class PGCMissionEdge
    {
        public int fromId;
        public int toId;
        public string kind; // mission | plates_delivery | trash_delivery
    }

    public enum PGCCell : short
    {
        Wall = 0,
        Path = 1,
        Facility = 10, // 实际存 10+facilityId
    }

    [Serializable]
    public class PGCLayoutResult
    {
        public int playW;
        public int playH;
        public int totalW;
        public int totalH;
        public float cellSize;
        public Vector3 origin; // 格子 (0,0) 的世界坐标
        public short[] grid;
        public List<PGCFacilityNode> facilities = new();
        public List<PGCMissionEdge> edges = new();
        public List<Vector3> spawnPoints = new();
        public int clearCount;
        public int wallCount;
        /// <summary>地图中心世界坐标（用于朝向 / A*）。</summary>
        public Vector3 centerWorld;
    }

    [Serializable]
    public class PGCLayoutParams
    {
        public int playW = 16;
        public int playH = 10;
        public int kernel = 3;
        public float randomness = 1.4f;
        public int extraEdges = 1;
        public int spawnCount = 4;
        public float cellSize = 1.5f;
        public int seed = 42;
    }
}
