using UnityEngine;
using UnityEngine.Rendering;
using System.Collections;
using System.Collections.Generic;
using CustomTypes;

/// <summary>
/// GPU-instanced impostor renderer for all LOD1+ map objects.
///
/// Replaces the old TreeRenderer. Every object placed on the map that
/// uses the instancing pathway (LOD1+ chunks) is rendered here.
///
/// Architecture overview:
///
///   [CPU → GPU upload, once at scene load]
///     - Global blotch buffer (BlotchData[] — all blotches across the entire map)
///     - Per-chunk visibility data (centerDir, cosThetaC, sinThetaC, boundCenterAlt, boundHalfH)
///
///   [CPU → GPU upload, every frame]
///     - Camera frustum planes (6 × float4 = 96 bytes)
///     - Player position / altitude (1 × float4 = 16 bytes)
///
///   [GPU-only, no CPU involvement]
///     1. CSVisibility    — Replicates VisibilitySystem.ClassifyChunk on GPU
///     2. CSExpandBlotches — Expands visible chunks' blotches, competes for grid cells
///     3. CSFillArgs      — Counts visible instances, writes indirect draw args
///     4. DrawMeshInstancedIndirect — GPU reads args and draws
///
/// The CPU never iterates instances. It only sets a few shader constants
/// and issues dispatch + draw calls.
/// =========================================================================
[DefaultExecutionOrder(1000)]
public class ImpostorRenderer : MonoBehaviour
{
    // ===== SERIALIZED FIELDS =====

    [Header("References")]
    [SerializeField] private ComputeShader impostorSolverCompute;
    [SerializeField] private MapObjectPrototypeRegistry prototypeRegistry;
    [SerializeField] private bool systemEnabled = true;
    [SerializeField] private bool enableRendering = true;
    [SerializeField] private bool castShadows = true;
    [SerializeField] private bool receiveShadows = true;

    [Header("Per-LOD Configuration")]
    [Tooltip("Density multiplier per chunk LOD. Index = LOD. "
           + "1.0 = full density, 0.5 = half, etc. "
           + "Overrides BlotchExpansionDefines.DefaultDensityMultiplierPerLOD when provided.")]
    [SerializeField] private float[] densityMultiplierPerLOD;

    [Tooltip("Width multiplier per chunk LOD. Applied in vertex shader to fill gaps from reduced density. "
           + "Overrides BlotchExpansionDefines.DefaultWidthMultiplierPerLOD when provided.")]
    [SerializeField] private float[] widthMultiplierPerLOD;

    [Header("Wind")]
    [SerializeField] private bool enableWind = true;
    [SerializeField] private float windSpeed = 1.0f;
    [SerializeField] private float windFrequency = 1.0f;
    [SerializeField] private float windStrength = 1.0f;

    [Header("Culling")]
    [Tooltip("Horizon margin for the analytic horizon test. Matches VisibilitySystem.DEFAULT_HORIZON_MARGIN.")]
    [SerializeField] private float horizonMargin = 0f;

    [Tooltip("Maximum distance (in meters) at which instanced impostors are drawn.")]
    [SerializeField] private float impostorCullDistance = 1000f; 

    [Header("Debug")]
    [SerializeField] private bool debugDrawVisibleChunks = false;
    [SerializeField] private bool debugLogStats = false;
    [SerializeField] private bool logBucketOverflow = true;
    private float _lastOverflowCheckTime;

    private Texture2DArray activeLOD0HeightmapArray;
    private ComputeBuffer activeLOD0SliceMap;
    private ComputeBuffer activeLOD0ResolutionMap;
    private uint[] cpuActiveLOD0SliceMap;

    private ComputeBuffer chunkWidthRatioBuffer;
    private Vector2[] cpuChunkWidthRatio;
    private Vector2Int[] cpuActiveLOD0ResolutionMap;
    private Dictionary<int, int> slotToSliceMap = new Dictionary<int, int>();
    private Queue<int> freeSlices = new Queue<int>();
    private const int MAX_LOD0_SLICES = 255; // 25 * 128 * 128 * 2 bytes = 800KB VRAM

    // ===== CONSTANTS =====
    // Must match GrassSolver.compute and BlotchTypes.cs definitions.

    private const int MAX_LODS_PER_BUCKET = 16;
    // Per-LOD instance capacities. A single flat cap for every bucket is enormously wasteful:
    // the buffer is sized bucketCount * cap, so raising it high enough for the densest far-LOD
    // bucket also inflates every near-LOD bucket, which never needs more than a few thousand.
    //
    // The compute shader does InterlockedAdd then discards anything past the cap, and
    // InterlockedAdd assigns slots in nondeterministic GPU thread order — so an overflowing
    // bucket renders a DIFFERENT random subset of its instances every frame, which reads as
    // strobing rather than as missing geometry. Sizing far LODs generously is what stops that.
    //
    // Index = LOD level; the last entry is reused for any LOD beyond the array.
    // Tune per project: dense ground foliage (wheat) needs far more at high LOD than trees do.
    [SerializeField]
    private int[] lodInstanceCapacities = new int[]
    {
        16384,   // LOD0 — few, close, full detail
        32768,   // LOD1
        65536,   // LOD2
        131072,  // LOD3
        262144,  // LOD4
        524288,  // LOD5
        1048576, // LOD6 — billboards; by far the most numerous
    };

    /// <summary>Total slots across all buckets (prefix-sum end). Set by BuildBuckets.</summary>
    private int totalInstanceCapacity;
    private const int BLOTCH_STRIDE = 20; // BlotchData is 20 bytes (added packedRotation for explicit-yaw objects)
    private const int INSTANCE_STRIDE = 32; // InstanceData is 32 bytes on GPU
    private const int LOD0_HM_RES = 128;//CHUNKS HAVE VARIABLE RESOLUTION, so we'll need to make this variable at some point pehaps. A chunk can either be 64 or 128 

    // ===== COMPUTE SHADER KERNEL IDS =====

    private int kernelVisibility;
    private int kernelExpand;
    private int kernelFillArgs;
    private int kernelClear;

    // ===== GPU BUFFERS (permanent, allocated once) =====

    // -- Input (read-only on GPU, uploaded once at init) --
    private ComputeBuffer globalBlotchBuffer;           // StructuredBuffer<BlotchData>
    private ComputeBuffer blotchOffsetBuffer;
    private ComputeBuffer chunkVisibilityBuffer;        // StructuredBuffer<ChunkVisibilityData>
    private ComputeBuffer prototypeScalesBuffer;
    private ComputeBuffer protoFlagsBuffer;
    private ComputeBuffer protoHeightOffsetBuffer;
    private ComputeBuffer protoBlotchParamsBuffer;
    private ComputeBuffer protoSizeParamsBuffer;    // x=minScale/minH, y=maxScale/maxH, z=minW, w=maxW
    private ComputeBuffer protoSizeModeBuffer;      // x=mode, y=steepness
    private ComputeBuffer protoColorParamsBuffer;   // color override for impostors

    private ComputeBuffer protoLODModeBuffer;      //0 - chunk LOD, 1- distance LOD
    private ComputeBuffer protoLODDistancesBuffer; //float4: max dist for LOD 0,1,2,3

    // -- CPU-side cache of chunk visibility data (for debug hash comparison) --
    private ChunkVisibilityData[] cpuChunkVisibilityCache;

    // -- Arena (read-write, GPU manages internally) --
    private ComputeBuffer conflictGridArena;            // RWStructuredBuffer<uint> — slab arena
    private ComputeBuffer instanceOutputBuffer;         // RWStructuredBuffer<InstanceData>
    // -- Args buffer (structured + indirect, written by compute shader, consumed by DrawMeshInstancedIndirect) --
    private ComputeBuffer argsBuffer;                   // RWStructuredBuffer<uint> + IndirectArguments
    private ComputeBuffer atomicCounters;               // RWStructuredBuffer<uint> — [0]=instance count, [1+N]=per-bucket counts
    private ComputeBuffer bucketLimitsBuffer;           // StructuredBuffer<uint> — 2 per bucket: [cap, offset]

    // -- Bucket map lookup (protoIdx * MAX_LODS_PER_BUCKET + lod) -> bucketIdx --
    private ComputeBuffer bucketMapBuffer;              // StructuredBuffer<uint>

    // -- Temporary (read-write, per-frame) --
    private ComputeBuffer visibleChunkListBuffer;       // RWStructuredBuffer<uint>
    private ComputeBuffer visibilityCountBuffer;        // RWStructuredBuffer<uint> — single counter for visible chunks

    private ComputeBuffer globalChunkLODBuffer;
    private ComputeBuffer protoMaxLODBuffer;

    // ---- Two-phase distance-mode expansion ----
    private ComputeBuffer batchListBuffer;
    private ComputeBuffer batchCounterBuffer;
    private ComputeBuffer batchDispatchArgsBuffer;
    private ComputeBuffer protoLODInfoBuffer;
    private ComputeBuffer protoLODDistancesFlatBuffer;
    private ComputeBuffer protoKeepFractionsBuffer;
    private ComputeBuffer protoWidthMultipliersBuffer;  // per-proto per-LOD width scale
    private ComputeBuffer debugTraceOutputBuffer;        // per-instance distance/culling trace
    private ComputeBuffer debugBlotchTraceBuffer;        // blotch-level gate trace (CSCountDistanceBlotches)
    private ComputeBuffer debugChunkTraceBuffer;         // chunk-level trace, independent of prototype
    private ComputeBuffer cellStartPosBuffer;

    private int kernelCountDistance;
    private int kernelFillBatchArgs;
    private int kernelGenerateDistance;

    private const int BATCH_SIZE = 64;
    // 65536 batches * 64 = ~4.2M instance capacity for distance-mode foliage.
    // Bump if a single frame's visible grass exceeds this; each batch is 16 bytes.
    private const int MAX_DISTANCE_BATCHES = 65535;

    private uint[] cpuChunkLODs;

    /// <summary>Reads the CPU-side chunk LOD array directly for one storage slot, bypassing the
    /// GPU entirely. 255 means SetChunkLOD has never been called for this slot — i.e. neither
    /// CreateChunk (LOD0) nor CreateBatchedChunk (LOD1+) in ChunkRegistry has created this chunk
    /// yet, regardless of what any GPU-side visibility/culling trace reports.</summary>
    public uint GetCpuChunkLOD(int storageSlot)
    {
        if (cpuChunkLODs == null || storageSlot < 0 || storageSlot >= cpuChunkLODs.Length) return 255;
        return cpuChunkLODs[storageSlot];
    }
    private bool lodsDirty = false;

    // ===== PER-FRAME STATE =====

    // Debug counters
    private int lastVisibleChunkCount;
    private int lastInstanceCount;
    private int lastBucketCount;

    // Per chunk blotch buckets.

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct BlotchRange {
        public uint start;
        public uint count;
    }

    // Active indirect-draw buckets. Built once at init; args are zeroed each frame.
    private struct IndirectBucket
    {
        public Mesh mesh;
        public Material material;
        public ShadowCastingMode shadowMode;
        public bool receiveShadows;
        public int indexCount;
        public int startIndex;
        public int baseVertex;
        public int submeshIndex;     // which submesh of `mesh` this bucket draws
        public int instanceGroupId;  // buckets sharing this id share one instance region
        public int protoIdx;
        public int lod;
        public int argsBufferOffset; // in uints from start of argsBuffer
        public int instanceCapacity; // max instances this bucket may emit
        public int instanceOffset;   // start slot in the shared instance buffer (prefix sum)
    }
    private IndirectBucket[] buckets;
    private int bucketCount;

    // Bounds for DrawMeshInstancedIndirect (whole planet sphere).
    private Bounds planetBounds;

    //Planet's sphere center and radius, used for horizon culling. Set at init.
    private Vector3 sphereCenter;
    private float sphereRadius;
    private float halfChunkLinearSize;

    // Grid parameters for blotch world position calculation.
    private int minX;
    private int numberOfChunks;
    private int mapsPerRow;

    // ===== SINGLETON =====

    public static ImpostorRenderer Instance { get; private set; }

    // ===== PUBLIC PROPERTIES =====

    public bool SystemEnabled => systemEnabled && IsInitialized;
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Called by ChunkManager every frame to keep visibility in sync.
    /// Uploads camera data and dispatches all 3 compute kernels.
    /// Safe to call multiple times per frame (idempotent within a frame).
    /// </summary>
    private int lastFrameDrawn = -1;

    /// <summary>
    /// Called by ChunkManager every frame to keep visibility in sync.
    /// Uploads camera data and dispatches all 3 compute kernels.
    /// Idempotent within a frame.
    /// </summary>
    private Vector3 lastPlayerPosition;
    private float lastPlayerAltitude;

    private Texture2DArray[] globalHeightmapArrays; // index 0 = LOD1, index i = LOD(i+1)
    private int[] terrainGridSizePerLOD;
    private ComputeBuffer terrainGridSizeBuffer; // uploaded so the shader can pick the right grid math per chunkLOD

    public void PrepareFrame(Vector3 playerPosition, float playerAltitude)
    {
        if (!systemEnabled || !IsInitialized) return;
        lastPlayerPosition = playerPosition;
        lastPlayerAltitude = playerAltitude;
        // Let LateUpdate handle the double-fire guard to avoid clearing it prematurely
        LateUpdate();
    }

