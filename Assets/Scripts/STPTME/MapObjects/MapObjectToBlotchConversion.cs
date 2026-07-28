using UnityEngine;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Converts a placed map object (world position + world rotation) into a single-instance
    /// BlotchData, for prototypes that should be GPU-instanced. This is the one place in the
    /// codebase that computes an explicit yaw for a blotch — everywhere else (terrain trees)
    /// yaw is hash-derived on the GPU and this conversion is never involved.
    ///
    /// CRITICAL: the yaw extraction below MUST exactly reproduce the tangent/binormal frame
    /// ImpostorInstanced.shader builds per-instance, or a converted object's GPU-instanced
    /// LOD1+ orientation will not match its authored (LOD0 prefab) orientation. The shader's
    /// construction (both vertex passes, kept in lockstep) is:
    ///
    ///     dir      = normalize(worldPos - sphereCenter)
    ///     localUp  = abs(dir.y) &lt; 0.99 ? (0,1,0) : (1,0,0)
    ///     binormal = normalize(cross(localUp, dir))
    ///     tangent  = cross(dir, binormal)
    ///     rotation = (rotQ / 255) * 2π
    ///     // mesh local (0,0,1) [forward] maps to world direction:
    ///     //   binormal*cos(rotation) - tangent*sin(rotation)
    ///
    /// So given an object's actual world forward vector, projected onto the tangent-binormal
    /// plane as (f_tan, f_bin) components, the inverse is:
    ///     f_tan = -sin(rotation)  =>  sin(rotation) = -f_tan
    ///     f_bin =  cos(rotation)  =>  cos(rotation) =  f_bin
    ///     rotation = atan2(-f_tan, f_bin)
    /// </summary>
    public static class MapObjectToBlotchConversion
    {
        /// <summary>
        /// Reproduces the shader's per-instance tangent/binormal frame for a given world
        /// position. Must stay bit-for-bit consistent with ImpostorInstanced.shader's
        /// construction — if that shader's frame ever changes, this must change with it.
        /// </summary>
        public static void BuildTangentBinormalFrame(
            Vector3 worldPos, Vector3 sphereCenter,
            out Vector3 dir, out Vector3 tangent, out Vector3 binormal)
        {
            dir = (worldPos - sphereCenter).normalized;
            Vector3 localUp = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
            binormal = Vector3.Cross(localUp, dir).normalized;
            tangent = Vector3.Cross(dir, binormal);
        }

        /// <summary>
        /// Extracts the yaw (in degrees, matching BlotchData.QuantizeYaw's 0..360 convention)
        /// that reproduces this object's world-space forward direction under the shader's
        /// tangent/binormal frame. Returns 0 if the object's forward is degenerate (nearly
        /// parallel to the radial "up" — e.g. an object lying flat) rather than producing
        /// a meaningless angle from a near-zero-length projection.
        /// </summary>
        public static float ExtractYawDegrees(Vector3 worldPos, Quaternion worldRotation, Vector3 sphereCenter)
        {
            BuildTangentBinormalFrame(worldPos, sphereCenter, out Vector3 dir, out Vector3 tangent, out Vector3 binormal);

            Vector3 forward = worldRotation * Vector3.forward;
            Vector3 flatForward = Vector3.ProjectOnPlane(forward, dir);
            if (flatForward.sqrMagnitude < 1e-8f)
                return 0f;
            flatForward.Normalize();

            float fTan = Vector3.Dot(flatForward, tangent);
            float fBin = Vector3.Dot(flatForward, binormal);

            float radians = Mathf.Atan2(-fTan, fBin);
            float degrees = radians * Mathf.Rad2Deg;
            if (degrees < 0f) degrees += 360f;
            return degrees;
        }

        /// <summary>
        /// Converts one placed object into a single-instance BlotchData (radius=0, density=1
        /// — enforced here, not optional; map objects never carry cluster semantics, since
        /// there is no such thing as "a cluster of one placed fence"). No warning is needed
        /// for this forcing — it's the design, not a misconfiguration.
        /// </summary>
        public static BlotchData ConvertToBlotch(
            int chunkPacked, FaceId face, byte prototypeIndex, byte conflictCategory,
            Vector3 worldPos, Quaternion worldRotation, Vector3 sphereCenter,
            float localXMeters, float localZMeters, float chunkSizeMeters, uint seed)
        {
            float yawDegrees = ExtractYawDegrees(worldPos, worldRotation, sphereCenter);

            return new BlotchData(
                chunkPacked: chunkPacked,
                face: face,
                prototypeIndex: prototypeIndex,
                conflictCategory: conflictCategory,
                seed: seed,
                densityPerSqM: 1f,   // forced — single instance, always
                radiusMeters: 0f,    // forced — single instance, always
                localXMeters: localXMeters,
                localZMeters: localZMeters,
                chunkSizeMeters: chunkSizeMeters,
                explicitYawDegrees: yawDegrees);
        }
    }
}