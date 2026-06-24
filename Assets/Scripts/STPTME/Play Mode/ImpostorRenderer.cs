using UnityEngine;
using UnityEngine.Rendering;
using System;
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

    [Header("Debug")]
    [SerializeField] private bool debugDrawVisibleChunks = false;
    [SerializeField] private bool debugLogStats = false;

    // ===== CONSTANTS =====
    // Must match GrassSolver.compute and BlotchTypes.cs definitions.

    private const int MAX_LODS_PER_BUCKET = 16;
    private const int MAX_INSTANCES_PER_BUCKET = 65536;
    private const int BLOTCH_STRIDE = 16; // BlotchData is 16 bytes
    private const int INSTANCE_STRIDE = 20; // InstanceData is 20 bytes

    // ===== COMPUTE SHADER KERNEL IDS =====

    private int kernelVisibility;
    private int kernelExpand;
    private int kernelFillArgs;

    // ===== GPU BUFFERS (permanent, allocated once) =====

    // -- Input (read-only on GPU, uploaded once at init) --
    private ComputeBuffer globalBlotchBuffer;           // StructuredBuffer<BlotchData>
    private ComputeBuffer chunkVisibilityBuffer;        // StructuredBuffer<ChunkVisibilityData>

    // -- CPU-side cache of chunk visibility data (for debug hash comparison) --
    private ChunkVisibilityData[] cpuChunkVisibilityCache;

    // -- Arena (read-write, GPU manages internally) --
    private ComputeBuffer conflictGridArena;            // RWStructuredBuffer<uint> — slab arena
    private ComputeBuffer instanceOutputBuffer;         // RWStructuredBuffer<InstanceData>
    // -- Args buffer (structured + indirect, written by compute shader, consumed by DrawMeshInstancedIndirect) --
    private ComputeBuffer argsBuffer;                   // RWStructuredBuffer<uint> + IndirectArguments
    private ComputeBuffer chunkLODBuffer;               // RWStructuredBuffer<uint> — per-chunk LOD data
    private ComputeBuffer atomicCounters;               // RWStructuredBuffer<uint> — [0]=instance count, [1+N]=per-bucket counts

    // -- Bucket map lookup (protoIdx * MAX_LODS_PER_BUCKET + lod) -> bucketIdx --
    private ComputeBuffer bucketMapBuffer;              // StructuredBuffer<uint>

    // -- Temporary (read-write, per-frame) --
    private ComputeBuffer visibleChunkListBuffer;       // RWStructuredBuffer<uint>
    private ComputeBuffer visibilityCountBuffer;        // RWStructuredBuffer<uint> — single counter for visible chunks

    // ===== PER-FRAME STATE =====

    // Debug counters
    private int lastVisibleChunkCount;
    private int lastInstanceCount;
    private int lastBucketCount;

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
        public int protoIdx;
        public int lod;
        public int argsBufferOffset; // in uints from start of argsBuffer
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
        float halfChunkLinearSize,
        Vector3 halfExtent,
        int minX, int numberOfChunks, int mapsPerRow)
    {
        Instance = this;
        this.sphereCenter = sphereCenter;
        this.sphereRadius = sphereRadius;
        this.halfChunkLinearSize = halfChunkLinearSize;
        this.minX = minX;
        this.numberOfChunks = numberOfChunks;
        this.mapsPerRow = mapsPerRow;
        prototypeRegistry = registry;

        planetBounds = new Bounds(sphereCenter, halfExtent * 2f);

        if (!ValidateState()) return;

        // Resolve kernel IDs.
        kernelVisibility = impostorSolverCompute.FindKernel("CSVisibility");
        kernelExpand = impostorSolverCompute.FindKernel("CSExpandBlotches");
        kernelFillArgs = impostorSolverCompute.FindKernel("CSFillArgs");

        bool hasVisibility = kernelVisibility >= 0;
        bool hasExpand = kernelExpand >= 0;
        bool hasFillArgs = kernelFillArgs >= 0;

        if (!hasVisibility || !hasExpand || !hasFillArgs)
        {
            Debug.LogError($"[ImpostorRenderer] Compute shader missing kernels: "
                + $"CSVisibility={hasVisibility} CSExpandBlotches={hasExpand} CSFillArgs={hasFillArgs}");
            return;
        }

        // ---- 1. Global blotch buffer ----
        if (allBlotches != null && allBlotches.Length > 0)
        {
            globalBlotchBuffer = new ComputeBuffer(allBlotches.Length, BLOTCH_STRIDE, ComputeBufferType.Structured);
            globalBlotchBuffer.SetData(allBlotches);
            Debug.Log($"[ImpostorRenderer] GlobalBlotchBuffer uploaded with {globalBlotchBuffer.count} entries ({globalBlotchBuffer.count * BLOTCH_STRIDE} bytes)");
        }
        else
        {
            // Create a minimal buffer so SetBuffer doesn't fail.
            globalBlotchBuffer = new ComputeBuffer(1, BLOTCH_STRIDE, ComputeBufferType.Structured);
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

        // ---- 6. Chunk LOD buffer (per-chunk LOD data for visibility pass) ----
        chunkLODBuffer = new ComputeBuffer(
            ConflictGridDefines.MaxVisibleChunks, sizeof(uint),
            ComputeBufferType.Structured);
        chunkLODBuffer.SetData(new uint[ConflictGridDefines.MaxVisibleChunks]); // init to zeros

        // ---- 7. Atomic counters ----
        // Must match MAX_BUCKET_LODS in the compute shader (16) for correct bucket indexing.
        int maxBucketsTotal = ConflictGridDefines.MaxBuckets * MAX_LODS_PER_BUCKET;
        atomicCounters = new ComputeBuffer(
            1 + maxBucketsTotal,
            sizeof(uint), ComputeBufferType.Structured);

        // ---- 6. Args buffer ----
        bucketCount = BuildBuckets();
        Debug.Log($"[ImpostorRenderer] BUILT {bucketCount} buckets (total entries = {prototypeRegistry.entries?.Length ?? 0})");
        int instanceBufSize = Mathf.Max(bucketCount, 1) * MAX_INSTANCES_PER_BUCKET;
        instanceOutputBuffer = new ComputeBuffer(
        Mathf.Max(instanceBufSize, 1024), INSTANCE_STRIDE, ComputeBufferType.Structured);
        argsBuffer = new ComputeBuffer(
            Mathf.Max(bucketCount, 1) * 5, sizeof(uint),
            ComputeBufferType.IndirectArguments | ComputeBufferType.Structured);
        ResetArgsBuffer();

        // ---- 6b. Bucket map buffer: protoIdx * MAX_LODS_PER_BUCKET + lod -> bucketIdx ----
        uint[] bucketMap = new uint[256 * MAX_LODS_PER_BUCKET];
        for (int i = 0; i < bucketMap.Length; i++) bucketMap[i] = 0xFFFFFFFF;
        for (int b = 0; b < bucketCount; b++)
        {
            bucketMap[buckets[b].protoIdx * MAX_LODS_PER_BUCKET + buckets[b].lod] = (uint)b;
        }
        bucketMapBuffer = new ComputeBuffer(bucketMap.Length, sizeof(uint), ComputeBufferType.Structured);
        bucketMapBuffer.SetData(bucketMap);

        // ---- 7. Bind buffers to compute shader ----
        BindComputeBuffers();

        // ---- 8. Upload per-LOD config ----
        UploadLODConfig();

        Debug.Log($"[ImpostorRenderer] Initialized: {allBlotches?.Length ?? 0} blotches, "
            + $"{chunkData?.Length ?? 0} chunks, {bucketCount} buckets, "
            + $"arena={arenaSize * 4 / 1048576} MB");
        IsInitialized = true;
    }

    // ===== PER-FRAME UPDATE =====

    private void LateUpdate()
    {
        Debug.Log($"[ImpostorRenderer::LateUpdate] Frame {Time.frameCount} — ENTERED. systemEnabled={systemEnabled}, enableRendering={enableRendering}, IsInitialized={IsInitialized}");

        if (!systemEnabled || !enableRendering) { Debug.Log("[ImpostorRenderer::LateUpdate] EXIT: systemEnabled or enableRendering is false"); return; }
        if (!ValidateState()) { Debug.Log("[ImpostorRenderer::LateUpdate] EXIT: ValidateState returned false"); return; }

        // Guard against double-fire via PrepareFrame + Unity LateUpdate event.
        int curFrame = Time.frameCount;
        if (curFrame == lastFrameDrawn) { Debug.Log("[ImpostorRenderer::LateUpdate] EXIT: already drawn this frame via PrepareFrame"); return; }
        lastFrameDrawn = curFrame;

        // Step 0: Reset per-frame counters to 0
        if (visibilityCountBuffer != null)
            visibilityCountBuffer.SetData(new uint[] { 0 });
        if (atomicCounters != null)
        {
            var zeroArray = new uint[1 + ConflictGridDefines.MaxBuckets * MAX_LODS_PER_BUCKET];
            atomicCounters.SetData(zeroArray);
        }
        ResetArgsBuffer();

        // DEBUG: Log buffer sizes
        if (debugLogStats)
        {
            int blotchCount = globalBlotchBuffer?.count ?? 0;
            int chunksCount = chunkVisibilityBuffer?.count ?? 0;
            Debug.Log($"[ImpostorRenderer] Frame {Time.frameCount}: {blotchCount} blotches, {chunksCount} chunks, {bucketCount} buckets");
        }

        // Upload time in milliseconds (used by compute shader for slab timestamping)
        impostorSolverCompute.SetInt(ShaderIDs.TimeMS, (int)(Time.time * 1000f));

        // Step 1: Upload per-frame camera data (~112 bytes total).
        UploadCameraData();

        // Step 2: Dispatch visibility pass.
        int chunkCount = chunkVisibilityBuffer?.count ?? 0;
        if (chunkCount > 0)
        {
            int vGroups = (chunkCount + 63) / 64;
            impostorSolverCompute.Dispatch(kernelVisibility, vGroups, 1, 1);
            if (debugLogStats)
                Debug.Log($"[ImpostorRenderer] Dispatched CSVisibility: {vGroups} groups");
        }

        // [DEBUG] Read back visibility count
        var visCountData = new uint[1];
        if (visibilityCountBuffer != null)
        {
            visibilityCountBuffer.GetData(visCountData);
            Debug.Log($"[ImpostorRenderer::DEBUG] After CSVisibility: _VisibilityCount[0] = {visCountData[0]}");
        }

        // [DEBUG] Compare GPU visible chunks vs CPU VisibilitySystem
        if (visibilityCountBuffer != null && visibleChunkListBuffer != null && cpuChunkVisibilityCache != null)
        {
            int gpuCount = System.Math.Min((int)visCountData[0], ConflictGridDefines.MaxVisibleChunks);
            if (gpuCount > 0)
            {
                var gpuVisible = new uint[System.Math.Min(gpuCount, 1024)];
                visibleChunkListBuffer.GetData(gpuVisible, 0, 0, gpuVisible.Length);

                // Hash: order-independent XOR sum, plus count
                uint gpuHash = (uint)gpuCount;
                for (int gi = 0; gi < gpuVisible.Length; gi++) gpuHash ^= gpuVisible[gi];

                // CPU side: iterate all storage slots, call ClassifyChunk
                int cpuCount = 0;
                uint cpuHash = 0;
                uint cpuFirstPacked = 0;
                bool visReady = VisibilitySystem.IsReady;
                var visInst = VisibilitySystem.Instance;
                int maxCpuVisible = ConflictGridDefines.MaxVisibleChunks;
                if (visReady && visInst != null)
                {
                    for (int si = 0; si < cpuChunkVisibilityCache.Length && cpuCount < maxCpuVisible; si++)
                    {
                        if (visInst.ClassifyChunk(si) == VisibilitySystem.ChunkVisibility.Visible)
                        {
                            uint packed = (uint)cpuChunkVisibilityCache[si].chunkPacked;
                            cpuHash ^= packed;
                            if (cpuCount == 0) cpuFirstPacked = packed;
                            cpuCount++;
                        }
                    }
                }

                bool match = (gpuCount == cpuCount && gpuHash == cpuHash);

                // [DEBUG] Count map regions in GPU visible list — blotches exist in maps -3..+1 on BOTH axes
                int[] gpuMapCounts = new int[256];
                int blotchableMapChunks = 0;
                for (int gi = 0; gi < gpuVisible.Length; gi++)
                {
                    int mx = (sbyte)((gpuVisible[gi] >> 24) & 0xFF);
                    int my = (sbyte)((gpuVisible[gi] >> 16) & 0xFF);
                    gpuMapCounts[System.Math.Clamp(mx + 128, 0, 255)]++;
                    if (mx >= -3 && mx <= 1 && my >= -3 && my <= 1) blotchableMapChunks++;
                }
                string gpuMapSummary = "maps: ";
                for (int mi = 0; mi < 256; mi++)
                    if (gpuMapCounts[mi] > 0) gpuMapSummary += $"{(sbyte)(mi-128)}({gpuMapCounts[mi]}) ";
                Debug.Log($"[ImpostorRenderer::DEBUG] GPU map X-distribution — {gpuMapSummary}  blotchable={blotchableMapChunks}/{gpuVisible.Length}");

                Debug.Log($"[ImpostorRenderer::DEBUG] Visibility hash — GPU: count={gpuCount} hash=0x{gpuHash:X8} first=0x{gpuVisible[0]:X8}  CPU: count={cpuCount} hash=0x{cpuHash:X8} first=0x{cpuFirstPacked:X8}  MATCH={(match ? "YES" : "NO")}");
            }
        }

        // [DEBUG] Read back first 3 visible chunk packed values + first 3 blotch packed values
        if (visibleChunkListBuffer != null && visCountData[0] > 0)
        {
            var chunkData = new uint[System.Math.Min(3, (int)visCountData[0])];
            visibleChunkListBuffer.GetData(chunkData, 0, 0, chunkData.Length);
            for (int di = 0; di < chunkData.Length; di++)
                Debug.Log($"[ImpostorRenderer::DEBUG] VisibleChunk[{di}] = 0x{chunkData[di]:X8}");
        }
        if (globalBlotchBuffer != null)
        {
            // Sample blotches spread across the buffer to see map-coordinate range
            var singleBlotch = new CustomTypes.BlotchData[1];
            int[] samplePositions = { 0, 50000, 100000, 150000, 200000 };
            for (int si = 0; si < 5; si++)
            {
                if (samplePositions[si] < globalBlotchBuffer.count)
                {
                    globalBlotchBuffer.GetData(singleBlotch, 0, samplePositions[si], 1);
                    var b = singleBlotch[0];
                    int mapX = (sbyte)((b.chunkPacked >> 24) & 0xFF);
                    int mapY = (sbyte)((b.chunkPacked >> 16) & 0xFF);
                    int ckX = (sbyte)((b.chunkPacked >> 8) & 0xFF);
                    int ckY = (sbyte)(b.chunkPacked & 0xFF);
                    Debug.Log($"[ImpostorRenderer::DEBUG] Blotch[sample={samplePositions[si]}] chunkPacked=0x{b.chunkPacked:X8} map=({mapX},{mapY}) chunk=({ckX},{ckY}) proto={(b.packedMeta >> 8) & 0xFF} face={b.packedMeta & 0xFF}");
                }
            }
        }

        // Step 3: Dispatch blotch expansion.
        // Thread groups = number of visible chunks (capped by MaxVisibleChunks).
        if (globalBlotchBuffer != null)
        {
            impostorSolverCompute.Dispatch(kernelExpand,
                ConflictGridDefines.MaxVisibleChunks, 1, 1);
            if (debugLogStats)
                Debug.Log($"[ImpostorRenderer] Dispatched CSExpandBlotches: {ConflictGridDefines.MaxVisibleChunks} groups");
        }

        // [DEBUG] Read back atomic counters after expand
        if (atomicCounters != null && bucketCount > 0)
        {
            var counterData = new uint[1 + bucketCount];
            atomicCounters.GetData(counterData);
            uint totalInstances = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                totalInstances += counterData[1 + b];
            }
            uint firstBucketCount = bucketCount > 0 ? counterData[1] : 0;
            Debug.Log($"[ImpostorRenderer::DEBUG] After CSExpandBlotches: totalInstances={totalInstances}, firstBucket={firstBucketCount}, atomic[0]={counterData[0]}");
        }

        // Step 4: Dispatch fill-args.
        if (bucketCount > 0)
        {
            int aGroups = (bucketCount + 63) / 64;
            impostorSolverCompute.Dispatch(kernelFillArgs, aGroups, 1, 1);
            if (debugLogStats)
                Debug.Log($"[ImpostorRenderer] Dispatched CSFillArgs: {aGroups} groups");
        }

        // Step 5: Issue indirect draw calls.
        Debug.Log($"[ImpostorRenderer::LateUpdate] Frame {Time.frameCount} — dispatching draws, bucketCount={bucketCount}");
        DrawIndirect();
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
    }

    // ===== INDIRECT DRAW =====

    private MaterialPropertyBlock drawProps;

    private void DrawIndirect()
    {
        if (drawProps == null)
        {
            drawProps = new MaterialPropertyBlock();
            drawProps.SetBuffer(ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        }

        var shadowMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;

        // [DEBUG] Log if DrawIndirect is reached at all
        Debug.Log($"[ImpostorRenderer::DrawIndirect] bucketCount={bucketCount}, planetBounds=({planetBounds.center}, {planetBounds.extents})");

        for (int i = 0; i < bucketCount; i++)
        {
            ref var bucket = ref buckets[i];
            if (bucket.mesh == null || bucket.material == null)
            {
                Debug.LogWarning($"[ImpostorRenderer::DrawIndirect] Bucket {i} skipped: mesh={(bucket.mesh==null?"null":"ok")}, material={(bucket.material==null?"null":"ok")}");
                continue;
            }

            // [DEBUG] Read back the instance count from args buffer for the first 3 buckets only
            if (i < 3)
            {
                var debugArgs = new uint[5];
                argsBuffer.GetData(debugArgs, 0, bucket.argsBufferOffset / sizeof(uint), 5);
                Debug.Log($"[ImpostorRenderer::DrawIndirect] Bucket {i}: mesh={bucket.mesh.name}, mat={bucket.material.name}, indexCount={debugArgs[0]}, instanceCount={debugArgs[1]}, startInstance={debugArgs[4]}");
            }

            Graphics.DrawMeshInstancedIndirect(
                bucket.mesh, 0, bucket.material,
                planetBounds,
                argsBuffer, bucket.argsBufferOffset,
                drawProps, shadowMode, bucket.receiveShadows, 0, null);
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
        int totalSlots = protoCount * MAX_LODS_PER_BUCKET;
        var bucketList = new List<IndirectBucket>(totalSlots);

        for (int pi = 0; pi < protoCount; pi++)
        {
            var entry = entries[pi];
            if (entry == null || !entry.shouldInstance) continue;

            for (int lod = 0; lod < MAX_LODS_PER_BUCKET; lod++)
            {
                Mesh mesh = entry.GetMeshForLOD(lod);
                if (mesh == null) continue;
                if (entry.material == null) continue;

                // Compute the offset for this bucket based on its position in the final bucket array.
                int bucketIdx = bucketList.Count; // zero‑based index of the bucket we are about to add
                bucketList.Add(new IndirectBucket
                {
                    mesh = mesh,
                    material = entry.material,
                    shadowMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = receiveShadows,
                    indexCount = (int)mesh.GetIndexCount(0),
                    startIndex = 0,
                    baseVertex = 0,
                    // argsBufferOffset is a byte offset into the args buffer. Each entry is 5 uints (20 bytes).
                    argsBufferOffset = bucketIdx * 5 * sizeof(uint),
                    protoIdx = pi,
                    lod = lod
                });
            }
        }

        buckets = bucketList.ToArray();
        return buckets.Length;
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
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.ChunkLODBuffer, chunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        // Expand kernel.
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.GlobalBlotchBuffer, globalBlotchBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ConflictGridArena, conflictGridArena);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.VisibleChunkList, visibleChunkListBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ChunkLODBuffer, chunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.AtomicCounters, atomicCounters);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        // Fill-args kernel.
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.ArgsBuffer, argsBuffer);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.AtomicCounters, atomicCounters);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

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
        impostorSolverCompute.SetInt(ShaderIDs.DefaultChunkLOD, 1);
        impostorSolverCompute.SetInt(ShaderIDs.TotalBlotchCount, globalBlotchBuffer?.count ?? 0);
        impostorSolverCompute.SetInt(Shader.PropertyToID("_MaxInstancesPerBucket"), MAX_INSTANCES_PER_BUCKET);
        impostorSolverCompute.SetVector(ShaderIDs.SphereCenter, new Vector4(sphereCenter.x, sphereCenter.y, sphereCenter.z, 0f));
        impostorSolverCompute.SetFloat(ShaderIDs.SphereRadius, sphereRadius);
        impostorSolverCompute.SetFloat(ShaderIDs.HalfChunkLinearSize, halfChunkLinearSize);
        // SlabStride is the stride in uints for each slab (header + cells). Use ConflictGridDefines.SlabHeaderUints as base.
        int slabStrideUints = ConflictGridDefines.SlabHeaderUints +
            (ConflictGridDefines.ResolutionPerLOD[0] * ConflictGridDefines.ResolutionPerLOD[0] + ConflictGridDefines.CellsPerUint - 1) / ConflictGridDefines.CellsPerUint;
        impostorSolverCompute.SetInt(ShaderIDs.SlabStride, slabStrideUints);
        impostorSolverCompute.SetInt(ShaderIDs.NumBuckets, bucketCount);

        // Resolution per LOD array (up to 8 entries)
        var resArray = ConflictGridDefines.ResolutionPerLOD;
        impostorSolverCompute.SetInts(ShaderIDs.ResolutionPerLOD, resArray);
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
        chunkLODBuffer?.Release();            chunkLODBuffer = null;
        atomicCounters?.Release();            atomicCounters = null;
        bucketMapBuffer?.Release();           bucketMapBuffer = null;
    }

    // ===== HELPERS (stubs — to be wired to ChunkManager data) =====

    private static Camera GetActiveCamera()
    {
        Camera cam = VisibilitySystem.IsReady ? VisibilitySystem.Instance?.ActiveCamera : null;
        if (cam != null && cam.isActiveAndEnabled) return cam;
        return Camera.main;
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
    public static readonly int ChunkLODBuffer = Shader.PropertyToID("_ChunkLODBuffer");
    public static readonly int AtomicCounters = Shader.PropertyToID("_AtomicCounters");
    // New buffer IDs
    public static readonly int BucketMapBuffer = Shader.PropertyToID("_BucketMap");
    public static readonly int VisibilityCount = Shader.PropertyToID("_VisibilityCount");

    // Per-frame constants
    public static readonly int FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
    public static readonly int CameraPos = Shader.PropertyToID("_CameraPos");
    public static readonly int PlayerAltitude = Shader.PropertyToID("_PlayerAltitude");
    public static readonly int SphereCenter = Shader.PropertyToID("_SphereCenter");
    public static readonly int SphereRadius = Shader.PropertyToID("_SphereRadius");
    public static readonly int DefaultChunkLOD = Shader.PropertyToID("_DefaultChunkLOD");

    // LOD config
    public static readonly int DensityMultiplierPerLOD = Shader.PropertyToID("_DensityMultiplierPerLOD");
    public static readonly int WidthMultiplierPerLOD = Shader.PropertyToID("_WidthMultiplierPerLOD");
    public static readonly int WindSpeed = Shader.PropertyToID("_WindSpeed");
    public static readonly int WindFrequency = Shader.PropertyToID("_WindFrequency");
    public static readonly int WindStrength = Shader.PropertyToID("_WindStrength");
    public static readonly int HorizonMargin = Shader.PropertyToID("_HorizonMargin");

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
    public static readonly int ResolutionPerLOD = Shader.PropertyToID("_ResolutionPerLOD");
    public static readonly int NumBuckets = Shader.PropertyToID("_NumBuckets");
    public static readonly int HalfChunkLinearSize = Shader.PropertyToID("_HalfChunkLinearSize");
    public static readonly int MinX = Shader.PropertyToID("_MinX");
    public static readonly int NumberOfChunks = Shader.PropertyToID("_NumberOfChunks");
    public static readonly int MapsPerFace = Shader.PropertyToID("_MapsPerFace");
}