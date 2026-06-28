using System;
using System.Collections.Generic;
using UnityEngine;
using CustomTypes;
using STPTME.MapObjects;
using UnityEngine.Rendering;

/// <summary>
/// Central orchestrator for all map object instantiation (LOD0 GameObjects + GPU buffer prep).
/// 
/// Unified pipeline handling both data sources:
/// 1. Cell Object Data (from CellObjectReader)
/// 2. Blob Data (from CellBlotchReader)
/// 
/// Decision logic per data item:
/// - LOD0 + !instanceAlways → Spawn GameObject (via MapPrefabStreamer)
/// - LOD0 + instanceAlways → Add to GPU buffer (blobs only)
/// - LOD1+ + shouldInstance → Add to GPU buffer
/// - LOD1+ + !shouldInstance → Spawn GameObject (via MapPrefabStreamer)
/// 
/// GPU-eligible data is accumulated and passed to ImpostorRenderer at startup.
/// </summary>
public class ChunkObjectLoader : MonoBehaviour
{
    [SerializeField, Tooltip("Unified registry for instance rules and prototype data.")]
    private MapObjectPrototypeRegistry prototypeRegistry;

    [SerializeField, Tooltip("Handles LOD0 object spawning and pooling.")]
    private MapPrefabStreamer prefabStreamer;

    // ── Data readers ──────────────────────────────────────────────────────────
    private CellObjectReader _cellObjectReader;
    private int _numberOfChunks;
    private int _heightmapSubdivisions;
    private sbyte _minX;

    // ── GPU buffer accumulation ───────────────────────────────────────────────
    // Blobs that will be GPU instanced (LOD1+ or LOD0 with instanceAlways)
    private List<BlotchData> _gpuBlotches;

    // ── Runtime state ─────────────────────────────────────────────────────────
    private bool _initialized = false;
    private bool _gpuBufferSubmitted = false;
    
    // ── Track processed chunks to avoid re-spawning ──────────────────────────
    // Key format: (packed, face, lod) encoded as single long
    private HashSet<long> _processedChunks = new HashSet<long>();

    // ===== LIFECYCLE =====

    private void Awake()
    {
        _gpuBlotches = new List<BlotchData>();
    }

    private void Start()
    {
        Debug.Log("[ChunkObjectLoader::Start] Called");
        var settings = TerrainManagementSettings.Instance;
        _numberOfChunks = settings.numberOfChunks;
        _heightmapSubdivisions = settings.heightmapSubdivisions;
        _minX = settings.minX;
        Debug.Log($"[ChunkObjectLoader::Start] Loaded settings: chunks={_numberOfChunks}, subdivisions={_heightmapSubdivisions}, minX={_minX}");

        // Initialize data readers
        _cellObjectReader = new CellObjectReader();
        _cellObjectReader.Init(1 << _heightmapSubdivisions, _minX);

        // Initialize global blob cache (CellBlotchReader is static, so use CellBlotchQuery static methods)
        string cellsFolder = System.IO.Path.Combine(UnityEngine.Application.streamingAssetsPath, "MapAssets", "Cells");
        CellBlotchQuery.Initialize(cellsFolder, 1 << _heightmapSubdivisions, _minX);
        
        // Debug: If current chunk available, check if it has blobs
        var chunkMgr = ChunkManager.Instance;
        if (chunkMgr != null)
        {
            // We don't have direct access to current chunk yet, but we'll check during first HandleChunkCreated call
            //Debug.Log("[ChunkObjectLoader::Start] ChunkManager found - will debug blob queries as chunks are created");
        }

        // Validate required components
        if (prototypeRegistry == null)
        {
            Debug.LogError("[ChunkObjectLoader] No prototypeRegistry assigned.", this);
            enabled = false;
            return;
        }

        if (prefabStreamer == null)
        {
            Debug.LogError("[ChunkObjectLoader] No MapPrefabStreamer assigned.", this);
            enabled = false;
            return;
        }

        // Subscribe to chunk lifecycle events
        var chunkRegistry = ChunkManager.Instance?.chunkRegistry;
        if (chunkRegistry == null)
        {
            Debug.LogError("[ChunkObjectLoader] ChunkManager or ChunkRegistry not found.", this);
            enabled = false;
            return;
        }

        chunkRegistry.OnChunkCreated += HandleChunkCreated;
        chunkRegistry.OnChunkRemoved += HandleChunkRemoved;

        _initialized = true;
        //Debug.Log($"[ChunkObjectLoader] Initialized. Registry has {chunkRegistry.ToString() != null} - Awaiting chunks...");
    }