    // ===== LIFECYCLE =====

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        ReleaseBuffers();
    }

    /// <summary>Unity-object-safe .name accessor. `obj?.name` does NOT protect against a
    /// destroyed/missing Unity object reference — Unity overloads `==`/`!=` to treat those as
    /// null, but the null-conditional operator uses the raw C# reference check, which does not,
    /// so a stale reference sails through `?.` and throws inside Object.get_name's native
    /// getter. This funnels every diagnostic name lookup through the overloaded check instead.</summary>
    private static string SafeName(UnityEngine.Object obj) => (obj != null) ? obj.name : "null";

    private bool ValidateState()
    {
        if (!systemEnabled) return false;
        if (impostorSolverCompute == null)
        {
            Debug.LogWarning("[ImpostorRenderer] No compute shader assigned. Disabling.");
            return false;
        }
        if (prototypeRegistry == null || prototypeRegistry.entries == null || prototypeRegistry.entries.Length == 0)
        {
            Debug.LogWarning("[ImpostorRenderer] No prototype registry assigned or empty. Disabling.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Initialize the renderer. Called once by ChunkManager at scene start.
    ///
    /// Uploads:
    ///   1. Global blotch buffer — all BlotchData from the baked map
    ///   2. Chunk visibility data — per-chunk spheres, horizon angles (matches CPU VisibilitySystem)
    ///   3. Conflict grid arena — pre-allocated slab storage (~45 MB)
    ///   4. Instance output buffer — scratch space for solved instances
    ///   5. Builds bucket metadata — mesh/material per prototype
    /// </summary>
    public void Initialize(
    MapObjectPrototypeRegistry registry,
    Vector3 sphereCenter,
    float sphereRadius,
    BlotchData[] allBlotches,
    ChunkVisibilityData[] chunkData,
    Vector2[] cellStartPositions,   // NEW
    float halfChunkLinearSize,
    Vector3 halfExtent,
    int minX, int numberOfChunks, int mapsPerRow,
    Texture2DArray globalHeightmap = null, int terrainGridSize = 0)
    {
        Instance = this;
        this.sphereCenter = sphereCenter;
        this.sphereRadius = sphereRadius;
        this.halfChunkLinearSize = halfChunkLinearSize;
        this.minX = minX;
        this.numberOfChunks = numberOfChunks;
        this.mapsPerRow = mapsPerRow;
        prototypeRegistry = registry;

        // Legacy single-texture params: kept for signature compatibility, but the real per-LOD
        // heightmap data always arrives via SetGlobalHeightmaps(), called by ChunkManager right
        // after BuildGlobalHeightmapArray() finishes building every LOD. A single texture handed
        // in here (if ever non-null) is treated as LOD1-only, with every coarser LOD simply
        // absent — acceptable as a degraded fallback, never the normal path.
        if (globalHeightmap != null && globalHeightmapArrays == null)
        {
            globalHeightmapArrays = new Texture2DArray[] { globalHeightmap };
            terrainGridSizePerLOD = new int[] { terrainGridSize };
        }

        planetBounds = new Bounds(sphereCenter, halfExtent * 2f);

        if (!ValidateState()) return;

        // Resolve kernel IDs.
        kernelVisibility = impostorSolverCompute.FindKernel("CSVisibility");
        kernelExpand = impostorSolverCompute.FindKernel("CSExpandBlotches");
        kernelFillArgs = impostorSolverCompute.FindKernel("CSFillArgs");
        kernelClear = impostorSolverCompute.FindKernel("CSClearCounters");
        kernelCountDistance   = impostorSolverCompute.FindKernel("CSCountDistanceBlotches");
        kernelFillBatchArgs   = impostorSolverCompute.FindKernel("CSFillBatchDispatchArgs");
        kernelGenerateDistance= impostorSolverCompute.FindKernel("CSGenerateDistanceInstances");

        if (kernelCountDistance < 0 || kernelFillBatchArgs < 0 || kernelGenerateDistance < 0)
        {
            Debug.LogError("[ImpostorRenderer] Missing distance-mode kernels (CSCountDistanceBlotches / "
                + "CSFillBatchDispatchArgs / CSGenerateDistanceInstances). Reimport the compute shader.");
            return;
        }

        bool hasVisibility = kernelVisibility >= 0;
        bool hasExpand = kernelExpand >= 0;
        bool hasFillArgs = kernelFillArgs >= 0;

        if (!hasVisibility || !hasExpand || !hasFillArgs)
        {
            Debug.LogError($"[ImpostorRenderer] Compute shader missing kernels: "
                + $"CSVisibility={hasVisibility} CSExpandBlotches={hasExpand} CSFillArgs={hasFillArgs}");
            return;
        }

         float chunkSize = halfChunkLinearSize * 2.0f; 
         ConflictGridDefines.CalculateMaxVisibleChunks(impostorCullDistance, chunkSize);

        // ---- 1. Global blotch buffer & Offset Buffer ----
        int totalStorageSlots = chunkData.Length; // chunkData is sized to totalStorageSlots

        if (allBlotches != null && allBlotches.Length > 0)
        {
            // We must sort the blotches by their global storageSlot so they are contiguous per chunk!
            int maxX = minX + mapsPerRow - 1;
            var globalIndexCalculator = new STPTMEUtils.GlobalIndexCalculator((sbyte)minX, (sbyte)maxX, numberOfChunks);
            
            System.Array.Sort(allBlotches, (a, b) => {
                int slotA = FaceIdUtility.GetStorageIndex(globalIndexCalculator.GetIndex(a.chunkPacked), a.Face);
                int slotB = FaceIdUtility.GetStorageIndex(globalIndexCalculator.GetIndex(b.chunkPacked), b.Face);
                return slotA.CompareTo(slotB);
            });

            // Build the offset lookup array
            BlotchRange[] blotchOffsets = new BlotchRange[totalStorageSlots];
            int currentSlot = -1;
            int currentStart = 0;
            int currentCount = 0;

            for (int i = 0; i < allBlotches.Length; i++)
            {
                int slot = FaceIdUtility.GetStorageIndex(globalIndexCalculator.GetIndex(allBlotches[i].chunkPacked), allBlotches[i].Face);
                if (slot != currentSlot)
                {
                    if (currentSlot != -1)
                    {
                        blotchOffsets[currentSlot] = new BlotchRange { start = (uint)currentStart, count = (uint)currentCount };
                    }
                    currentSlot = slot;
                    currentStart = i;
                    currentCount = 1;
                }
                else
                {
                    currentCount++;
                }
            }
            if (currentSlot != -1)
            {
                blotchOffsets[currentSlot] = new BlotchRange { start = (uint)currentStart, count = (uint)currentCount };
            }

            // Upload sorted blotches
            globalBlotchBuffer = new ComputeBuffer(allBlotches.Length, BLOTCH_STRIDE, ComputeBufferType.Structured);
            globalBlotchBuffer.SetData(allBlotches);

            // Upload offset lookup map
            blotchOffsetBuffer = new ComputeBuffer(totalStorageSlots, sizeof(uint) * 2, ComputeBufferType.Structured);
            blotchOffsetBuffer.SetData(blotchOffsets);
        }
        else
        {
            globalBlotchBuffer = new ComputeBuffer(1, BLOTCH_STRIDE, ComputeBufferType.Structured);
            blotchOffsetBuffer = new ComputeBuffer(1, sizeof(uint) * 2, ComputeBufferType.Structured);
        }

        // ---- 2. Chunk visibility data ----
        if (chunkData != null && chunkData.Length > 0)
        {
            cpuChunkVisibilityCache = chunkData; // cache CPU copy for debug comparison
            chunkVisibilityBuffer = new ComputeBuffer(chunkData.Length, ChunkVisibilityData.Stride,
                ComputeBufferType.Structured);
            chunkVisibilityBuffer.SetData(chunkData);
        }
        else
        {
            chunkVisibilityBuffer = new ComputeBuffer(1, ChunkVisibilityData.Stride, ComputeBufferType.Structured);
        }

        // ---- 2b. Global Chunk LOD Buffer ----
        cpuChunkLODs = new uint[chunkData.Length];
        for (int i = 0; i < cpuChunkLODs.Length; i++) cpuChunkLODs[i] = 255; // 255 = no chunk
        globalChunkLODBuffer = new ComputeBuffer(chunkData.Length, sizeof(uint), ComputeBufferType.Structured);
        globalChunkLODBuffer.SetData(cpuChunkLODs);

        // ---- 2c. Active LOD0 slice map ----

        cpuActiveLOD0SliceMap = new uint[chunkData.Length];
        for (int i = 0; i < cpuActiveLOD0SliceMap.Length; i++) cpuActiveLOD0SliceMap[i] = 0xFFFFFFFF;
        activeLOD0SliceMap = new ComputeBuffer(chunkData.Length, sizeof(uint), ComputeBufferType.Structured);
        activeLOD0SliceMap.SetData(cpuActiveLOD0SliceMap);

        // ---- 2c-bis. Active LOD0 resolution map (per-chunk width/height in texels) ----
        cpuActiveLOD0ResolutionMap = new Vector2Int[chunkData.Length];
        for (int i = 0; i < cpuActiveLOD0ResolutionMap.Length; i++) cpuActiveLOD0ResolutionMap[i] = new Vector2Int(0, 0);
        activeLOD0ResolutionMap = new ComputeBuffer(chunkData.Length, sizeof(uint) * 2, ComputeBufferType.Structured);
        activeLOD0ResolutionMap.SetData(cpuActiveLOD0ResolutionMap);

        activeLOD0HeightmapArray = new Texture2DArray(LOD0_HM_RES, LOD0_HM_RES, MAX_LOD0_SLICES, TextureFormat.RFloat, false, true);
        activeLOD0HeightmapArray.filterMode = FilterMode.Bilinear;
        activeLOD0HeightmapArray.wrapMode = TextureWrapMode.Clamp;

        for (int i = 0; i < MAX_LOD0_SLICES; i++) freeSlices.Enqueue(i);

        // ---- 2c-ter. Chunk width ratio — corrects the last-chunk-in-a-cell sample-count
        // deviation baked in by MeshSaver's cell-border overlap logic. Defaults to (1,1);
        // only updated when a chunk's actual LOD0 sample count differs from the nominal.
        cpuChunkWidthRatio = new Vector2[chunkData.Length];
        for (int i = 0; i < cpuChunkWidthRatio.Length; i++) cpuChunkWidthRatio[i] = Vector2.one;
        chunkWidthRatioBuffer = new ComputeBuffer(chunkData.Length, sizeof(float) * 2, ComputeBufferType.Structured);
        chunkWidthRatioBuffer.SetData(cpuChunkWidthRatio);

        // ---- 2d. Cell start position buffer — exact baked cell origin per storage slot.
        // Feeds the exact cube-face reconstruction on GPU; replaces the tangent-plane approximation
        // that only agreed with the mesh at chunk center.
        if (cellStartPositions != null && cellStartPositions.Length > 0)
        {
            cellStartPosBuffer = new ComputeBuffer(cellStartPositions.Length, sizeof(float) * 2, ComputeBufferType.Structured);
            cellStartPosBuffer.SetData(cellStartPositions);
        }
        else
        {
            cellStartPosBuffer = new ComputeBuffer(1, sizeof(float) * 2, ComputeBufferType.Structured);
        }

        // ---- 3. Conflict grid arena ----
        int arenaSize = ConflictGridDefines.ArenaUints;
        conflictGridArena = new ComputeBuffer(arenaSize, sizeof(uint),
            ComputeBufferType.Structured);

        // ---- 4. Instance output buffer (re-allocated below after bucketCount is known) ----

        // ---- 5. Visible chunk list ----
        visibleChunkListBuffer = new ComputeBuffer(
            ConflictGridDefines.MaxVisibleChunks, sizeof(uint),
            ComputeBufferType.Structured);
        // ---- 5b. Visibility count buffer (single uint, reset to 0 each frame) ----
        visibilityCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);


        // ---- 7. Atomic counters ----
        // Must match MAX_BUCKET_LODS in the compute shader (16) for correct bucket indexing.
        int maxBucketsTotal = ConflictGridDefines.MaxBuckets * MAX_LODS_PER_BUCKET;
        atomicCounters = new ComputeBuffer(
            1 + maxBucketsTotal,
            sizeof(uint), ComputeBufferType.Structured);

        // ---- 6. Args buffer ----
        bucketCount = BuildBuckets();
        // Sized from the prefix sum of per-bucket capacities, not bucketCount * flatCap — that
        // is the entire memory saving of per-LOD capacities.
        int instanceBufSize = Mathf.Max(totalInstanceCapacity, 1);
        instanceOutputBuffer = new ComputeBuffer(
        Mathf.Max(instanceBufSize, 1024), INSTANCE_STRIDE, ComputeBufferType.Structured);

        // Per-bucket (capacity, offset) table for the compute shader. Packed as two uints per
        // bucket in one buffer so a thread fetches both in a single cache line rather than
        // hitting two separate buffers.
        // 3 uints per bucket: [capacity, offset, countSourceBucket].
        // countSourceBucket = the bucket whose atomic counter holds this group's instance count:
        // itself for the group's first bucket, otherwise that first bucket. Extra submesh buckets
        // never get instances written to their own counter, so without this they'd draw zero.
        var firstOfGroup = new Dictionary<int, int>();
        for (int b = 0; b < bucketCount; b++)
            if (!firstOfGroup.ContainsKey(buckets[b].instanceGroupId))
                firstOfGroup[buckets[b].instanceGroupId] = b;

        var bucketLimits = new uint[Mathf.Max(bucketCount, 1) * 3];
        for (int b = 0; b < bucketCount; b++)
        {
            bucketLimits[b * 3 + 0] = (uint)buckets[b].instanceCapacity;
            bucketLimits[b * 3 + 1] = (uint)buckets[b].instanceOffset;
            bucketLimits[b * 3 + 2] = (uint)firstOfGroup[buckets[b].instanceGroupId];
        }
        bucketLimitsBuffer = new ComputeBuffer(Mathf.Max(bucketCount, 1) * 3, sizeof(uint), ComputeBufferType.Structured);
        bucketLimitsBuffer.SetData(bucketLimits);
        argsBuffer = new ComputeBuffer(
            Mathf.Max(bucketCount, 1) * 5, sizeof(uint),
            ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);
        ResetArgsBuffer();

        // ---- 6b. Bucket map buffer: protoIdx * MAX_LODS_PER_BUCKET + lod -> bucketIdx ----
        uint[] bucketMap = new uint[256 * MAX_LODS_PER_BUCKET];
        for (int i = 0; i < bucketMap.Length; i++) bucketMap[i] = 0xFFFFFFFF;
        for (int b = 0; b < bucketCount; b++)
        {
            // First submesh bucket wins: the compute shader increments exactly ONE counter per
            // (proto, LOD); CSFillArgs mirrors that count onto the group's other submesh buckets.
            // Without first-wins this pointed at the LAST submesh, leaving the rest at zero.
            int mapIdx = buckets[b].protoIdx * MAX_LODS_PER_BUCKET + buckets[b].lod;
            if (bucketMap[mapIdx] == 0xFFFFFFFF)
                bucketMap[mapIdx] = (uint)b;
        }
        
        // DEBUG: Log bucket map entries for every shouldInstance prototype (previously only
        // instanceAlways ones were logged, which made it impossible to see whether normal
        // shouldInstance trees actually got buckets at LOD1+ or not).
        var bucketMapDebug = new System.Text.StringBuilder();
        bucketMapDebug.AppendLine("[ImpostorRenderer] Bucket map for ALL shouldInstance prototypes:");
        for (int pi = 0; pi < prototypeRegistry.entries.Length; pi++)
        {
            var entry = prototypeRegistry.entries[pi];
            if (entry != null && entry.shouldInstance)
            {
                bucketMapDebug.AppendLine($"  [{pi}] '{entry.name}' (instanceAlways={entry.instanceAlways}, maxLOD={(entry.lodMeshes != null ? entry.lodMeshes.Length - 1 : -1)}):");
                for (int lod = 0; lod < MAX_LODS_PER_BUCKET; lod++)
                {
                    uint bucketIdx = bucketMap[pi * MAX_LODS_PER_BUCKET + lod];
                    if (bucketIdx != 0xFFFFFFFF)
                    {
                        bucketMapDebug.AppendLine($"    LOD{lod} -> bucket {bucketIdx} (mesh={SafeName(buckets[bucketIdx].mesh)})");
                    }
                }
            }
        }
        Debug.Log(bucketMapDebug.ToString());
        
        bucketMapBuffer = new ComputeBuffer(bucketMap.Length, sizeof(uint), ComputeBufferType.Structured);
        bucketMapBuffer.SetData(bucketMap);

        // ---- 6c. Prototype scales buffer
        Vector3[] scales = new Vector3[prototypeRegistry.entries.Length];
        uint[] maxLods = new uint[prototypeRegistry.entries.Length];
        for (int i = 0; i < scales.Length; i++)
        {
            if (prototypeRegistry.entries[i] != null && prototypeRegistry.entries[i].sourcePrefab != null)
                scales[i] = prototypeRegistry.entries[i].sourcePrefab.transform.localScale;
            else
                scales[i] = Vector3.one;

             // Store the max LOD index for this prototype
            if (prototypeRegistry.entries[i] != null && prototypeRegistry.entries[i].lodMeshes != null && prototypeRegistry.entries[i].lodMeshes.Length > 0)
                maxLods[i] = (uint)(prototypeRegistry.entries[i].lodMeshes.Length - 1);
            else
                maxLods[i] = 0;
        }
        prototypeScalesBuffer = new ComputeBuffer(scales.Length, sizeof(float) * 3);
        prototypeScalesBuffer.SetData(scales);

        protoMaxLODBuffer = new ComputeBuffer(maxLods.Length, sizeof(uint));
        protoMaxLODBuffer.SetData(maxLods);

        // ---- 6d. Prototype flags buffer
        // Bit 0: shouldInstance
        // Bit 1: instanceAlways
        uint[] protoFlags = new uint[prototypeRegistry.entries.Length];
        for (int i = 0; i < protoFlags.Length; i++)
        {
            uint flags = 0;
            if (prototypeRegistry.entries[i] != null)
            {
                if (prototypeRegistry.entries[i].shouldInstance) flags |= 1u;
                if (prototypeRegistry.entries[i].instanceAlways) flags |= 2u;
            }
            protoFlags[i] = flags;
        }
        
        // DEBUG: Log proto flags for instanceAlways prototypes
        var flagsDebug = new System.Text.StringBuilder();
        flagsDebug.AppendLine("[ImpostorRenderer] Proto flags for instanceAlways prototypes:");
        for (int i = 0; i < prototypeRegistry.entries.Length; i++)
        {
            var entry = prototypeRegistry.entries[i];
            if (entry != null && entry.instanceAlways)
            {
                flagsDebug.AppendLine($"  [{i}] '{entry.name}': flags={protoFlags[i]:X2} (shouldInstance={entry.shouldInstance}, instanceAlways={entry.instanceAlways})");
            }
        }
        Debug.Log(flagsDebug.ToString());
        
        protoFlagsBuffer = new ComputeBuffer(protoFlags.Length, sizeof(uint));
        protoFlagsBuffer.SetData(protoFlags);

         // ---- 6e. Prototype height offsets buffer (x = base, y = lod1Plus)
        Vector2[] heightOffsets = new Vector2[prototypeRegistry.entries.Length];
        for (int i = 0; i < heightOffsets.Length; i++)
        {
            if (prototypeRegistry.entries[i] != null)
                heightOffsets[i] = new Vector2(prototypeRegistry.entries[i].heightOffset, prototypeRegistry.entries[i].lod1PlusHeightOffset);
            else
                heightOffsets[i] = Vector2.zero;
        }
        protoHeightOffsetBuffer = new ComputeBuffer(heightOffsets.Length, sizeof(float) * 2);
        protoHeightOffsetBuffer.SetData(heightOffsets);

         // ---- 6f. Prototype blotch params buffer (x = radius, y = density)
        Vector4[] blotchParams = new Vector4[prototypeRegistry.entries.Length];
        for (int i = 0; i < blotchParams.Length; i++)
        {
            if (prototypeRegistry.entries[i] != null)
            {
                var e = prototypeRegistry.entries[i];
                blotchParams[i] = new Vector4(
                    e.blotchRadius,
                    e.blotchDensity,
                    e.densityFadeStart,
                    e.densityFadeEnabled ? 1f : 0f);
            }
            else
                blotchParams[i] = Vector4.zero;
        }
        protoBlotchParamsBuffer = new ComputeBuffer(blotchParams.Length, sizeof(float) * 4);
        protoBlotchParamsBuffer.SetData(blotchParams);

        // ---- 6g. Prototype size variability buffers
        Vector4[] sizeParams = new Vector4[prototypeRegistry.entries.Length];
        Vector2[] sizeMode = new Vector2[prototypeRegistry.entries.Length];
        Vector4[] colorParams = new Vector4[prototypeRegistry.entries.Length];
        for (int i = 0; i < prototypeRegistry.entries.Length; i++)
        {
            if (prototypeRegistry.entries[i] != null && prototypeRegistry.entries[i].sizeVariability.enabled)
            {
                sizeParams[i] = prototypeRegistry.entries[i].sizeVariability.PackForGPU();
                sizeMode[i] = prototypeRegistry.entries[i].sizeVariability.PackModeAndSteepness();
                colorParams[i] = prototypeRegistry.entries[i].impostorColorOverride;
            }
            else
            {
                // Disabled: use scale 1.0 for everything
                sizeParams[i] = new Vector4(1f, 1f, 1f, 1f);
                sizeMode[i] = new Vector2(0f, 1f); // Uniform mode, steepness 1
                colorParams[i] = new Vector4(1f, 1f, 1f, 1f); // No override (white = use material color)
            }
        }
        protoSizeParamsBuffer = new ComputeBuffer(sizeParams.Length, sizeof(float) * 4);
        protoSizeParamsBuffer.SetData(sizeParams);
        protoSizeModeBuffer = new ComputeBuffer(sizeMode.Length, sizeof(float) * 2);
        protoSizeModeBuffer.SetData(sizeMode);
        protoColorParamsBuffer = new ComputeBuffer(colorParams.Length, sizeof(float) * 4);
        protoColorParamsBuffer.SetData(colorParams);

        // ---- 6h. Prototype LOD mode + distance buffers
        // _ProtoLODModeBuffer: 0 = chunk LOD, 1 = distance LOD
        // _ProtoLODDistancesBuffer: x,y,z,w = max distance for LOD 0,1,2,3
        uint[] lodModes = new uint[prototypeRegistry.entries.Length];
        Vector4[] lodDistances = new Vector4[prototypeRegistry.entries.Length];
        for (int i = 0; i < prototypeRegistry.entries.Length; i++)
        {
            var e = prototypeRegistry.entries[i];
            if (e != null)
            {
                lodModes[i] = e.useDistanceLOD ? 1u : 0u;
                lodDistances[i] = e.lodDistances;
            }
            else
            {
                lodModes[i] = 0u;
                lodDistances[i] = Vector4.zero;
            }
        }
        protoLODModeBuffer = new ComputeBuffer(lodModes.Length, sizeof(uint));
        protoLODModeBuffer.SetData(lodModes);
        protoLODDistancesBuffer = new ComputeBuffer(lodDistances.Length, sizeof(float) * 4);
        protoLODDistancesBuffer.SetData(lodDistances);

        // ---- Two-phase batch buffers ----
        batchListBuffer = new ComputeBuffer(MAX_DISTANCE_BATCHES, sizeof(uint) * 4, ComputeBufferType.Structured);
        batchCounterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
        batchDispatchArgsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.IndirectArguments);
        batchDispatchArgsBuffer.SetData(new uint[] { 0, 1, 1 });

        // ---- Variable-length LOD distance + keep-fraction buffers ----
        BuildVariableLODBuffers();

        // ---- 7. Bind buffers to compute shader ----
        BindComputeBuffers();

        // ---- 8. Upload per-LOD config ----
        UploadLODConfig();


        IsInitialized = true;

        // SetGlobalHeightmaps() is called by ChunkManager.BuildGlobalHeightmapArray() BEFORE
        // this Initialize() runs, so its own internal bind attempt correctly no-oped (IsInitialized
        // was still false, kernels didn't exist yet). Re-bind now that kernels are real, or every
        // LOD texture stays unset and every dispatch errors despite the data having arrived and
        // been stored correctly.
        if (globalHeightmapArrays != null)
        {
            BindGlobalHeightmapsToKernel(kernelExpand);
            BindGlobalHeightmapsToKernel(kernelGenerateDistance);
        }
    }

    // ===== PER-FRAME UPDATE =====

    private int debugLogInterval = 60; // Log every 60 frames
    private int debugLogCounter = 0;

    private void LateUpdate()
    {
        if (!systemEnabled || !enableRendering) return;
        if (!IsInitialized) return; // Initialize() may have thrown partway through and left buffers null
        if (!ValidateState()) return;

        int curFrame = Time.frameCount;
        if (curFrame == lastFrameDrawn) return;
        lastFrameDrawn = curFrame;

        // Upload camera data first (needed by all kernels)
        impostorSolverCompute.SetInt(ShaderIDs.TimeMS, (int)(Time.time * 1000f));
        UploadCameraData();

        // OPTIMIZATION 1: Only upload LODs if the CPU array was modified!
        if (lodsDirty && globalChunkLODBuffer != null && cpuChunkLODs != null)
        {
            globalChunkLODBuffer.SetData(cpuChunkLODs);
            lodsDirty = false;
        }

        // OPTIMIZATION 2: The GPU clears the counters via CSClearCounters!
        // We do NOT need to call SetData on visibilityCountBuffer or atomicCounters anymore!
        // This eliminates massive CPU-GPU sync stalls.

        // OPTIMIZATION 3: Do NOT call ResetArgsBuffer() here!
        // The argsBuffer was initialized once in Initialize(). CSFillArgs updates the instance count.
        // Calling SetData here overwrites the GPU's data and causes a stall.

        // Clear (also zeroes the batch counter)
        int clearGroups = (bucketCount + 1 + 63) / 64;
        impostorSolverCompute.Dispatch(kernelClear, clearGroups, 1, 1);

        // Visibility
        int chunkCount = chunkVisibilityBuffer?.count ?? 0;
        if (chunkCount > 0)
        {
            int vGroups = (chunkCount + 63) / 64;
            impostorSolverCompute.Dispatch(kernelVisibility, vGroups, 1, 1);
        }

        // Clear debug trace buffers before ANY dispatch that could write them this cycle
        // (kernelExpand's chunk-LOD trace runs before the distance-mode dispatches below, so
        // this must happen before kernelExpand too — clearing afterward would wipe out whatever
        // kernelExpand had just written, before the async readback ever saw it).
        if (debugScaleProtoIndex >= 0)
        {
            if (debugTraceOutputBuffer != null)
                debugTraceOutputBuffer.SetData(new uint[3 * 4]);
            if (debugBlotchTraceBuffer != null)
                debugBlotchTraceBuffer.SetData(new uint[2 * 4]);
            if (debugChunkTraceBuffer != null)
                debugChunkTraceBuffer.SetData(new uint[2 * 4]);
        }

        // Chunk-LOD expansion (trees + conflict-grid foliage). Skips distance-mode blotches.
        if (globalBlotchBuffer != null)
            impostorSolverCompute.Dispatch(kernelExpand, ConflictGridDefines.MaxVisibleChunks, 1, 1);

        // Distance-mode grass: Phase A (count/emit batches) -> args -> Phase B (instance-parallel)
        if (globalBlotchBuffer != null)
        {
            impostorSolverCompute.Dispatch(kernelCountDistance, ConflictGridDefines.MaxVisibleChunks, 1, 1);
            impostorSolverCompute.Dispatch(kernelFillBatchArgs, 1, 1, 1);
            impostorSolverCompute.DispatchIndirect(kernelGenerateDistance, batchDispatchArgsBuffer);
        }

        if (Time.frameCount % 60 == 0)
        {
            AsyncGPUReadback.Request(instanceOutputBuffer, 32, 0, (req) =>
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                // InstanceData: worldPos(3 floats), heightScale(float), packedMeta(uint), seed(uint), widthScale(float), pad3(uint)
                float wpx = System.BitConverter.Int32BitsToSingle((int)data[0]);
                float wpy = System.BitConverter.Int32BitsToSingle((int)data[1]);
                float wpz = System.BitConverter.Int32BitsToSingle((int)data[2]);
                float hScale = System.BitConverter.Int32BitsToSingle((int)data[3]);
                uint packedMeta = data[4];
                float wScale = System.BitConverter.Int32BitsToSingle((int)data[6]);
                uint protoIdx = packedMeta & 0xFFu;
                //Debug.Log($"[InstanceReadback] slot0: proto={protoIdx} pos=({wpx:F1},{wpy:F1},{wpz:F1}) heightScale={hScale:F4} widthScale={wScale:F4}");
            });
        }

        // Fill draw args
        if (bucketCount > 0)
        {
            int aGroups = (bucketCount + 63) / 64;
            impostorSolverCompute.Dispatch(kernelFillArgs, aGroups, 1, 1);
        }

        if (logBucketOverflow && Time.time - _lastOverflowCheckTime > 1f)
        {
            _lastOverflowCheckTime = Time.time;
            CheckBucketOverflow();
        }

        // Runs after the solve dispatches, so the buffer holds this frame's instances.
        DebugInstanceScale();

        DrawIndirect();
    }

    private System.Collections.IEnumerator LogInstanceAlwaysCounters()
    {
        // Read back debug counters from GPU
        var request = AsyncGPUReadback.Request(atomicCounters);
        yield return new WaitUntil(() => request.done);

        if (request.hasError)
        {
            Debug.LogWarning("[ImpostorRenderer] Failed to read instanceAlways debug counters");
            yield break;
        }

        var data = request.GetData<uint>();
        uint lod0Blotches = data[4002];
        uint lod1PlusBlotches = data[4003];
        uint lod0Instances = data[4000];
        uint lod1PlusInstances = data[4001];

        Debug.Log($"[ImpostorRenderer] instanceAlways debug: " +
            $"LOD0 blotches={lod0Blotches}, instances={lod0Instances} | " +
            $"LOD1+ blotches={lod1PlusBlotches}, instances={lod1PlusInstances}");
    }

    // ===== CAMERA DATA (the only CPU→GPU upload every frame) =====

    private void UploadCameraData()
    {
        Camera cam = GetActiveCamera();
        if (cam == null) return;

        // Frustum planes (from the camera — needs correct camera position).
        var planes = GeometryUtility.CalculateFrustumPlanes(cam);
        var frustumArray = new Vector4[6];
        for (int i = 0; i < 6; i++)
            frustumArray[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);

        impostorSolverCompute.SetVectorArray(ShaderIDs.FrustumPlanes, frustumArray);

        // Horizon test position (must match VisibilitySystem's playerPosition).
        Vector3 pos = lastPlayerPosition;
        impostorSolverCompute.SetVector(ShaderIDs.CameraPos, new Vector4(pos.x, pos.y, pos.z, 0f));

        // Player altitude (from player height above sphere surface).
        float alt = lastPlayerAltitude;

        // Player altitude (also used for horizon test).
        impostorSolverCompute.SetFloat(ShaderIDs.PlayerAltitude, alt);
        impostorSolverCompute.SetFloat(ShaderIDs.PlayerAltitude, alt);
        impostorSolverCompute.SetFloat("_ImpostorCullDistance", impostorCullDistance);

        // LOD distance must come from a LIVE, per-frame position. lastPlayerPosition is only
        // refreshed by ChunkManager on chunk transitions, which freezes the LOD band field
        // between crossings — that's the "rings pinned to the map / camera does nothing" bug.
        Vector3 eye = cam.transform.position;          // live every frame; billboards already prove this updates
        impostorSolverCompute.SetVector("_EyePos", eye);

        // Leave the horizon reference as the player position (must match VisibilitySystem):
        // impostorSolverCompute.SetVector(ShaderIDs.CameraPos, new Vector4(pos.x, pos.y, pos.z, 0f));  // unchanged
    }

    public void SetChunkWidthRatio(int storageSlot, float ratioX, float ratioZ)
    {
        if (storageSlot < 0 || storageSlot >= cpuChunkWidthRatio.Length) return;
        var r = new Vector2(ratioX, ratioZ);
        if (cpuChunkWidthRatio[storageSlot] == r) return; // avoid redundant uploads
        cpuChunkWidthRatio[storageSlot] = r;
        chunkWidthRatioBuffer.SetData(cpuChunkWidthRatio);
    }

    public void SetActiveLOD0Heightmap(int storageSlot, ushort[,] heights, float maxHeight, int xOffset, int yOffset, int width, int height)
    {
        if (slotToSliceMap.TryGetValue(storageSlot, out int existingSlice))
            return;

        if (freeSlices.Count == 0) return;

        int slice = freeSlices.Dequeue();
        slotToSliceMap[storageSlot] = slice;

        int srcResX = heights.GetLength(1);
        int srcResY = heights.GetLength(0);

        // Use the chunk's NATIVE resolution (width × height) so we get 1:1
        // texel-to-vertex mapping. No bilinear interpolation loss vs the mesh.
        // The texture array is sized at LOD0_HM_RES (128, the max), so smaller
        // chunks (64) just use the upper-left quadrant — the GPU samples only
        // within [0, width/LOD0_HM_RES] which we scale the UV to below.
        int dstResX = width;
        int dstResY = height;
        float[] sliceData = new float[LOD0_HM_RES * LOD0_HM_RES];

        for (int y = 0; y < dstResY; y++)
        {
            // 1:1 mapping — no resampling, direct source pixel read
            int srcY = Mathf.Clamp(yOffset + y, 0, srcResY - 1);

            for (int x = 0; x < dstResX; x++)
            {
                int srcX = Mathf.Clamp(xOffset + x, 0, srcResX - 1);

                float h = heights[srcY, srcX] / 65535f;
                float heightMeters = h * maxHeight;

                sliceData[y * LOD0_HM_RES + x] = heightMeters;
            }
        }

        activeLOD0HeightmapArray.SetPixelData(sliceData, 0, slice);
        activeLOD0HeightmapArray.Apply(false, false);

        cpuActiveLOD0SliceMap[storageSlot] = (uint)slice;
        cpuActiveLOD0ResolutionMap[storageSlot] = new Vector2Int(dstResX, dstResY);
        activeLOD0SliceMap.SetData(cpuActiveLOD0SliceMap);
        activeLOD0ResolutionMap.SetData(cpuActiveLOD0ResolutionMap);
    }

    public void ClearActiveLOD0Heightmap(int storageSlot)
    {
        if (slotToSliceMap.TryGetValue(storageSlot, out int slice))
        {
            slotToSliceMap.Remove(storageSlot);
            freeSlices.Enqueue(slice);
            cpuActiveLOD0SliceMap[storageSlot] = 0xFFFFFFFF;
            cpuActiveLOD0ResolutionMap[storageSlot] = new Vector2Int(0, 0);
            activeLOD0SliceMap.SetData(cpuActiveLOD0SliceMap);
            activeLOD0ResolutionMap.SetData(cpuActiveLOD0ResolutionMap);
        }
    }

    // ===== INDIRECT DRAW =====

    private MaterialPropertyBlock drawProps;

    private void DrawIndirect()
    {
        if (drawProps == null)
            drawProps = new MaterialPropertyBlock();

        drawProps.Clear();
        drawProps.SetBuffer(ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        
        if (prototypeScalesBuffer != null)
            drawProps.SetBuffer("_PrototypeScales", prototypeScalesBuffer);

        if (protoMaxLODBuffer != null)
            drawProps.SetBuffer("_ProtoMaxLODs", protoMaxLODBuffer);

        // ADD THIS: Bind the color params buffer!
        if (protoColorParamsBuffer != null)
            drawProps.SetBuffer("_ProtoColorParamsBuffer", protoColorParamsBuffer);

        drawProps.SetVector("_SphereCenter", sphereCenter);
        
        // Pass Camera Position for billboarding!
        Camera cam = GetActiveCamera();
        if (cam != null)
            drawProps.SetVector("_CameraPos", cam.transform.position);

        var shadowMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;

        for (int i = 0; i < bucketCount; i++)
        {
            ref var bucket = ref buckets[i];
            if (bucket.mesh == null || bucket.material == null) continue;

            drawProps.SetFloat("_InstanceOffset", bucket.instanceOffset);

            // LOD0 casts shadows; LOD1+ impostors don't (major shadow-pass savings).
            var bucketShadowMode = (castShadows && bucket.lod == 0)
                ? ShadowCastingMode.On
                : ShadowCastingMode.Off;

            Graphics.DrawMeshInstancedIndirect(
                bucket.mesh, 0, bucket.material,
                planetBounds,
                argsBuffer, bucket.argsBufferOffset,
                drawProps, bucketShadowMode, bucket.receiveShadows, 0, null);
        }
    }

    /// <summary>Legacy single-array entry point, kept for any external caller that hasn't moved
    /// to the multi-LOD path. Treated as LOD1-only.</summary>
    public void SetGlobalHeightmap(Texture2DArray heightmapArray, int gridSize)
        => SetGlobalHeightmaps(new Texture2DArray[] { heightmapArray }, new int[] { gridSize });

    /// <summary>
    /// Binds one heightmap array PER chunk LOD level, so BlotchWorldPosDir's non-LOD0 branch and
    /// CSGenerateDistanceInstances' coarseAlt sample can read elevation at a resolution matching
    /// the instance's ACTUAL rendered chunk LOD, instead of always reading the single finest
    /// array regardless of how coarse that chunk's own geometry is.
    ///
    /// heightmapArrays[i] = chunk LOD (i+1)'s heightmap (index 0 = LOD1; chunkLOD 0 uses the
    /// exact active-terrain sampling path in BlotchWorldPosDir and never reads these arrays).
    /// </summary>
    public void SetGlobalHeightmaps(Texture2DArray[] heightmapArrays, int[] gridSizePerLOD)
    {
        globalHeightmapArrays = heightmapArrays;
        terrainGridSizePerLOD = gridSizePerLOD;

        if (globalHeightmapArrays == null || globalHeightmapArrays.Length == 0) return;

        terrainGridSizeBuffer?.Release();
        var gridSizesUint = new uint[globalHeightmapArrays.Length];
        for (int i = 0; i < globalHeightmapArrays.Length; i++)
            gridSizesUint[i] = (uint)(gridSizePerLOD != null && i < gridSizePerLOD.Length ? gridSizePerLOD[i] : 0);
        terrainGridSizeBuffer = new ComputeBuffer(Mathf.Max(1, gridSizesUint.Length), sizeof(uint), ComputeBufferType.Structured);
        terrainGridSizeBuffer.SetData(gridSizesUint);

        BindGlobalHeightmapsToKernel(kernelExpand);
        BindGlobalHeightmapsToKernel(kernelGenerateDistance);
    }

    /// <summary>Binds every available LOD's heightmap array to one kernel under its own named
    /// property (_GlobalHeightmapArray_LOD1.._LOD{N}), plus the shared grid-size buffer.</summary>
    private void BindGlobalHeightmapsToKernel(int kernel)
    {
        if (!IsInitialized || impostorSolverCompute == null || kernel < 0 || globalHeightmapArrays == null) return;

        for (int i = 0; i < globalHeightmapArrays.Length; i++)
        {
            if (globalHeightmapArrays[i] == null) continue;
            impostorSolverCompute.SetTexture(kernel, $"_GlobalHeightmapArray_LOD{i + 1}", globalHeightmapArrays[i]);
        }
        if (terrainGridSizeBuffer != null)
            impostorSolverCompute.SetBuffer(kernel, "_TerrainGridSizePerLOD", terrainGridSizeBuffer);
        impostorSolverCompute.SetInt("_GlobalHeightmapLODCount", globalHeightmapArrays.Length);

        if (globalHeightmapArrays.Length > 0 && terrainGridSizePerLOD != null && terrainGridSizePerLOD.Length > 0)
            impostorSolverCompute.SetInt("_TerrainGridSize", terrainGridSizePerLOD[0]);
    }

    public void SetChunkLOD(int storageSlot, byte lod)
    {
        if (storageSlot >= 0 && storageSlot < cpuChunkLODs.Length)
        {
            if (cpuChunkLODs[storageSlot] != lod)
            {
                cpuChunkLODs[storageSlot] = lod;
                lodsDirty = true;
            }
        }
        else
        {
            Debug.LogError($"[ImpostorRenderer] SetChunkLOD out of bounds! Slot: {storageSlot}, Length: {cpuChunkLODs.Length}");
        }
    }


    // ===== BUCKET BUILDING =====

    /// <summary>
    /// Builds bucket metadata from the prototype registry.
    /// Each (prototypeIndex, LOD) pair gets a bucket slot.
    /// The bucket holds the mesh + material for that LOD.
    /// Args buffer offsets are: slot = (protoIndex * MAX_LODS_PER_BUCKET + lod).
    /// </summary>
    private int BuildBuckets()
    {
        var entries = prototypeRegistry.entries;
        int protoCount = entries.Length;
        var bucketList = new List<IndirectBucket>();

        // DEBUG: Track instanceAlways prototypes
        var instanceAlwaysProtos = new System.Text.StringBuilder();
        int instanceGroupCounter = 0;
        instanceAlwaysProtos.AppendLine("[ImpostorRenderer] BuildBuckets - instanceAlways prototypes:");

        for (int pi = 0; pi < protoCount; pi++)
        {
            var entry = entries[pi];
            if (entry == null || !entry.shouldInstance) continue;
            if (entry.lodMeshes == null) continue;

            // DEBUG: Log instanceAlways prototypes
            if (entry.instanceAlways)
            {
                // NOTE: intentionally NOT `?.name` — Unity overloads `==`/`!=` to treat a
                // destroyed/missing object reference as null, but the null-conditional operator
                // uses the raw C# reference check underneath, which does NOT see it as null. A
                // missing material reference (e.g. deleted after being assigned) sails through
                // `?.` and throws inside Object.get_name's native getter instead.
                var lod0Sub0Mat = entry.GetSubmeshMaterial(0, 0);
                string lod0Sub0MatName = (lod0Sub0Mat != null) ? lod0Sub0Mat.name : "null";
                instanceAlwaysProtos.AppendLine($"  [{pi}] '{entry.name}': lodMeshes.Length={entry.lodMeshes.Length}, LOD0 submesh0 material={lod0Sub0MatName}");
                for (int lod = 0; lod < entry.lodMeshes.Length; lod++)
                {
                    instanceAlwaysProtos.AppendLine($"    LOD{lod}: mesh={SafeName(entry.lodMeshes[lod])}");
                }
            }

            // FIX: Only iterate up to the actual number of LODs!
            int maxLod = entry.lodMeshes.Length;
            for (int lod = 0; lod < maxLod; lod++)
            {
                Mesh mesh = entry.lodMeshes[lod];
                if (mesh == null)
                {
                    // DEBUG: Log if instanceAlways prototype is missing LOD0 mesh
                    if (lod == 0 && entry.instanceAlways)
                    {
                        Debug.LogWarning($"[ImpostorRenderer] instanceAlways prototype '{entry.name}' (index {pi}) has no LOD0 mesh! " +
                            $"It will not render at LOD0. Assign lodMeshes[0] to fix this.");
                    }
                    continue;
                }
                if (entry.GetSubmeshMaterial(lod, 0) == null) continue;

                // ONE BUCKET PER SUBMESH. A multi-material prefab is one mesh with several
                // submeshes (separate index ranges). Drawing only submesh 0 renders a PARTIAL
                // mesh: with the connecting triangles absent, the object looks torn into offset
                // fragments rather than simply missing a colour. (A texture atlas can make that
                // partial geometry still show several colours, which makes the symptom look like
                // distortion rather than a missing submesh.)
                //
                // Per-LOD, NOT a single shared list: submesh count genuinely varies by LOD (a
                // hand-authored single-triangle LOD6 has 1 submesh even when LOD0 has 3) — a flat
                // array shared across every LOD cannot represent that and silently mismatches
                // whichever LOD it doesn't fit, which is exactly what caused this to be reworked.
                //
                // All submeshes of a (proto, LOD) share the SAME instance data, linked by
                // instanceGroupId and given one shared capacity/offset below — the compute shader
                // fills that region once and the extra submeshes are pure draw calls over it.
                int submeshCount = Mathf.Max(1, mesh.subMeshCount);
                int authoredSlots = entry.GetSubmeshMaterialCount(lod);
                if (submeshCount != authoredSlots)
                {
                    Debug.LogWarning($"[ImpostorRenderer] '{entry.name}' LOD{lod} mesh '{mesh.name}' has {submeshCount} " +
                        $"submesh(es) but {authoredSlots} material slot(s) are authored for this LOD. " +
                        (submeshCount > authoredSlots
                            ? $"Submeshes {authoredSlots}..{submeshCount - 1} will NOT render (partial mesh)."
                            : "The mesh may have been re-imported with merged/fewer submeshes.") +
                        " Run 'Sync Submesh Materials From Meshes' on the registry to fix.");
                    submeshCount = Mathf.Min(submeshCount, authoredSlots);
                }

                int instanceGroupId = instanceGroupCounter++;

                for (int sm = 0; sm < submeshCount; sm++)
                {
                    Material submeshMat = entry.GetSubmeshMaterial(lod, sm);
                    if (submeshMat == null)
                    {
                        Debug.LogError($"[ImpostorRenderer] '{entry.name}' LOD{lod} submesh {sm} has a null material " +
                            "— that submesh will NOT render (missing geometry, not a colour/texture bug). " +
                            "Check submeshMaterialsPerLOD for this LOD on this prototype.");
                        continue;
                    }

                    int bucketIdx = bucketList.Count;
                    bucketList.Add(new IndirectBucket
                    {
                        mesh = mesh,
                        material = submeshMat,
                        shadowMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                        receiveShadows = receiveShadows,
                        indexCount = (int)mesh.GetIndexCount(sm),
                        startIndex = (int)mesh.GetIndexStart(sm),
                        baseVertex = (int)mesh.GetBaseVertex(sm),
                        argsBufferOffset = bucketIdx * 5 * sizeof(uint),
                        protoIdx = pi,
                        lod = lod,
                        submeshIndex = sm,
                        instanceGroupId = instanceGroupId
                    });
                }
            }
        }

        // DEBUG: Log instanceAlways summary
        Debug.Log(instanceAlwaysProtos.ToString());

        buckets = bucketList.ToArray();

        // Assign per-bucket capacities from the LOD table and prefix-sum them into offsets.
        // Buckets are no longer at fixed stride in the instance buffer — each starts where the
        // previous ended — so both the compute shader (writes) and DrawIndirect (reads) must
        // use these offsets rather than bucketIdx * flatCap.
        int running = 0;
        // Submeshes of one (proto, LOD) share identical instance data — allocating a region per
        // submesh bucket would multiply instance memory by the submesh count for no benefit.
        var groupAlloc = new Dictionary<int, (int cap, int offset)>();
        var capReport = new System.Text.StringBuilder();
        capReport.AppendLine("[ImpostorRenderer] Per-bucket instance capacities:");
        for (int b = 0; b < buckets.Length; b++)
        {
            int lod = Mathf.Clamp(buckets[b].lod, 0, lodInstanceCapacities.Length - 1);
            int cap = Mathf.Max(1, lodInstanceCapacities[lod]);

            int gid = buckets[b].instanceGroupId;
            if (!groupAlloc.TryGetValue(gid, out var alloc))
            {
                alloc = (cap, running);
                groupAlloc[gid] = alloc;
                running += cap;   // one region per GROUP, not per submesh bucket
            }
            buckets[b].instanceCapacity = alloc.cap;
            buckets[b].instanceOffset = alloc.offset;

            capReport.AppendLine($"  bucket {b} (proto {buckets[b].protoIdx}, LOD {buckets[b].lod}, " +
                $"mesh={SafeName(buckets[b].mesh)}): cap={cap}, offset={buckets[b].instanceOffset}");
        }
        totalInstanceCapacity = running;

        capReport.AppendLine($"  TOTAL: {totalInstanceCapacity} slots " +
            $"({(totalInstanceCapacity * 32L) / (1024 * 1024)} MB at 32 B/instance)");
        Debug.Log(capReport.ToString());

        return buckets.Length;
    }

    private void BuildVariableLODBuffers()
    {
        int protoCount = prototypeRegistry.entries.Length;
        var infoPacked   = new uint[protoCount * 2]; // (offset, count) per proto
        var flatDistances = new List<float>();
        var flatKeeps     = new List<float>();
        var flatWidths    = new List<float>();

        for (int i = 0; i < protoCount; i++)
        {
            var e = prototypeRegistry.entries[i];
            int offset = flatDistances.Count;

            // Distances are only meaningful for useDistanceLOD prototypes. Keep fractions and
            // width multipliers are read from this SAME table by the per-chunk-LOD kernel too
            // (via selectedLOD = mesh LOD), independent of useDistanceLOD — so gating the whole
            // table's length behind useDistanceLOD leaves count==0 for non-distance prototypes,
            // making KeepFractionForLOD/WidthMultiplierForLOD silently no-op for them.
            int distCount = (e != null && e.useDistanceLOD && e.lodDistancesVariable != null)
                ? e.lodDistancesVariable.Length : 0;
            int keepCount = (e?.lodKeepFractions != null) ? e.lodKeepFractions.Length : 0;
            int widthCount = (e?.lodWidthMultipliers != null) ? e.lodWidthMultipliers.Length : 0;

            // For a distance-mode prototype count must be EXACTLY distCount: SelectLODByDistance
            // treats "no threshold matched within count" as cull, and the padding sentinel below
            // (float.MaxValue) is greater than any real distance — so extending count past
            // distCount would create a slot that matches everything, disabling culling.
            int count = (distCount > 0) ? distCount : Mathf.Max(keepCount, widthCount);

            for (int l = 0; l < count; l++)
            {
                flatDistances.Add(l < distCount ? e.lodDistancesVariable[l] : float.MaxValue);

                float keep = (l < keepCount) ? Mathf.Clamp01(e.lodKeepFractions[l]) : 1f;
                flatKeeps.Add(keep);

                float width = (l < widthCount) ? e.lodWidthMultipliers[l] : 1f;
                flatWidths.Add(Mathf.Max(0.001f, width));
            }

            infoPacked[i * 2 + 0] = (uint)offset;
            infoPacked[i * 2 + 1] = (uint)count;
        }

        // Buffers must be non-empty even if no prototype has any per-LOD table.
        if (flatDistances.Count == 0) { flatDistances.Add(0f); flatKeeps.Add(1f); flatWidths.Add(1f); }

        protoLODInfoBuffer = new ComputeBuffer(protoCount, sizeof(uint) * 2, ComputeBufferType.Structured);
        protoLODInfoBuffer.SetData(infoPacked);

        protoLODDistancesFlatBuffer = new ComputeBuffer(flatDistances.Count, sizeof(float), ComputeBufferType.Structured);
        protoLODDistancesFlatBuffer.SetData(flatDistances.ToArray());

        protoKeepFractionsBuffer = new ComputeBuffer(flatKeeps.Count, sizeof(float), ComputeBufferType.Structured);
        protoKeepFractionsBuffer.SetData(flatKeeps.ToArray());

        // WITHOUT THIS the compute shader's _ProtoWidthMultipliers is an UNBOUND buffer. It is
        // declared and read (WidthMultiplierForLOD, multiplied into scales.y at both instance
        // write sites), so an unbound read returns undefined values that differ per dispatch —
        // instances render at wildly wrong scale (tiny one moment, astronomic the next) while
        // their POSITION stays correct, because only the scale term is affected.
        protoWidthMultipliersBuffer = new ComputeBuffer(flatWidths.Count, sizeof(float), ComputeBufferType.Structured);
        protoWidthMultipliersBuffer.SetData(flatWidths.ToArray());

        // 4 uints per slot (instDist bits, coarseAlt bits, survived, distBandLOD), 3 slots.
        debugTraceOutputBuffer = new ComputeBuffer(3, sizeof(uint) * 4, ComputeBufferType.Structured);
        debugBlotchTraceBuffer = new ComputeBuffer(2, sizeof(uint) * 4, ComputeBufferType.Structured);
        debugChunkTraceBuffer = new ComputeBuffer(2, sizeof(uint) * 4, ComputeBufferType.Structured);
    }

    private void ResetArgsBuffer()
    {
        if (argsBuffer == null || buckets == null) return;
        var argsData = new uint[bucketCount * 5];
        for (int i = 0; i < bucketCount; i++)
        {
            ref var b = ref buckets[i];
            int offset = i * 5;
            argsData[offset + 0] = (uint)b.indexCount;
            argsData[offset + 1] = 0; // instance count — reset to 0 each frame
            argsData[offset + 2] = (uint)b.startIndex;
            argsData[offset + 3] = (uint)b.baseVertex;
            argsData[offset + 4] = 0; // start instance location
        }
        argsBuffer.SetData(argsData);
    }

    // ===== SHADER BINDING =====

    private void BindComputeBuffers()
    {
        // Visibility kernel.
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.ChunkVisibilityBuffer, chunkVisibilityBuffer);
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.VisibleChunkList, visibleChunkListBuffer);
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        
        // Bind LOD buffer to BOTH kernels!
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.GlobalChunkLODBuffer, globalChunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.GlobalChunkLODBuffer, globalChunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ActiveLOD0SliceMap, activeLOD0SliceMap);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ActiveLOD0ResolutionMap, activeLOD0ResolutionMap);
        impostorSolverCompute.SetTexture(kernelExpand, ShaderIDs.ActiveLOD0HeightmapArray, activeLOD0HeightmapArray);

        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        // Expand kernel.
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ChunkVisibilityBuffer, chunkVisibilityBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.GlobalBlotchBuffer, globalBlotchBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ConflictGridArena, conflictGridArena);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.VisibleChunkList, visibleChunkListBuffer);
         impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.BlotchOffsetBuffer, blotchOffsetBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.AtomicCounters, atomicCounters);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.BucketLimits, bucketLimitsBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoFlagsBuffer, protoFlagsBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoHeightOffsetBuffer, protoHeightOffsetBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoBlotchParamsBuffer, protoBlotchParamsBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoSizeParamsBuffer, protoSizeParamsBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoSizeModeBuffer, protoSizeModeBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoColorParamsBuffer, protoColorParamsBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoLODModeBuffer, protoLODModeBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ProtoLODDistancesBuffer, protoLODDistancesBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, "_ProtoMaxLODs", protoMaxLODBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, "_CellStartPosBuffer", cellStartPosBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, "_ChunkWidthRatioBuffer", chunkWidthRatioBuffer);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        // Fill-args kernel.
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.ArgsBuffer, argsBuffer);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.AtomicCounters, atomicCounters);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.BucketLimits, bucketLimitsBuffer);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        //Clear kernel.
        impostorSolverCompute.SetBuffer(kernelClear, ShaderIDs.AtomicCounters, atomicCounters);
        impostorSolverCompute.SetBuffer(kernelClear, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        impostorSolverCompute.SetBuffer(kernelClear, "_BatchCounter", batchCounterBuffer);

        // ---- Count kernel (Phase A) ----
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.ChunkVisibilityBuffer, chunkVisibilityBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.GlobalBlotchBuffer, globalBlotchBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.BlotchOffsetBuffer, blotchOffsetBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.GlobalChunkLODBuffer, globalChunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.VisibleChunkList, visibleChunkListBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.ProtoBlotchParamsBuffer, protoBlotchParamsBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, ShaderIDs.ProtoLODModeBuffer, protoLODModeBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_ProtoLODInfo", protoLODInfoBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_ProtoLODDistancesFlat", protoLODDistancesFlatBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_BatchList", batchListBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_BatchCounter", batchCounterBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_CellStartPosBuffer", cellStartPosBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_ChunkWidthRatioBuffer", chunkWidthRatioBuffer);

        // ---- Fill batch dispatch args kernel ----
        impostorSolverCompute.SetBuffer(kernelFillBatchArgs, "_BatchCounter", batchCounterBuffer);
        impostorSolverCompute.SetBuffer(kernelFillBatchArgs, "_BatchDispatchArgs", batchDispatchArgsBuffer);

        // ---- Generate kernel (Phase B) ----
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ChunkVisibilityBuffer, chunkVisibilityBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.GlobalBlotchBuffer, globalBlotchBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.GlobalChunkLODBuffer, globalChunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.AtomicCounters, atomicCounters);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.BucketLimits, bucketLimitsBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.BucketMapBuffer, bucketMapBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ProtoHeightOffsetBuffer, protoHeightOffsetBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ProtoBlotchParamsBuffer, protoBlotchParamsBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ProtoSizeParamsBuffer, protoSizeParamsBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ProtoSizeModeBuffer, protoSizeModeBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ProtoLODModeBuffer, protoLODModeBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_ProtoLODInfo", protoLODInfoBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_ProtoLODDistancesFlat", protoLODDistancesFlatBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_ProtoKeepFractions", protoKeepFractionsBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_ProtoWidthMultipliers", protoWidthMultipliersBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_DebugTraceOutputBuffer", debugTraceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, "_DebugTraceOutputBuffer", debugTraceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_DebugBlotchTraceBuffer", debugBlotchTraceBuffer);
        impostorSolverCompute.SetBuffer(kernelCountDistance, "_DebugChunkTraceBuffer", debugChunkTraceBuffer);
        // Also bound here so the shader compiles cleanly against a single global declaration
        // even though this kernel never writes it.
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_DebugBlotchTraceBuffer", debugBlotchTraceBuffer);
        impostorSolverCompute.SetInt("_DebugTraceProtoIdx", debugScaleProtoIndex);
        // kernelExpand reads WidthMultiplierForLOD too, which indexes via _ProtoLODInfo.
        impostorSolverCompute.SetBuffer(kernelExpand, "_ProtoWidthMultipliers", protoWidthMultipliersBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, "_ProtoKeepFractions", protoKeepFractionsBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, "_ProtoLODInfo", protoLODInfoBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ActiveLOD0SliceMap, activeLOD0SliceMap);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, ShaderIDs.ActiveLOD0ResolutionMap, activeLOD0ResolutionMap);
        impostorSolverCompute.SetTexture(kernelGenerateDistance, ShaderIDs.ActiveLOD0HeightmapArray, activeLOD0HeightmapArray);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_BatchList", batchListBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_BatchCounter", batchCounterBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_ProtoMaxLODs", protoMaxLODBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_CellStartPosBuffer", cellStartPosBuffer);
        impostorSolverCompute.SetBuffer(kernelGenerateDistance, "_ChunkWidthRatioBuffer", chunkWidthRatioBuffer);
        if (globalHeightmapArrays != null)
            BindGlobalHeightmapsToKernel(kernelGenerateDistance);

        // Global constants.
        impostorSolverCompute.SetInt(ShaderIDs.BucketCount, bucketCount);
        impostorSolverCompute.SetInt(ShaderIDs.MaxLodsPerBucket, MAX_LODS_PER_BUCKET);
        impostorSolverCompute.SetInt(ShaderIDs.MaxVisibleChunks, ConflictGridDefines.MaxVisibleChunks);
        impostorSolverCompute.SetInt(ShaderIDs.ArenaUints, ConflictGridDefines.ArenaUints);
        impostorSolverCompute.SetInt(ShaderIDs.SlabHeaderUints, ConflictGridDefines.SlabHeaderUints);
        impostorSolverCompute.SetInt(ShaderIDs.CellsPerUint, ConflictGridDefines.CellsPerUint);
        impostorSolverCompute.SetInt(ShaderIDs.MinX, minX);
        impostorSolverCompute.SetInt(ShaderIDs.NumberOfChunks, numberOfChunks);
        impostorSolverCompute.SetInt(ShaderIDs.MapsPerFace, mapsPerRow);
        impostorSolverCompute.SetInt(ShaderIDs.TotalBlotchCount, globalBlotchBuffer?.count ?? 0);
        impostorSolverCompute.SetVector(ShaderIDs.SphereCenter, new Vector4(sphereCenter.x, sphereCenter.y, sphereCenter.z, 0f));
        impostorSolverCompute.SetFloat(ShaderIDs.SphereRadius, sphereRadius);
        impostorSolverCompute.SetFloat(ShaderIDs.HalfChunkLinearSize, halfChunkLinearSize);
        impostorSolverCompute.SetInt("_MaxDistanceBatches", MAX_DISTANCE_BATCHES);
        
        int slabStrideUints = ConflictGridDefines.SlabHeaderUints +
            (ConflictGridDefines.resolution * ConflictGridDefines.resolution + ConflictGridDefines.CellsPerUint - 1) / ConflictGridDefines.CellsPerUint;
        impostorSolverCompute.SetInt(ShaderIDs.SlabStride, slabStrideUints);
        impostorSolverCompute.SetInt(ShaderIDs.NumBuckets, bucketCount);
        impostorSolverCompute.SetInt(ShaderIDs.ConflictGridResolution, ConflictGridDefines.resolution);

        if (globalHeightmapArrays != null)
            BindGlobalHeightmapsToKernel(kernelExpand);
    }

    private void UploadLODConfig()
    {
        // Density multipliers.
        var density = densityMultiplierPerLOD != null && densityMultiplierPerLOD.Length > 0
            ? densityMultiplierPerLOD
            : BlotchExpansionDefines.DefaultDensityMultiplierPerLOD;
        // Density multipliers — use SetFloats for proper array addressing.
        impostorSolverCompute.SetFloats(ShaderIDs.DensityMultiplierPerLOD, density);

        // Width multipliers.
        var width = widthMultiplierPerLOD != null && widthMultiplierPerLOD.Length > 0
            ? widthMultiplierPerLOD
            : BlotchExpansionDefines.DefaultWidthMultiplierPerLOD;
        impostorSolverCompute.SetFloats(ShaderIDs.WidthMultiplierPerLOD, width);

        // Wind constants.
        impostorSolverCompute.SetFloat(ShaderIDs.WindSpeed, windSpeed);
        impostorSolverCompute.SetFloat(ShaderIDs.WindFrequency, windFrequency);
        impostorSolverCompute.SetFloat(ShaderIDs.WindStrength, windStrength);
        impostorSolverCompute.SetFloat(ShaderIDs.HorizonMargin, horizonMargin);
    }

    // ===== RESOURCE MANAGEMENT =====

    private void ReleaseBuffers()
    {
        globalBlotchBuffer?.Release();        globalBlotchBuffer = null;
        chunkVisibilityBuffer?.Release();     chunkVisibilityBuffer = null;
        conflictGridArena?.Release();         conflictGridArena = null;
        instanceOutputBuffer?.Release();      instanceOutputBuffer = null;
        argsBuffer?.Release();                argsBuffer = null;
        visibleChunkListBuffer?.Release();    visibleChunkListBuffer = null;
        visibilityCountBuffer?.Release();     visibilityCountBuffer = null;
        atomicCounters?.Release();            atomicCounters = null;
        bucketLimitsBuffer?.Release();        bucketLimitsBuffer = null;
        bucketMapBuffer?.Release();           bucketMapBuffer = null;
        blotchOffsetBuffer?.Release();        blotchOffsetBuffer = null;
        prototypeScalesBuffer?.Release();     prototypeScalesBuffer = null;
        globalChunkLODBuffer?.Release();      globalChunkLODBuffer = null;
        protoMaxLODBuffer?.Release();         protoMaxLODBuffer = null;
        protoFlagsBuffer?.Release();          protoFlagsBuffer = null;
        protoHeightOffsetBuffer?.Release();  protoHeightOffsetBuffer = null;
        protoBlotchParamsBuffer?.Release(); protoBlotchParamsBuffer = null;
        protoSizeParamsBuffer?.Release();    protoSizeParamsBuffer = null;
        protoSizeModeBuffer?.Release();     protoSizeModeBuffer = null;
        protoColorParamsBuffer?.Release();  protoColorParamsBuffer = null;
        activeLOD0SliceMap?.Release();      activeLOD0SliceMap = null;
        activeLOD0ResolutionMap?.Release();  activeLOD0ResolutionMap = null;
        protoLODModeBuffer?.Release();      protoLODModeBuffer = null;
        protoLODDistancesBuffer?.Release(); protoLODDistancesBuffer = null;
        batchListBuffer?.Release();            batchListBuffer = null;
        batchCounterBuffer?.Release();         batchCounterBuffer = null;
        batchDispatchArgsBuffer?.Release();    batchDispatchArgsBuffer = null;
        protoLODInfoBuffer?.Release();         protoLODInfoBuffer = null;
        protoLODDistancesFlatBuffer?.Release();protoLODDistancesFlatBuffer = null;
        protoKeepFractionsBuffer?.Release();   protoKeepFractionsBuffer = null;
        protoWidthMultipliersBuffer?.Release(); protoWidthMultipliersBuffer = null;
        debugTraceOutputBuffer?.Release();       debugTraceOutputBuffer = null;
        debugBlotchTraceBuffer?.Release();       debugBlotchTraceBuffer = null;
        debugChunkTraceBuffer?.Release();        debugChunkTraceBuffer = null;
        terrainGridSizeBuffer?.Release();        terrainGridSizeBuffer = null;
        cellStartPosBuffer?.Release(); cellStartPosBuffer = null;
        chunkWidthRatioBuffer?.Release(); chunkWidthRatioBuffer = null;
    }

    // ===== HELPERS (stubs — to be wired to ChunkManager data) =====

    private static Camera GetActiveCamera()
    {
        Camera cam = VisibilitySystem.IsReady ? VisibilitySystem.Instance?.ActiveCamera : null;
        if (cam != null && cam.isActiveAndEnabled) return cam;
        return Camera.main;
    }

        public IEnumerator DebugAllBuckets()
    {
        if (argsBuffer == null || buckets == null) yield break;
        yield return new WaitForEndOfFrame();

        int totalBytes = bucketCount * 5 * sizeof(uint);
        var request = AsyncGPUReadback.Request(argsBuffer, totalBytes, 0, (req) =>
        {
            if (req.hasError) { Debug.LogError("Readback error"); return; }
            var data = req.GetData<uint>();
            
            Debug.Log($"<color=orange>===== ALL BUCKETS REPORT =====</color>");
            for (int i = 0; i < bucketCount; i++)
            {
                int offset = i * 5;
                uint indexCount = data[offset + 0];
                uint instanceCount = data[offset + 1];
                
                int protoIdx = buckets[i].protoIdx;
                int lod = buckets[i].lod;
                string protoName = prototypeRegistry.entries[protoIdx]?.name ?? "Unknown";
                
                Debug.Log($"[Bucket {i}] Proto: {protoIdx} ({protoName}) | LOD: {lod} | IndexCount: {indexCount} | <b>InstanceCount: {instanceCount}</b>");
            }
        });
        while (!request.done) yield return null;
    }

    public IEnumerator DebugBucketMap()
    {
        if (bucketMapBuffer == null) yield break;
        yield return new WaitForEndOfFrame();

        int totalBytes = 256 * MAX_LODS_PER_BUCKET * sizeof(uint);
        var request = AsyncGPUReadback.Request(bucketMapBuffer, totalBytes, 0, (req) =>
        {
            if (req.hasError) return;
            var data = req.GetData<uint>();
            
            Debug.Log($"<color=cyan>===== BUCKET MAP REPORT =====</color>");
            for (int pi = 0; pi < prototypeRegistry.entries.Length; pi++)
            {
                if (prototypeRegistry.entries[pi] == null) continue;
                
                for (int lod = 0; lod < MAX_LODS_PER_BUCKET; lod++)
                {
                    uint bucketIdx = data[pi * MAX_LODS_PER_BUCKET + lod];
                    if (bucketIdx != 0xFFFFFFFF)
                    {
                        Debug.Log($"Proto {pi} ({prototypeRegistry.entries[pi].name}) LOD {lod} -> Maps to Bucket {bucketIdx}");
                    }
                }
            }
        });
        while (!request.done) yield return null;
    }

 
    public IEnumerator DebugInstancePositions()
    {
        if (instanceOutputBuffer == null || !IsInitialized) yield break;

        yield return new WaitForEndOfFrame();

        // Read first 5 instances from Bucket 0
        const int instancesToRead = 5;
        int byteSize = instancesToRead * 32; // 32 bytes per instance
        int bucket0ByteOffset = 0; 

        var request = AsyncGPUReadback.Request(instanceOutputBuffer, byteSize, bucket0ByteOffset, (req) =>
        {
            if (req.hasError) { Debug.LogError("Readback error"); return; }

            var data = req.GetData<uint>();
            for (int i = 0; i < instancesToRead; i++)
            {
                int offset = i * 8; // 32 bytes / 4 = 8 uints per instance

                float x = System.BitConverter.Int32BitsToSingle((int)data[offset + 0]);
                float y = System.BitConverter.Int32BitsToSingle((int)data[offset + 1]);
                float z = System.BitConverter.Int32BitsToSingle((int)data[offset + 2]);
                
                uint protoAndLod = data[offset + 4];
                uint seed = data[offset + 5];

                Debug.Log($"<color=green>[GPU Debug] Bucket 0 Instance {i}: WorldPos ({x:F2}, {y:F2}, {z:F2}) | Proto/Lod: {protoAndLod}</color>");
            }
        });

        while (!request.done) yield return null;
        
        // Also check the counters
        if (atomicCounters != null)
        {
            var counterReq = AsyncGPUReadback.Request(atomicCounters, sizeof(uint) * 5, 0, (req) =>
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                Debug.Log($"<color=yellow>[GPU Debug] Total Instances: {data[0]} | Bucket 0: {data[1]} | Bucket 1: {data[2]}</color>");
            });
            while (!counterReq.done) yield return null;
        }
    }

    public IEnumerator DebugCounters()
    {
        if (atomicCounters == null) yield break;

        yield return new WaitForEndOfFrame();

        // Read standard counters (Total, B0, B1)
        var request = AsyncGPUReadback.Request(atomicCounters, sizeof(uint) * 5, 0, (req) =>
        {
            if (req.hasError) { Debug.LogError("Counter Readback error"); return; }
            var data = req.GetData<uint>();
            Debug.Log($"<color=yellow>[GPU Debug] Total: {data[0]} | B0: {data[1]} | B1: {data[2]}</color>");
        });
        while (!request.done) yield return null;

        // Read debug counters at index 4000
        var debugReq = AsyncGPUReadback.Request(atomicCounters, sizeof(uint) * 4, 4000 * 4, (req) =>
        {
            if (req.hasError) { Debug.LogError("Debug Counter Readback error"); return; }
            var data = req.GetData<uint>();
            Debug.Log($"<color=cyan>[GPU Debug] Blotches Processed: {data[0]} | Passed LOD: {data[1]} | Valid Bucket: {data[2]} | Won Claim: {data[3]}</color>");
        });
        while (!debugReq.done) yield return null;
    }

    public IEnumerator DebugVisibilityAndBlotches()
    {
        if (visibilityCountBuffer == null || globalBlotchBuffer == null)
        {
            Debug.LogWarning("Buffers not ready.");
            yield break;
        }

        yield return new WaitForEndOfFrame();

        // 1. Read how many chunks passed visibility
        var visRequest = AsyncGPUReadback.Request(visibilityCountBuffer, sizeof(uint), 0, (req) =>
        {
            if (req.hasError) return;
            uint visibleChunks = req.GetData<uint>()[0];
            Debug.Log($"<color=cyan>[GPU Debug] Visible Chunks count: {visibleChunks}</color>");
        });
        while (!visRequest.done) yield return null;

        // 2. Read the first 5 visible chunks
        if (visibleChunkListBuffer != null)
        {
            var chunkRequest = AsyncGPUReadback.Request(visibleChunkListBuffer, sizeof(uint) * 5, 0, (req) =>
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                Debug.Log($"[GPU Debug] First 5 visible chunks packed: {data[0]}, {data[1]}, {data[2]}, {data[3]}, {data[4]}");
            });
            while (!chunkRequest.done) yield return null;
        }

        // 3. Read the first 5 blotches from the GlobalBlotchBuffer
        if (globalBlotchBuffer != null)
        {
            // BlotchData is 16 bytes (4 uints)
            var blotchRequest = AsyncGPUReadback.Request(globalBlotchBuffer, 16 * 5, 0, (req) =>
            {
                if (req.hasError) return;
                var data = req.GetData<uint>();
                for (int i = 0; i < 5; i++)
                {
                    int offset = i * 4;
                    int chunkPacked = (int)data[offset + 0];
                    uint packedMeta = data[offset + 1];
                    
                    // Extract face and prototype just to see what it is
                    uint face = packedMeta & 0xFFu;
                    uint proto = (packedMeta >> 8) & 0xFFu;

                    Debug.Log($"[GPU Debug] Blotch {i}: chunkPacked={chunkPacked} | Face={face} | Proto={proto}");
                }
            });
            while (!blotchRequest.done) yield return null;
        }
    }

    public IEnumerator DebugArgsBuffer()
    {
        if (argsBuffer == null) yield break;
        yield return new WaitForEndOfFrame();

        // Args buffer is 5 uints per bucket: indexCount, instanceCount, startIndex, baseVertex, startInstance
        uint[] args = new uint[5];
        // Read the first bucket (bucket 0)
        var request = AsyncGPUReadback.Request(argsBuffer, sizeof(uint) * 5, 20, (req) =>
        {
            if (req.hasError) return;
            var data = req.GetData<uint>();
            for (int i = 0; i < 5; i++) args[i] = data[i];

            Debug.Log($"<color=orange>[GPU Debug] Args Buffer (Bucket this bucket):</color>");
            Debug.Log($"  Index Count: {args[0]}");
            Debug.Log($"  <b>Instance Count: {args[1]}</b>"); // THIS IS THE CRITICAL ONE
            Debug.Log($"  Start Index: {args[2]}");
            Debug.Log($"  Base Vertex: {args[3]}");
            Debug.Log($"  Start Instance: {args[4]}");
        });
        while (!request.done) yield return null;
    }

    public IEnumerator DebugSpecificBucket(int bucketIndex)
    {
        if (instanceOutputBuffer == null || !IsInitialized) yield break;

        yield return new WaitForEndOfFrame();

        const int instancesToRead = 5;
        int byteSize = instancesToRead * 32; // 32 bytes per instance
        int bucketByteOffset = buckets[bucketIndex].instanceOffset * 32; // per-bucket prefix-sum offset

        var request = AsyncGPUReadback.Request(instanceOutputBuffer, byteSize, bucketByteOffset, (req) =>
        {
            if (req.hasError) { Debug.LogError("Readback error"); return; }

            var data = req.GetData<uint>();
            for (int i = 0; i < instancesToRead; i++)
            {
                int offset = i * 8; // 32 bytes / 4 = 8 uints per instance

                float x = System.BitConverter.Int32BitsToSingle((int)data[offset + 0]);
                float y = System.BitConverter.Int32BitsToSingle((int)data[offset + 1]);
                float z = System.BitConverter.Int32BitsToSingle((int)data[offset + 2]);
                
                uint protoAndLod = data[offset + 4];

                // Calculate the exact height above the sphere surface!
                Vector3 worldPos = new Vector3(x, y, z);
                float altitude = Vector3.Distance(worldPos, sphereCenter) - sphereRadius;

                Debug.Log($"<color=green>[GPU Debug] Bucket {bucketIndex} Instance {i}: WorldPos ({x:F2}, {y:F2}, {z:F2}) | Altitude: {altitude:F2}m</color>");
            }
        });

        while (!request.done) yield return null;
    }

    // Peak instance demand per bucket across the session. Walk the whole map, then dump this to
    // find which prototypes/LODs are near or over capacity — a single-frame sample misses the
    // worst spot unless you happen to be standing in it.
    private uint[] _bucketPeakDemand;

    [Header("Scale Debug")]
    [Tooltip("Prototype index to inspect with the instance scale readback. -1 = off. " +
             "Reads the FIRST instance of that prototype's largest-count bucket.")]
    [SerializeField] private int debugScaleProtoIndex = 25;
    private float _lastScaleDebugTime;

    /// <summary>
    /// Reads back one real instance for a chosen prototype and reports its final scale factors.
    /// Targets that prototype's own bucket — reading slot 0 blindly is useless because slot 0
    /// belongs to bucket 0, whose region is only written if that particular prototype has
    /// instances nearby (otherwise it reads as all zeros and tells you nothing).
    /// </summary>
    private void DebugInstanceScale()
    {
        if (debugScaleProtoIndex < 0 || buckets == null || instanceOutputBuffer == null) return;
        if (Time.time - _lastScaleDebugTime < 1f) return;
        _lastScaleDebugTime = Time.time;

        // Keep the shader's copy in sync with live Inspector edits — the value set at init
        // won't reflect changes made while playing otherwise.
        if (impostorSolverCompute != null)
            impostorSolverCompute.SetInt("_DebugTraceProtoIdx", debugScaleProtoIndex);

        if (debugTraceOutputBuffer != null)
        {
            AsyncGPUReadback.Request(debugTraceOutputBuffer, (treq) =>
            {
                if (treq.hasError) return;
                var td = treq.GetData<uint>();
                if (td.Length < 12) return; // 3 slots * 4 uints

                for (int slot = 0; slot < 3; slot++)
                {
                    int o = slot * 4;
                    if (td[o] == 0 && td[o + 1] == 0 && td[o + 2] == 0 && td[o + 3] == 0) continue; // never written

                    float instDist = System.BitConverter.Int32BitsToSingle((int)td[o + 0]);
                    float coarseAlt = System.BitConverter.Int32BitsToSingle((int)td[o + 1]);
                    bool survived = td[o + 2] != 0;
                    uint distBandLOD = td[o + 3];
                    Debug.Log($"[DistTrace] proto={debugScaleProtoIndex} inst={slot}: instDist={instDist:F2} " +
                        $"coarseAlt={coarseAlt:F2} survived={survived} distBandLOD={distBandLOD}");

                    // Same physical slot, written by kernelExpand's chunk-LOD bucket lookup with
                    // RAW uint semantics instead — printed separately since DistTrace's read above
                    // would bit-cast these as floats and show nonsense. For a non-distance-mode
                    // prototype only THIS interpretation is meaningful (kernelGenerateDistance
                    // never runs for it, so DistTrace's own line above will be garbage/stale).
                    uint bucketIdx = td[o + 2];
                    string bucketNote = bucketIdx == 0xFFFFFFFFu ? " <<< NO BUCKET (0xFFFFFFFF) — this LOD was never created" : "";
                    Debug.Log($"[ChunkLODTrace] proto={debugScaleProtoIndex}: chunkLOD={td[o + 0]} " +
                        $"selectedLOD={td[o + 1]} bucketIdx={bucketIdx}{bucketNote} protoMaxLOD={td[o + 3]}");
                }
            });
        }

        if (debugBlotchTraceBuffer != null)
        {
            AsyncGPUReadback.Request(debugBlotchTraceBuffer, (breq) =>
            {
                if (breq.hasError) return;
                var bd = breq.GetData<uint>();
                if (bd.Length < 8) return; // 2 slots * 4 uints

                if (bd[0] == 0 && bd[1] == 0 && bd[2] == 0 && bd[3] == 0)
                {
                    Debug.Log($"[BlotchTrace] proto={debugScaleProtoIndex}: NEVER WRITTEN this frame — " +
                        "no thread in CSCountDistanceBlotches reached the trace line for this prototype " +
                        "(chunk not visible, blotch range empty, or LOD mode routed elsewhere).");
                    return;
                }

                float centerDist = System.BitConverter.Int32BitsToSingle((int)bd[0]);
                float radius = System.BitConverter.Int32BitsToSingle((int)bd[1]);
                float maxLODDist = System.BitConverter.Int32BitsToSingle((int)bd[2]);
                bool blotchInRange = bd[3] != 0;

                float boundCenterAlt = System.BitConverter.Int32BitsToSingle((int)bd[4]);
                float eyeX = System.BitConverter.Int32BitsToSingle((int)bd[5]);
                float eyeY = System.BitConverter.Int32BitsToSingle((int)bd[6]);
                float eyeZ = System.BitConverter.Int32BitsToSingle((int)bd[7]);

                Debug.Log($"[BlotchTrace] proto={debugScaleProtoIndex}: centerDist={centerDist:F2} radius={radius:F2} " +
                    $"maxLODDist={maxLODDist:F2} blotchInRange={blotchInRange} " +
                    $"boundCenterAlt={boundCenterAlt:F2} eyePos=({eyeX:F1},{eyeY:F1},{eyeZ:F1})" +
                    (float.IsNaN(centerDist) || float.IsInfinity(centerDist) ? "   <<< centerDist NOT FINITE" : ""));
            });
        }

        if (debugChunkTraceBuffer != null)
        {
            AsyncGPUReadback.Request(debugChunkTraceBuffer, (creq) =>
            {
                if (creq.hasError) return;
                var cd = creq.GetData<uint>();
                if (cd.Length < 8) return;

                if (cd[0] == 0 && cd[1] == 0 && cd[2] == 0 && cd[3] == 0)
                {
                    Debug.Log("[ChunkTrace] NEVER WRITTEN — CSCountDistanceBlotches dispatched zero threads " +
                        "that reached the chunk-level trace, meaning _VisibilityCount[0] was 0 or every " +
                        "storageSlot in _VisibleChunkList was 0xFFFFFFFF this frame.");
                    return;
                }

                float chunkCenterDist = System.BitConverter.Int32BitsToSingle((int)cd[0]);
                uint storageSlot = cd[1];
                uint chunkLOD = cd[2];
                uint blotchCount = cd[3];
                uint blotchStart = cd[4];
                float boundCenterAlt = System.BitConverter.Int32BitsToSingle((int)cd[5]);

                Debug.Log($"[ChunkTrace] CLOSEST chunk to eye: dist={chunkCenterDist:F2} storageSlot={storageSlot} " +
                    $"chunkLOD={chunkLOD}{(chunkLOD == 255 ? " <<< SKIP SENTINEL" : "")} " +
                    $"blotchRange=[{blotchStart}, count={blotchCount}]{(blotchCount == 0 ? " <<< EMPTY — no blotches of ANY prototype in this chunk" : "")} " +
                    $"boundCenterAlt={boundCenterAlt:F2}");

                Vector3 knownTomatoPosition = new Vector3(-4006.0061f, 7126.51563f, 21.7850056f);
                uint lod = ChunkManager.Instance.DebugGetChunkLODForPosition(knownTomatoPosition);
                Debug.Log($"[CpuChunkLOD] = {lod}{(lod == 255 ? " <<< NEVER CREATED" : "")}");

            });
        }

        // Find a bucket belonging to this prototype.
        int targetBucket = -1;
        for (int b = 0; b < bucketCount; b++)
        {
            if (buckets[b].protoIdx == debugScaleProtoIndex) { targetBucket = b; break; }
        }
        if (targetBucket < 0)
        {
            Debug.LogWarning($"[ScaleDebug] No bucket found for prototype {debugScaleProtoIndex}.");
            return;
        }

        int byteOffset = buckets[targetBucket].instanceOffset * INSTANCE_STRIDE;
        int lod = buckets[targetBucket].lod;

        AsyncGPUReadback.Request(instanceOutputBuffer, INSTANCE_STRIDE, byteOffset, (req) =>
        {
            if (req.hasError) { Debug.LogError("[ScaleDebug] Readback error."); return; }
            var d = req.GetData<uint>();
            if (d.Length < 8) return;

            // InstanceData: worldPos(3f), heightScale(f), packedMeta(u), seed(u), widthScale(f), pad3(u)
            float px = System.BitConverter.Int32BitsToSingle((int)d[0]);
            float py = System.BitConverter.Int32BitsToSingle((int)d[1]);
            float pz = System.BitConverter.Int32BitsToSingle((int)d[2]);
            float hScale = System.BitConverter.Int32BitsToSingle((int)d[3]);
            uint packedMeta = d[4];
            float wScale = System.BitConverter.Int32BitsToSingle((int)d[6]);

            uint protoFromMeta = packedMeta & 0xFFu;
            bool allZero = (d[0] | d[1] | d[2] | d[3] | d[4] | d[6]) == 0u;

            if (allZero)
            {
                Debug.Log($"[ScaleDebug] proto={debugScaleProtoIndex} bucket={targetBucket} LOD{lod}: " +
                    "slot empty this frame (no instances emitted here) — move closer or pick another prototype.");
                return;
            }

            Debug.Log($"[ScaleDebug] proto={debugScaleProtoIndex} (meta says {protoFromMeta}) bucket={targetBucket} LOD{lod}: " +
                $"pos=({px:F1},{py:F1},{pz:F1}) heightScale={hScale:F4} widthScale={wScale:F4}" +
                (Mathf.Abs(hScale) > 100f || Mathf.Abs(wScale) > 100f || hScale <= 0f || wScale <= 0f
                    ? "   <<< OUT OF SANE RANGE" : ""));

            // The instance data is correct — but that only means it was WRITTEN. Whether it's
            // DRAWN depends on the indirect args for this bucket, and on the prototype base
            // scale the shader multiplies in. Read both: an instanceCount of 0 means the draw
            // call renders nothing regardless of buffer contents, and a zero baseScale collapses
            // the geometry to a point while position/scale factors still look perfect.
            var b = buckets[targetBucket];
            AsyncGPUReadback.Request(argsBuffer, 5 * sizeof(uint), b.argsBufferOffset, (areq) =>
            {
                if (areq.hasError) { Debug.LogError("[ScaleDebug] Args readback error."); return; }
                var a = areq.GetData<uint>();
                if (a.Length < 5) return;

                string flag = a[1] == 0 ? "   <<< instanceCount is ZERO — nothing will be drawn" : "";
                Debug.Log($"[ScaleDebug] bucket={targetBucket} ARGS: indexCount={a[0]} instanceCount={a[1]} " +
                    $"startIndex={a[2]} baseVertex={a[3]} startInstance={a[4]}" +
                    $"  (expected indexCount={b.indexCount}, startIndex={b.startIndex}, baseVertex={b.baseVertex}, " +
                    $"submesh={b.submeshIndex}, capacity={b.instanceCapacity}, offset={b.instanceOffset}){flag}");
            });

            AsyncGPUReadback.Request(prototypeScalesBuffer, sizeof(float) * 3, debugScaleProtoIndex * sizeof(float) * 3, (sreq) =>
            {
                if (sreq.hasError) return;
                var sc = sreq.GetData<uint>();
                if (sc.Length < 3) return;
                float sx = System.BitConverter.Int32BitsToSingle((int)sc[0]);
                float sy = System.BitConverter.Int32BitsToSingle((int)sc[1]);
                float sz = System.BitConverter.Int32BitsToSingle((int)sc[2]);
                string zflag = (sx == 0f || sy == 0f || sz == 0f) ? "   <<< ZERO base scale — geometry collapses to a point" : "";
                //Debug.Log($"[ScaleDebug] proto={debugScaleProtoIndex} baseScale=({sx:F4},{sy:F4},{sz:F4}) " +
                    //$"-> finalScale=({sx * wScale:F4},{sy * hScale:F4},{sz * wScale:F4}){zflag}");
            });
        });
    }

    private void CheckBucketOverflow()
    {
        if (atomicCounters == null || bucketCount <= 0 || buckets == null) return;

        AsyncGPUReadback.Request(atomicCounters, (req) =>
        {
            if (req.hasError) return;
            var data = req.GetData<uint>();

            if (_bucketPeakDemand == null || _bucketPeakDemand.Length != bucketCount)
                _bucketPeakDemand = new uint[bucketCount];

            for (int b = 0; b < bucketCount; b++)
            {
                int idx = 1 + b;
                if (idx >= data.Length) break;

                uint wanted = data[idx];
                if (wanted > _bucketPeakDemand[b]) _bucketPeakDemand[b] = wanted;

                int cap = buckets[b].instanceCapacity;
                if (cap <= 0) continue;

                if (wanted > cap)
                {
                    Debug.LogWarning($"[ImpostorRenderer] Bucket {b} OVERFLOW: wanted {wanted}, cap {cap}, " +
                        $"dropped {wanted - cap} (proto {buckets[b].protoIdx}, LOD {buckets[b].lod}, " +
                        $"mesh={SafeName(buckets[b].mesh)}). Dropped instances vary per frame → strobing.");
                }
                else if (wanted > cap * 0.5f)
                {
                    // Early warning while authoring: this spot is close to the ceiling, so a
                    // slightly denser area elsewhere would start strobing.
                    Debug.Log($"[ImpostorRenderer] Bucket {b} at {(wanted * 100f / cap):F0}% capacity " +
                        $"({wanted}/{cap}, LOD {buckets[b].lod}, mesh={SafeName(buckets[b].mesh)}).");
                }
            }
        });

        // Visible-chunk-list overflow. Same InterlockedAdd-then-drop pattern as the bucket
        // overflow above, but for _VisibilityCount/_VisibleChunkList — chunk-level visibility,
        // upstream of everything else in this pipeline. If the true number of frustum-visible
        // chunks exceeds ConflictGridDefines.MaxVisibleChunks, InterlockedAdd's nondeterministic
        // thread order means a DIFFERENT random subset of chunks wins a slot each frame — a
        // chunk can drop in and out of the visible list purely because of which OTHER chunks won
        // the race that frame, not because its own visibility genuinely changed. This looks
        // exactly like "content flickers depending on camera angle while standing still", since
        // panning the camera changes which chunks get tested and in what order threads reach the
        // InterlockedAdd — nothing to do with any one prototype's density or poly count.
        if (visibilityCountBuffer != null)
        {
            AsyncGPUReadback.Request(visibilityCountBuffer, (vreq) =>
            {
                if (vreq.hasError) return;
                var vdata = vreq.GetData<uint>();
                if (vdata.Length == 0) return;

                uint wantedChunks = vdata[0];
                int cap = ConflictGridDefines.MaxVisibleChunks;
                if (wantedChunks > cap)
                {
                    Debug.LogWarning($"[ImpostorRenderer] VISIBLE CHUNK LIST OVERFLOW: wanted {wantedChunks} chunks, " +
                        $"cap {cap} — {wantedChunks - cap} chunk(s) randomly dropped EACH FRAME. Foliage in the " +
                        "dropped chunks flickers in/out based on which OTHER chunks win the race, which can look " +
                        "like it's tied to camera angle even while standing still. This affects EVERY prototype " +
                        "in the dropped chunks, not just one — raise ConflictGridDefines.MaxVisibleChunks.");
                }
                else if (wantedChunks > cap * 0.8f)
                {
                    Debug.Log($"[ImpostorRenderer] Visible chunk list at {(wantedChunks * 100f / cap):F0}% " +
                        $"({wantedChunks}/{cap}).");
                }
            });
        }
    }

    /// <summary>Dumps peak demand per bucket for the whole session. Call after walking the map.</summary>
    [ContextMenu("Dump Bucket Peak Demand")]
    public void DumpBucketPeakDemand()
    {
        if (_bucketPeakDemand == null || buckets == null)
        {
            Debug.LogWarning("[ImpostorRenderer] No peak data yet — enable logBucketOverflow and move around first.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[ImpostorRenderer] Peak instance demand per bucket (session high-water marks):");

        // Sort by how close each got to its ceiling — the ones needing authoring attention first.
        var order = new List<int>();
        for (int b = 0; b < bucketCount; b++) order.Add(b);
        order.Sort((x, y) =>
        {
            float rx = buckets[x].instanceCapacity > 0 ? (float)_bucketPeakDemand[x] / buckets[x].instanceCapacity : 0f;
            float ry = buckets[y].instanceCapacity > 0 ? (float)_bucketPeakDemand[y] / buckets[y].instanceCapacity : 0f;
            return ry.CompareTo(rx);
        });

        foreach (int b in order)
        {
            int cap = buckets[b].instanceCapacity;
            uint peak = _bucketPeakDemand[b];
            if (peak == 0) continue; // never rendered — nothing to report

            float pct = cap > 0 ? peak * 100f / cap : 0f;
            string flag = peak > cap ? "  ** OVERFLOWED **" : (pct > 80f ? "  (near limit)" : "");
            sb.AppendLine($"  bucket {b} LOD{buckets[b].lod} {SafeName(buckets[b].mesh)}: " +
                $"peak {peak}/{cap} ({pct:F0}%){flag}");
        }
        Debug.Log(sb.ToString());
    }


    void Update()
    {
        if (Input.GetKeyDown(KeyCode.P))
        {
            StartCoroutine(DebugInstancePositions());
            StartCoroutine(DebugArgsBuffer());
            StartCoroutine(DebugCounters());
            StartCoroutine(DebugVisibilityAndBlotches());
        }
        if (Input.GetKeyDown(KeyCode.O))
        {
            StartCoroutine(DebugAllBuckets());
            StartCoroutine(DebugBucketMap());
        }
        
        // ADD THIS
        if (Input.GetKeyDown(KeyCode.I))
        {
            // 37 is the bucket index for Grass0 LOD0 from your previous debug report
            StartCoroutine(DebugSpecificBucket(37)); 
        }

        if (freeSlices.Count == 0)
        {
            Debug.LogWarning($"[ImpostorRenderer] LOD0 heightmap slice pool EXHAUSTED " +
                $"(cap={MAX_LOD0_SLICES}, active={slotToSliceMap.Count}). ");
            return;
        }
    }

    

}


