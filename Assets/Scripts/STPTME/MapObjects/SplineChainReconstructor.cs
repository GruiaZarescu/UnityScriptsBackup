using System.Collections.Generic;
using UnityEngine;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Reconstructs continuous "spline runs" from already-placed map objects, using nothing but
    /// data that already exists: each object's transform plus its prototype's connector offsets.
    ///
    /// This works because consecutive spline-placed objects were positioned so one's END
    /// connector lands EXACTLY on the next one's START connector (see SplinePlacementTool's
    /// chained placement) — so chain membership is directly recoverable rather than guessed at.
    /// The practical consequence is that runs placed BEFORE any of this existed are just as
    /// editable as new ones, and no additional data ever needs serializing.
    ///
    /// Uses a spatial hash rather than pairwise comparison: O(n) to build, O(1) per lookup,
    /// so this stays fast at tens of thousands of objects instead of degenerating into O(n²).
    /// </summary>
    public static class SplineChainReconstructor
    {
        /// <summary>One reconstructed run: ordered object ids plus the connector points between
        /// them. connectorPoints has exactly (objectIds.Count + 1) entries — the start of the
        /// first object, every shared joint, and the end of the last.</summary>
        public class Chain
        {
            public List<ulong> objectIds = new List<ulong>();
            public List<Vector3> connectorPoints = new List<Vector3>();
            /// <summary>Prototype of the first object in the run — used to make an extending
            /// run inherit the material of whatever it connects to.</summary>
            public int prototypeIndex = -1;

            public Vector3 StartPoint => connectorPoints[0];
            public Vector3 EndPoint => connectorPoints[connectorPoints.Count - 1];
        }

        private struct Endpoints
        {
            public ulong id;
            public Vector3 start, end;
            public int prototypeIndex;
        }

        /// <summary>
        /// Builds every chain in the database. Objects whose prototype lacks connectors
        /// (hasConnectors == false) are ignored entirely — they were never spline-placed and
        /// have no meaningful chain membership.
        /// </summary>
        /// <param name="cellSize">Spatial hash cell size. Also the join tolerance: two
        /// connectors within this distance are treated as the same joint. Connectors are placed
        /// to coincide exactly and only drift by float error, so this can be small — but too
        /// tight breaks visually-continuous chains, too loose merges lines that merely pass
        /// near each other.</param>
        /// <param name="breakOnPrototypeChange">When true, a chain ends where the prototype
        /// changes (e.g. wood meeting stone). Default false: a run that transitions materials
        /// is still one continuous fence line for select/delete purposes.</param>
        public static List<Chain> BuildChains(
            MapObjectDatabase database,
            MapObjectPrototypeRegistry registry,
            float cellSize = 0.25f,
            bool breakOnPrototypeChange = false)
        {
            var chains = new List<Chain>();
            if (database == null || registry == null) return chains;

            // ── Collect endpoints for every connector-bearing object ──
            var objects = new Dictionary<ulong, Endpoints>();
            foreach (var entry in database.All)
            {
                if (entry.prototypeIndex < 0 || entry.prototypeIndex >= registry.entries.Length) continue;
                var proto = registry.entries[entry.prototypeIndex];
                if (proto == null || !proto.hasConnectors) continue;

                objects[entry.id] = new Endpoints
                {
                    id = entry.id,
                    start = entry.worldPosition + entry.worldRotation * proto.connectorStartLocal,
                    end = entry.worldPosition + entry.worldRotation * proto.connectorEndLocal,
                    prototypeIndex = entry.prototypeIndex,
                };
            }
            if (objects.Count == 0) return chains;

            // ── Spatial hash of every endpoint ──
            var hash = new Dictionary<Vector3Int, List<(ulong id, bool isStart)>>();
            foreach (var kv in objects)
            {
                AddToHash(hash, kv.Value.start, kv.Key, true, cellSize);
                AddToHash(hash, kv.Value.end, kv.Key, false, cellSize);
            }

            // ── Walk chains ──
            var visited = new HashSet<ulong>();
            foreach (var kv in objects)
            {
                if (visited.Contains(kv.Key)) continue;

                var ordered = new List<ulong> { kv.Key };
                visited.Add(kv.Key);

                // Walk forward: this object's END meets the next object's START.
                ulong current = kv.Key;
                while (true)
                {
                    var cur = objects[current];
                    if (!TryFindConnected(hash, objects, cur.end, current, wantStart: true,
                            cellSize, visited, cur.prototypeIndex, breakOnPrototypeChange, out ulong next))
                        break;
                    ordered.Add(next);
                    visited.Add(next);
                    current = next;
                }

                // Walk backward: this object's START meets the previous object's END.
                current = kv.Key;
                while (true)
                {
                    var cur = objects[current];
                    if (!TryFindConnected(hash, objects, cur.start, current, wantStart: false,
                            cellSize, visited, cur.prototypeIndex, breakOnPrototypeChange, out ulong prev))
                        break;
                    ordered.Insert(0, prev);
                    visited.Add(prev);
                    current = prev;
                }

                var chain = new Chain { objectIds = ordered, prototypeIndex = objects[ordered[0]].prototypeIndex };
                chain.connectorPoints.Add(objects[ordered[0]].start);
                foreach (ulong id in ordered)
                    chain.connectorPoints.Add(objects[id].end);
                chains.Add(chain);
            }

            return chains;
        }

        private static void AddToHash(Dictionary<Vector3Int, List<(ulong, bool)>> hash,
            Vector3 point, ulong id, bool isStart, float cellSize)
        {
            Vector3Int cell = ToCell(point, cellSize);
            if (!hash.TryGetValue(cell, out var list))
            {
                list = new List<(ulong, bool)>();
                hash[cell] = list;
            }
            list.Add((id, isStart));
        }

        private static Vector3Int ToCell(Vector3 p, float cellSize) => new Vector3Int(
            Mathf.FloorToInt(p.x / cellSize),
            Mathf.FloorToInt(p.y / cellSize),
            Mathf.FloorToInt(p.z / cellSize));

        /// <summary>Searches the 27-cell neighbourhood around <paramref name="point"/> for an
        /// unvisited object whose start (or end) connector coincides with it.</summary>
        private static bool TryFindConnected(
            Dictionary<Vector3Int, List<(ulong id, bool isStart)>> hash,
            Dictionary<ulong, Endpoints> objects,
            Vector3 point, ulong excludeId, bool wantStart, float cellSize,
            HashSet<ulong> visited, int currentPrototype, bool breakOnPrototypeChange,
            out ulong found)
        {
            found = 0;
            Vector3Int centre = ToCell(point, cellSize);
            float tolSq = cellSize * cellSize;

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var cell = new Vector3Int(centre.x + dx, centre.y + dy, centre.z + dz);
                if (!hash.TryGetValue(cell, out var candidates)) continue;

                foreach (var (id, isStart) in candidates)
                {
                    if (id == excludeId || isStart != wantStart || visited.Contains(id)) continue;

                    var other = objects[id];
                    if (breakOnPrototypeChange && other.prototypeIndex != currentPrototype) continue;

                    Vector3 otherPoint = wantStart ? other.start : other.end;
                    if ((otherPoint - point).sqrMagnitude <= tolSq)
                    {
                        found = id;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Finds the chain and the exact point on it nearest to <paramref name="worldPoint"/>.
        /// This is what makes "click anywhere on an existing fence line and connect to it" work
        /// even when the click lands mid-segment rather than on a joint: the returned point is
        /// projected onto the actual polyline, so a new spline can start precisely there.
        /// </summary>
        public static bool TryFindNearestPointOnChains(
            List<Chain> chains, Vector3 worldPoint, float maxDistance,
            out Chain nearestChain, out Vector3 nearestPoint, out int segmentIndex)
        {
            nearestChain = null;
            nearestPoint = Vector3.zero;
            segmentIndex = -1;

            float bestSq = maxDistance * maxDistance;
            foreach (var chain in chains)
            {
                for (int i = 1; i < chain.connectorPoints.Count; i++)
                {
                    Vector3 a = chain.connectorPoints[i - 1];
                    Vector3 b = chain.connectorPoints[i];
                    Vector3 ab = b - a;
                    float len2 = ab.sqrMagnitude;
                    float t = len2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(worldPoint - a, ab) / len2) : 0f;
                    Vector3 closest = a + ab * t;

                    float dSq = (closest - worldPoint).sqrMagnitude;
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        nearestChain = chain;
                        nearestPoint = closest;
                        segmentIndex = i - 1;
                    }
                }
            }
            return nearestChain != null;
        }
    }
}