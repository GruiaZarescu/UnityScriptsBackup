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

    [SerializeField, Tooltip("Live authoring database. Assigned = editor authoring is available. " +
    "NEVER read in shipped builds regardless of this field's value — see forceUseBakedFiles.")]
    private MapObjectDatabase mapObjectDatabase;

    [SerializeField, Tooltip("Force the baked-file path even in-editor, for testing shipped-build behavior.")]
    private bool forceUseBakedFiles = false;

    private STPTME.MapObjects.IMapObjectSource _objectSource;

    // ===== LIFECYCLE =====

    private void Awake()
    {
        _gpuBlotches = new List<BlotchData>();
    }

    /// <summary>
    /// Forces both object passes to re-run for a specific chunk, bypassing the
    /// "already processed" guard. Used by editor-time authoring tools so a newly Add()'d or
    /// Remove()'d MapObjectDatabase entry shows up immediately.
    ///
    /// MUST mirror HandleChunkCreated's full sequence (cell objects AND blobs), because
    /// DespawnChunkObjects clears the entire chunk bucket — which is shared by both spawn
    /// paths. Re-running only ProcessCellObjects would permanently delete every
    /// single-instance blotch prefab (trees, etc.) in the chunk until it reloaded.
    /// </summary>
    public void ForceReprocessChunkObjects(int packed, FaceId face, byte lod)
    {
        if (!_initialized) return;

        prefabStreamer.DespawnChunkObjects(packed, face, lod);

        ProcessCellObjects(packed, face, lod);
        ProcessBlobs(packed, face, lod);
    }

    /// <summary>
    /// Resolves the (packed, face) chunk address for a world position and force-reprocesses
    /// its standalone objects. Convenience wrapper for authoring tools that only have a
    /// hit point, not a chunk address, on hand.
    /// </summary>
    public bool ForceReprocessChunkObjectsAt(Vector3 worldPosition, byte lod = 0)
    {
        var settings = TerrainManagementSettings.Instance;
        float chunkSize = settings.terrainSize / settings.tilingFactor;
        int subdivPow2 = 1 << _heightmapSubdivisions;
        float faceWorldSize = (settings.maxX - settings.minX + 1) * (settings.terrainSize / subdivPow2);

        if (!MapObjectChunkMath.TryResolve(worldPosition, settings.sphereCenter, chunkSize, faceWorldSize,
                _numberOfChunks, settings.minX, settings.maxX, out var addr))
            return false;

        ForceReprocessChunkObjects(addr.packed, addr.face, lod);
        return true;
    }

    private void Start()
    {
        Debug.Log("[ChunkObjectLoader::Start] Called");
        var settings = TerrainManagementSettings.Instance;
        _numberOfChunks = settings.numberOfChunks;
        _heightmapSubdivisions = settings.heightmapSubdivisions;
        _minX = settings.minX;
        Debug.Log($"[ChunkObjectLoader::Start] Loaded settings: chunks={_numberOfChunks}, subdivisions={_heightmapSubdivisions}, minX={_minX}");

        _cellObjectReader = new CellObjectReader();
        _cellObjectReader.Init(1 << _heightmapSubdivisions, _minX);

        bool useLiveDatabase = false;
        #if UNITY_EDITOR
        useLiveDatabase = mapObjectDatabase != null && !forceUseBakedFiles;
        #endif

        if (useLiveDatabase)
        {
            float chunkSize = settings.terrainSize / settings.tilingFactor;
            int subdivPow2 = 1 << _heightmapSubdivisions;
            float faceWorldSize = (settings.maxX - settings.minX + 1) * (settings.terrainSize / subdivPow2);

            _objectSource = new STPTME.MapObjects.LiveDatabaseObjectSource(
                mapObjectDatabase, settings.sphereCenter, chunkSize, faceWorldSize,
                _numberOfChunks, _minX, settings.maxX);

            Debug.Log("[ChunkObjectLoader] Using LIVE MapObjectDatabase — editor authoring mode.");
        }
        else
        {
            _objectSource = new STPTME.MapObjects.BakedFileObjectSource(_cellObjectReader);
        }
        Debug.Log($"[ChunkObjectLoader] Using object source: {_objectSource.GetType().Name}");

        // Terrain blotch data is no longer loaded/reloaded here — ChunkManager.Awake already
        // ran MapContentOrchestrator.Build (once) and populated TerrainBlotchIndex from that
        // single pass. Loading it again here would be a second, entirely redundant full scan
        // of every cell file.
        if (!STPTME.MapObjects.TerrainBlotchIndex.IsInitialized)
            Debug.LogWarning("[ChunkObjectLoader] TerrainBlotchIndex not initialized yet — " +
                "ensure ChunkManager.Awake runs before this component's Start().");
        
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
        Debug.Log($"[ChunkObjectLoader] Start() finished subscribing at frame {Time.frameCount}");

        // Catch up on chunks that already exist. The center chunk (the one the player spawns on)
        // is created synchronously during ChunkManager's initial generation cycle, which can
        // complete before this Start() runs — that event is missed entirely, which is why objects
        // on the spawn chunk only appeared after walking away and back.
        var preExisting = chunkRegistry.GetAllLoadedChunks();
        if (preExisting.Count > 0)
            Debug.Log($"[ChunkObjectLoader] Catching up on {preExisting.Count} pre-existing chunk record(s).");
        for (int i = 0; i < preExisting.Count; i++)
        {
            var (packed, face, lod) = preExisting[i];
            HandleChunkCreated(packed, face, lod);
        }

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
        Debug.Log($"[ChunkObjectLoader] HandleChunkCreated FIRED for packed={packed} face={face} lod={lod} at frame {Time.frameCount}");
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
         Debug.Log($"[ChunkObjectLoader] HandleChunkRemoved FIRED for packed={packed} face={face} lod={lod} at frame {Time.frameCount}");
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
        // NOTE: every stored object's lodLevel field is hardcoded to 0 at bake time —
        // objects aren't actually per-LOD-tagged on disk (see MapObjectBaker.WriteGroupFile).
        // Passing the CHUNK's actual `lod` here as the filter was wrong: it required an exact
        // match against that always-0 field, so ANY object silently stopped being found the
        // moment its chunk left LOD0 — including !shouldInstance objects that are supposed to
        // persist as a prefab at EVERY LOD forever. Always query the sentinel value (0); the
        // real per-chunk-LOD spawn decision is entry.ShouldSpawnAsPrefabAtLOD(lod) below.
        var segment = _objectSource.GetObjectsForChunk(packed, face, _numberOfChunks, 0);
        Debug.Log($"[ChunkObjectLoader] ProcessCellObjects packed={packed} face={face} lod={lod} → segment.Count={segment.Count} (frame {Time.frameCount})");
        if (segment.Count == 0) return;

        // Get the chunk's GameObject for parenting
        var chunkRegistry = ChunkManager.Instance?.chunkRegistry;
        Transform parentTransform = null;
        if (chunkRegistry != null && chunkRegistry.TryGetChunkGameObject(packed, face, lod, out GameObject chunkGO))
            parentTransform = chunkGO.transform;

        Debug.Log($"[ChunkObjectLoader] Processing {segment.Count} cell objects for chunk {packed} LOD {lod}");

        foreach (var sourcedObjectInstance in segment)
        {
            var entry = prototypeRegistry.GetEntry(sourcedObjectInstance.prototypeIndex);
            if (entry == null)
            {
                Debug.LogWarning($"[ChunkObjectLoader] No registry entry for prototypeIndex={sourcedObjectInstance.prototypeIndex}");
                continue;
            }

            // Decide: GPU instance or spawn GameObject?
            // Reaching this branch is now the EXPECTED outcome, not an edge case: if this
            // object's prototype is shouldInstance, MapContentOrchestrator already converted
            // it into a blotch and merged it into the GPU buffer at scene load — spawning a
            // GameObject here too would double-render it. No warning needed.
            if (entry.IsInstancedAtLOD(lod))
                continue;

            // Spawn as GameObject
            var settings = TerrainManagementSettings.Instance;
            
            // Compute deterministic size scales for cell objects
            // Use position hash as seed for cell objects (they don't have blotch seeds)
            uint cellSeed = (uint)sourcedObjectInstance.position.GetHashCode();
            Vector2 cellScales = entry.sizeVariability.ComputeSizeScalesCPU(cellSeed);
            
            var spawned = prefabStreamer.SpawnObject(
            sourcedObjectInstance.prototypeIndex,
            parentTransform,
            packed,
            face,
            lod,
            sourcedObjectInstance.position,
            sourcedObjectInstance.rotation.eulerAngles.y,
            cellScales.x,
            cellSeed,
            settings.sphereCenter,
            cellScales.y,
            mapObjectId: sourcedObjectInstance.mapObjectId,
            sourceDatabase: _objectSource.SourceDatabaseOrNull,
            // Map objects carry a REAL authored orientation. Passing the full quaternion here
            // bypasses SpawnObject's upright+yaw reconstruction, which would otherwise discard
            // pitch and roll entirely (the reason spline-placed fences never tilted on slopes,
            // no matter how correct the spline tool's own math was).
            explicitRotation: sourcedObjectInstance.rotation);
        }
    }

    private void ProcessBlobs(int packed, FaceId face, byte lod)
    {
        var chunkRegistry = ChunkManager.Instance?.chunkRegistry;
        Transform parentTransform = null;
        if (chunkRegistry != null && chunkRegistry.TryGetChunkGameObject(packed, face, lod, out GameObject chunkGO))
            parentTransform = chunkGO.transform;

        // NOTE: keyed by (packed, face) — the old CellBlotchQuery keyed by packed alone,
        // which doesn't encode face, so two faces sharing the same (mapX,mapY,chunkX,chunkY)
        // coordinates could silently have their blotches merged. Fixed by TerrainBlotchIndex.
        var blobs = STPTME.MapObjects.TerrainBlotchIndex.GetBlobsForChunk(packed, face);
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

                // Compute deterministic size scales (matches GPU pipeline)
                // For single-instance blotches, instanceID = 0
                uint sizeSeed = (uint)((blob.Seed & 0xFFFF) << 16) | 0u;
                Vector2 scales = entry.sizeVariability.ComputeSizeScalesCPU(sizeSeed);
                float heightScale = scales.x;
                float widthScale = scales.y;

                prefabStreamer.SpawnObject(
                    blob.PrototypeIndex,
                    parentTransform,
                    packed,
                    face,
                    lod,
                    worldPos,
                    0f,  
                    heightScale,  
                    blob.Seed,
                    settings.sphereCenter,
                    widthScale);
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