// =========================================================================
// CHUNK VISIBILITY DATA — uploaded to GPU for the visibility pass.
//
// Mirrors the CPU-side VisibilitySystem's per-chunk arrays.
// One entry per storage slot: (packed × 6 faces + face).
// =========================================================================

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct ChunkVisibilityData
{
    public float centerDirX;
    public float centerDirY;
    public float centerDirZ;
    public float cosThetaC;
    public float sinThetaC;
    public float boundCenterAlt;
    public float boundHalfH;
    public int chunkPacked;
    public const int Stride = 32; // 7 floats + 1 int = 32 bytes
}

// =========================================================================
// SHADER ID CONSTANTS — centralised string-to-id mapping.
// =========================================================================

public static class ShaderIDs
{
    // Buffers
    public static readonly int ChunkVisibilityBuffer = Shader.PropertyToID("_ChunkVisibilityBuffer");
    public static readonly int VisibleChunkList = Shader.PropertyToID("_VisibleChunkList");
    public static readonly int GlobalBlotchBuffer = Shader.PropertyToID("_GlobalBlotchBuffer");
    public static readonly int ConflictGridArena = Shader.PropertyToID("_ConflictGridArena");
    public static readonly int InstanceOutputBuffer = Shader.PropertyToID("_InstanceOutputBuffer");
    public static readonly int ArgsBuffer = Shader.PropertyToID("_ArgsBuffer");
    public static readonly int GlobalChunkLODBuffer = Shader.PropertyToID("_GlobalChunkLODBuffer");
    public static readonly int AtomicCounters = Shader.PropertyToID("_AtomicCounters");
    public static readonly int BucketLimits = Shader.PropertyToID("_BucketLimits");
    // New buffer IDs
    public static readonly int BucketMapBuffer = Shader.PropertyToID("_BucketMap");
    public static readonly int ProtoFlagsBuffer = Shader.PropertyToID("_ProtoFlags");
    public static readonly int VisibilityCount = Shader.PropertyToID("_VisibilityCount");
    public static readonly int ProtoHeightOffsetBuffer = Shader.PropertyToID("_ProtoHeightOffsetBuffer");
    public static readonly int ProtoBlotchParamsBuffer = Shader.PropertyToID("_ProtoBlotchParamsBuffer");
    public static readonly int ProtoSizeParamsBuffer = Shader.PropertyToID("_ProtoSizeParamsBuffer");
    public static readonly int ProtoSizeModeBuffer = Shader.PropertyToID("_ProtoSizeModeBuffer");
    public static readonly int ProtoColorParamsBuffer = Shader.PropertyToID("_ProtoColorParamsBuffer");