    private void OnDestroy()
    {
        var cr = ChunkManager.Instance?.chunkRegistry;
        if (cr != null)
        {
            cr.OnChunkCreated -= HandleChunkCreated;
            cr.OnChunkRemoved -= HandleChunkRemoved;
        }
    }

    // ===== CHUNK LIFECYCLE HANDLERS =====

    /// <summary>
    /// When a chunk is created:
    /// 1. Load cell objects from CellObjectReader
    /// 2. Load blobs from CellBlotchReader
    /// 3. Route each data item based on registry rules
    /// 4. Spawn GameObjects or accumulate GPU blobs
    /// </summary>
    
    private int debugCallCountHandleChunkCreated = 0; 
    private void HandleChunkCreated(int packed, FaceId face, byte lod)
    {
        if (!_initialized) 
        {
            Debug.LogWarning($"[ChunkObjectLoader::HandleChunkCreated] Called but not initialized!");
            return;
        }

        //Debug.Log($"[ChunkObjectLoader::HandleChunkCreated] Chunk packed=0x{packed:X8} ({packed}) face={face} LOD={lod}");

        // Skip if this chunk has already been processed
        long chunkKey = EncodeChunkKey(packed, face, lod);
        if (_processedChunks.Contains(chunkKey))
        {
            Debug.Log($"[ChunkObjectLoader] Chunk already processed (skipping duplicate): 0x{packed:X8} face={face} lod={lod}");
            return;
        }
        _processedChunks.Add(chunkKey);

        // ── Step 1: Process cell objects ──
        ProcessCellObjects(packed, face, lod);

        // ── Step 2: Process blobs ──
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        Debug.Log($"[ChunkObjectLoader] Processing blobs for chunk 0x{packed:X8} face={face} LOD={lod}, unopacked = ({mapX},{mapY},{chunkX},{chunkY})");
        ProcessBlobs(packed, face, lod);

        // ── Step 3: Submit GPU buffer if this is the initial load ──
        // (First chunk creation triggers final setup)
        if (!_gpuBufferSubmitted && _gpuBlotches.Count > 0)
        {
            SubmitGPUBuffer();
        }
    }

    private void HandleChunkRemoved(int packed, FaceId face, byte lod)
    {
        if (!_initialized) return;

        // Remove from processed set so it can be re-processed if recreated
        long chunkKey = EncodeChunkKey(packed, face, lod);
        _processedChunks.Remove(chunkKey);

        // Despawn GameObjects for this chunk
        prefabStreamer.DespawnChunkObjects(packed, face, lod);

        // TODO: If dynamic GPU buffer removal is needed, remove blobs here
        // For now, GPU blobs persist (they're already uploaded)
    }

    // ===== ROUTING LOGIC =====

