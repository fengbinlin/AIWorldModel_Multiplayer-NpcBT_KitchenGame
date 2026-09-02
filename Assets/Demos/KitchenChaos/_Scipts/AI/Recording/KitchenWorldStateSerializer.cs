using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.PGC;
using UnityEngine;

namespace Kitchen.AI.Recording
{
    public static class KitchenWorldStateSerializer
    {
        public static WorldStateSnapshot Capture(KitchenBlackboard bb)
        {
            if (bb == null)
                return new WorldStateSnapshot();

            var snapshot = new WorldStateSnapshot
            {
                facilities = CaptureFacilities(bb),
                items = CaptureItems(bb),
                orders = CaptureOrders(bb),
                tasks = CaptureTasks(bb),
            };
            return snapshot;
        }

        /// <summary>
        /// Static scene layout for manifest (facility placement / spawn points).
        /// </summary>
        public static Scene3DInfo CaptureScene3D(
            KitchenBlackboard bb,
            IReadOnlyList<Vector3> spawnPositions,
            IReadOnlyList<Vector3> groundDropPositions = null)
        {
            var scene = new Scene3DInfo
            {
                coordinateSystem = "Unity left-handed, Y-up, world units in meters",
                facilities = CaptureSceneFacilities(bb),
                spawnPoints = CaptureSpawnPoints(spawnPositions),
                groundDropPoints = CaptureSpawnPoints(groundDropPositions),
            };
            return scene;
        }

        /// <summary>
        /// Capture the physical actors that define the generated kitchen. The grid in
        /// PGCLayoutResult is the logical topology; these records are the corresponding
        /// world transforms and collision bounds.
        /// </summary>
        public static GameActorSnapshot[] CaptureMapActors(PGCLayoutResult layout)
        {
            var actorsById = new Dictionary<string, GameActorSnapshot>();
            var generatedRoot = GameObject.Find("PGC_Kitchen")?.transform;

            if (generatedRoot != null)
            {
                var children = new List<Transform>();
                for (int i = 0; i < generatedRoot.childCount; i++)
                    children.Add(generatedRoot.GetChild(i));

                foreach (var child in children
                    .Where(t => t != null && t.gameObject.activeInHierarchy)
                    .OrderBy(t => t.name)
                    .ThenBy(t => t.position.x)
                    .ThenBy(t => t.position.z))
                {
                    var node = FindLayoutNode(layout, child.position);
                    var counter = child.GetComponent<BaseCounter>();
                    string actorId = node != null
                        ? $"map:{node.id}"
                        : counter != null ? GetMapActorId(counter, layout) : $"map:name:{child.name}";
                    string actorType = node?.isWallPlaceholder == true
                        ? "Wall"
                        : counter != null ? counter.GetType().Name : child.name;
                    var actor = CaptureActor(child, actorId, "map", actorType);
                    actor.layoutId = node?.id ?? -1;
                    actor.gridX = node?.gridX ?? -1;
                    actor.gridY = node?.gridY ?? -1;
                    actorsById[actor.actorId] = actor;
                }
            }

            // NGO may detach spawned NetworkObjects from PGC_Kitchen. Merge active
            // counters with root children and de-duplicate them by logical actor id.
            foreach (var counter in Object.FindObjectsOfType<BaseCounter>()
                .Where(c => c != null && c.gameObject.activeInHierarchy)
                .OrderBy(c => c.name))
            {
                var node = FindLayoutNode(layout, counter.transform.position);
                var actor = CaptureActor(counter, GetMapActorId(counter, layout), "map", counter.GetType().Name);
                actor.layoutId = node?.id ?? -1;
                actor.gridX = node?.gridX ?? -1;
                actor.gridY = node?.gridY ?? -1;
                actorsById[actor.actorId] = actor;
            }

            return actorsById.Values
                .OrderBy(a => a.layoutId < 0 ? int.MaxValue : a.layoutId)
                .ThenBy(a => a.actorId)
                .ToArray();
        }

        /// <summary>Capture moving gameplay actors (AI chefs and kitchen items).</summary>
        public static GameActorSnapshot[] CaptureDynamicActors(KitchenBlackboard bb)
        {
            var actors = new List<GameActorSnapshot>();
            if (bb == null)
                return actors.ToArray();

            foreach (var agent in bb.agents.OrderBy(a => a.agentId))
            {
                if (agent?.controller == null) continue;
                var actor = CaptureActor(
                    agent.controller,
                    GetAgentActorId(agent.controller.agentId),
                    "agent",
                    nameof(AIChefController));
                actor.layoutId = -1;
                actor.gridX = -1;
                actor.gridY = -1;
                actors.Add(actor);
            }

            foreach (var item in bb.items.OrderBy(i => i?.objectId ?? 0UL))
            {
                if (item?.kitchenObj == null) continue;
                var actor = CaptureActor(
                    item.kitchenObj,
                    GetItemActorId(item.kitchenObj),
                    "item",
                    item.itemType.ToString());
                actor.layoutId = -1;
                actor.gridX = -1;
                actor.gridY = -1;
                actor.holderActorId = GetHolderActorId(item.kitchenObj.GetHolder() as Component);
                actors.Add(actor);
            }

            return actors.ToArray();
        }

