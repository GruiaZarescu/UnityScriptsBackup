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

    [Header("Debug")]
    [SerializeField] private bool debugDrawVisibleChunks = false;
    [SerializeField] private bool debugLogStats = false;

    // ===== CONSTANTS =====
    // Must match GrassSolver.compute and BlotchTypes.cs definitions.

    private const int MAX_LODS_PER_BUCKET = 16;
    private const int MAX_INSTANCES_PER_BUCKET = 65536;
    private const int BLOTCH_STRIDE = 16; // BlotchData is 16 bytes
    private const int INSTANCE_STRIDE = 32; // InstanceData is 32 bytes on GPU

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

    private ComputeBuffer globalChunkLODBuffer;
    private uint[] cpuChunkLODs;
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

    private Texture2DArray globalHeightmapArray;
    private int terrainGridSize;

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
        kernelClear = impostorSolverCompute.FindKernel("CSClearCounters");

        bool hasVisibility = kernelVisibility >= 0;
        bool hasExpand = kernelExpand >= 0;
        bool hasFillArgs = kernelFillArgs >= 0;

        if (!hasVisibility || !hasExpand || !hasFillArgs)
        {
            Debug.LogError($"[ImpostorRenderer] Compute shader missing kernels: "
                + $"CSVisibility={hasVisibility} CSExpandBlotches={hasExpand} CSFillArgs={hasFillArgs}");
            return;
        }

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

        // ---- 6c. Prototype scales buffer
        Vector3[] scales = new Vector3[prototypeRegistry.entries.Length];
        for (int i = 0; i < scales.Length; i++)
        {
            if (prototypeRegistry.entries[i] != null && prototypeRegistry.entries[i].sourcePrefab != null)
                scales[i] = prototypeRegistry.entries[i].sourcePrefab.transform.localScale;
            else
                scales[i] = Vector3.one;
        }
        prototypeScalesBuffer = new ComputeBuffer(scales.Length, sizeof(float) * 3);
        prototypeScalesBuffer.SetData(scales);

        // ---- 7. Bind buffers to compute shader ----
        BindComputeBuffers();

        // ---- 8. Upload per-LOD config ----
        UploadLODConfig();


        IsInitialized = true;
    }

    // ===== PER-FRAME UPDATE =====

    private void LateUpdate()
    {
        if (!systemEnabled || !enableRendering) return;
        if (!ValidateState()) return;

        int curFrame = Time.frameCount;
        if (curFrame == lastFrameDrawn) return;
        lastFrameDrawn = curFrame;

        // Upload camera data first (needed by all kernels)
        impostorSolverCompute.SetInt(ShaderIDs.TimeMS, (int)(Time.time * 1000f));
        UploadCameraData();

        // Upload LODs (CPU → GPU, once per frame)
        if (globalChunkLODBuffer != null && cpuChunkLODs != null)
        {
            globalChunkLODBuffer.SetData(cpuChunkLODs);
            lodsDirty = false;
        }

        // NOW dispatch in order: Clear → Visibility → Expand → FillArgs
        int clearGroups = (bucketCount + 1 + 63) / 64;
        impostorSolverCompute.Dispatch(kernelClear, clearGroups, 1, 1);

        int chunkCount = chunkVisibilityBuffer?.count ?? 0;
        if (chunkCount > 0)
        {
            int vGroups = (chunkCount + 63) / 64;
            impostorSolverCompute.Dispatch(kernelVisibility, vGroups, 1, 1);
        }

        if (globalBlotchBuffer != null)
        {
            impostorSolverCompute.Dispatch(kernelExpand, ConflictGridDefines.MaxVisibleChunks, 1, 1);
        }

        if (bucketCount > 0)
        {
            int aGroups = (bucketCount + 63) / 64;
            impostorSolverCompute.Dispatch(kernelFillArgs, aGroups, 1, 1);
        }

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
            drawProps = new MaterialPropertyBlock();

        drawProps.Clear();
        drawProps.SetBuffer(ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        
        if (prototypeScalesBuffer != null)
            drawProps.SetBuffer("_PrototypeScales", prototypeScalesBuffer);

        // FIX: Set _SphereCenter BEFORE the loop so the vertex shader receives it!
        drawProps.SetVector("_SphereCenter", sphereCenter);

        var shadowMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;

        for (int i = 0; i < bucketCount; i++)
        {
            ref var bucket = ref buckets[i];
            if (bucket.mesh == null || bucket.material == null) continue;

            drawProps.SetFloat("_InstanceOffset", i * MAX_INSTANCES_PER_BUCKET);

            Graphics.DrawMeshInstancedIndirect(
                bucket.mesh, 0, bucket.material,
                planetBounds,
                argsBuffer, bucket.argsBufferOffset,
                drawProps, shadowMode, bucket.receiveShadows, 0, null);
        }
    }

    public void SetGlobalHeightmap(Texture2DArray heightmapArray, int gridSize)
    {
        this.globalHeightmapArray = heightmapArray;
        this.terrainGridSize = gridSize;
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
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        
        // Bind LOD buffer to BOTH kernels!
        impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.GlobalChunkLODBuffer, globalChunkLODBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.GlobalChunkLODBuffer, globalChunkLODBuffer);

        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelVisibility, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        // Expand kernel.
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.GlobalBlotchBuffer, globalBlotchBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.ConflictGridArena, conflictGridArena);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.VisibleChunkList, visibleChunkListBuffer);
         impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.VisibilityCount, visibilityCountBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.BlotchOffsetBuffer, blotchOffsetBuffer);
        impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.AtomicCounters, atomicCounters);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelExpand, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        // Fill-args kernel.
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.InstanceOutputBuffer, instanceOutputBuffer);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.ArgsBuffer, argsBuffer);
        impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.AtomicCounters, atomicCounters);
        if (bucketMapBuffer != null)
            impostorSolverCompute.SetBuffer(kernelFillArgs, ShaderIDs.BucketMapBuffer, bucketMapBuffer);

        //Clear kernel.
        impostorSolverCompute.SetBuffer(kernelClear, ShaderIDs.AtomicCounters, atomicCounters);
        impostorSolverCompute.SetBuffer(kernelClear, ShaderIDs.VisibilityCount, visibilityCountBuffer);

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
        impostorSolverCompute.SetInt(Shader.PropertyToID("_MaxInstancesPerBucket"), MAX_INSTANCES_PER_BUCKET);
        impostorSolverCompute.SetVector(ShaderIDs.SphereCenter, new Vector4(sphereCenter.x, sphereCenter.y, sphereCenter.z, 0f));
        impostorSolverCompute.SetFloat(ShaderIDs.SphereRadius, sphereRadius);
        impostorSolverCompute.SetFloat(ShaderIDs.HalfChunkLinearSize, halfChunkLinearSize);
        
        int slabStrideUints = ConflictGridDefines.SlabHeaderUints +
            (ConflictGridDefines.ResolutionPerLOD[0] * ConflictGridDefines.ResolutionPerLOD[0] + ConflictGridDefines.CellsPerUint - 1) / ConflictGridDefines.CellsPerUint;
        impostorSolverCompute.SetInt(ShaderIDs.SlabStride, slabStrideUints);
        impostorSolverCompute.SetInt(ShaderIDs.NumBuckets, bucketCount);

        var resArray = ConflictGridDefines.ResolutionPerLOD;
        impostorSolverCompute.SetInts(ShaderIDs.ResolutionPerLOD, resArray);

        if (globalHeightmapArray != null)
        {
            impostorSolverCompute.SetTexture(kernelExpand, "_GlobalHeightmapArray", globalHeightmapArray);
            impostorSolverCompute.SetInt("_TerrainGridSize", terrainGridSize);
        }
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
        blotchOffsetBuffer?.Release();        blotchOffsetBuffer = null;
        prototypeScalesBuffer?.Release();     prototypeScalesBuffer = null;
        globalChunkLODBuffer?.Release();      globalChunkLODBuffer = null;
    }

    // ===== HELPERS (stubs — to be wired to ChunkManager data) =====

    private static Camera GetActiveCamera()
    {
        Camera cam = VisibilitySystem.IsReady ? VisibilitySystem.Instance?.ActiveCamera : null;
        if (cam != null && cam.isActiveAndEnabled) return cam;
        return Camera.main;
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


    void Update()
    {
        if (Input.GetKeyDown(KeyCode.P))
        {
            StartCoroutine(DebugInstancePositions());
            StartCoroutine(DebugArgsBuffer());
            StartCoroutine(DebugCounters());
            StartCoroutine(DebugVisibilityAndBlotches());
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
    // New buffer IDs
    public static readonly int BucketMapBuffer = Shader.PropertyToID("_BucketMap");
    public static readonly int VisibilityCount = Shader.PropertyToID("_VisibilityCount");

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
    public static readonly int BlotchOffsetBuffer = Shader.PropertyToID("_BlotchOffsetBuffer");
}