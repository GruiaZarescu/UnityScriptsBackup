#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Round-trip validation for MapObjectCompactFormat, run BEFORE this format is ever wired
    /// into real file I/O. Given how many subtle rotation-frame mismatches this whole system
    /// has hit historically (tangent-plane approximations, wrong triangulation diagonals,
    /// stripped pitch on respawn), this checks the actual math against known-good inputs
    /// rather than trusting it by inspection alone.
    /// </summary>
    public static class MapObjectCompactFormatTests
    {
        /// <summary>
        /// Angle between two quaternions WITHOUT Unity's internal Quaternion.Angle snap-to-
        /// zero optimization. Unity's version checks (dot > 1 - epsilon) and returns exactly
        /// 0f for "close enough" pairs without computing acos at all — that epsilon corresponds
        /// to roughly a 0.16 degree dead zone, which silently swallowed this test's real
        /// (correct, expected) quantization error entirely. This computes the true angle so
        /// the reported numbers can actually be trusted.
        /// </summary>
        private static float RawQuaternionAngleDeg(Quaternion a, Quaternion b)
        {
            float dot = Mathf.Clamp(Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w), -1f, 1f);
            return 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
        }

        [MenuItem("STPTME/Debug/Test MapObjectCompactFormat Round-Trip")]
        public static void RunTest()
        {
            var settings = TerrainManagementSettings.Instance;
            if (settings == null) { Debug.LogError("[CompactFormatTest] TerrainManagementSettings not found."); return; }

            Vector3 sphereCenter = settings.sphereCenter;
            float sphereRadius = settings.sphereRadius;
            float chunkSize = settings.terrainSize / settings.tilingFactor;

            TestTerrainAnchored(sphereCenter, sphereRadius, chunkSize, 300);
            TestWorldFixed(300);
            TestRotationCompressionAlone(500);
        }

        private static void TestTerrainAnchored(Vector3 sphereCenter, float sphereRadius, float chunkSize, int count)
        {
            float maxPosError = 0f, maxScaleError = 0f, maxAngleErrorDeg = 0f;

            for (int i = 0; i < count; i++)
            {
                // Random point roughly on the sphere surface.
                Vector3 dir = Random.onUnitSphere;
                float height = Random.Range(-50f, 500f);
                Vector3 worldPos = sphereCenter + dir * (sphereRadius + height);

                // A genuinely "no-roll" orientation — built EXACTLY the way SplinePlacementTool
                // authors fence rotations (Cross(travelDir, up) fed as LookRotation's forward
                // parameter, so local +X becomes the travel/tilt-carrying axis), not a generic
                // Z-forward LookRotation. The original bug here was that this test built its
                // synthetic data the "generic" way, matching the (wrong) pack function's own
                // assumption — so pack/unpack round-tripped against EACH OTHER correctly while
                // neither matched real authored rotations. Testing against the real convention
                // is what actually would have caught it.
                Vector3 randomTravelDir = Vector3.ProjectOnPlane(Random.onUnitSphere, dir);
                if (randomTravelDir.sqrMagnitude < 0.01f) randomTravelDir = Vector3.ProjectOnPlane(Vector3.right, dir);
                randomTravelDir.Normalize();
                // Tilt the travel direction away from horizontal to simulate a real slope —
                // this is exactly what must survive the round-trip.
                Vector3 travelDir = Vector3.Slerp(randomTravelDir, dir, Random.Range(-0.3f, 0.3f)).normalized;
                Vector3 lookForwardParam = Vector3.Cross(travelDir, dir);
                Quaternion worldRot = Quaternion.LookRotation(lookForwardParam, dir);

                Vector3 scale = new Vector3(Random.Range(0.1f, 10f), Random.Range(0.1f, 10f), Random.Range(0.1f, 10f));

                var entry = new MapObjectDatabase.MapObjectEntry
                {
                    id = (ulong)(i + 1), prototypeIndex = i % 50,
                    worldPosition = worldPos, worldRotation = worldRot, localScale = scale,
                    anchorMode = MapObjectDatabase.AnchorMode.TerrainSurface
                };

                // Local X/Z are arbitrary here for a math-only test — real callers derive these
                // from actual chunk geometry (BlotchBaker's convention), which this test doesn't
                // need to reproduce to validate the pack/unpack round-trip itself.
                float localX = Random.Range(0f, chunkSize);
                float localZ = Random.Range(0f, chunkSize);

                var record = MapObjectCompactFormat.PackTerrainAnchored(entry, sphereCenter, localX, localZ, chunkSize);

                // Unpack needs instanceDir + terrainHeight as if a caller had already resolved
                // them from chunk context — for this test we just feed back the exact values
                // that produced worldPos, since we're validating orientation/scale math, not
                // the (separately, already-fixed) chunk-address reconstruction.
                var result = MapObjectCompactFormat.UnpackTerrainAnchored(record, dir, sphereCenter, sphereRadius, height);

                float posErr = Vector3.Distance(result.worldPosition, worldPos);
                float scaleErr = Vector3.Distance(result.localScale, scale);
                float angleErr = RawQuaternionAngleDeg(result.worldRotation, worldRot);

                maxPosError = Mathf.Max(maxPosError, posErr);
                maxScaleError = Mathf.Max(maxScaleError, scaleErr);
                maxAngleErrorDeg = Mathf.Max(maxAngleErrorDeg, angleErr);
            }

            bool pass = maxPosError < 0.01f && maxScaleError < 0.01f && maxAngleErrorDeg < 0.5f;
            string result2 = pass ? "PASS" : "FAIL";
            Debug.Log($"[CompactFormatTest] TerrainAnchored ({count} samples): {result2} — " +
                $"max pos error={maxPosError:F5}m, max scale error={maxScaleError:F5}, max angle error={maxAngleErrorDeg:F4}°");
        }

        private static void TestWorldFixed(int count)
        {
            float maxPosError = 0f, maxScaleError = 0f, maxAngleErrorDeg = 0f;

            for (int i = 0; i < count; i++)
            {
                var entry = new MapObjectDatabase.MapObjectEntry
                {
                    id = (ulong)(i + 1), prototypeIndex = i % 50,
                    worldPosition = Random.insideUnitSphere * 5000f,
                    worldRotation = Random.rotationUniform, // fully arbitrary — this path supports true 3D rotation
                    localScale = new Vector3(Random.Range(0.1f, 10f), Random.Range(0.1f, 10f), Random.Range(0.1f, 10f)),
                    anchorMode = MapObjectDatabase.AnchorMode.WorldFixed
                };

                var record = MapObjectCompactFormat.PackWorldFixed(entry);
                var result = MapObjectCompactFormat.UnpackWorldFixed(record);

                maxPosError = Mathf.Max(maxPosError, Vector3.Distance(result.worldPosition, entry.worldPosition));
                maxScaleError = Mathf.Max(maxScaleError, Vector3.Distance(result.localScale, entry.localScale));
                maxAngleErrorDeg = Mathf.Max(maxAngleErrorDeg, RawQuaternionAngleDeg(result.worldRotation, entry.worldRotation));
            }

            // World-fixed position is stored raw (float, no quantization) — should be exact
            // modulo float round-trip noise, not the quantization tolerance used elsewhere.
            bool pass = maxPosError < 0.0001f && maxScaleError < 0.01f && maxAngleErrorDeg < 0.5f;
            string result2 = pass ? "PASS" : "FAIL";
            Debug.Log($"[CompactFormatTest] WorldFixed ({count} samples): {result2} — " +
                $"max pos error={maxPosError:F6}m, max scale error={maxScaleError:F5}, max angle error={maxAngleErrorDeg:F4}°");
        }

        private static void TestRotationCompressionAlone(int count)
        {
            float maxAngleErrorDeg = 0f;
            for (int i = 0; i < count; i++)
            {
                Quaternion q = Random.rotationUniform;
                uint packed = MapObjectCompactFormat.PackRotation(q);
                Quaternion result = MapObjectCompactFormat.UnpackRotation(packed);

                if (i < 3) // print a few raw examples
                    Debug.Log($"orig=({q.x:F5},{q.y:F5},{q.z:F5},{q.w:F5}) " +
                            $"result=({result.x:F5},{result.y:F5},{result.z:F5},{result.w:F5}) packed={packed}");

                maxAngleErrorDeg = Mathf.Max(maxAngleErrorDeg, RawQuaternionAngleDeg(q, result));
            }
            Debug.Log($"[CompactFormatTest] Smallest-three compression alone ({count} samples): " +
                $"max angle error={maxAngleErrorDeg:F4}° (expected roughly ~0.1–0.3° at 10 bits/component)");
        }
    }
}
#endif