        public static string GetAgentActorId(int agentId) => $"agent:{agentId}";

        public static string GetItemActorId(KitchenObj item)
        {
            if (item == null) return "";
            return item.RuntimeObjectId != 0UL
                ? $"item:{item.RuntimeObjectId}"
                : $"item:instance:{item.GetInstanceID()}";
        }

        public static string GetMapActorId(BaseCounter counter)
            => GetMapActorId(counter, PGCManager.Instance?.LastLayout);

        private static string GetMapActorId(BaseCounter counter, PGCLayoutResult layout)
        {
            if (counter == null) return "";
            var node = FindLayoutNode(layout, counter.transform.position);
            if (node != null)
                return $"map:{node.id}";
            return counter.NetworkObject != null && counter.NetworkObject.IsSpawned
                ? $"map:net:{counter.NetworkObject.NetworkObjectId}"
                : $"map:name:{counter.name}";
        }

        private static string GetHolderActorId(Component holder)
        {
            if (holder == null) return "";
            if (holder is AIChefController chef)
                return GetAgentActorId(chef.agentId);
            if (holder is BaseCounter counter)
                return GetMapActorId(counter);
            return $"scene:instance:{holder.GetInstanceID()}";
        }

        private static PGCFacilityNode FindLayoutNode(PGCLayoutResult layout, Vector3 position)
        {
            if (layout?.facilities == null) return null;
            const float maxSqrDistance = 0.01f;
            PGCFacilityNode closest = null;
            float closestSqr = float.MaxValue;
            foreach (var node in layout.facilities)
            {
                if (node == null) continue;
                float sqr = (node.worldPos - position).sqrMagnitude;
                if (sqr <= maxSqrDistance && sqr < closestSqr)
                {
                    closest = node;
                    closestSqr = sqr;
                }
            }
            return closest;
        }

        private static GameActorSnapshot CaptureActor(
            Component component,
            string actorId,
            string category,
            string actorType)
        {
            Transform t = component.transform;
            Quaternion q = t.rotation;
            Vector3 scale = t.lossyScale;
            return new GameActorSnapshot
            {
                actorId = actorId,
                category = category,
                actorType = actorType,
                name = component.name,
                active = component.gameObject.activeInHierarchy,
                posX = t.position.x,
                posY = t.position.y,
                posZ = t.position.z,
                rotX = q.x,
                rotY = q.y,
                rotZ = q.z,
                rotW = q.w,
                scaleX = scale.x,
                scaleY = scale.y,
                scaleZ = scale.z,
                aabb = CaptureBounds(component.gameObject),
                holderActorId = "",
            };
        }

        private static ActorAabbSnapshot CaptureBounds(GameObject go)
        {
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (var collider in go.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            // Visual-only actors may have no collider. Preserve a useful AABB fallback.
            if (!hasBounds)
            {
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;
                    if (!hasBounds)
                    {
                        bounds = renderer.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(renderer.bounds);
                    }
                }
            }

            Vector3 center = hasBounds ? bounds.center : go.transform.position;
            Vector3 size = hasBounds ? bounds.size : Vector3.zero;
            Vector3 min = hasBounds ? bounds.min : center;
            Vector3 max = hasBounds ? bounds.max : center;
            return new ActorAabbSnapshot
            {
                valid = hasBounds,
                centerX = center.x,
                centerY = center.y,
                centerZ = center.z,
                sizeX = size.x,
                sizeY = size.y,
                sizeZ = size.z,
                minX = min.x,
                minY = min.y,
                minZ = min.z,
                maxX = max.x,
                maxY = max.y,
                maxZ = max.z,
            };
        }

        private static SceneFacilityInfo[] CaptureSceneFacilities(KitchenBlackboard bb)
        {
            var list = new List<SceneFacilityInfo>();
            if (bb?.facilities == null)
                return list.ToArray();

            foreach (var f in bb.facilities)
            {
                if (f?.counter == null) continue;
                var t = f.counter.transform;
                GetBoundsSize(f.counter.gameObject, out float sx, out float sy, out float sz);
                list.Add(new SceneFacilityInfo
                {
                    name = f.counter.name,
                    facilityType = f.type.ToString(),
                    posX = t.position.x,
                    posY = t.position.y,
                    posZ = t.position.z,
                    rotY = t.eulerAngles.y,
                    sizeX = sx,
                    sizeY = sy,
                    sizeZ = sz,
                });
            }
            return list.ToArray();
        }