    // Per-frame constants
    public static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
    public static readonly int CameraPos = Shader.PropertyToID("_CameraPos");
    public static readonly int PlayerAltitude = Shader.PropertyToID("_PlayerAltitude");
    public static readonly int SphereCenter = Shader.PropertyToID("_SphereCenter");
    public static readonly int SphereRadius = Shader.PropertyToID("_SphereRadius");

    // LOD config
    public static readonly int DensityMultiplierPerLOD = Shader.PropertyToID("_DensityMultiplierPerLOD");
    public static readonly int WidthMultiplierPerLOD = Shader.PropertyToID("_WidthMultiplierPerLOD");
    public static readonly int WindSpeed = Shader.PropertyToID("_WindSpeed");
    public static readonly int WindFrequency = Shader.PropertyToID("_WindFrequency");
    public static readonly int WindStrength = Shader.PropertyToID("_WindStrength");
    public static readonly int HorizonMargin = Shader.PropertyToID("_HorizonMargin");
    public static readonly int ActiveLOD0SliceMap = Shader.PropertyToID("_ActiveLOD0SliceMap");
    public static readonly int ActiveLOD0ResolutionMap = Shader.PropertyToID("_ActiveLOD0ResolutionMap");
    public static readonly int ActiveLOD0HeightmapArray = Shader.PropertyToID("_ActiveLOD0HeightmapArray");
    public static readonly int ProtoLODModeBuffer      = Shader.PropertyToID("_ProtoLODModeBuffer");
    public static readonly int ProtoLODDistancesBuffer = Shader.PropertyToID("_ProtoLODDistancesBuffer");

