using UnityEngine;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Compact on-disk encoding for MapObjectDatabase entries. Two conventions:
    ///
    ///   TerrainSurface (20 bytes) — position is never stored; it's reconstructed at load time
    ///     from chunk context (same exact cube-projection ChunkManager.GetBlotchWorldPosition /
    ///     the GPU's ComputeExactInstanceDir already use) plus a packed in-chunk local UV.
    ///     Orientation is a heading + tilt pair, NOT a full quaternion — "up" is always the
    ///     sphere's radial direction for this convention, matching how every terrain-anchored
    ///     object this system spawns is already built (no independent roll is ever used).
    ///     Chunk/face are never stored either — implied entirely by which bucket a record is
    ///     read from, same as the existing CellObjectGroup format already does.
    ///
    ///   WorldFixed (28 bytes) — full raw position + a smallest-three-compressed quaternion,
    ///     for content that must never move regardless of terrain or chunk changes.
    ///
    /// Purely a SERIALIZATION concern. MapObjectDatabase's in-memory representation (full
    /// Vector3/Quaternion/ulong/int) is completely untouched by this — every existing tool
    /// (drag-to-move, spline placement/removal math, mass override) keeps working exactly as
    /// it does today. Pack/unpack only happen at the bake/load boundary.
    /// </summary>
    public static class MapObjectCompactFormat
    {
        // ── Terrain-anchored record: 20 bytes ──────────────────────────────────
        //   uint   id                   4
        //   ushort prototypeIndex       2
        //   uint   packedLocalPos       4   (BlotchData.PackLocalPos convention — reused, not
        //                                    reinvented, so both systems stay bit-comparable)
        //   uint   packedHeadingTilt    4   (heading: 16-bit / 0..360°, tilt: 16-bit / ±90°)
        //   ushort scaleX, scaleY, scaleZ  6
        public struct TerrainAnchoredRecord
        {
            public uint id;
            public ushort prototypeIndex;
            public uint packedLocalPos;
            public uint packedHeadingTilt;
            public ushort scaleX, scaleY, scaleZ;
        }

        // ── World-fixed record: 28 bytes ───────────────────────────────────────
        public struct WorldFixedRecord
        {
            public uint id;
            public ushort prototypeIndex;
            public float posX, posY, posZ;
            public uint packedRotation;
            public ushort scaleX, scaleY, scaleZ;
        }

        private const float SCALE_MIN = 0.1f;
        private const float SCALE_MAX = 10f;

        // ═══════════════════════════ Scale quantization (shared) ═══════════════════════════

        public static ushort QuantizeScale(float s)
        {
            float t = Mathf.InverseLerp(SCALE_MIN, SCALE_MAX, Mathf.Clamp(s, SCALE_MIN, SCALE_MAX));
            return (ushort)Mathf.RoundToInt(t * 65535f);
        }

        public static float DequantizeScale(ushort q) => Mathf.Lerp(SCALE_MIN, SCALE_MAX, q / 65535f);

        // ═══════════════════════════ Angle quantization (shared) ═══════════════════════════

        /// <summary>Full-turn angle, 0..360°, uniform 16-bit steps (~0.0055°/step).</summary>
        public static ushort QuantizeAngle360(float degrees)
        {
            float wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            return (ushort)Mathf.RoundToInt(wrapped / 360f * 65535f);
        }

        public static float DequantizeAngle360(ushort q) => q / 65535f * 360f;

        /// <summary>Signed angle, -90°..+90°, uniform 16-bit steps (~0.00275°/step) — used for
        /// tilt specifically, since its real range is half of heading's and a dedicated
        /// quantizer gets roughly double the precision for the same 2 bytes instead of wasting
        /// half the range on angles tilt can never actually reach.</summary>
        public static ushort QuantizeAngleSigned90(float degrees)
        {
            float clamped = Mathf.Clamp(degrees, -90f, 90f);
            float t = (clamped + 90f) / 180f;
            return (ushort)Mathf.RoundToInt(t * 65535f);
        }

        public static float DequantizeAngleSigned90(ushort q) => (q / 65535f) * 180f - 90f;

        // ═══════════════════════════ Local-position pack/unpack ═══════════════════════════
        // Thin wrapper matching BlotchData.PackLocalPos/GetLocalPosition exactly, since this
        // struct stores the packed value as a raw uint rather than wrapping a full BlotchData.

        public static void UnpackLocalPos(uint packedPos, float chunkSizeMeters, out float localX, out float localZ)
        {
            const float maxVal = 65535f;
            localX = (packedPos & 0xFFFF) / maxVal * chunkSizeMeters;
            localZ = ((packedPos >> 16) & 0xFFFF) / maxVal * chunkSizeMeters;
        }

        // ═══════════════════════════ Terrain-anchored pack/unpack ═══════════════════════════

        /// <summary>
        /// Packs a fully-resolved entry into the compact terrain-anchored record. Caller
        /// supplies the entry's chunk-local X/Z in meters (identical convention BlotchBaker
        /// already uses) — this function owns only the NEW part, orientation/scale packing.
        /// </summary>
        public static TerrainAnchoredRecord PackTerrainAnchored(
            MapObjectDatabase.MapObjectEntry entry,
            Vector3 sphereCenter,
            float localXMeters, float localZMeters, float chunkSizeMeters)
        {
            if (entry.id > uint.MaxValue)
                Debug.LogWarning($"[MapObjectCompactFormat] id {entry.id} exceeds uint range — will be truncated.");
            if (entry.prototypeIndex < 0 || entry.prototypeIndex > ushort.MaxValue)
                Debug.LogWarning($"[MapObjectCompactFormat] prototypeIndex {entry.prototypeIndex} out of ushort range — will be truncated.");

            Vector3 up = (entry.worldPosition - sphereCenter).normalized;
            MapObjectToBlotchConversion.BuildTangentBinormalFrame(entry.worldPosition, sphereCenter,
                out _, out Vector3 tangent, out Vector3 binormal);

            // Local +X, NOT +Z. SplinePlacementTool (and the fence connector convention it
            // implements) builds rotations as LookRotation(Cross(travelDir, up), up) — which
            // makes local +Z structurally, exactly perpendicular to `up` by the definition of
            // a cross product, for EVERY such rotation, regardless of actual slope. Extracting
            // tilt from local +Z therefore always measured a value that's guaranteed to be
            // zero — not approximately, exactly, every time — which is why fences never tilted
            // when loaded from a bake. Local +X is the real travel/connector axis and is what
            // actually deviates from horizontal on a slope.
            Vector3 forward = (entry.worldRotation * Vector3.right).normalized;

            // Heading: direction of forward's flattened (tangent-plane) component.
            Vector3 flatForward = Vector3.ProjectOnPlane(forward, up);
            float heading;
            if (flatForward.sqrMagnitude < 1e-8f)
            {
                heading = 0f; // forward ~parallel to up — heading is genuinely undefined here
            }
            else
            {
                flatForward.Normalize();
                float fTan = Vector3.Dot(flatForward, tangent);
                float fBin = Vector3.Dot(flatForward, binormal);
                heading = Mathf.Atan2(fBin, fTan) * Mathf.Rad2Deg;
                if (heading < 0f) heading += 360f;
            }

            // Tilt: how far forward points above/below the local horizontal plane.
            float tilt = Mathf.Asin(Mathf.Clamp(Vector3.Dot(forward, up), -1f, 1f)) * Mathf.Rad2Deg;

            uint packedLocalPos = BlotchData.PackLocalPos(localXMeters, localZMeters, chunkSizeMeters);
            uint headingQ = QuantizeAngle360(heading);
            uint tiltQ = QuantizeAngleSigned90(tilt);
            uint packedHeadingTilt = headingQ | (tiltQ << 16);

            return new TerrainAnchoredRecord
            {
                id = (uint)entry.id,
                prototypeIndex = (ushort)entry.prototypeIndex,
                packedLocalPos = packedLocalPos,
                packedHeadingTilt = packedHeadingTilt,
                scaleX = QuantizeScale(entry.localScale.x),
                scaleY = QuantizeScale(entry.localScale.y),
                scaleZ = QuantizeScale(entry.localScale.z),
            };
        }

        /// <summary>
        /// Reconstructs a full entry from a terrain-anchored record. The caller resolves
        /// <paramref name="instanceDir"/> and <paramref name="terrainHeight"/> beforehand —
        /// that's the exact same chunk/cell-address + height-sampling math already correctly
        /// implemented in ChunkManager.GetBlotchWorldPosition, deliberately not duplicated a
        /// third time here. This function owns only turning heading/tilt/scale back into a
        /// Vector3/Quaternion once position is already known.
        /// </summary>
        public static MapObjectDatabase.MapObjectEntry UnpackTerrainAnchored(
            TerrainAnchoredRecord record,
            Vector3 instanceDir, Vector3 sphereCenter, float sphereRadius, float terrainHeight)
        {
            Vector3 worldPosition = sphereCenter + instanceDir * (sphereRadius + terrainHeight);
            Vector3 up = instanceDir;

            MapObjectToBlotchConversion.BuildTangentBinormalFrame(worldPosition, sphereCenter,
                out _, out Vector3 tangent, out Vector3 binormal);

            ushort headingQ = (ushort)(record.packedHeadingTilt & 0xFFFF);
            ushort tiltQ = (ushort)((record.packedHeadingTilt >> 16) & 0xFFFF);
            float heading = DequantizeAngle360(headingQ);
            float tilt = DequantizeAngleSigned90(tiltQ);

            float headingRad = heading * Mathf.Deg2Rad;
            Vector3 flatForward = tangent * Mathf.Cos(headingRad) + binormal * Mathf.Sin(headingRad);

            float tiltRad = tilt * Mathf.Deg2Rad;
            // This reconstructs the object's local +X (the travel/connector axis) — NOT +Z.
            Vector3 reconstructedX = (flatForward * Mathf.Cos(tiltRad) + up * Mathf.Sin(tiltRad)).normalized;

            // LookRotation only accepts a local-Z parameter directly. Mirrors
            // SplinePlacementTool's own construction (lookForward = Cross(travelDir, up))
            // exactly, so a rotation built by that tool round-trips back to itself: feeding
            // Cross(reconstructedX, up) as LookRotation's forward parameter makes the
            // RESULTING local +X equal reconstructedX (LookRotation derives local X as
            // Cross(up, forward) = Cross(up, Cross(X, up)) = X, by the vector triple product
            // identity, whenever X is perpendicular to up — which it always is here).
            Vector3 lookForwardParam = Vector3.Cross(reconstructedX, up);

            return new MapObjectDatabase.MapObjectEntry
            {
                id = record.id,
                prototypeIndex = record.prototypeIndex,
                worldPosition = worldPosition,
                worldRotation = Quaternion.LookRotation(lookForwardParam, up),
                localScale = new Vector3(
                    DequantizeScale(record.scaleX), DequantizeScale(record.scaleY), DequantizeScale(record.scaleZ)),
                anchorMode = MapObjectDatabase.AnchorMode.TerrainSurface,
            };
        }

        // ═══════════════════════════ World-fixed pack/unpack ═══════════════════════════

        /// <summary>
        /// "Smallest three" quaternion compression: store the 3 smallest-magnitude components
        /// at 10 bits each plus which component was dropped (2 bits), reconstruct the dropped
        /// one from the unit-length constraint. Roughly 0.06% max per-component error —
        /// visually lossless for anything this system places. Full quaternion in 4 bytes
        /// instead of 16.
        /// </summary>
        public static uint PackRotation(Quaternion q)
        {
            q.Normalize();
            float[] c = { q.x, q.y, q.z, q.w };

            int largest = 0;
            float largestAbs = Mathf.Abs(c[0]);
            for (int i = 1; i < 4; i++)
            {
                float a = Mathf.Abs(c[i]);
                if (a > largestAbs) { largestAbs = a; largest = i; }
            }

            // q and -q represent the same rotation — negating so the dropped (largest)
            // component is always positive means we never need to store its sign.
            if (c[largest] < 0f)
                for (int i = 0; i < 4; i++) c[i] = -c[i];

            const float RANGE = 0.70710678f; // 1/sqrt(2) — max magnitude any non-largest component can have
            uint packed = (uint)largest; // bits 0-1

            int bitOffset = 2;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                float normalized = (c[i] / RANGE + 1f) * 0.5f; // -> 0..1
                uint quant = (uint)Mathf.Clamp(Mathf.RoundToInt(normalized * 1023f), 0, 1023); // 10 bits
                packed |= quant << bitOffset;
                bitOffset += 10;
            }
            return packed;
        }

        public static Quaternion UnpackRotation(uint packed)
        {
            int largest = (int)(packed & 0x3);
            const float RANGE = 0.70710678f;

            float[] c = new float[4];
            float sumSq = 0f;
            int bitOffset = 2;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                uint quant = (packed >> bitOffset) & 0x3FF;
                bitOffset += 10;
                float normalized = quant / 1023f;
                float value = (normalized * 2f - 1f) * RANGE;
                c[i] = value;
                sumSq += value * value;
            }
            c[largest] = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSq));

            return new Quaternion(c[0], c[1], c[2], c[3]);
        }

        public static WorldFixedRecord PackWorldFixed(MapObjectDatabase.MapObjectEntry entry)
        {
            if (entry.id > uint.MaxValue)
                Debug.LogWarning($"[MapObjectCompactFormat] id {entry.id} exceeds uint range — will be truncated.");
            if (entry.prototypeIndex < 0 || entry.prototypeIndex > ushort.MaxValue)
                Debug.LogWarning($"[MapObjectCompactFormat] prototypeIndex {entry.prototypeIndex} out of ushort range — will be truncated.");

            return new WorldFixedRecord
            {
                id = (uint)entry.id,
                prototypeIndex = (ushort)entry.prototypeIndex,
                posX = entry.worldPosition.x, posY = entry.worldPosition.y, posZ = entry.worldPosition.z,
                packedRotation = PackRotation(entry.worldRotation),
                scaleX = QuantizeScale(entry.localScale.x),
                scaleY = QuantizeScale(entry.localScale.y),
                scaleZ = QuantizeScale(entry.localScale.z),
            };
        }

        public static MapObjectDatabase.MapObjectEntry UnpackWorldFixed(WorldFixedRecord record)
        {
            return new MapObjectDatabase.MapObjectEntry
            {
                id = record.id,
                prototypeIndex = record.prototypeIndex,
                worldPosition = new Vector3(record.posX, record.posY, record.posZ),
                worldRotation = UnpackRotation(record.packedRotation),
                localScale = new Vector3(
                    DequantizeScale(record.scaleX), DequantizeScale(record.scaleY), DequantizeScale(record.scaleZ)),
                anchorMode = MapObjectDatabase.AnchorMode.WorldFixed,
            };
        }
    }
}