        private static SceneSpawnPointInfo[] CaptureSpawnPoints(IReadOnlyList<Vector3> spawnPositions)
        {
            if (spawnPositions == null || spawnPositions.Count == 0)
                return new SceneSpawnPointInfo[0];

            var list = new SceneSpawnPointInfo[spawnPositions.Count];
            for (int i = 0; i < spawnPositions.Count; i++)
            {
                var p = spawnPositions[i];
                list[i] = new SceneSpawnPointInfo { posX = p.x, posY = p.y, posZ = p.z };
            }
            return list;
        }

        private static void GetBoundsSize(GameObject go, out float sx, out float sy, out float sz)
        {
            var bounds = CaptureBounds(go);
            sx = bounds.sizeX;
            sy = bounds.sizeY;
            sz = bounds.sizeZ;
        }

        private static FacilitySnapshot[] CaptureFacilities(KitchenBlackboard bb)
        {
            var list = new List<FacilitySnapshot>();
            foreach (var f in bb.facilities)
            {
                if (f?.counter == null) continue;
                var pos = f.counter.transform.position;
                list.Add(new FacilitySnapshot
                {
                    name = f.counter.name,
                    facilityType = f.type.ToString(),
                    state = f.state,
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    counterItem = f.counter.HasKitchenObj()
                        ? f.counter.GetKitchenObj().objEnum.ToString()
                        : "",
                    reservedByAgent = f.reservedByAgent,
                    occupiedByAgent = f.occupiedByAgent,
                    timer = f.timer,
                    providedIngredient = f.providedIngredient.ToString(),
                });
            }
            return list.ToArray();
        }

        private static ItemSnapshot[] CaptureItems(KitchenBlackboard bb)
        {
            var list = new List<ItemSnapshot>();
            foreach (var i in bb.items)
            {
                if (i == null) continue;
                var pos = i.Position;
                var holder = i.kitchenObj != null ? i.kitchenObj.GetHolder() as Component : null;
                list.Add(new ItemSnapshot
                {
                    id = i.id,
                    objectId = i.objectId.ToString(),
                    itemType = i.itemType.ToString(),
                    stage = i.stage.ToString(),
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    carriedByAgent = i.carriedByAgent,
                    reservedByTask = i.reservedByTask,
                    exclusiveDelivererAgentId = i.exclusiveDelivererAgentId,
                    orderId = i.orderId,
                    holderName = holder != null ? holder.name : "",
                });
            }
            return list.ToArray();
        }

        private static OrderSnapshot[] CaptureOrders(KitchenBlackboard bb)
        {
            var list = new List<OrderSnapshot>();
            for (int i = 0; i < bb.activeOrders.Count; i++)
            {
                var recipe = bb.activeOrders[i];
                if (recipe == null) continue;
                int orderId = i < bb.activeOrderIds.Count ? bb.activeOrderIds[i] : 0;
                list.Add(new OrderSnapshot
                {
                    orderId = orderId,
                    orderCode = bb.orderCodeById.TryGetValue(orderId, out var code)
                        ? code
                        : $"ORDER_{orderId:D6}",
                    recipeName = recipe.recipeName,
                    ingredients = new[] { recipe.requiredItem.ToString() },
                });
            }
            return list.ToArray();
        }

        private static TaskSnapshot[] CaptureTasks(KitchenBlackboard bb)
        {
            var list = new List<TaskSnapshot>();
            foreach (var t in bb.taskPool)
            {
                if (t == null) continue;
                list.Add(new TaskSnapshot
                {
                    taskId = t.id,
                    actionType = t.actionType.ToString(),
                    objectAId = t.objectAId.ToString(),
                    objectBId = t.objectBId.ToString(),
                    taskType = t.type.ToString(),
                    label = t.label,
                    status = t.status,
                    assignedAgentId = t.assignedAgentId,
                    orderId = t.orderId,
                    dependencyTaskIds = t.dependencyTaskIds?.ToArray() ?? System.Array.Empty<int>(),
                    preconditions = t.preconditions?.Select(c => c.type.ToString()).ToArray()
                        ?? System.Array.Empty<string>(),
                    postconditions = t.postconditions?.Select(c => c.type.ToString()).ToArray()
                        ?? System.Array.Empty<string>(),
                });
            }
            return list.ToArray();
        }
    }
}