    private void ProcessCellObjects(int packed, FaceId face, byte lod)
    {
        var segment = _cellObjectReader.GetObjectsForChunk(packed, face, _numberOfChunks, lod);
        if (segment.Count == 0) return;

        // Get the chunk's GameObject for parenting
        var chunkRegistry = ChunkManager.Instance?.chunkRegistry;
        Transform parentTransform = null;
        if (chunkRegistry != null && chunkRegistry.TryGetChunkGameObject(packed, face, lod, out GameObject chunkGO))
            parentTransform = chunkGO.transform;

        Debug.Log($"[ChunkObjectLoader] Processing {segment.Count} cell objects for chunk {packed} LOD {lod}");

        foreach (var cellObj in segment)
        {
            var entry = prototypeRegistry.GetEntry(cellObj.prototypeIndex);
            if (entry == null)
            {
                Debug.LogWarning($"[ChunkObjectLoader] No registry entry for prototypeIndex={cellObj.prototypeIndex}");
                continue;
            }

            // Decide: GPU instance or spawn GameObject?
            if (entry.IsInstancedAtLOD(lod))
            {
                // Convert to GPU buffer format (if we support cell objects in GPU buffer)
                // For now, cell objects are only LOD0 objects, which shouldn't be GPU instanced
                // (unless instanceAlways, but that requires a GameObject with colliders anyway)
                Debug.LogWarning($"[ChunkObjectLoader] Cell object marked for instancing (should be rare): proto {cellObj.prototypeIndex} at LOD {lod}");
                continue;
            }

            // Spawn as GameObject
            var settings = TerrainManagementSettings.Instance;
            var spawned = prefabStreamer.SpawnObject(
                cellObj.prototypeIndex,
                parentTransform,
                packed,
                face,
                lod,
                cellObj.position,
                cellObj.rotation.eulerAngles.y,
                cellObj.scale.magnitude,
                (uint)System.DateTime.Now.GetHashCode(), // Use timestamp-based seed
                settings.sphereCenter);
        }
    }

    private void ProcessBlobs(int packed, FaceId face, byte lod)
    {
        var chunkRegistry = ChunkManager.Instance?.chunkRegistry;
        Transform parentTransform = null;
        if (chunkRegistry != null && chunkRegistry.TryGetChunkGameObject(packed, face, lod, out GameObject chunkGO))
            parentTransform = chunkGO.transform;

        var blobs = CellBlotchQuery.GetBlobsForChunk(packed);
        int spawnedCount = 0;

        foreach (var blob in blobs)
        {
            var entry = prototypeRegistry.GetEntry(blob.PrototypeIndex);
            if (entry == null) continue;

            if (entry.ShouldSpawnAsPrefabAtLOD(lod))
            {
                var settings = TerrainManagementSettings.Instance;
                // CPU calculates exact world position for prefab
                var worldPos = ChunkManager.Instance.GetBlotchWorldPosition(blob, ChunkManager.Instance.GetFaceWorldSize());    
                
                Vector3 normal = (worldPos - settings.sphereCenter).normalized;
                worldPos += normal * entry.heightOffset; 

                prefabStreamer.SpawnObject(
                    blob.PrototypeIndex,
                    parentTransform,
                    packed,
                    face,
                    lod,
                    worldPos,
                    0f,  
                    1f,  
                    blob.Seed,
                    settings.sphereCenter);
                spawnedCount++;
            }
        }
    }

    // ===== GPU BUFFER SUBMISSION =====

    private void SubmitGPUBuffer()
    {
        if (_gpuBufferSubmitted) return;

        Debug.Log($"[ChunkObjectLoader] Submitting {_gpuBlotches.Count} blobs to GPU buffer...");

        var impostorRenderer = ImpostorRenderer.Instance;
        if (impostorRenderer == null)
        {
            Debug.LogError("[ChunkObjectLoader] ImpostorRenderer.Instance not found. GPU blobs not submitted.");
            return;
        }

        var chunkManager = ChunkManager.Instance;
        if (chunkManager == null)
        {
            Debug.LogError("[ChunkObjectLoader] ChunkManager.Instance not found. GPU blobs not submitted.");
            return;
        }

        // TODO: Prepare chunk visibility data and pass to ImpostorRenderer
        // impostorRenderer.Initialize(
        //     prototypeRegistry,
        //     chunkManager.sphereCenter,
        //     chunkManager.sphereRadius,
        //     _gpuBlotches.ToArray(),
        //     ...);

        _gpuBufferSubmitted = true;
        Debug.Log("[ChunkObjectLoader] GPU buffer submitted.");
    }

    // ===== HELPERS =====

    private long EncodeChunkKey(int packed, FaceId face, byte lod)
    {
        return ((long)(uint)packed << 16) | ((uint)face << 8) | lod;
    }
}
