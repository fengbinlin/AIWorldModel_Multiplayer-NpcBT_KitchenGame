using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kitchen.PGC
{
    /// <summary>
    /// Mission-and-Space 布局：2×2 粗块通路 + K×K 膨胀 + 贴通路墙→空台。
    /// 最小格 = 一个柜子。
    /// </summary>
    public class KitchenLayoutGenerator
    {
        private System.Random _rng;

        public PGCLayoutResult Generate(
            List<PGCFacilityNode> facilities,
            List<PGCMissionEdge> edges,
            PGCLayoutParams p)
        {
            _rng = new System.Random(p.seed);
            int kernel = p.kernel;
            if (kernel % 2 == 0) kernel += 1;

            int playW = Mathf.Max(8, p.playW);
            int playH = Mathf.Max(6, p.playH);
            // 设施多时自动放大地盘
            int minCells = Mathf.Max(playW * playH, facilities.Count * 6 + 40);
            while (playW * playH < minCells)
            {
                playW += 2;
                playH += 2;
            }

            int totalW = playW + 2;
            int totalH = playH + 2;
            var grid = new short[totalW * totalH];

            PlaceFacilities(facilities, playW, playH);
            MarkFacilities(grid, totalW, facilities);

            // Mission + extra 粗块通路
            var missionPairs = BuildUniquePairs(edges);
            CarvePairs(grid, totalW, facilities, missionPairs, playW, playH, p.randomness);

            var extras = PickExtraPairs(facilities, missionPairs, p.extraEdges);
            CarvePairs(grid, totalW, facilities, extras, playW, playH, p.randomness);

            DilatePaths(grid, totalW, playW, playH, facilities, kernel);

            int clearCount = SpawnClearsFromPathWalls(
                grid, totalW, totalH, playW, playH, facilities);

            // 剩余墙格（含外围圈）全部用空柜占位
            int wallCount = SpawnWallPlaceholders(grid, totalW, totalH, facilities);

            MarkFacilities(grid, totalW, facilities);

            float cell = p.cellSize;
            // 居中：以地盘中心为世界原点附近
            var origin = new Vector3(
                -totalW * cell * 0.5f + cell * 0.5f,
                0f,
                -totalH * cell * 0.5f + cell * 0.5f);

            var centerWorld = new Vector3(
                origin.x + (totalW - 1) * 0.5f * cell,
                0f,
                origin.z + (totalH - 1) * 0.5f * cell);

            foreach (var f in facilities)
            {
                f.worldPos = GridToWorld(f.gridX, f.gridY, origin, cell);
                f.yawDegrees = YawTowardCenterSnapped90(f.worldPos, centerWorld);
            }

            var spawns = PickSpawnPoints(
                grid, totalW, playW, playH, facilities, p.spawnCount, origin, cell);

            return new PGCLayoutResult
            {
                playW = playW,
                playH = playH,
                totalW = totalW,
                totalH = totalH,
                cellSize = cell,
                origin = origin,
                centerWorld = centerWorld,
                grid = grid,
                facilities = facilities,
                edges = edges,
                spawnPoints = spawns,
                clearCount = clearCount,
                wallCount = wallCount,
            };
        }

        /// <summary>面向中心，Y 轴旋转吸附到 90°。</summary>
        public static float YawTowardCenterSnapped90(Vector3 worldPos, Vector3 centerWorld)
        {
            Vector3 dir = centerWorld - worldPos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f)
                return 0f;

            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return Mathf.Round(yaw / 90f) * 90f;
        }

        private static Vector3 GridToWorld(int gx, int gy, Vector3 origin, float cell)
        {
            return origin + new Vector3(gx * cell, 0f, gy * cell);
        }

        private static int Idx(int x, int y, int tw) => y * tw + x;

        private static bool OnPlayfield(int x, int y, int playW, int playH)
            => x >= 1 && y >= 1 && x <= playW && y <= playH;

        private static bool IsBorder(int x, int y, int tw, int th)
            => x == 0 || y == 0 || x == tw - 1 || y == th - 1;

        private static bool IsPlayfieldBoundary(int x, int y, int playW, int playH)
            => x == 1 || y == 1 || x == playW || y == playH;

        private static (int BW, int BH) BlockDims(int playW, int playH)
            => (Mathf.FloorToInt(playW / 2f), Mathf.FloorToInt(playH / 2f));

        private static (int bx, int by) ToBlock(int x, int y, int playW, int playH)
        {
            var (bw, bh) = BlockDims(playW, playH);
            if (bw <= 0 || bh <= 0) return (0, 0);
            int lx = x - 1, ly = y - 1;
            return (
                Mathf.Clamp(lx / 2, 0, bw - 1),
                Mathf.Clamp(ly / 2, 0, bh - 1));
        }

        private static (int x, int y) BlockOrigin(int bx, int by)
            => (1 + bx * 2, 1 + by * 2);

        private void PlaceFacilities(List<PGCFacilityNode> facilities, int playW, int playH)
        {
            var occupied = new HashSet<long>();
            var placed = new List<PGCFacilityNode>();

            var deliveryCells = new List<(int x, int y)>();
            for (int y = 1; y <= playH; y++)
            for (int x = 1; x <= playW; x++)
                if (IsPlayfieldBoundary(x, y, playW, playH))
                    deliveryCells.Add((x, y));

            foreach (var f in facilities)
            {
                if (f.type != Kitchen.AI.FacilityType.ServingCounter) continue;
                var cell = deliveryCells[_rng.Next(deliveryCells.Count)];
                f.gridX = cell.x;
                f.gridY = cell.y;
                occupied.Add(Pack(cell.x, cell.y));
                placed.Add(f);
            }

            var candidates = new List<(int x, int y)>();
            for (int y = 1; y <= playH; y++)
            for (int x = 1; x <= playW; x++)
                if (!occupied.Contains(Pack(x, y)))
                    candidates.Add((x, y));
            Shuffle(candidates);

            foreach (var f in facilities)
            {
                if (f.type == Kitchen.AI.FacilityType.ServingCounter) continue;

                (int x, int y)? selected = null;
                foreach (var c in candidates)
                {
                    if (occupied.Contains(Pack(c.x, c.y))) continue;
                    bool illegal = false;
                    foreach (var other in placed)
                    {
                        if (FacilityPairIllegal(c.x, c.y, other.gridX, other.gridY))
                        {
                            illegal = true;
                            break;
                        }
                    }

                    if (!illegal)
                    {
                        selected = c;
                        break;
                    }
                }

                if (selected == null)
                {
                    foreach (var c in candidates)
                    {
                        if (!occupied.Contains(Pack(c.x, c.y)))
                        {
                            selected = c;
                            break;
                        }
                    }
                }

                if (selected == null)
                {
                    Debug.LogWarning($"[PGC-Layout] No cell for {f.label}, forcing (1,1)");
                    selected = (1, 1);
                }

                f.gridX = selected.Value.x;
                f.gridY = selected.Value.y;
                occupied.Add(Pack(f.gridX, f.gridY));
                placed.Add(f);
            }
        }

        private static bool FacilityPairIllegal(int ax, int ay, int bx, int by)
        {
            int dx = Mathf.Abs(ax - bx);
            int dy = Mathf.Abs(ay - by);
            if (dx == 0 && dy == 0) return true;
            if (dx >= 2 || dy >= 2) return false;
            return !((dx == 1 && dy == 0) || (dx == 0 && dy == 1));
        }

        private static void MarkFacilities(short[] grid, int tw, List<PGCFacilityNode> facilities)
        {
            foreach (var f in facilities)
            {
                int i = Idx(f.gridX, f.gridY, tw);
                if (i >= 0 && i < grid.Length)
                    grid[i] = (short)(10 + f.id);
            }
        }

        private List<(int a, int b)> BuildUniquePairs(List<PGCMissionEdge> edges)
        {
            var set = new HashSet<long>();
            var list = new List<(int a, int b)>();
            foreach (var e in edges)
            {
                int lo = Mathf.Min(e.fromId, e.toId);
                int hi = Mathf.Max(e.fromId, e.toId);
                long key = ((long)lo << 32) | (uint)hi;
                if (set.Add(key))
                    list.Add((e.fromId, e.toId));
            }
            return list;
        }

        private List<(int a, int b)> PickExtraPairs(
            List<PGCFacilityNode> facilities,
            List<(int a, int b)> existing,
            int extraCount)
        {
            var existingKeys = new HashSet<long>();
            foreach (var e in existing)
            {
                int lo = Mathf.Min(e.a, e.b);
                int hi = Mathf.Max(e.a, e.b);
                existingKeys.Add(((long)lo << 32) | (uint)hi);
            }

            var candidates = new List<(int a, int b)>();
            for (int i = 0; i < facilities.Count; i++)
            {
                for (int j = i + 1; j < facilities.Count; j++)
                {
                    var a = facilities[i];
                    var b = facilities[j];
                    if (a.type == Kitchen.AI.FacilityType.ServingCounter
                        || b.type == Kitchen.AI.FacilityType.ServingCounter)
                        continue;
                    long key = ((long)a.id << 32) | (uint)b.id;
                    int lo = Mathf.Min(a.id, b.id);
                    int hi = Mathf.Max(a.id, b.id);
                    key = ((long)lo << 32) | (uint)hi;
                    if (existingKeys.Contains(key)) continue;
                    candidates.Add((a.id, b.id));
                }
            }

            Shuffle(candidates);
            int n = Mathf.Min(extraCount, candidates.Count);
            return candidates.GetRange(0, n);
        }

        private void CarvePairs(
            short[] grid,
            int tw,
            List<PGCFacilityNode> facilities,
            List<(int a, int b)> pairs,
            int playW,
            int playH,
            float randomness)
        {
            var map = new Dictionary<int, PGCFacilityNode>();
            foreach (var f in facilities) map[f.id] = f;

            var (bw, bh) = BlockDims(playW, playH);
            foreach (var pair in pairs)
            {
                if (!map.TryGetValue(pair.a, out var fa) || !map.TryGetValue(pair.b, out var fb))
                    continue;
                var fromB = ToBlock(fa.gridX, fa.gridY, playW, playH);
                var toB = ToBlock(fb.gridX, fb.gridY, playW, playH);
                var path = RandomBlockPath(fromB, toB, bw, bh, randomness);
                foreach (var bl in path)
                    PaintBlock(grid, tw, facilities, playW, playH, bl.bx, bl.by);
            }
        }

        private List<(int bx, int by)> RandomBlockPath(
            (int bx, int by) fromB,
            (int bx, int by) toB,
            int bw,
            int bh,
            float randomness)
        {
            string StartKey = $"{fromB.bx},{fromB.by}";
            string GoalKey = $"{toB.bx},{toB.by}";
            if (StartKey == GoalKey)
                return new List<(int, int)> { fromB };

            var dist = new Dictionary<string, float> { [StartKey] = 0f };
            var prev = new Dictionary<string, string>();
            var open = new List<(string key, int bx, int by, float g, float f)>
            {
                (StartKey, fromB.bx, fromB.by, 0f,
                    Mathf.Abs(fromB.bx - toB.bx) + Mathf.Abs(fromB.by - toB.by))
            };

            int[][] dirs = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };

            while (open.Count > 0)
            {
                open.Sort((a, b) => a.f.CompareTo(b.f));
                var cur = open[0];
                open.RemoveAt(0);
                if (cur.key == GoalKey) break;
                if (dist.TryGetValue(cur.key, out var known) && cur.g > known + 1e-4f) continue;

                foreach (var d in dirs)
                {
                    int bx = cur.bx + d[0];
                    int by = cur.by + d[1];
                    if (bx < 0 || by < 0 || bx >= bw || by >= bh) continue;
                    string nk = $"{bx},{by}";
                    float step = 1f + (float)_rng.NextDouble() * randomness;
                    float nd = cur.g + step;
                    if (dist.TryGetValue(nk, out var old) && nd >= old) continue;
                    dist[nk] = nd;
                    prev[nk] = cur.key;
                    float h = Mathf.Abs(bx - toB.bx) + Mathf.Abs(by - toB.by);
                    open.Add((nk, bx, by, nd, nd + h + (float)_rng.NextDouble() * randomness));
                }
            }

            if (!prev.ContainsKey(GoalKey) && StartKey != GoalKey)
            {
                // 轴对齐折线兜底
                var fallback = new List<(int, int)> { fromB };
                int cx = fromB.bx, cy = fromB.by;
                while (cx != toB.bx)
                {
                    cx += cx < toB.bx ? 1 : -1;
                    fallback.Add((cx, cy));
                }
                while (cy != toB.by)
                {
                    cy += cy < toB.by ? 1 : -1;
                    fallback.Add((cx, cy));
                }
                return fallback;
            }

            var path = new List<(int, int)>();
            string cursor = GoalKey;
            while (cursor != null)
            {
                var parts = cursor.Split(',');
                path.Add((int.Parse(parts[0]), int.Parse(parts[1])));
                prev.TryGetValue(cursor, out cursor);
            }
            path.Reverse();
            return path;
        }

        private static void PaintBlock(
            short[] grid,
            int tw,
            List<PGCFacilityNode> facilities,
            int playW,
            int playH,
            int bx,
            int by)
        {
            var (ox, oy) = BlockOrigin(bx, by);
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int x = ox + dx, y = oy + dy;
                if (!OnPlayfield(x, y, playW, playH)) continue;
                if (OccupiedByFacility(x, y, facilities)) continue;
                int i = Idx(x, y, tw);
                if (grid[i] >= 10) continue;
                grid[i] = (short)PGCCell.Path;
            }
        }

        private static bool OccupiedByFacility(int x, int y, List<PGCFacilityNode> facilities)
        {
            foreach (var f in facilities)
                if (f.gridX == x && f.gridY == y) return true;
            return false;
        }

        private void DilatePaths(
            short[] grid,
            int tw,
            int playW,
            int playH,
            List<PGCFacilityNode> facilities,
            int kernel)
        {
            if (kernel <= 1) return;
            int r = kernel / 2;
            var source = (short[])grid.Clone();

            for (int y = 1; y <= playH; y++)
            for (int x = 1; x <= playW; x++)
            {
                int i = Idx(x, y, tw);
                if (source[i] != (short)PGCCell.Wall) continue;
                if (OccupiedByFacility(x, y, facilities)) continue;

                bool hit = false;
                for (int dy = -r; dy <= r && !hit; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (!OnPlayfield(nx, ny, playW, playH)) continue;
                    if (source[Idx(nx, ny, tw)] == (short)PGCCell.Path)
                    {
                        hit = true;
                        break;
                    }
                }

                if (hit) grid[i] = (short)PGCCell.Path;
            }
        }

        private int SpawnClearsFromPathWalls(
            short[] grid,
            int tw,
            int th,
            int playW,
            int playH,
            List<PGCFacilityNode> facilities)
        {
            var occupied = new HashSet<long>();
            foreach (var f in facilities) occupied.Add(Pack(f.gridX, f.gridY));

            var cells = new List<(int x, int y)>();
            int[][] dirs = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };

            for (int y = 1; y <= playH; y++)
            for (int x = 1; x <= playW; x++)
            {
                if (grid[Idx(x, y, tw)] != (short)PGCCell.Wall) continue;
                if (occupied.Contains(Pack(x, y))) continue;

                bool touches = false;
                foreach (var d in dirs)
                {
                    int nx = x + d[0], ny = y + d[1];
                    if (nx < 0 || ny < 0 || nx >= tw || ny >= th) continue;
                    if (grid[Idx(nx, ny, tw)] == (short)PGCCell.Path)
                    {
                        touches = true;
                        break;
                    }
                }

                if (touches) cells.Add((x, y));
            }

            int nextId = 0;
            foreach (var f in facilities)
                if (f.id >= nextId) nextId = f.id + 1;

            foreach (var c in cells)
            {
                facilities.Add(new PGCFacilityNode
                {
                    id = nextId++,
                    type = Kitchen.AI.FacilityType.AssemblyTable,
                    recipeIndex = -1,
                    label = "空台",
                    gridX = c.x,
                    gridY = c.y,
                });
                occupied.Add(Pack(c.x, c.y));
            }

            return cells.Count;
        }

        /// <summary>
        /// 所有仍为 Wall 的格子（含最外圈）→ 空柜墙占位。
        /// </summary>
        private int SpawnWallPlaceholders(
            short[] grid,
            int tw,
            int th,
            List<PGCFacilityNode> facilities)
        {
            var occupied = new HashSet<long>();
            foreach (var f in facilities)
                occupied.Add(Pack(f.gridX, f.gridY));

            int nextId = 0;
            foreach (var f in facilities)
                if (f.id >= nextId) nextId = f.id + 1;

            int made = 0;
            for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                if (grid[Idx(x, y, tw)] != (short)PGCCell.Wall) continue;
                if (occupied.Contains(Pack(x, y))) continue;

                facilities.Add(new PGCFacilityNode
                {
                    id = nextId++,
                    type = Kitchen.AI.FacilityType.AssemblyTable,
                    recipeIndex = -1,
                    label = "墙",
                    isWallPlaceholder = true,
                    gridX = x,
                    gridY = y,
                });
                occupied.Add(Pack(x, y));
                made++;
            }

            return made;
        }

        private List<Vector3> PickSpawnPoints(
            short[] grid,
            int tw,
            int playW,
            int playH,
            List<PGCFacilityNode> facilities,
            int count,
            Vector3 origin,
            float cell)
        {
            var pathCells = new List<(int x, int y)>();
            for (int y = 1; y <= playH; y++)
            for (int x = 1; x <= playW; x++)
            {
                if (grid[Idx(x, y, tw)] != (short)PGCCell.Path) continue;
                if (OccupiedByFacility(x, y, facilities)) continue;
                pathCells.Add((x, y));
            }

            Shuffle(pathCells);
            var result = new List<Vector3>();
            int n = Mathf.Min(count, pathCells.Count);
            for (int i = 0; i < n; i++)
                result.Add(GridToWorld(pathCells[i].x, pathCells[i].y, origin, cell));

            // 不够则在通路上重复偏移
            while (result.Count < count && pathCells.Count > 0)
            {
                var c = pathCells[result.Count % pathCells.Count];
                result.Add(GridToWorld(c.x, c.y, origin, cell) + new Vector3(0.1f * result.Count, 0, 0));
            }

            return result;
        }

        private void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;
    }
}