    // Counts
    public static readonly int BucketCount = Shader.PropertyToID("_BucketCount");
    public static readonly int MaxLodsPerBucket = Shader.PropertyToID("_MaxLodsPerBucket");
    public static readonly int MaxVisibleChunks = Shader.PropertyToID("_MaxVisibleChunks");
    public static readonly int ArenaUints = Shader.PropertyToID("_ArenaUints");
    public static readonly int SlabHeaderUints = Shader.PropertyToID("_SlabHeaderUints");
    public static readonly int CellsPerUint = Shader.PropertyToID("_CellsPerUint");
    // New constant IDs
    public static readonly int TimeMS = Shader.PropertyToID("_TimeMS");
    public static readonly int TotalBlotchCount = Shader.PropertyToID("_TotalBlotchCount");
    public static readonly int SlabStride = Shader.PropertyToID("_SlabStride");

    public static readonly int ConflictGridResolution = Shader.PropertyToID("_ConflictGridResolution");
    public static readonly int NumBuckets = Shader.PropertyToID("_NumBuckets");
    public static readonly int HalfChunkLinearSize = Shader.PropertyToID("_HalfChunkLinearSize");
    public static readonly int MinX = Shader.PropertyToID("_MinX");
    public static readonly int NumberOfChunks = Shader.PropertyToID("_NumberOfChunks");
    public static readonly int MapsPerFace = Shader.PropertyToID("_MapsPerFace");
    public static readonly int BlotchOffsetBuffer = Shader.PropertyToID("_BlotchOffsetBuffer");
}