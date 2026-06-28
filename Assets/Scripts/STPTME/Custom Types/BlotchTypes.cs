using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CustomTypes
{
    // =========================================================================
    // BLOTCH DATA — compact 16-byte instruction for procedural instance generation.
    //
    // A "blotch" describes a group of instances clustered around a center point.
    // The GPU expands each blotch into individual instances at runtime using a
    // deterministic hash, so we never store per-instance data on disk or in VRAM.
    //
    // Blotches are authored by placing Unity terrain trees. The prototype entry
    // (in MapObjectPrototypeRegistry or legacy TreePrototypeRegistry) defines
    // the blotch parameters (radius, density, conflictCategory). MeshSaver serializes
    // each such tree as a BlotchData instead of an STPTMETreeInstance.
    //
    // Layout (16 bytes, GPU-aligned):
    //   chunkPacked     4B  — (mapX<<24)|(mapY<<16)|(chunkX<<8)|chunkY  [matches STPTMEUtils]
    //   packedMeta      4B  — face|prototypeIndex|conflictCategory|flags
    //   seedAndDensity  4B  — seed|densityQuantized|radiusQuantized
    //   packedPos       4B  — localX|localZ (quantized position within chunk)
    // =========================================================================

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BlotchData
    {
        // (mapX<<24)|(mapY<<16)|(chunkX<<8)|chunkY — identical to STPTMEUtils.WriteFourSBytesInInt
        // Identifies which chunk this blotch belongs to. The GPU uses this to filter
        // blotches during the per-chunk expansion pass.
        public int chunkPacked;

        // Bit layout:
        //   bits  0-7:  face            (FaceId: 0=Up, 1=Down, 2=Left, 3=Right, 4=Forward, 5=Back)
        //   bits  8-15: prototypeIndex  (index into TreePrototypeRegistry.prototypes)
        //   bits 16-23: conflictCategory (see ConflictCategory constants below)
        //   bits 24-31: flags           (bit 24: cullLODOverride; bit 25: instanceAlways; bits 26-31: reserved)
        public uint packedMeta;

        // Bit layout:
        //   bits  0-15: seed            (deterministic seed for instance placement hash)
        //   bits 16-23: densityQuantized(0-255 → 0..127.5 instances/m² via *0.5)
        //   bits 24-31: radiusQuantized (0-255 → 0..63.75 meters via *0.25)
        public uint seedAndDensity;

        // Bit layout:
        //   bits  0-15: localXQuantized (0-65535 → 0..chunkSize meters, ~1.1mm precision at 75m)
        //   bits 16-31: localZQuantized (0-65535 → 0..chunkSize meters)
        // Position of the blotch center relative to the chunk's plane-space origin.
        // The GPU converts this to a world position via FaceIdUtility.ProjectFacePlanePoint.
        public uint packedPos;

        // ===== Pack/Unpack helpers =====

        public BlotchData(
            int chunkPacked,
            FaceId face,
            byte prototypeIndex,
            byte conflictCategory,
            uint seed,
            float densityPerSqM,
            float radiusMeters,
            float localXMeters,
            float localZMeters,
            float chunkSizeMeters)
        {
            this.chunkPacked = chunkPacked;

            uint f = (uint)face & 0xFF;
            uint p = prototypeIndex;
            uint c = conflictCategory;
            this.packedMeta = f | (p << 8) | (c << 16);

            uint s = seed & 0xFFFF;
            uint d = QuantizeDensity(densityPerSqM);
            uint r = QuantizeRadius(radiusMeters);
            this.seedAndDensity = s | (d << 16) | (r << 24);

            this.packedPos = PackLocalPos(localXMeters, localZMeters, chunkSizeMeters);
        }

        public FaceId Face => (FaceId)(packedMeta & 0xFF);
        public byte PrototypeIndex => (byte)((packedMeta >> 8) & 0xFF);
        public byte ConflictCategory => (byte)((packedMeta >> 16) & 0xFF);

        public uint Seed => seedAndDensity & 0xFFFF;
        public float DensityPerSqM => ((seedAndDensity >> 16) & 0xFF) * 0.5f;
        public float RadiusMeters => ((seedAndDensity >> 24) & 0xFF) * 0.25f;

        public void GetLocalPosition(float chunkSizeMeters, out float localX, out float localZ)
        {
            float maxVal = 65535f;
            localX = (packedPos & 0xFFFF) / maxVal * chunkSizeMeters;
            localZ = ((packedPos >> 16) & 0xFFFF) / maxVal * chunkSizeMeters;
        }

        // ===== Static quantization helpers =====

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint QuantizeDensity(float densityPerSqM)
        {
            // 0..127.5 instances/m² → 0..255
            float clamped = Mathf.Clamp(densityPerSqM, 0f, 127.5f);
            return (uint)Mathf.RoundToInt(clamped / 0.5f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint QuantizeRadius(float radiusMeters)
        {
            // 0..63.75 meters → 0..255
            float clamped = Mathf.Clamp(radiusMeters, 0f, 63.75f);
            return (uint)Mathf.RoundToInt(clamped / 0.25f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PackLocalPos(float localXMeters, float localZMeters, float chunkSizeMeters)
        {
            float maxVal = 65535f;
            uint xQ = (uint)Mathf.Clamp(Mathf.RoundToInt(localXMeters / chunkSizeMeters * maxVal), 0, 65535);
            uint zQ = (uint)Mathf.Clamp(Mathf.RoundToInt(localZMeters / chunkSizeMeters * maxVal), 0, 65535);
            return xQ | (zQ << 16);
        }

        //Consider looser packing on the GPU if we find that
        //the total vram used by all blotches is small, but the GPU compute for bit ops is more significant.
    }

    // =========================================================================
    // INSTANCE DATA — output of the GPU blotch expansion pass.
    //
    // One per solved instance that won a conflict-grid cell. Written by the
    // CSExpandBlotches kernel into a StructuredBuffer, then consumed by
    // DrawMeshInstancedIndirect via the vertex shader.
    //
    // Layout (20 bytes):
    //   worldPosition  12B  — float3 world position on sphere surface
    //   packedMeta      4B  — prototypeIndex|chunkLOD|rotationQ|scaleQ
    //   seed            4B  — per-instance seed for wind/variation
    // =========================================================================

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InstanceData
    {
        public float worldX;
        public float worldY;
        public float worldZ;
        public float padding; // 4-byte padding for alignment (unused for now, flaot3 is 16 bytes on GPU)

        // Bit layout:
        //   bits  0-7:  prototypeIndex
        //   bits  8-15: chunkLOD (for width compensation in vertex shader)
        //   bits 16-23: rotationQuantized (0-255 → 0..360°)
        //   bits 24-31: scaleQuantized    (0-255 → 0.5..2.0 scale)
        public uint packedMeta;

        // Per-instance seed for deterministic wind phase / color variation.
        public uint seed;
        public uint pad2;
        public uint pad3; 

        // ===== Helpers =====

        public InstanceData(Vector3 worldPos, byte prototypeIndex, byte chunkLOD, float rotationDeg, float scale, uint seed, float padding = 0f)
        {
            worldX = worldPos.x;
            worldY = worldPos.y;
            worldZ = worldPos.z;
            this.padding = padding;

            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(rotationDeg / 360f * 255f), 0, 255);
            uint s = (uint)Mathf.Clamp(Mathf.RoundToInt(Mathf.InverseLerp(0.5f, 2.0f, scale) * 255f), 0, 255);
            packedMeta = prototypeIndex | ((uint)chunkLOD << 8) | (r << 16) | (s << 24);

            this.seed = seed;
            this.pad2 = 0;
            this.pad3 = 0;
        }

        public Vector3 WorldPosition => new Vector3(worldX, worldY, worldZ);
        public byte PrototypeIndex => (byte)(packedMeta & 0xFF);
        public byte ChunkLOD => (byte)((packedMeta >> 8) & 0xFF);
        public float RotationDeg => ((packedMeta >> 16) & 0xFF) / 255f * 360f;
        public float Scale => Mathf.Lerp(0.5f, 2.0f, ((packedMeta >> 24) & 0xFF) / 255f);
    }

    // =========================================================================
    // CONFLICT CATEGORY CONSTANTS
    //
    // Cell values are a bitmask of categories present in that cell.
    // Multiple categories can coexist (e.g. grass + canopy = 0b011 = 3).
    //
    // The "forbidden mask" for a category is the set of bits that, if already
    // present in the cell, prevent this category from spawning.
    //
    // Rules (from design conversation):
    //   - Grass can spawn on: empty, canopy.     Cannot spawn on: grass, trunk, offlimits.
    //   - Canopy can spawn on: empty, grass.     Cannot spawn on: canopy, trunk, offlimits.
    //   - Trunk can spawn on: empty only.        Cannot spawn on: anything.
    //   - Offlimits: pre-baked from splatmap, blocks everything.
    //
    // Claim order (deterministic): Trunk → Canopy → Grass
    // =========================================================================

    public static class ConflictCategory
    {
        // Cell value bits (what gets OR'd into the grid when an instance spawns)
        public const byte Empty     = 0b000;  // 0 — no instances
        public const byte Grass     = 0b001;  // 1 — grass present
        public const byte Canopy    = 0b010;  // 2 — tree canopy present
        public const byte Trunk     = 0b100;  // 4 — tree trunk present (highest priority)
        public const byte OffLimits = 0b111;  // 7 — road/building/water (pre-baked from splatmap)

        // Forbidden masks: (grid[cell] & myForbiddenMask) != 0  →  cannot spawn
        // Grass is blocked by: grass (001) or trunk (100) → mask = 101
        public const byte GrassForbiddenMask     = Grass | Trunk;        // 0b101 = 5
        // Canopy is blocked by: canopy (010) or trunk (100) → mask = 110
        public const byte CanopyForbiddenMask    = Canopy | Trunk;       // 0b110 = 6
        // Trunk is blocked by: anything non-empty → mask = 111
        public const byte TrunkForbiddenMask     = Grass | Canopy | Trunk; // 0b111 = 7

        /// <summary>
        /// Returns the forbidden mask for the given category.
        /// Used by the GPU to check if an instance can spawn in a cell.
        /// </summary>
        public static byte GetForbiddenMask(byte category)
        {
            switch (category)
            {
                case Grass:  return GrassForbiddenMask;
                case Canopy: return CanopyForbiddenMask;
                case Trunk:  return TrunkForbiddenMask;
                default:     return 0b111; // Unknown category: block everything (safe default)
            }
        }

        /// <summary>
        /// Returns the solve priority for the given category. Lower = solved first.
        /// Trunks are solved before canopies, canopies before grass, so that
        /// higher-priority instances claim their cells before lower-priority ones try.
        /// </summary>
        public static int GetSolvePriority(byte category)
        {
            switch (category)
            {
                case Trunk:  return 0;
                case Canopy: return 1;
                case Grass:  return 2;
                default:     return 3;
            }
        }
    }

    // =========================================================================
    // CONFLICT GRID DEFINES
    //
    // The conflict grid is a per-chunk 2D bit array stored in a flat uint buffer.
    // Each cell uses 4 bits (values 0-7 from ConflictCategory), so 8 cells per uint32.
    //
    // Grid resolution scales down with chunk LOD to save memory and compute:
    //   LOD0: 300×300 (25cm cells for a 75m chunk)
    //   LOD1: 150×150 (50cm cells)
    //   LOD2:  75×75  (1m cells)
    //   LOD3:  38×38  (2m cells)
    //   LOD4:  19×19  (4m cells)
    //
    // All visible chunks' grids are stored in a single large "slab arena" buffer.
    // Each slab has a 4-uint header followed by packed cell data.
    // =========================================================================

    public static class ConflictGridDefines
    {
        // ----- Cell format -----
        public const int BitsPerCell = 4;
        public const int CellsPerUint = 32 / BitsPerCell;  // 8
        public const uint CellMask = (1u << BitsPerCell) - 1u;  // 0xF

        // ----- Grid resolution per LOD -----
        // Index = chunk LOD. LOD0 = highest resolution.
        // 75m chunk / resolution = cell size in meters.
        public static readonly int resolution = 225;

        /// <summary>
        /// Returns the cell size in meters for the given chunk LOD.
        /// Based on a 75m chunk (settings may vary; this is the default).
        /// </summary>
        public static float GetCellSizeMeters(int chunkLOD, float chunkSizeMeters = 75f)
        {
            return chunkSizeMeters / resolution;
        }

        // ----- Slab layout -----
        // Each slab in the arena buffer:
        //   [uint 0] header:   (frameID << 16) | resolution
        //   [uint 1] chunkPacked: which chunk owns this slab
        //   [uint 2] stride:    uint32s per grid row
        //   [uint 3] reserved:  padding for 16-byte alignment
        //   [uint 4..] cells:   packed 4-bit cell data
        public const int SlabHeaderUints = 4;

        /// <summary>
        /// Returns the total number of uint32s needed for a slab at the given resolution,
        /// including the 4-uint header.
        /// </summary>
        public static int GetSlabUints(int resolution)
        {
            int cells = resolution * resolution;
            int cellUints = (cells + CellsPerUint - 1) / CellsPerUint;
            // Round up to multiple of 4 for 16-byte alignment
            int total = SlabHeaderUints + cellUints;
            total = (total + 3) & ~3;
            return total;
        }

        /// <summary>
        /// Maximum slab size (at LOD0 resolution = 300×300).
        /// 300×300 = 90,000 cells / 8 = 11,250 uints + 4 header = 11,254 → rounded to 11,256.
        /// </summary>
        public static int MaxSlabUints => GetSlabUints(resolution);

        /// <summary>
        /// Maximum slab size in bytes.
        /// </summary>
        public static int MaxSlabBytes => MaxSlabUints * 4;

        // ----- Arena sizing -----
        /// <summary>
        /// Maximum number of chunks visible at once. Conservative estimate for the
        /// spherical planet with 8 LOD rings.
        /// </summary>
        public static int MaxVisibleChunks {get; private set;} = 4096;

        public static void CalculateMaxVisibleChunks(float cullDistance, float chunkSize)
        {
            // Radius in chunks
            float radiusChunks = cullDistance / chunkSize;
            // Bounding box square + a 2 chunk safety margin
            float sideLength = (radiusChunks * 2.0f) + 4.0f;
            int maxChunks = Mathf.CeilToInt(sideLength * sideLength);
            
            MaxVisibleChunks = Mathf.Max(maxChunks, 64); // Never go below 64
        }

        /// <summary>
        /// Total arena buffer size in uint32s. Enough for MaxVisibleChunks slabs at
        /// max resolution.
        /// </summary>
        public static int ArenaUints => MaxSlabUints * MaxVisibleChunks;

        /// <summary>
        /// Total arena buffer size in bytes (~45 MB).
        /// </summary>
        public static int ArenaBytes => ArenaUints * 4;

        // ----- Bucket counts -----
        /// <summary>
        /// Maximum number of LOD buckets per prototype (one per LOD level).
        /// </summary>
        public const int MaxLODsPerBucket = 8;

        /// <summary>
        /// Maximum number of prototypes (buckets = prototypes × MaxLODsPerBucket).
        /// </summary>
        public const int MaxBuckets = 256;

        // ----- Cell addressing helpers -----
        // These mirror the HLSL bit math in GrassSolver.compute.

        /// <summary>
        /// Converts a grid (x, y) coordinate to a flat cell index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CellIndex(int x, int y, int resolution)
        {
            return y * resolution + x;
        }

        /// <summary>
        /// Converts a flat cell index to the uint32 offset within the slab's cell data
        /// (excluding the header).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CellUintOffset(int cellIndex)
        {
            return cellIndex / CellsPerUint;
        }

        /// <summary>
        /// Returns the bit shift amount to access the cell within its uint32.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CellBitShift(int cellIndex)
        {
            return (cellIndex % CellsPerUint) * BitsPerCell;
        }

        /// <summary>
        /// Returns the absolute uint32 index within the slab (including header)
        /// for the given cell index.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SlabUintIndex(int cellIndex)
        {
            return SlabHeaderUints + CellUintOffset(cellIndex);
        }
    }

    // =========================================================================
    // BLOTCH EXPANSION CONSTANTS
    //
    // Parameters that control how the GPU expands a blotch into instances.
    // These are uploaded as shader constants each frame.
    // =========================================================================

    public static class BlotchExpansionDefines
    {
        /// <summary>
        /// Maximum instances a single blotch can produce before LOD pruning.
        /// Caps the inner loop in the compute shader. Based on max density (127.5/m²)
        /// and max radius (63.75m): area = π * r² ≈ 12,748 m², instances = 127.5 * 12,748
        /// ≈ 1.6M. That's way too high for a single thread group.
        ///
        /// In practice, blotches are small (radius 1-5m, density 5-20/m²).
        /// A 5m radius blotch at 20/m² = π * 25 * 20 ≈ 1,570 instances.
        /// We cap at 4096 to be safe; larger blotches should be split at authoring time.
        /// </summary>
        public const int MaxInstancesPerBlotch = 4096;

        /// <summary>
        /// Thread group size for the blotch expansion kernel.
        /// Each thread group processes one blotch.
        /// </summary>
        public const int ExpandThreadGroupSize = 64;

        /// <summary>
        /// Default density multipliers per chunk LOD. Index = chunk LOD.
        /// LOD0 = full density (but LOD0 uses GameObjects, so this is for overlap only —
        /// the solver still needs to know what would be there so it can skip those cells).
        /// Higher LODs reduce density; the vertex shader compensates visually via width multiplication.
        /// </summary>
        public static readonly float[] DefaultDensityMultiplierPerLOD =
        {
            1.00f,  // LOD 0 — full density (used for conflict-grid occupancy only; rendered as GameObjects)
            0.75f,  // LOD 1
            0.50f,  // LOD 2
            0.35f,  // LOD 3
            0.20f,  // LOD 4
            0.10f,  // LOD 5
            0.05f,  // LOD 6
            0.025f, // LOD 7
        };

        /// <summary>
        /// Default width multipliers per chunk LOD. Index = chunk LOD.
        /// As density decreases, instances are widened to fill gaps.
        /// Applied in the vertex shader: localPos.xz *= widthMult.
        /// </summary>
        public static readonly float[] DefaultWidthMultiplierPerLOD =
        {
            1.00f,  // LOD 0
            1.15f,  // LOD 1
            1.35f,  // LOD 2
            1.60f,  // LOD 3
            2.00f,  // LOD 4
            2.50f,  // LOD 5
            3.00f,  // LOD 6
            4.00f,  // LOD 7
        };

        /// <summary>
        /// Returns the density multiplier for the given chunk LOD.
        /// Clamped to the array; LODs beyond the table use the last entry.
        /// </summary>
        public static float GetDensityMultiplier(int chunkLOD)
        {
            if (chunkLOD < 0) chunkLOD = 0;
            if (chunkLOD >= DefaultDensityMultiplierPerLOD.Length)
                chunkLOD = DefaultDensityMultiplierPerLOD.Length - 1;
            return DefaultDensityMultiplierPerLOD[chunkLOD];
        }

        /// <summary>
        /// Returns the width multiplier for the given chunk LOD.
        /// Clamped to the array; LODs beyond the table use the last entry.
        /// </summary>
        public static float GetWidthMultiplier(int chunkLOD)
        {
            if (chunkLOD < 0) chunkLOD = 0;
            if (chunkLOD >= DefaultWidthMultiplierPerLOD.Length)
                chunkLOD = DefaultWidthMultiplierPerLOD.Length - 1;
            return DefaultWidthMultiplierPerLOD[chunkLOD];
        }
    }

    // =========================================================================
    // DETERMINISTIC HASH — PCG hash for reproducible instance placement.
    //
    // The GPU uses this to generate per-instance positions within a blotch.
    // Same seed + instance index → same position, every frame, no flickering.
    //
    // This must match the HLSL implementation in GrassSolver.compute exactly.
    // =========================================================================

    public static class BlotchHash
    {
        /// <summary>
        /// PCG (Permuted Congruential Generator) hash. Returns a uint32 in [0, 2^32).
        /// High quality, GPU-friendly, deterministic.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint PCGHash(uint input)
        {
            uint state = input * 747796405u + 2891336453u;
            uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }

        /// <summary>
        /// Returns a float in [0, 1) from a hash input.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Random01(uint input)
        {
            return PCGHash(input) / 4294967296.0f; // 2^32
        }

        /// <summary>
        /// Generates a deterministic position within a blotch.
        /// Uses polar coordinates for uniform disk distribution.
        /// </summary>
        /// <param name="blotchSeed">The blotch's 16-bit seed.</param>
        /// <param name="instanceIndex">The instance index within the blotch (0, 1, 2, ...).</param>
        /// <param name="radius">Blotch radius in meters.</param>
        /// <param name="outAngle">Output: angle in radians [0, 2π).</param>
        /// <param name="outDistance">Output: distance from center [0, radius].</param>
        public static void GenerateInstanceOffset(
            uint blotchSeed, uint instanceIndex, float radius,
            out float outAngle, out float outDistance)
        {
            // Combine seed and instance index into a single hash input
            uint hashInput = (blotchSeed << 16) | (instanceIndex & 0xFFFF);
            uint h1 = PCGHash(hashInput);
            uint h2 = PCGHash(h1 + 1);

            // Uniform disk sampling: angle = 2π * rand1, distance = radius * sqrt(rand2)
            outAngle = (h1 / 4294967296.0f) * Mathf.PI * 2f;
            float rand2 = h2 / 4294967296.0f;
            outDistance = radius * Mathf.Sqrt(rand2);
        }
    }
}
