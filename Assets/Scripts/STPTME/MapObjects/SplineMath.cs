using System.Collections.Generic;
using UnityEngine;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Curve evaluation and terrain-anchored arc-length sampling shared by every spline-based
    /// authoring tool (placement, removal, and any future one). Kept as a single utility
    /// deliberately: this system has repeatedly broken when two tools each had their own copy
    /// of "the same" math and quietly drifted apart. Waypoint-specific concerns (like the
    /// placement tool's gap-elimination snap) stay in their own tool, not here.
    /// </summary>
    public static class SplineMath
    {
        public struct ArcSample { public Vector3 point; public float cumulativeLength; }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                           + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>Evaluates the path at a global parameter t in [0, waypoints.Count-1], using
        /// Catmull-Rom with clamped end neighbors (straight line if only 2 waypoints).</summary>
        public static Vector3 EvaluatePath(List<Vector3> wp, float globalT)
        {
            int n = wp.Count;
            if (n < 2) return n == 1 ? wp[0] : Vector3.zero;

            globalT = Mathf.Clamp(globalT, 0f, n - 1);
            int seg = Mathf.Clamp(Mathf.FloorToInt(globalT), 0, n - 2);
            float t = globalT - seg;

            if (n == 2) return Vector3.Lerp(wp[0], wp[1], t);

            Vector3 p0 = wp[Mathf.Max(seg - 1, 0)];
            Vector3 p1 = wp[seg];
            Vector3 p2 = wp[seg + 1];
            Vector3 p3 = wp[Mathf.Min(seg + 2, n - 1)];
            return CatmullRom(p0, p1, p2, p3, t);
        }

        /// <summary>Snaps a raw evaluated path point onto the real terrain surface along its own
        /// radial direction — same principle as SnapToGround elsewhere in this tool set.</summary>
        public static Vector3 SnapToSurface(Vector3 rawPoint)
        {
            Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
            Vector3 dirFromCenter = rawPoint - sphereCenter;
            float dist = dirFromCenter.magnitude;
            if (dist < 0.01f) return rawPoint;
            dirFromCenter /= dist;

            float castStartRadius = TerrainManagementSettings.Instance.sphereRadius + 2000f;
            Vector3 castOrigin = sphereCenter + dirFromCenter * castStartRadius;

            // Terrain-only: a plain raycast here would happily land on a tree canopy or an
            // already-placed fence.
            var ray = new Ray(castOrigin, -dirFromCenter);
            if (AuthoringRaycast.TryRaycastTerrain(ray, castStartRadius + 2000f, out RaycastHit hit))
                return hit.point;
            return rawPoint; // no terrain under this sample — leave it at the raw curve position
        }

        /// <summary>Builds a fine-grained (point, cumulativeArcLength) table over the whole path,
        /// with each sample re-snapped to the real terrain surface.</summary>
        public static List<ArcSample> BuildArcLengthTable(List<Vector3> wp, int stepsPerSegment)
        {
            int n = wp.Count;
            int totalSteps = (n - 1) * stepsPerSegment;
            var table = new List<ArcSample>(totalSteps + 1);

            Vector3 prev = SnapToSurface(EvaluatePath(wp, 0f));
            table.Add(new ArcSample { point = prev, cumulativeLength = 0f });

            float cumulative = 0f;
            for (int i = 1; i <= totalSteps; i++)
            {
                float t = (float)i / stepsPerSegment;
                Vector3 p = SnapToSurface(EvaluatePath(wp, t));
                cumulative += Vector3.Distance(prev, p);
                table.Add(new ArcSample { point = p, cumulativeLength = cumulative });
                prev = p;
            }
            return table;
        }

        /// <summary>Finds the world point (and forward tangent) at a given arc-length distance
        /// along the path, via linear interpolation between the nearest table entries.</summary>
        public static Vector3 SampleAtArcLength(List<Vector3> wp, List<ArcSample> table, float targetLength, out Vector3 tangent)
        {
            targetLength = Mathf.Clamp(targetLength, 0f, table[table.Count - 1].cumulativeLength);

            int lo = 0, hi = table.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (table[mid].cumulativeLength < targetLength) lo = mid + 1; else hi = mid;
            }
            int idx = Mathf.Max(lo, 1);
            var a = table[idx - 1];
            var b = table[idx];
            float span = b.cumulativeLength - a.cumulativeLength;
            float frac = span > 1e-6f ? (targetLength - a.cumulativeLength) / span : 0f;

            tangent = (b.point - a.point).sqrMagnitude > 1e-8f ? (b.point - a.point).normalized : Vector3.forward;
            return Vector3.Lerp(a.point, b.point, frac);
        }

        /// <summary>
        /// Minimum distance from <paramref name="point"/> to the polyline described by
        /// <paramref name="table"/> — used by the removal tool to test whether a placed object
        /// falls within the corridor around a drawn line.
        /// </summary>
        public static float DistanceToPolyline(Vector3 point, List<ArcSample> table)
        {
            float best = float.MaxValue;
            for (int i = 1; i < table.Count; i++)
            {
                Vector3 a = table[i - 1].point;
                Vector3 b = table[i].point;
                Vector3 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / len2) : 0f;
                Vector3 closest = a + ab * t;
                float d = Vector3.Distance(point, closest);
                if (d < best) best = d;
            }
            return best;
        }
    }
}