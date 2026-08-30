using System.Collections.Generic;
using Kitchen.AI;
using Kitchen.Skin;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen.PGC
{
    /// <summary>
    /// 按布局结果实例化柜子预制体（Resources/Prefab/Counters/）。
    /// 墙格用无功能的 WallCounter 填充；全部柜子朝向地图中心（90° 吸附）。
    /// </summary>
    public static class PGCKitchenSpawner
    {
        private const string PrefabDir = "Prefab/Counters/";
        private const string WallPrefabName = "WallCounter";

        private static readonly Dictionary<FacilityType, string> PrefabNames = new()
        {
            { FacilityType.Storage, "ContainerCounter" },
            { FacilityType.CuttingBoard, "CuttingCounter" },
            { FacilityType.FryingPan, "StoveCounter" },
            { FacilityType.Oven, "OvenCounter" },
            { FacilityType.Blender, "BlenderCounter" },
            { FacilityType.AssemblyTable, "ClearCounter" },
            { FacilityType.PlatesCounter, "PlateCounter" },
            { FacilityType.ServingCounter, "DeliveryCounter" },
            { FacilityType.TrashCan, "TrashCounter" },
        };

        public static Transform Spawn(
            PGCLayoutResult layout,
            Transform parent,
            bool destroyExistingCounters,
            bool networkSpawn)
        {
            if (layout == null) return null;

            if (destroyExistingCounters)
                DestroyExistingCounters();

            var root = new GameObject("PGC_Kitchen").transform;
            if (parent != null)
                root.SetParent(parent, false);

            int walls = 0;
            int clears = 0;

            foreach (var node in layout.facilities)
            {
                string prefabName;
                if (node.isWallPlaceholder)
                {
                    prefabName = WallPrefabName;
                }
                else if (!PrefabNames.TryGetValue(node.type, out prefabName))
                {
                    Debug.LogWarning($"[PGC-Spawn] No prefab for {node.type}");
                    continue;
                }

                var prefab = Resources.Load<GameObject>(PrefabDir + prefabName);
                if (prefab == null)
                {
                    Debug.LogError($"[PGC-Spawn] Missing prefab Resources/{PrefabDir}{prefabName}");
                    continue;
                }

                var rot = Quaternion.Euler(0f, node.yawDegrees, 0f);
                var go = Object.Instantiate(prefab, node.worldPos, rot, root);

                if (node.isWallPlaceholder)
                {
                    go.name = $"Wall_{node.gridX}_{node.gridY}";
                    walls++;
                }
                else
                {
                    go.name = $"{node.type}_{node.id}_{node.label}";
                    if (node.type == FacilityType.AssemblyTable)
                        clears++;
                }

                if (node.type == FacilityType.Storage && node.hasIngredient && !node.isWallPlaceholder)
                {
                    var container = go.GetComponent<ContainerCounter>();
                    if (container != null)
                    {
                        container.objEnum = node.ingredient;
                        // ApplyOn below will also refresh; call after Apply for safety.
                    }
                }

                if (networkSpawn)
                {
                    var net = go.GetComponent<NetworkObject>();
                    if (net != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                    {
                        if (!net.IsSpawned)
                            net.Spawn(true);
                    }
                }

                // 显式套皮（不依赖 Start 时序）
                CounterSkinApplier.ApplyOn(go);

                if (node.type == FacilityType.Storage && node.hasIngredient && !node.isWallPlaceholder)
                {
                    var container = go.GetComponent<ContainerCounter>();
                    container?.RefreshSampleDisplay();
                }
            }

            Debug.Log(
                $"[PGC-Spawn] Spawned {layout.facilities.Count} counters " +
                $"(clears={clears}, walls={walls}), spawns={layout.spawnPoints.Count}");
            return root;
        }

        private static void DestroyExistingCounters()
        {
            var counters = Object.FindObjectsOfType<BaseCounter>();
            foreach (var c in counters)
            {
                if (c == null) continue;
                var net = c.GetComponent<NetworkObject>();
                if (net != null && net.IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                    net.Despawn(true);
                else
                    Object.Destroy(c.gameObject);
            }
        }
    }
}
