using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using CustomTypes;
using Unity.Collections;
using Unity.VisualScripting;
/// <summary>
/// Tracks a single chunk instance: LOD, GameObject reference, and splatmap binding.
/// Collider presence is derived from LOD (LOD 0 always has a collider).
/// </summary>
public struct ChunkRecord : IEquatable<ChunkRecord>
{
    public readonly GameObject gameObject;       
    public readonly byte lod;
    public readonly int splatSliceIndex;
    public readonly byte splatTier;
    public readonly int normalSliceIndex;
    public readonly byte normalTier;
    public ChunkRecord(byte lod, GameObject gameObject, int splatSliceIndex, byte splatTier,
        int normalSliceIndex = -1, byte normalTier = 0)
    {
        this.lod = lod;
        this.gameObject = gameObject;
        this.splatSliceIndex = splatSliceIndex;
        this.splatTier = splatTier;
        this.normalSliceIndex = normalSliceIndex;
        this.normalTier = normalTier;
    }

    public bool Equals(ChunkRecord other)
    {
        return gameObject == other.gameObject && lod == other.lod &&
               splatSliceIndex == other.splatSliceIndex &&
               splatTier == other.splatTier;
    }

    public override bool Equals(object obj)
    {
        return obj is ChunkRecord other && Equals(other);
    }

    public override int GetHashCode()
    {
        return gameObject != null ? gameObject.GetHashCode() : 0;
    }
}
/// <summary>
/// Inline storage for 0–2 ChunkRecords per chunk slot, replacing HashSet&lt;ChunkRecord&gt;.
/// Eliminates per-slot heap allocation and enumerator boxing.
/// Stored as value type in a flat array — always access via ref to avoid copies.
/// </summary>
struct ChunkSlot
{
    private ChunkRecord r0, r1;
    private byte count;

    public int Count => count;
    public bool IsEmpty => count == 0;

    public ChunkRecord this[int i]
    {
        get
        {
            if (i == 0) return r0;
            return r1;
        }
    }

    public void Add(ChunkRecord record)
    {
        if (count == 0) { r0 = record; count = 1; }
        else if (count == 1) { r1 = record; count = 2; }
    }

    public bool Remove(ChunkRecord record)
    {
        if (count >= 1 && r0.Equals(record))
        {
            r0 = (count == 2) ? r1 : default;
            r1 = default;
            count--;
            return true;
        }
        if (count == 2 && r1.Equals(record))
        {
            r1 = default;
            count--;
            return true;
        }
        return false;
    }

    public bool RemoveByLodAndBatch(byte lod, GameObject batch)
    {
        if (count >= 1 && r0.lod == lod && r0.gameObject == batch)
        {
            r0 = (count == 2) ? r1 : default;
            r1 = default;
            count--;
            return true;
        }
        if (count == 2 && r1.lod == lod && r1.gameObject == batch)
        {
            r1 = default;
            count--;
            return true;
        }
        return false;
    }

    public bool HasLod(byte lod, out ChunkRecord found)
    {
        if (count >= 1 && r0.lod == lod) { found = r0; return true; }
        if (count == 2 && r1.lod == lod) { found = r1; return true; }
        found = default;
        return false;
    }

    public void Clear() { r0 = default; r1 = default; count = 0; }
}
/// <summary>
/// Tracks the chunks stored in a mesh batch. Used for managing which chunks are in which pooled GameObjects, and for updating/removing them when needed.
/// </summary>
struct PoolEntry
{
    public FaceId face;
    public byte lod;
    public int packed;
    public int splatSliceIndex;
    public byte splatTier;
    public int normalSliceIndex;
    public byte normalTier;
    public int vertCount; // Actual vertex count for this chunk (edge chunks have fewer verts than LUT predicts)
    public int vertOffset; // Start index of this chunk's vertices in the batch mesh
    public int triOffset;  // Start index of this chunk's triangles in the batch index buffer
    public int triCount;   // Number of triangle indices for this chunk
    public PoolEntry(FaceId face, byte lod, int packed, int vertCount, int splatSliceIndex = -1, byte splatTier = 0,
        int normalSliceIndex = -1, byte normalTier = 0)
    {
        this.face = face;
        this.lod = lod;
        this.packed = packed;
        this.vertCount = vertCount;
        this.splatSliceIndex = splatSliceIndex;
        this.splatTier = splatTier;
        this.normalSliceIndex = normalSliceIndex;
        this.normalTier = normalTier;
        this.vertOffset = 0;
        this.triOffset = 0;
        this.triCount = 0;
    }

    public PoolEntry(FaceId face, byte lod, int packed, int vertCount, int vertOffset, int triOffset, int triCount,
        int splatSliceIndex = -1, byte splatTier = 0,
        int normalSliceIndex = -1, byte normalTier = 0)
    {
        this.face = face;
        this.lod = lod;
        this.packed = packed;
        this.vertCount = vertCount;
        this.vertOffset = vertOffset;
        this.triOffset = triOffset;
        this.triCount = triCount;
        this.splatSliceIndex = splatSliceIndex;
        this.splatTier = splatTier;
        this.normalSliceIndex = normalSliceIndex;
        this.normalTier = normalTier;
    }

    public bool Equals(PoolEntry other)
    {
        return lod == other.lod && packed == other.packed && face == other.face;
    }

    public override bool Equals(object obj)
    {
       return obj is PoolEntry other && Equals(other);
    }

    public override int GetHashCode()
    {
        return (packed * 397) ^ ((int)face << 8) ^ lod;
    }

    public static bool operator == (PoolEntry a,PoolEntry b)
    {
        return a.Equals(b);
    }

    public static bool operator !=(PoolEntry a,PoolEntry b)
    {
        return !a.Equals(b);
    }
}

/// <summary>
/// Manages chunk GameObjects: creation, destruction, tracking, collider toggling, mesh batching.
/// </summary>
public class ChunkRegistry : MonoBehaviour
{

    //Tracking
    private ChunkSlot[] chunks;//Aim to have only one chunk in one spot at once, removing duplicates would simplify logic a lot. When replacing a chunk, say LOD1 to LOD0, prepare the new one and replace atomically, so no duplicates. To consider implementing. ChunkRegistry needs many other simplifiactions, huge redundant boilerplate.
    private Dictionary<GameObject,List<PoolEntry>>chunksByPool = new Dictionary<GameObject, List<PoolEntry>>();

    //Config
    private float[] chunkDistanceByLOD;
    private byte maxLOD;
    private byte maxChunkGenOpsPerFrame;
    private ushort maxChunkGenWorkPerFrame;
    private ushort maxVertsPerOuterChunkMesh;
    private int numberOfChunks;
    private sbyte minX,maxX;
    private int nonBatchedOuterChunkRings;
    /// <summary>
    /// Maximum BFS depth for chunk loading. Equals one full cube face side in chunks
    /// (numberOfChunks * heightmapsPerRow), since you can only see half the sphere at once.
    /// </summary>
    private int halfSphereChunkDistance;
    /// <summary>DEBUG ONLY — see TerrainManagementSettings.debugDisableBatching.</summary>
    private bool debugDisableBatching;
    private bool debugLoadFullSphere;
    private int centerChunk;
    private FaceId centerChunkFace = FaceId.Up;
    private int centerFlatIdx; // cached flatGridBFS.ChunkKeyToFlat(centerChunk, centerChunkFace)
    private bool loadTextures = true;
    private int totalChunkCount;
    private int generationVersion;

    private Transform chunkPoolParent;
    private List<ChunkRecord> tempChunkRecordList;
    // Reusable buffers for saving/restoring edge caches across batched chunk removal
    private int[] batchEdgeSaveIndices;
    private EdgeData[] batchEdgeSaveData;
    private readonly List<int> reusableTriList = new List<int>();

    // Reusable queues for RunGenerationCycle to avoid per-cycle GC
    private readonly Queue<(int packed, FaceId face)> genCycleCollisionGen = new Queue<(int, FaceId)>();
    private readonly Queue<(int packed, FaceId face, byte lod)> genCycleNormalGen = new Queue<(int, FaceId, byte)>();
    private readonly Queue<(int packed, FaceId face, ChunkRecord chunk)> genCycleEarlyRemovals = new Queue<(int, FaceId, ChunkRecord)>();
    private readonly Queue<(int packed, FaceId face, ChunkRecord chunk)> genCycleLateRemovals = new Queue<(int, FaceId, ChunkRecord)>();
    [SerializeField]private Material tempMat;
    private STPTMEUtils.GlobalIndexCalculator globalIndexCalculator;

    // Baked tree-to-UV mapping. When loaded, completely replaces TryProjectPointToChunkUV
    // at runtime (zero mesh projection per tree — array lookup only).
    public CanopyUVCache canopyUVCache;
    public void SetCanopyUVCache(CanopyUVCache cache) => canopyUVCache = cache;

    private HashSet<ChunkKey> ringPositions;
    private bool[] ringFlags; // flat bool[] indexed by storage index for O(1) ring membership test
    private FlatGridBFS flatGridBFS;

    private ChunkMaterialManager chunkMaterialManager;
    private TextureStreamer textureStreamer;
    [System.Obsolete("Replaced by ImpostorRenderer")] private object treeColliderManager;

    // Tracks batches currently being rebuilt - prevents further modifications during rebuild
    private HashSet<GameObject> batchesBeingRebuilt = new HashSet<GameObject>();

    // Pending rebuilds: batches that need to be rebuilt with certain entries removed
    // Key = batch GameObject, Value = set of entries to remove from that batch
    private Dictionary<GameObject, HashSet<PoolEntry>> pendingBatchRebuilds = new Dictionary<GameObject, HashSet<PoolEntry>>();

    // Reusable dictionary for RemoveChunksFromMeshes to avoid per-call GC
    private readonly Dictionary<GameObject, List<(PoolEntry entry, ChunkRecord record, int packed, FaceId face)>> reusableByPool
        = new Dictionary<GameObject, List<(PoolEntry, ChunkRecord, int, FaceId)>>();
    // Pool of reusable lists for reusableByPool values
    private readonly Stack<List<(PoolEntry, ChunkRecord, int, FaceId)>> byPoolListPool
        = new Stack<List<(PoolEntry, ChunkRecord, int, FaceId)>>();

    // Pool for chunk GameObjects (MeshFilter + MeshRenderer, no collider).
    // Shared by both non-batched (near camera) and batched (far) chunks.
    // When debugDisableBatching is true ALL chunks are non-batched.
    private readonly Stack<GameObject> chunkPool = new Stack<GameObject>();

    // ===== PHASE B: VISIBILITY-DRIVEN BATCH RENDERER TOGGLING =====
    /// <summary>Sector grid resolution per face side. With faceSpan=96 chunks and SECTOR_GRID_SIZE=8,
    /// each sector is a 12×12 chunk square — small enough that a batch confined to one sector
    /// has a tight bounding sphere instead of being a player-centered ring that no frustum can cull.</summary>
    private const int SECTOR_GRID_SIZE = 8;
    /// <summary>Side of one sector in chunks. Computed in Init from faceSpan/SECTOR_GRID_SIZE.</summary>
    private int sectorSize;
    /// <summary>Maps each pooled batch/standalone renderer GameObject to its index
    /// in the <see cref="rendererBatchIds"/> / <see cref="rendererList"/> SoA arrays.
    /// The dictionary is only used on (un)register; the per-frame visibility apply
    /// loop iterates the flat arrays for cache-friendly traversal and zero hashing.</summary>
    private readonly Dictionary<GameObject, int> batchRenderIndexByGO
        = new Dictionary<GameObject, int>();
    /// <summary>SoA: parallel arrays storing live batch ids and their MeshRenderers.
    /// Compacted via swap-back removal. Iterated linearly by ApplyBatchVisibility.</summary>
    private int[] rendererBatchIds = new int[256];
    private MeshRenderer[] rendererList = new MeshRenderer[256];
    /// <summary>Reverse-lookup: GameObject for each active SoA slot (so we can update the
    /// dictionary's index entry when swap-back moves the tail item into the hole).</summary>
    private GameObject[] rendererGOList = new GameObject[256];
    private int rendererCount = 0;
    /// <summary>Reusable scratch for collecting member storage indices when registering a batch.</summary>
    private int[] batchRegisterScratch = new int[64];

    // ===== PHASE 4 PLAN BUFFERS (counting-sort by sector key) =====
    // Replaces an O(N log N) List<T>.Sort with delegate dispatch + 24-32B/item tuple
    // allocations. Steady-state: zero GC after warm-up. SoA layout for cache density.
    /// <summary>BFS-order staging arrays drained from normalGen in Phase 4.</summary>
    private int[]    bfsPacked = Array.Empty<int>();
    private byte[]   bfsFace   = Array.Empty<byte>();
    private byte[]   bfsLod    = Array.Empty<byte>();
    /// <summary>Dense sector key per BFS item: face*SECTOR_GRID_SIZE² + sy*SECTOR_GRID_SIZE + sx.</summary>
    private ushort[] bfsKey    = Array.Empty<ushort>();
    /// <summary>Sector-grouped output of the counting sort. Same SoA layout as bfs* above.</summary>
    private int[]    sortedPacked = Array.Empty<int>();
    private byte[]   sortedFace   = Array.Empty<byte>();
    private byte[]   sortedLod    = Array.Empty<byte>();
    private ushort[] sortedKey    = Array.Empty<ushort>();
    /// <summary>Per-bucket counts (pass 1) then running write cursors (pass 2). 6×SECTOR_GRID_SIZE² entries.</summary>
    private readonly int[] sectorBucketCount  = new int[6 * SECTOR_GRID_SIZE * SECTOR_GRID_SIZE];
    private readonly int[] sectorBucketCursor = new int[6 * SECTOR_GRID_SIZE * SECTOR_GRID_SIZE];
    /// <summary>Bucket keys in order of first BFS appearance. Used to emit sector groups
    /// in nearest-first order instead of ascending key order — without this the counting
    /// sort would always start with bucket 0 (Up face, sector (0,0)) regardless of where
    /// the player is, producing reversed/scrambled load order.</summary>
    private readonly int[] bucketEmissionOrder = new int[6 * SECTOR_GRID_SIZE * SECTOR_GRID_SIZE];

    private bool subscribedToVisibility;

    // ===== CHUNK LIFECYCLE EVENTS =====
    // Fired after a chunk is fully created or just before its GameObject is destroyed/pooled.
    // Parameters: (packed, face, lod). ChunkObjectLoader subscribes to these to spawn/despawn
    // world objects that belong to each chunk.
    public event Action<int, FaceId, byte> OnChunkCreated;
    public event Action<int, FaceId, byte> OnChunkRemoved;

    // ===== SKIRTS =====
    // Skirts stitch the seam between two adjacent chunks of differing edge resolutions
    // (different LOD, or same LOD with different per-cell dsSteps). One standalone GameObject
    // per skirt, pooled separately from chunks. A skirt's collider is enabled only when both
    // sides are LOD 0, since LOD 0 chunks already carry colliders and gaps between them are
    // walkable.

    /// <summary>Edge directions, matching FlatGridBFS conventions: 0=right(+x), 1=left(-x), 2=up(+z), 3=down(-z).</summary>
    private const int DIR_RIGHT = 0, DIR_LEFT = 1, DIR_UP = 2, DIR_DOWN = 3;

    private struct EdgeData
    {
        // Edge vertex/normal/uv arrays for each of the 4 sides. Indexed [DIR_RIGHT/LEFT/UP/DOWN].
        // Right/Left edges have 'edgeHeight' verts (v varies). Up/Down edges have 'edgeWidth' verts (u varies).
        // These are LIVE values: when an edge gets stitched against a lower-res neighbour, the
        // verts/normals here are overwritten in place. The orig* arrays preserve the pristine
        // snapshot for unstitching.
        public Vector3[] vertsRight, vertsLeft, vertsUp, vertsDown;
        public Vector3[] normalsRight, normalsLeft, normalsUp, normalsDown;
        // Pristine snapshots taken at mesh-build time. Used to restore the edge when its stitch
        // is dropped (e.g. neighbour removed/replaced).
        public Vector3[] origVertsRight, origVertsLeft, origVertsUp, origVertsDown;
        public Vector3[] origNormalsRight, origNormalsLeft, origNormalsUp, origNormalsDown;
        // Vertices one row inside the edge. Needed to recompute edge normals after stitching
        // (cross of along-edge tangent × inward connector to the inner row).
        public Vector3[] innerVertsRight, innerVertsLeft, innerVertsUp, innerVertsDown;
        // Per-chunk UVs at the edge. These are the same per-chunk [0,1] UVs the chunk mesh
        // uses, which the shader remaps via uvOffsetScale into the chunk's splatmap slice.
        // Reusing them on the skirt makes it sample the matching edge strip of the source
        // chunk's splat, instead of the entire slice.
        public Vector2[] uvsRight, uvsLeft, uvsUp, uvsDown;
        public byte lod;
        // Bit per direction: 1 = currently stitched (edge verts conform to a lower-res neighbour).
        // (1 << DIR_*).
        public byte stitchedFlags;
        // Vertex grid dimensions of the source mesh. Used to map edge-array indices into
        // mesh-vertex indices when patching the mesh post-stitch.
        public ushort edgeWidth, edgeHeight;
        public Renderer sourceRenderer; // Used to copy MPB onto skirts that pick this chunk's material.
        // Splat/normal metadata cached at build time so skirts can construct a proper MPB
        // even when the source chunk uses the batched material path (per-vertex UV1/UV2/UV3).
        public int splatSliceIndex;
        public byte splatTier;
        public Vector4 uvOffsetScale;
        public int normalSliceIndex;
        public byte normalTier;
        public Vector4 normalUvOffsetScale;

        public Vector3[] GetVerts(int dir) => dir switch
        {
            DIR_RIGHT => vertsRight, DIR_LEFT => vertsLeft, DIR_UP => vertsUp, _ => vertsDown
        };
        public Vector3[] GetNormals(int dir) => dir switch
        {
            DIR_RIGHT => normalsRight, DIR_LEFT => normalsLeft, DIR_UP => normalsUp, _ => normalsDown
        };
        public Vector3[] GetOrigVerts(int dir) => dir switch
        {
            DIR_RIGHT => origVertsRight, DIR_LEFT => origVertsLeft, DIR_UP => origVertsUp, _ => origVertsDown
        };
        public Vector3[] GetOrigNormals(int dir) => dir switch
        {
            DIR_RIGHT => origNormalsRight, DIR_LEFT => origNormalsLeft, DIR_UP => origNormalsUp, _ => origNormalsDown
        };
        public Vector3[] GetInnerVerts(int dir) => dir switch
        {
            DIR_RIGHT => innerVertsRight, DIR_LEFT => innerVertsLeft, DIR_UP => innerVertsUp, _ => innerVertsDown
        };
        public Vector2[] GetUvs(int dir) => dir switch
        {
            DIR_RIGHT => uvsRight, DIR_LEFT => uvsLeft, DIR_UP => uvsUp, _ => uvsDown
        };
    }

    private struct SkirtKey : IEquatable<SkirtKey>
    {
        public int packedA;
        public int packedB;
        public byte faceA;
        public byte faceB;

        public static SkirtKey Make(int p1, FaceId f1, int p2, FaceId f2)
        {
            // Canonical ordering so {A,B} and {B,A} hash equal.
            ulong k1 = ((ulong)(byte)f1 << 32) | (uint)p1;
            ulong k2 = ((ulong)(byte)f2 << 32) | (uint)p2;
            if (k1 <= k2)
                return new SkirtKey { packedA = p1, faceA = (byte)f1, packedB = p2, faceB = (byte)f2 };
            return new SkirtKey { packedA = p2, faceA = (byte)f2, packedB = p1, faceB = (byte)f1 };
        }

        public bool Equals(SkirtKey other) =>
            packedA == other.packedA && packedB == other.packedB &&
            faceA == other.faceA && faceB == other.faceB;
        public override bool Equals(object obj) => obj is SkirtKey sk && Equals(sk);
        public override int GetHashCode() =>
            (packedA * 397) ^ packedB ^ (faceA << 16) ^ (faceB << 8);
    }

    // Storage-indexed edge cache (one entry per chunk slot, parallel to `chunks`).
    // EdgeData.lod==0 with all-null arrays means "no entry".
    private EdgeData[] edgeCache;

    private readonly Dictionary<SkirtKey, GameObject> skirts = new Dictionary<SkirtKey, GameObject>();
    private readonly Stack<GameObject> skirtPool = new Stack<GameObject>();
    // Reusable buffers for skirt mesh construction
    private Vector3[] skirtVertBuf = Array.Empty<Vector3>();
    private Vector3[] skirtNormalBuf = Array.Empty<Vector3>();
    private Vector2[] skirtUvBuf = Array.Empty<Vector2>();
    private int[] skirtTriBuf = Array.Empty<int>();
    private MaterialPropertyBlock skirtMpb;
    // Reusable list buffers for stitch mesh writes (Mesh.GetVertices/Normals fill these).
    private List<Vector3> stitchVertList;
    private List<Vector3> stitchNormalList;

    public void Init
    (int numberOfChunks,sbyte minX,sbyte maxX,byte maxLOD,
    ushort maxVertsPerOuterChunkMesh, int nonBatchedOuterChunkRings,
    byte maxChunkGenOpsPerFrame, ushort maxChunkGenWorkPerFrame,
    float[]chunkDistanceByLOD,
    Transform chunkPoolParent,Material tempMat,
    int totalChunkCount,
    STPTMEUtils.GlobalIndexCalculator globalIndexCalculator,
    ChunkMaterialManager chunkMaterialManager,
    TextureStreamer textureStreamer,
    bool enableTextureGeneration,
    bool disableChunkBatching,
    FlatGridBFS flatGridBFS)
    {
        this.numberOfChunks = numberOfChunks;
        this.minX = minX;
        this.maxX = maxX;
        this.maxLOD = maxLOD;
        this.maxVertsPerOuterChunkMesh = maxVertsPerOuterChunkMesh;
        this.nonBatchedOuterChunkRings = nonBatchedOuterChunkRings;
        this.maxChunkGenOpsPerFrame = maxChunkGenOpsPerFrame;
        this.maxChunkGenWorkPerFrame = maxChunkGenWorkPerFrame;
        this.chunkDistanceByLOD = chunkDistanceByLOD;
        this.chunkPoolParent = chunkPoolParent;
        this.tempMat = tempMat;
        this.totalChunkCount = totalChunkCount;
        this.globalIndexCalculator = globalIndexCalculator;
        chunks = new ChunkSlot[totalChunkCount * FaceIdUtility.StorageFaceCount];
        edgeCache = new EdgeData[totalChunkCount * FaceIdUtility.StorageFaceCount];
        this.chunkMaterialManager = chunkMaterialManager;
        this.textureStreamer = textureStreamer;
        this.loadTextures = enableTextureGeneration;
        this.debugDisableBatching = disableChunkBatching;
        this.debugLoadFullSphere = TerrainManagementSettings.Instance.debugLoadFullSphere;
        this.flatGridBFS = flatGridBFS ?? new FlatGridBFS(numberOfChunks, minX, maxX);
        this.halfSphereChunkDistance = numberOfChunks * (maxX - minX + 1);
        if (this.debugLoadFullSphere)
        {
            this.halfSphereChunkDistance *= 2;
        }

        // Sector size: divide each face's chunk grid into SECTOR_GRID_SIZE² squares.
        // Phase B uses these as batch boundaries so batches are spatially coherent and
        // their bounding spheres can actually be frustum-culled (BFS-order batching
        // produced ring shapes that intersect every plane regardless of camera angle).
        int faceSpan = numberOfChunks * (maxX - minX + 1);
        sectorSize = Mathf.Max(1, faceSpan / SECTOR_GRID_SIZE);

        // Hook into VisibilitySystem so MeshRenderer.enabled tracks per-batch visibility.
        // VisibilitySystem.Initialize is called by ChunkManager before chunkRegistry.Init,
        // so Instance is guaranteed non-null in normal startup.
        if (VisibilitySystem.Instance != null && !subscribedToVisibility)
        {
            VisibilitySystem.Instance.OnBatchVisibilityChanged += ApplyBatchVisibility;
            subscribedToVisibility = true;
        }
    }

    private void OnDestroy()
    {
        if (subscribedToVisibility && VisibilitySystem.Instance != null)
        {
            VisibilitySystem.Instance.OnBatchVisibilityChanged -= ApplyBatchVisibility;
            subscribedToVisibility = false;
        }
    }

    public bool HasChunk(int packed, FaceId face, byte lod)
    {
        int storageIdx = GetStorageIndex(packed, face);
        return chunks[storageIdx].HasLod(lod, out _);
    }

    /// <summary>
    /// Returns the chunk's GameObject for a given (packed, face, lod), or false if it doesn't exist.
    /// This lets consumers (e.g. MapPrefabStreamer) parent objects directly to the chunk's terrain mesh,
    /// without maintaining a separate parent dictionary.
    /// </summary>
    public bool TryGetChunkGameObject(int packed, FaceId face, byte lod, out GameObject chunkGO)
    {
        int storageIdx = GetStorageIndex(packed, face);
        if (chunks[storageIdx].HasLod(lod, out ChunkRecord record))
        {
            chunkGO = record.gameObject;
            return chunkGO != null;
        }
        chunkGO = null;
        return false;
    }

    private int GetStorageIndex(int packed, FaceId face)
    {
        return FaceIdUtility.GetStorageIndex(globalIndexCalculator.GetIndex(packed), face);
    }

    /// <summary>
    /// Enumerates every chunk currently resident, so late subscribers (e.g. ChunkObjectLoader)
    /// can catch up on chunks created before they attached to OnChunkCreated. The center chunk
    /// is created synchronously in RunGenerationCycle's collision phase ("Process center chunk
    /// first without yielding"), which can complete before another component's Start() runs —
    /// its OnChunkCreated fires with nobody listening, so its objects never spawn.
    ///
    /// Mirrors the half-sphere cleanup sweep's flat→(packed,face) reversal, since chunks[] is
    /// keyed by storage index and has no direct inverse.
    /// </summary>
    public List<(int packed, FaceId face, byte lod)> GetAllLoadedChunks()
    {
        var result = new List<(int, FaceId, byte)>();
        if (chunks == null || flatGridBFS == null) return result;

        for (int f = 0; f < flatGridBFS.totalCells; f++)
        {
            int storageIdx = flatGridBFS.GetStorageIndex(f);
            if (storageIdx < 0 || storageIdx >= chunks.Length) continue;
            if (chunks[storageIdx].IsEmpty) continue;

            int packed = flatGridBFS.GetPacked(f);
            FaceId face = flatGridBFS.GetFace(f);

            for (int i = 0; i < chunks[storageIdx].Count; i++)
                result.Add((packed, face, chunks[storageIdx][i].lod));
        }
        return result;
    }

    // ===== PHASE B: BATCH VISIBILITY HELPERS =====

    /// <summary>
    /// Stable sector key for a chunk. Two chunks share a sector iff they live in the
    /// same SECTOR_GRID_SIZE×SECTOR_GRID_SIZE quadrant on the same face. Used to
    /// stable-sort BFS results before batching so each batch is spatially coherent.
    /// </summary>
    private int SectorKey(int packed, FaceId face)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        int gx = (mapX - minX) * numberOfChunks + chunkX;
        int gy = (mapY - minX) * numberOfChunks + chunkY;
        int sx = gx / sectorSize;
        int sy = gy / sectorSize;
        // Plenty of headroom: face<<16 | sy<<8 | sx (sx,sy < SECTOR_GRID_SIZE≤16).
        return ((int)face << 16) | (sy << 8) | sx;
    }

    /// <summary>
    /// Dense sector key in [0, 6·SECTOR_GRID_SIZE²) for use as a counting-sort bucket
    /// index. Same partitioning as <see cref="SectorKey"/> but tightly packed.
    /// </summary>
    private ushort DenseSectorKey(int packed, FaceId face)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        int gx = (mapX - minX) * numberOfChunks + chunkX;
        int gy = (mapY - minX) * numberOfChunks + chunkY;
        int sx = gx / sectorSize;
        int sy = gy / sectorSize;
        // Clamp to grid (defensive — chunks at faceSpan boundary shouldn't overflow but
        // integer division on edge values can produce sx==SECTOR_GRID_SIZE at the seam).
        if (sx >= SECTOR_GRID_SIZE) sx = SECTOR_GRID_SIZE - 1;
        if (sy >= SECTOR_GRID_SIZE) sy = SECTOR_GRID_SIZE - 1;
        return (ushort)(((int)face * (SECTOR_GRID_SIZE * SECTOR_GRID_SIZE)) + sy * SECTOR_GRID_SIZE + sx);
    }

    /// <summary>Grow plan SoA arrays to hold at least <paramref name="n"/> items. Never shrinks.</summary>
    private void EnsurePlanCapacity(int n)
    {
        if (bfsPacked.Length >= n) return;
        // Round up to the next power of two to amortise growth across cycles.
        int newCap = bfsPacked.Length == 0 ? 1024 : bfsPacked.Length;
        while (newCap < n) newCap *= 2;
        bfsPacked    = new int[newCap];
        bfsFace      = new byte[newCap];
        bfsLod       = new byte[newCap];
        bfsKey       = new ushort[newCap];
        sortedPacked = new int[newCap];
        sortedFace   = new byte[newCap];
        sortedLod    = new byte[newCap];
        sortedKey    = new ushort[newCap];
    }

    /// <summary>
    /// Registers <paramref name="obj"/>'s renderer with VisibilitySystem so its
    /// enabled state will be driven by the per-frame batch visibility test.
    /// Builds bounds from the supplied member chunk storage indices. Safe to call
    /// when VisibilitySystem hasn't initialised yet — becomes a no-op.
    /// </summary>
    private void RegisterRendererBatch(GameObject obj, MeshRenderer renderer, int[] storageIdxs, int count)
    {
        if (obj == null || renderer == null) return;
        var sys = VisibilitySystem.Instance;
        if (sys == null) return;

        // If a stale registration exists for this pooled GameObject (shouldn't, but be defensive),
        // drop it so we don't leak batch ids when the pool reuses the GameObject.
        if (batchRenderIndexByGO.TryGetValue(obj, out int staleIdx))
        {
            sys.UnregisterBatch(rendererBatchIds[staleIdx]);
            RemoveRendererSlot(staleIdx);
        }

        var bounds = sys.BuildBatchBoundsFromStorageIdxs(storageIdxs, count);
        // Pass member storage indices so VisibilitySystem can OR per-chunk horizon
        // tests instead of using the conservative batch-level cone (which kept
        // entire sector batches lit while only one near member was actually above
        // the horizon).
        int id = sys.RegisterBatch(in bounds, storageIdxs, count);

        // Append to flat SoA.
        if (rendererCount >= rendererBatchIds.Length)
        {
            int newCap = rendererBatchIds.Length * 2;
            Array.Resize(ref rendererBatchIds, newCap);
            Array.Resize(ref rendererList, newCap);
            Array.Resize(ref rendererGOList, newCap);
        }
        int slot = rendererCount++;
        rendererBatchIds[slot] = id;
        rendererList[slot] = renderer;
        rendererGOList[slot] = obj;
        batchRenderIndexByGO[obj] = slot;

        // Sync immediately so the renderer doesn't flash on for one frame before the
        // next OnBatchVisibilityChanged event.
        renderer.enabled = sys.IsBatchVisible(id);
    }

    private void UnregisterRendererBatch(GameObject obj)
    {
        if (obj == null) return;
        if (!batchRenderIndexByGO.TryGetValue(obj, out int slot)) return;
        VisibilitySystem.Instance?.UnregisterBatch(rendererBatchIds[slot]);
        RemoveRendererSlot(slot);
    }

    /// <summary>Swap-back removal of an entry from the renderer SoA. Updates the
    /// dictionary to reflect the moved tail entry's new index.</summary>
    private void RemoveRendererSlot(int slot)
    {
        int last = rendererCount - 1;
        var removedGO = rendererGOList[slot];
        if (slot != last)
        {
            rendererBatchIds[slot] = rendererBatchIds[last];
            rendererList[slot]     = rendererList[last];
            rendererGOList[slot]   = rendererGOList[last];
            batchRenderIndexByGO[rendererGOList[slot]] = slot;
        }
        rendererList[last]   = null;
        rendererGOList[last] = null;
        rendererCount = last;
        if (removedGO != null) batchRenderIndexByGO.Remove(removedGO);
    }

    /// <summary>
    /// Subscribed to <see cref="VisibilitySystem.OnBatchVisibilityChanged"/>. Walks all
    /// registered terrain renderers and pushes the latest visibility bit onto
    /// MeshRenderer.enabled. Cheap: O(batchCount), and event only fires when at least
    /// one batch flipped this frame.
    /// </summary>
    private void ApplyBatchVisibility()
    {
        var sys = VisibilitySystem.Instance;
        if (sys == null) return;
        // Flat SoA loop — no hashing, no enumerator allocation, contiguous reads.
        int n = rendererCount;
        for (int i = 0; i < n; i++)
        {
            var r = rendererList[i];
            if (r == null) continue;
            r.enabled = sys.IsBatchVisible(rendererBatchIds[i]);
        }
    }

    private int[] EnsureBatchScratch(int needed)
    {
        if (batchRegisterScratch.Length < needed)
            batchRegisterScratch = new int[Mathf.Max(needed, batchRegisterScratch.Length * 2)];
        return batchRegisterScratch;
    }

    // ========== CREATION ============

    private void ReturnToPool(GameObject obj)
    {
        if (obj == null) return;
        // Drop any visibility batch registration tied to this pooled renderer. The
        // GameObject may be re-used later as either a single-chunk or multi-chunk
        // renderer; either path will register fresh bounds when it does.
        UnregisterRendererBatch(obj);
        MeshCollider mc = obj.GetComponent<MeshCollider>();
        if (mc != null) Destroy(mc);
        obj.SetActive(false);
        chunkPool.Push(obj);
    }

    /// <summary>
    /// Creates a chunk GameObject from MeshData. Disposes the MeshData after use.
    /// </summary>

    public void CreateChunk(int packed, FaceId face, byte lod,
        ref ChunkManager.MeshData meshData)
    {
        bool withCollider = lod == 0;
        //Remove old chunk if it exists
        int slotIdx = GetStorageIndex(packed, face);
        ref ChunkSlot existingSlot = ref chunks[slotIdx];
        if(!existingSlot.IsEmpty)
        {
            if (tempChunkRecordList == null) tempChunkRecordList = new List<ChunkRecord>();
            tempChunkRecordList.Clear();
            for (int i = 0; i < existingSlot.Count; i++)
                tempChunkRecordList.Add(existingSlot[i]);
            foreach(var chunk in tempChunkRecordList)
            {
                RemoveChunkImmediate(packed, face, chunk);
            }
        }

        STPTMEUtils.ReadFourSBytesFromInt(packed,
            out sbyte heightmapX, out sbyte heightmapY, out sbyte chunkX, out sbyte chunkY);

        GameObject obj;
        Mesh mesh;
        MeshRenderer renderer;

        if (chunkPool.Count > 0)
        {
            obj = chunkPool.Pop();
            obj.name = $"chunk:{packed}-unpacked: ({heightmapX},{heightmapY},{chunkX},{chunkY})-face:{face}-LOD:{lod}";
            obj.SetActive(true);
            mesh = obj.GetComponent<MeshFilter>().sharedMesh;
            mesh.Clear();
            renderer = obj.GetComponent<MeshRenderer>();
        }
        else
        {
            obj = new GameObject($"chunk:{packed}-unpacked: ({heightmapX},{heightmapY},{chunkX},{chunkY})-face:{face}-LOD:{lod}");
            obj.transform.SetParent(chunkPoolParent, false);
            mesh = new Mesh();
            obj.AddComponent<MeshFilter>().mesh = mesh;
            renderer = obj.AddComponent<MeshRenderer>();
        }

        // Save vertex count before ApplyMeshData disposes the MeshData
        int actualVertCount = meshData.vertCount;
        // Sample-space placement of this chunk within its cell — captured here for the same
        // reason as actualVertCount. Used below so texture UVs follow the chunk's TRUE width
        // rather than assuming an even 1/chunksPerAxis split, which is wrong for the last
        // chunk on each axis and produces one-texel seams against its neighbour.
        int uvSampleOffsetX = meshData.sampleOffsetX;
        int uvSampleOffsetY = meshData.sampleOffsetY;
        int uvSampleSpanX   = Mathf.Max(1, meshData.edgeWidth  - 1);
        int uvSampleSpanY   = Mathf.Max(1, meshData.edgeHeight - 1);
        int uvCellSamplesX  = meshData.cellSamplesX;
        int uvCellSamplesY  = meshData.cellSamplesY;
        // Cache the four edge vertex/normal arrays before ApplyMeshData disposes the data.
        // Source renderer is set just below — patched in after the renderer is configured.
        CacheChunkEdges(packed, face, lod, renderer, ref meshData);
        MeshUtils.ApplyMeshData(mesh, ref meshData);

        int sliceIndex = -1; byte tier = 0;
        int normalSlice = -1; byte normalTierByte = 0;
        Vector4 uvOffsetScale = new Vector4(0, 0, 1, 1);
        Vector4 normalUvOffsetScale = new Vector4(0, 0, 1, 1);

        if(textureStreamer != null && chunkMaterialManager != null && loadTextures)
        {
            Vector2SByte map = new Vector2SByte(heightmapX, heightmapY);

            // Check uniform classification first — if this cell is single-layer,
            // skip splatmap loading entirely and use the single-layer shader path.
            int uniformLayer = chunkMaterialManager.GetUniformDominantLayer(map.x, map.y, face);
            bool isUniformCell = uniformLayer >= 0;

            if (isUniformCell)
            {
                // Uniform cell: no splatmap needed. Compute UVs (for triplanar sampling)
                // and bind with the dominant layer flag. Normals still load independently.
                uvOffsetScale = ChunkMaterialManager.ComputeChunkUVOffsetScale(
                    chunkX, chunkY, numberOfChunks, 1, 1, 1);

                // Bind uniform cell: sets _UniformDominantLayer on MPB (no splatmap slice).
                // This must happen BEFORE the normal bind so normals overwrite correctly.
                chunkMaterialManager.BindUniformCellToRenderer(
                    renderer, (sbyte)uniformLayer,
                    uvOffsetScale, normalUvOffsetScale,
                    normalSlice, normalTierByte);
                sliceIndex = -2; // marker: uniform path active (no splatmap slice)
            }
            else
            {
                // Multi-layer (standard) path: load and bind splatmap.
                tier = textureStreamer.GetTierForLOD(lod);
                TextureStreamer.SplatmapTile tile = textureStreamer.GetOrLoadSync(map, tier, face);

                if (tile.IsValid)
                {
                    uvOffsetScale = ChunkMaterialManager.ComputeChunkUVOffsetScaleExact(
                        uvSampleOffsetX, uvSampleSpanX, uvCellSamplesX,
                        uvSampleOffsetY, uvSampleSpanY, uvCellSamplesY,
                        tile.borderPixels, tile.width, tile.height);

                    sliceIndex = chunkMaterialManager.AllocateAndBind(
                        renderer, map, tier, face, tile, uvOffsetScale);
                }
                else
                {
                    renderer.sharedMaterial = chunkMaterialManager.SharedMaterial;
                }
            }

            // Heightmap-derived normal map (loaded AFTER splatmap/uniform bind so its MPB
            // overwrites correctly). Loaded regardless of uniform/hybrid path.
            if (textureStreamer.HasHeightmapNormals && chunkMaterialManager.NormalsEnabled)
            {
                normalTierByte = textureStreamer.GetNormalTierForLOD(lod);
                TextureStreamer.NormalTile normTile = textureStreamer.GetOrLoadNormalSync(map, normalTierByte, face);
                if (normTile.IsValid)
                {
                    normalUvOffsetScale = ChunkMaterialManager.ComputeChunkUVOffsetScaleExact(
                        uvSampleOffsetX, uvSampleSpanX, uvCellSamplesX,
                        uvSampleOffsetY, uvSampleSpanY, uvCellSamplesY,
                        normTile.borderPixels, normTile.width, normTile.height);
                    normalSlice = chunkMaterialManager.AllocateAndBindNormal(
                        renderer, map, normalTierByte, face, normTile, normalUvOffsetScale);
                }
            }
        }
        else
        {
            //No texture system available -> tempMat
            renderer.sharedMaterial = tempMat;
        }

        // Store splat metadata on the edge cache so skirts can build a correct MPB.
        ref EdgeData ec = ref edgeCache[slotIdx];
        ec.splatSliceIndex = sliceIndex;
        ec.splatTier = tier;
        ec.uvOffsetScale = uvOffsetScale;
        ec.normalSliceIndex = normalSlice;
        ec.normalTier = normalTierByte;
        ec.normalUvOffsetScale = normalUvOffsetScale;

        if(withCollider)
        {
            MeshCollider mc = obj.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }

        ChunkRecord record = new ChunkRecord(lod, obj, sliceIndex, tier, normalSlice, normalTierByte);

        chunks[slotIdx].Add(record);

        if(!chunksByPool.TryGetValue(obj,out var list))
        {
            list = new List<PoolEntry>();
            chunksByPool[obj] = list;
        }
        list.Add(new PoolEntry(face, lod, packed, actualVertCount, sliceIndex, tier, normalSlice, normalTierByte));

        // Register trees for GPU instanced rendering
        if (TreeRenderer.HasActiveSystem)
        {
            int flatIdx = flatGridBFS.ChunkKeyToFlat(packed, face);
            int treeDist = flatGridBFS.GetBFSDepth(flatIdx);
            bool isInPriorityRing = ringPositions != null && ringPositions.Contains(new ChunkKey(packed, face));
            TreeRenderer.Instance.RegisterChunk(packed, face, lod, treeDist, isInPriorityRing);//All TreeRenderer logic is obsolete, no more TreeRenderer in use. Can be removed in future versions.
        }
        // Legacy tree collider path removed — use ImpostorRenderer.

        // Build/refresh seams (stitch or visual skirt) to all 4 same-face neighbours.
        RebuildSeamsForChunk(packed, face);

        // Notify subscribers (e.g. ChunkObjectLoader) that a chunk is ready.
        OnChunkCreated?.Invoke(packed, face, lod);

        // Phase B: register this single-chunk renderer with VisibilitySystem so its
        // MeshRenderer.enabled is driven by horizon + frustum tests every frame.
        var scratch = EnsureBatchScratch(1);
        scratch[0] = slotIdx;
        RegisterRendererBatch(obj, renderer, scratch, 1);
        if (ImpostorRenderer.Instance != null)
        ImpostorRenderer.Instance.SetChunkLOD(slotIdx, lod);
    }


    // ========= REMOVAL ===========

    /// <summary>
    /// Zeroes out a chunk's triangle indices in the batch mesh, making them degenerate (invisible).
    /// O(triCount) instead of O(batch_size) — avoids regenerating all other chunks in the batch.
    /// </summary>
    private void DegenerateChunkTriangles(GameObject batchObj, PoolEntry entry)
    {
        if (entry.triCount <= 0) return;
        var mf = batchObj.GetComponent<MeshFilter>();
        if (mf == null || mf.mesh == null) return;
        Mesh mesh = mf.mesh;

        mesh.GetTriangles(reusableTriList, 0);
        int end = entry.triOffset + entry.triCount;
        if (end > reusableTriList.Count) return; // safety
        for (int i = entry.triOffset; i < end; i++)
            reusableTriList[i] = 0; // degenerate: all reference vertex 0
        mesh.SetTriangles(reusableTriList, 0);
    }

    /// <summary>
    /// Removes a PoolEntry from a batch's tracking list. If the batch is now empty, destroys it.
    /// Returns true if the batch was destroyed.
    /// </summary>
    private bool RemoveEntryFromBatchTracking(GameObject batchObj, PoolEntry toRemove)
    {
        if (!chunksByPool.TryGetValue(batchObj, out var entries)) return false;

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Equals(toRemove))
            {
                entries.RemoveAt(i);
                break;
            }
        }

        if (entries.Count == 0)
        {
            chunksByPool.Remove(batchObj);
            pendingBatchRebuilds.Remove(batchObj);
            batchesBeingRebuilt.Remove(batchObj);
            ReturnToPool(batchObj);
            return true;
        }
        return false;
    }

    private void RemoveChunkImmediate(int packed, FaceId face, ChunkRecord chunk)
    {
        // Drop any skirts attached to this chunk and clear its edge cache. Adjacent chunks
        // that survive will re-evaluate their skirts the next time they're rebuilt; until
        // then they keep their existing skirt geometry, which is acceptable as a transient.
        DropChunkEdgesAndSkirts(packed, face);

        // Unregister trees from renderer - only if this chunk's LOD is still the registered one.
        // If a newer LOD has already been registered (e.g. LOD0 replaced LOD2), don't erase it.
        if (TreeRenderer.HasActiveSystem &&
            TreeRenderer.Instance.GetRegisteredLOD(packed, face) == chunk.lod)
        {
            TreeRenderer.Instance.UnregisterChunk(packed, face);
        }

        // Notify subscribers that this chunk's objects should be despawned.
        OnChunkRemoved?.Invoke(packed, face, chunk.lod);

        GameObject poolObj = chunk.gameObject;
        if(poolObj == null) return;

        // Create the entry to match
        PoolEntry toRemove = new PoolEntry(face, chunk.lod, packed, 0, 
            chunk.splatSliceIndex, chunk.splatTier);

        if(chunksByPool.TryGetValue(poolObj, out var entries))
        {
            // Find the actual entry with offset metadata for degenerate removal
            PoolEntry actualEntry = default;
            bool found = false;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Equals(toRemove))
                {
                    actualEntry = entries[i];
                    found = true;
                    break;
                }
            }

            if (entries.Count == 1 && found)
            {
                // Single-chunk — return to pool for reuse
                if (chunk.splatSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferRelease(chunk.splatSliceIndex, chunk.splatTier);
                if (chunk.normalSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferReleaseNormal(chunk.normalSliceIndex, chunk.normalTier);

                chunksByPool.Remove(poolObj);
                pendingBatchRebuilds.Remove(poolObj);
                batchesBeingRebuilt.Remove(poolObj);
                ReturnToPool(poolObj);
            }
            else if (found)
            {
                // Multi-chunk batch — degenerate triangles instead of full rebuild.
                // The chunk becomes invisible instantly; the batch mesh stays valid.
                // Release splatmap slice (degenerate triangles won't sample it).
                if (chunk.splatSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferRelease(chunk.splatSliceIndex, chunk.splatTier);
                if (chunk.normalSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferReleaseNormal(chunk.normalSliceIndex, chunk.normalTier);

                DegenerateChunkTriangles(poolObj, actualEntry);
                RemoveEntryFromBatchTracking(poolObj, toRemove);
            }
        }

        // Update chunk records
        int chunkSlotIdx = GetStorageIndex(packed, face);
        chunks[chunkSlotIdx].Remove(chunk);
        if (chunks[chunkSlotIdx].Count == 0) // If this is the last record for this slot
        {
            if (ImpostorRenderer.Instance != null)
                ImpostorRenderer.Instance.SetChunkLOD(chunkSlotIdx, 255);
        }
        if (chunk.lod == 0 && ImpostorRenderer.Instance != null)
        {
            ImpostorRenderer.Instance.ClearActiveLOD0Heightmap(GetStorageIndex(packed, face));
        }
    }

    /// <summary>
    /// Process all pending batch rebuilds. Called at the end of a generation cycle.
    /// Completes all pending rebuilds to avoid orphaned batches.
    /// </summary>
    private IEnumerator ProcessPendingBatchRebuilds(int genVersion)
    {
        if (pendingBatchRebuilds.Count == 0) yield break;
        
        // Take snapshot of pending work to avoid modification during iteration
        var workItems = new List<(GameObject batch, HashSet<PoolEntry> toRemove)>();
        foreach (var kvp in pendingBatchRebuilds)
        {
            if (kvp.Key != null)
                workItems.Add((kvp.Key, kvp.Value));
        }
        pendingBatchRebuilds.Clear();
        
        // Use fast degenerate removal instead of full rebuild
        foreach (var (batch, toRemove) in workItems)
        {
            if (batch == null) continue;
            if (!chunksByPool.TryGetValue(batch, out var batchEntries)) continue;

            Mesh mesh = null;
            bool meshDirty = false;
            var mf = batch.GetComponent<MeshFilter>();
            if (mf != null && mf.mesh != null)
            {
                mesh = mf.mesh;
                mesh.GetTriangles(reusableTriList, 0);
            }

            foreach (var removeEntry in toRemove)
            {
                // Release splatmap
                if (removeEntry.splatSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferRelease(removeEntry.splatSliceIndex, removeEntry.splatTier);
                if (removeEntry.normalSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferReleaseNormal(removeEntry.normalSliceIndex, removeEntry.normalTier);

                // Unregister trees
                if (TreeRenderer.HasActiveSystem &&
                    TreeRenderer.Instance.GetRegisteredLOD(removeEntry.packed, removeEntry.face) == removeEntry.lod)
                    TreeRenderer.Instance.UnregisterChunk(removeEntry.packed, removeEntry.face);

                // Find and degenerate
                if (mesh != null)
                {
                    for (int i = 0; i < batchEntries.Count; i++)
                    {
                        if (batchEntries[i].Equals(removeEntry))
                        {
                            var actual = batchEntries[i];
                            int end = actual.triOffset + actual.triCount;
                            if (end <= reusableTriList.Count)
                            {
                                for (int t = actual.triOffset; t < end; t++)
                                    reusableTriList[t] = 0;
                                meshDirty = true;
                            }
                            batchEntries.RemoveAt(i);
                            break;
                        }
                    }
                }
                else
                {
                    for (int i = batchEntries.Count - 1; i >= 0; i--)
                        if (batchEntries[i].Equals(removeEntry)) { batchEntries.RemoveAt(i); break; }
                }

                // Update chunk records
                int rebuildSlotIdx = GetStorageIndex(removeEntry.packed, removeEntry.face);
                chunks[rebuildSlotIdx].RemoveByLodAndBatch(removeEntry.lod, batch);
            }

            if (meshDirty && mesh != null)
                mesh.SetTriangles(reusableTriList, 0);

            if (batchEntries.Count == 0)
            {
                chunksByPool.Remove(batch);
                batchesBeingRebuilt.Remove(batch);
                ReturnToPool(batch);
            }
            else
            {
                batchesBeingRebuilt.Remove(batch);
            }
        }

        yield break;
    }

    private IEnumerator RemoveChunksFromMeshes(Queue<(int packed, FaceId face, ChunkRecord chunk)>toRemove,
    int genVersion)
    {
        if(!ChunkManager.Instance.isGenerationVersionValid(genVersion))yield break;

        // Use fast degenerate-triangle removal: zero out each chunk's triangles in-place.
        // No mesh regeneration needed — the old batch mesh is patched directly.
        // Group removals by batch to minimise mesh.triangles get/set round-trips.
        var byPool = reusableByPool;
        // Return all value lists to pool and clear
        foreach (var kvp in byPool)
        {
            kvp.Value.Clear();
            byPoolListPool.Push(kvp.Value);
        }
        byPool.Clear();

        while(toRemove.Count > 0)
        {
            var item = toRemove.Dequeue();
            GameObject obj = item.chunk.gameObject;
            if (obj == null) continue;

            PoolEntry matchEntry = new PoolEntry(item.face, item.chunk.lod, item.packed, 0, 
                item.chunk.splatSliceIndex, item.chunk.splatTier);

            if(!byPool.TryGetValue(obj, out var list))
            {
                list = byPoolListPool.Count > 0 ? byPoolListPool.Pop() : new List<(PoolEntry, ChunkRecord, int, FaceId)>();
                byPool[obj] = list;
            }
            list.Add((matchEntry, item.chunk, item.packed, item.face));
        }

        foreach(var kvp in byPool)
        {
            GameObject obj = kvp.Key;
            if(obj == null) continue;

            var removals = kvp.Value;

            // Get the batch's entry list to find actual entries with offset metadata
            if (!chunksByPool.TryGetValue(obj, out var batchEntries))
            {
                // Not a batch (shouldn't happen) — just clean up chunk records
                foreach (var (matchEntry, record, packed, face) in removals)
                {
if (TreeRenderer.HasActiveSystem &&
                    TreeRenderer.Instance.GetRegisteredLOD(packed, face) == record.lod)
                    TreeRenderer.Instance.UnregisterChunk(packed, face);

                    var cs = GetStorageIndex(packed, face);
                    chunks[cs].Remove(record);
                }
                continue;
            }

            // For multi-chunk remove: get mesh triangles once, patch all removed entries, set once
            Mesh mesh = null;
            bool meshDirty = false;
            bool isSingleChunkBatch = batchEntries.Count <= removals.Count; // will be empty after removals

            if (!isSingleChunkBatch)
            {
                var mf = obj.GetComponent<MeshFilter>();
                if (mf != null && mf.mesh != null)
                {
                    mesh = mf.mesh;
                    mesh.GetTriangles(reusableTriList, 0);
                }
            }

            foreach (var (matchEntry, record, packed, face) in removals)
            {
                // Unregister trees
                if (TreeRenderer.HasActiveSystem &&
                    TreeRenderer.Instance.GetRegisteredLOD(packed, face) == record.lod)
                    TreeRenderer.Instance.UnregisterChunk(packed, face);

                // Release splatmap
                if (record.splatSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferRelease(record.splatSliceIndex, record.splatTier);
                if (record.normalSliceIndex >= 0 && chunkMaterialManager != null)
                    chunkMaterialManager.DeferReleaseNormal(record.normalSliceIndex, record.normalTier);

                // Find actual entry with offsets and degenerate its triangles
                if (mesh != null)
                {
                    for (int i = 0; i < batchEntries.Count; i++)
                    {
                        if (batchEntries[i].Equals(matchEntry))
                        {
                            var actual = batchEntries[i];
                            int end = actual.triOffset + actual.triCount;
                            if (end <= reusableTriList.Count)
                            {
                                for (int t = actual.triOffset; t < end; t++)
                                    reusableTriList[t] = 0;
                                meshDirty = true;
                            }
                            batchEntries.RemoveAt(i);
                            break;
                        }
                    }
                }
                else
                {
                    // Single-chunk or missing mesh — just remove tracking
                    for (int i = batchEntries.Count - 1; i >= 0; i--)
                    {
                        if (batchEntries[i].Equals(matchEntry)) { batchEntries.RemoveAt(i); break; }
                    }
                }

                // Update chunk records
                int removeSlotIdx = GetStorageIndex(packed, face);
                chunks[removeSlotIdx].Remove(record);
                if (chunks[removeSlotIdx].Count == 0) // If this is the last record for this slot
                {
                    if (ImpostorRenderer.Instance != null)
                        ImpostorRenderer.Instance.SetChunkLOD(removeSlotIdx, 255);
                }
                if (record.lod == 0 && ImpostorRenderer.Instance != null)
                {
                    ImpostorRenderer.Instance.ClearActiveLOD0Heightmap(GetStorageIndex(packed, face));
                }
            }

            // Apply patched triangles to mesh once per batch
            if (meshDirty && mesh != null)
                mesh.SetTriangles(reusableTriList, 0);

            // Destroy batch if now empty
            if (batchEntries.Count == 0)
            {
                chunksByPool.Remove(obj);
                pendingBatchRebuilds.Remove(obj);
                batchesBeingRebuilt.Remove(obj);
                ReturnToPool(obj);
            }
        }

        yield break; // coroutine signature satisfied, no actual yielding needed
    }

    // ============= BATCHING ==============

    private bool IsInNonBatchedRing(int centerPacked, FaceId centerFace, int chunkPacked, FaceId chunkFace)
    {
        if (debugDisableBatching) return true; // debug flag — see TerrainManagementSettings
        if (nonBatchedOuterChunkRings <= 0) return false;
        int dist = flatGridBFS.ChebyshevDistance(
            centerFlatIdx,
            flatGridBFS.ChunkKeyToFlat(chunkPacked, chunkFace));
        return dist <= nonBatchedOuterChunkRings;
    }

    /// <summary>
    /// Creates a single batched GameObject from NativeArray data. Disposes nothing — caller manages lifetime.
    /// </summary>
    private void CreateBatchedChunk(NativeArray<Vector3> verts, NativeArray<int> tris, NativeArray<Vector2> uvs,
        NativeArray<Vector3> normals, NativeArray<Vector4> uv1, NativeArray<Vector2> uv2,
        NativeArray<Vector2> uv3, NativeArray<Vector2> uv4,
        int vertCount, int triCount, List<PoolEntry> entries, Texture2D canopyMaskAtlas = null)
    {
        // PendBatchedEdgeCache already populated edgeCache for each entry during Batcher.Add.
        // The removal loop below calls DropChunkEdgesAndSkirts which clears those entries.
        // Save them here so we can restore after removal — the edge data is still valid
        // (it was built from the NEW mesh data, not the old chunk's).
        int entryCount = entries.Count;
        if (batchEdgeSaveIndices == null || batchEdgeSaveIndices.Length < entryCount)
        {
            batchEdgeSaveIndices = new int[entryCount];
            batchEdgeSaveData = new EdgeData[entryCount];
        }
        for (int i = 0; i < entryCount; i++)
        {
            int sIdx = GetStorageIndex(entries[i].packed, entries[i].face);
            batchEdgeSaveIndices[i] = sIdx;
            batchEdgeSaveData[i] = edgeCache[sIdx];
        }

        foreach (var entry in entries)
        {
            int entrySlotIdx = GetStorageIndex(entry.packed, entry.face);
            ref ChunkSlot entrySlot = ref chunks[entrySlotIdx];
            if (!entrySlot.IsEmpty)
            {
                // Copy to temp list to avoid modifying slot during iteration
                if (tempChunkRecordList == null) tempChunkRecordList = new List<ChunkRecord>();
                tempChunkRecordList.Clear();
                for (int i = 0; i < entrySlot.Count; i++)
                    tempChunkRecordList.Add(entrySlot[i]);
                foreach (var chunk in tempChunkRecordList)
                    RemoveChunkImmediate(entry.packed, entry.face, chunk);
            }
        }

        // Restore edge caches that were cleared by DropChunkEdgesAndSkirts during removal.
        for (int i = 0; i < entryCount; i++)
            edgeCache[batchEdgeSaveIndices[i]] = batchEdgeSaveData[i];

        GameObject obj;
        Mesh mesh;
        MeshRenderer renderer;

        if (chunkPool.Count > 0)
        {
            obj = chunkPool.Pop();
            obj.name = "batch";
            obj.SetActive(true);
            mesh = obj.GetComponent<MeshFilter>().sharedMesh;
            mesh.Clear();
            renderer = obj.GetComponent<MeshRenderer>();
        }
        else
        {
            
            obj = new GameObject("batch");
            obj.transform.SetParent(chunkPoolParent, false);
            mesh = new Mesh();
            obj.AddComponent<MeshFilter>().mesh = mesh;
            renderer = obj.AddComponent<MeshRenderer>();
        }

        MeshUtils.ApplyBatchToMesh(mesh, verts, tris, uvs, normals, vertCount, triCount, uv1, uv2, uv3, uv4);

        if(chunkMaterialManager != null && chunkMaterialManager.SharedBatchedMaterial != null && loadTextures)
        {
            renderer.sharedMaterial = chunkMaterialManager.SharedBatchedMaterial;
            MaterialPropertyBlock mpb = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb);
            if (canopyMaskAtlas != null)
            {
                // Each batch gets its own atlas in a per-renderer MPB so flushes
                // don't overwrite each other's atlas on the shared material.
                mpb.SetTexture("_CanopyMaskAtlas", canopyMaskAtlas);
            }
            renderer.SetPropertyBlock(mpb);
            renderer.sharedMaterial.EnableKeyword("USE_SPLATMAPS");
        }
        else
        {
            renderer.sharedMaterial = tempMat;
        }

        var poolList = new List<PoolEntry>(entries);
        chunksByPool[obj] = poolList;

        foreach (var entry in entries)
        {
            ChunkRecord record = new ChunkRecord(entry.lod, obj, entry.splatSliceIndex, entry.splatTier,
                entry.normalSliceIndex, entry.normalTier);
            chunks[GetStorageIndex(entry.packed, entry.face)].Add(record);
        }

        // Patch each batched sub-chunk's edge cache with the batch renderer (cached eagerly
        // by PendBatchedEdgeCache with a null renderer placeholder), then rebuild any skirts
        // that touch each sub-chunk.
        foreach (var entry in entries)
        {
            int sIdx = GetStorageIndex(entry.packed, entry.face);
            if (edgeCache[sIdx].vertsRight != null)
                edgeCache[sIdx].sourceRenderer = renderer;
            RebuildSeamsForChunk(entry.packed, entry.face);
        }

        // Register trees synchronously — batch creation is already frame-budgeted
        // by the batcher's opsThisFrame limit, so registrations are naturally spread
        // across frames. Deferring to Phase 4b caused trees to flash out for hundreds
        // of frames while draining 1 registration per frame.
        if (TreeRenderer.HasActiveSystem)
        {
            foreach (var entry in entries)
            {
                int flatIdx = flatGridBFS.ChunkKeyToFlat(entry.packed, entry.face);
                int treeDist = flatGridBFS.GetBFSDepth(flatIdx);
                bool isInPriorityRing = ringPositions != null && ringPositions.Contains(new ChunkKey(entry.packed, entry.face));
                TreeRenderer.Instance.RegisterChunk(entry.packed, entry.face, entry.lod, treeDist, isInPriorityRing);
            }
        }

        // Phase B: register the merged batch with VisibilitySystem so the shared
        // MeshRenderer toggles based on a tight per-batch bound (made spatially
        // coherent by the sector-stable-sort upstream).
        var scratch = EnsureBatchScratch(entryCount);
        for (int i = 0; i < entryCount; i++)
            scratch[i] = batchEdgeSaveIndices[i]; // already-computed storage indices
        RegisterRendererBatch(obj, renderer, scratch, entryCount);

        if (ImpostorRenderer.Instance != null)
        {
            for (int i = 0; i < entryCount; i++)
            {
                ImpostorRenderer.Instance.SetChunkLOD(batchEdgeSaveIndices[i], entries[i].lod);
            }
        }
    }

    /// <summary>
    /// Accumulates chunk mesh data into NativeArrays and flushes into batched GameObjects when vertex limit is reached.
    /// </summary>
    private class ChunkBatcher : IDisposable
    {
        // Cached canopy mask tile pixels keyed by chunk id + tile shape.
        // Static so it persists across generation cycles; cleared when tree data changes.
        // This pre-bakes the expensive per-tree blob painting so repeat visits to the same
        // chunk LOD re-use the cached pixels rather than recomputing.
        private static readonly Dictionary<(int, int, byte, int, int), Color32[]> s_maskTileCache
            = new Dictionary<(int, int, byte, int, int), Color32[]>();

        /// <summary>Clears the pre-baked canopy mask tile cache (call when tree data changes).</summary>
        public static void ClearMaskCache() => s_maskTileCache.Clear();
        private NativeArray<Vector3> batchVerts;
        private NativeArray<Vector3> batchNormals;
        private NativeArray<int> batchTris;
        private NativeArray<Vector2> batchUvs;
        private NativeArray<Vector4> batchUv1;
        private NativeArray<Vector2> batchUv2;
        private NativeArray<Vector2> batchUv3;
        private NativeArray<Vector2> batchUv4;
        private int vertCount;
        private int triCount;
        private readonly List<PoolEntry> batchEntries = new List<PoolEntry>();
        private readonly ChunkRegistry registry;
        private readonly ushort maxVertsPerMesh;
        private bool disposed;

        // Reusable managed staging arrays — populated once per Add() canopy pass to avoid
        // NativeArray.get_Item overhead (bounds-check + safety-handle) inside TryProjectPointToChunkUV.
        private int[]     meshTrisStaging  = Array.Empty<int>();
        private Vector3[] meshVertsStaging = Array.Empty<Vector3>();
        private Vector2[] meshUvsStaging   = Array.Empty<Vector2>();

        // Reusable float accumulators for canopy mask painting — avoids 4× heap allocation per chunk.
        private float[]   canopyAccumR = Array.Empty<float>();
        private float[]   canopyAccumG = Array.Empty<float>();
        private float[]   canopyAccumB = Array.Empty<float>();
        private float[]   canopyAccumA = Array.Empty<float>();

        // Reusable staging buffer for current-chunk tree copy (shared decode buffer workaround).
        private TreeDecoder.DecodedTreeInstance[] currentTreesStaging = Array.Empty<TreeDecoder.DecodedTreeInstance>();

        // Per-chunk canopy mask tiles, accumulated during Add() and packed into an atlas during Flush().
        private readonly List<CanopyMaskTile> pendingMaskTiles = new List<CanopyMaskTile>();

        private struct CanopyMaskTile
        {
            public Color32[] pixels;
            public int size;
            public int interiorSize;
            public int border;
            public int vertOffset; // start vertex index in batch arrays
            public int vertCount;  // number of vertices in this chunk
        }

        public ChunkBatcher(ChunkRegistry registry, ushort maxVertsPerMesh)
        {
            this.registry = registry;
            this.maxVertsPerMesh = maxVertsPerMesh;
            // Allocate max-size buffers once, reuse across flushes
            batchVerts = new NativeArray<Vector3>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchNormals = new NativeArray<Vector3>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchTris = new NativeArray<int>(maxVertsPerMesh * 6, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchUvs = new NativeArray<Vector2>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchUv1 = new NativeArray<Vector4>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchUv2 = new NativeArray<Vector2>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchUv3 = new NativeArray<Vector2>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            batchUv4 = new NativeArray<Vector2>(maxVertsPerMesh, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            vertCount = 0;
            triCount = 0;
        }

        public void Add(int packed, FaceId face, byte lod, ref ChunkManager.MeshData data)
        {
            if (vertCount > 0 && vertCount + data.vertCount > maxVertsPerMesh)
                Flush();

            // Copy verts and normals with offset
            NativeArray<Vector3>.Copy(data.verts, 0, batchVerts, vertCount, data.vertCount);
            NativeArray<Vector3>.Copy(data.normals, 0, batchNormals, vertCount, data.vertCount);
            NativeArray<Vector2>.Copy(data.uvs, 0, batchUvs, vertCount, data.vertCount);

            // Copy tris with vertex offset
            for (int i = 0; i < data.triCount; i++)
                batchTris[triCount + i] = data.tris[i] + vertCount;

             // Compute per-vertex splatmap metadata (UV1) and normal-map metadata (UV2)
            float sliceIndex = -1f;
            float tier = 0f;
            float normalSliceF = -1f;
            float normalTierF = 0f;
            int allocatedNormalSlice = -1;
            byte allocatedNormalTier = 0;
            Vector4 uvOffsetScale = new Vector4(0, 0, 1, 1);
            Vector4 normalUvOffsetScale = new Vector4(0, 0, 1, 1);

            if (registry.textureStreamer != null && registry.chunkMaterialManager != null && registry.loadTextures)
            {
                STPTMEUtils.ReadFourSBytesFromInt(packed,
                    out sbyte heightmapX, out sbyte heightmapY, out sbyte chunkX, out sbyte chunkY);
                Vector2SByte map = new Vector2SByte(heightmapX, heightmapY);

                byte tierByte = registry.textureStreamer.GetTierForLOD(lod);
                tier = tierByte;

                // UNIFORM CELL CHECK — must mirror CreateChunk's non-batched path.
                // Uniform cells have NO splatmap baked (that's the point of classifying them),
                // so GetOrLoadSync returns an invalid tile and sliceIndex stays -1. That -1 goes
                // into the vertex stream, and since batched geometry reads _UniformDominantLayer
                // from sharedBatchedMaterial (permanently -1, never set per-chunk), the shader
                // took the MULTI-LAYER path and sampled the splat array at index -1 — which
                // clamps to slice 0, making the whole cell render whatever cell happens to own
                // slice 0. That's the "large rectangular blocks of the wrong texture at LOD1+"
                // bug: cell-sized because uniform classification is per-cell, and it looked like
                // "sand spreading" only because slice 0 was often a sand cell — uniform GRASS
                // cells were rendering it too.
                //
                // sharedBatchedMaterial can't carry a per-chunk value, so the dominant layer is
                // encoded into sliceIndex as -(layer + 2): -2 => layer 0, -3 => layer 1, etc.
                // The shader decodes this under BATCHED_CHUNKS. -1 keeps its old meaning
                // ("no splatmap, not uniform" — a genuine missing-file case).
                int uniformLayer = registry.chunkMaterialManager.GetUniformDominantLayer(map.x, map.y, face);
                if (uniformLayer >= 0)
                {
                    sliceIndex = -(uniformLayer + 2);
                    // splatUV is unused on the uniform path (it's triplanar from positionWS),
                    // so identity offset/scale is fine here.
                    uvOffsetScale = new Vector4(0f, 0f, 1f, 1f);
                }

                TextureStreamer.SplatmapTile tile = uniformLayer >= 0
                    ? default
                    : registry.textureStreamer.GetOrLoadSync(map, tierByte, face);

                int slice = -1;
                if (tile.IsValid)
                {
                    // Sample-space exact — see ComputeChunkUVOffsetScaleExact. Chunk widths are
                    // not uniform (the last chunk on each axis absorbs the integer-division
                    // remainder), so an even 1/chunksPerAxis split misaligns its textures by a
                    // texel against its neighbour.
                    uvOffsetScale = ChunkMaterialManager.ComputeChunkUVOffsetScaleExact(
                        data.sampleOffsetX, Mathf.Max(1, data.edgeWidth - 1), data.cellSamplesX,
                        data.sampleOffsetY, Mathf.Max(1, data.edgeHeight - 1), data.cellSamplesY,
                        tile.borderPixels, tile.width, tile.height);

                    slice = registry.chunkMaterialManager.AllocateSlice(map, tierByte, face, tile);
                    sliceIndex = slice;
                }

                if (uniformLayer < 0 && !tile.IsValid)
                {
                    Debug.LogError($"[SplatMissing] map=({map.x},{map.y}) face={face} lod={lod} tier={tierByte} " +
                        $"— no splatmap tile, will render layer 0 solid.");
                }

                // Heightmap-derived normal map (independent tier system).
                if (registry.textureStreamer.HasHeightmapNormals && registry.chunkMaterialManager.NormalsEnabled)
                {
                    allocatedNormalTier = registry.textureStreamer.GetNormalTierForLOD(lod);
                    TextureStreamer.NormalTile normTile = registry.textureStreamer.GetOrLoadNormalSync(map, allocatedNormalTier, face);
                    if (normTile.IsValid)
                    {
                        normalUvOffsetScale = ChunkMaterialManager.ComputeChunkUVOffsetScaleExact(
                            data.sampleOffsetX, Mathf.Max(1, data.edgeWidth - 1), data.cellSamplesX,
                            data.sampleOffsetY, Mathf.Max(1, data.edgeHeight - 1), data.cellSamplesY,
                            normTile.borderPixels, normTile.width, normTile.height);
                        allocatedNormalSlice = registry.chunkMaterialManager.AllocateNormalSlice(
                            map, allocatedNormalTier, face, normTile);
                        normalSliceF = allocatedNormalSlice;
                        normalTierF = allocatedNormalTier;
                    }
                }

                if (tile.IsValid)
                {
                    batchEntries.Add(new PoolEntry(face, lod, packed, data.vertCount, vertCount, triCount, data.triCount,
                        slice, tierByte, allocatedNormalSlice, allocatedNormalTier));
                }
                else
                {
                    batchEntries.Add(new PoolEntry(face, lod, packed, data.vertCount, vertCount, triCount, data.triCount,
                        -1, 0, allocatedNormalSlice, allocatedNormalTier));
                }
            }
            else
            {
                batchEntries.Add(new PoolEntry(face, lod, packed, data.vertCount, vertCount, triCount, data.triCount));
            }

            // Write pre-computed splatmap UV into UV1 and normal slice/tier into UV2 for each vertex
            for (int i = 0; i < data.vertCount; i++)
            {
                Vector2 baseUV = data.uvs[i];
                float splatU = baseUV.x * uvOffsetScale.z + uvOffsetScale.x;
                float splatV = baseUV.y * uvOffsetScale.w + uvOffsetScale.y;
                float normalU = baseUV.x * normalUvOffsetScale.z + normalUvOffsetScale.x;
                float normalV = baseUV.y * normalUvOffsetScale.w + normalUvOffsetScale.y;
                // Clamp to avoid sampling outside tile
                splatU = Mathf.Clamp(splatU, 1e-5f, 1f - 1e-5f);
                splatV = Mathf.Clamp(splatV, 1e-5f, 1f - 1e-5f);
                normalU = Mathf.Clamp(normalU, 1e-5f, 1f - 1e-5f);
                normalV = Mathf.Clamp(normalV, 1e-5f, 1f - 1e-5f);
                batchUv1[vertCount + i] = new Vector4(splatU, splatV, sliceIndex, tier);
                batchUv2[vertCount + i] = new Vector2(normalSliceF, normalTierF);
                batchUv3[vertCount + i] = new Vector2(normalU, normalV);
            }

            // ===== Canopy overlay system (per-prototype, mask-textured, shader-blended) =====
            // Each tree prototype has its own CanopyMaskSettings[] per LOD — no global LOD gate.
            // UV0.xy: x = palette index (0-4), y = canopy mode (0=none, 2=atlas-colour).
            // UV4.xy = local chunk UV for canopy mask atlas sampling.

            var protoReg = ChunkManager.Instance != null ? ChunkManager.Instance.TreePrototypes : null;

            // Always initialize UV0 and UV4 (prevents stale values from pool reuse).
            for (int i = 0; i < data.vertCount; i++)
            {
                batchUvs[vertCount + i] = Vector2.zero;
                batchUv4[vertCount + i] = data.uvs[i];
            }

            var manager = ChunkManager.Instance;
            var treesSeg = manager != null
                ? manager.GetDecodedTreesForChunk(packed, face)
                : default(ArraySegment<TreeDecoder.DecodedTreeInstance>);
            TreeDecoder.DecodedTreeInstance[] currentTrees = null;
            if (treesSeg.Count > 0)
            {
                int treeCount = treesSeg.Count;
                if (currentTreesStaging.Length < treeCount)
                    currentTreesStaging = new TreeDecoder.DecodedTreeInstance[treeCount * 2];
                currentTrees = currentTreesStaging;
                Array.Copy(treesSeg.Array, treesSeg.Offset, currentTrees, 0, treeCount);
                treesSeg = new ArraySegment<TreeDecoder.DecodedTreeInstance>(currentTrees, 0, treeCount);
            }

            if (protoReg != null && protoReg.prototypes != null && manager != null)
            {
                // Compute effective tile dimensions: max maskSize and max blob radius across
                // all prototypes that have canopy mask enabled at this LOD.
                int effectiveMaskSize = 4;
                int effectiveMaxRadius = 1;
                bool useMask = false;

                foreach (var p in protoReg.prototypes)
                {
                    if (p == null || !p.canopyOverlayEnabled || p.canopyPaletteIndex < 0) continue;
                    var pms = p.GetCanopyMaskSettingsForLOD(lod);
                    if (pms == null || !pms.enabled) continue;
                    useMask = true;
                    int pSize   = Mathf.Clamp(pms.maskSize, 4, 64);
                    int pRadius = Mathf.Max(1, Mathf.RoundToInt(pSize * Mathf.Clamp(pms.softRadius, 0.1f, 1f) * 0.6f));
                    if (pSize   > effectiveMaskSize)   effectiveMaskSize   = pSize;
                    if (pRadius > effectiveMaxRadius)  effectiveMaxRadius  = pRadius;
                }

                int maskSize = 0;
                int maskTileSize = 0;
                int maskBorder = 0;
                Color32[] maskPixels = null;
                (int, int, byte, int, int) cacheKey = default;

                if (useMask)
                {
                    maskSize     = effectiveMaskSize;
                    maskBorder   = effectiveMaxRadius;   // border = worst-case blob radius
                    maskTileSize = maskSize + maskBorder * 2;
                    cacheKey     = (packed, (int)face, lod, maskSize, maskBorder);

                    if (s_maskTileCache.TryGetValue(cacheKey, out Color32[] cached))
                        maskPixels = cached;
                    else
                        maskPixels = new Color32[maskTileSize * maskTileSize];
                }

                bool anyCanopy = false;
                int dominantCanopyType = -1;
                int treeEnd = treesSeg.Offset + treesSeg.Count;
                bool needsPaint = useMask && maskPixels != null && !s_maskTileCache.ContainsKey(cacheKey);

                for (int ti = treesSeg.Offset; ti < treeEnd; ti++)
                {
                    ref readonly var tree = ref treesSeg.Array[ti];
                    if (tree.prototypeIndex >= protoReg.prototypes.Length) continue;
                    var proto = protoReg.prototypes[tree.prototypeIndex];
                    if (!proto.canopyOverlayEnabled || proto.canopyPaletteIndex < 0) continue;
                    var protoMask = proto.GetCanopyMaskSettingsForLOD(lod);
                    if (protoMask == null || !protoMask.enabled) continue;
                    if (dominantCanopyType < 0) dominantCanopyType = proto.canopyPaletteIndex;
                    anyCanopy = true;
                }

                if (needsPaint)
                {
                    int pixelCount = maskTileSize * maskTileSize;

                    // Grow or reuse batcher-level accum arrays — only clear the used portion.
                    if (canopyAccumR.Length < pixelCount)
                    {
                        canopyAccumR = new float[pixelCount];
                        canopyAccumG = new float[pixelCount];
                        canopyAccumB = new float[pixelCount];
                        canopyAccumA = new float[pixelCount];
                    }
                    else
                    {
                        Array.Clear(canopyAccumR, 0, pixelCount);
                        Array.Clear(canopyAccumG, 0, pixelCount);
                        Array.Clear(canopyAccumB, 0, pixelCount);
                        Array.Clear(canopyAccumA, 0, pixelCount);
                    }
                    float[] accumR = canopyAccumR;
                    float[] accumG = canopyAccumG;
                    float[] accumB = canopyAccumB;
                    float[] accumA = canopyAccumA;

                    bool hasCacheForProjection = registry.canopyUVCache != null && registry.canopyUVCache.IsLoaded;

                    // Copy mesh geometry to managed arrays only when the UV cache is absent —
                    // once the cache is baked, mesh projection is never needed at runtime.
                    if (!hasCacheForProjection)
                    {
                        if (meshTrisStaging.Length  < data.triCount)  meshTrisStaging  = new int[data.triCount];
                        if (meshVertsStaging.Length < data.vertCount) meshVertsStaging = new Vector3[data.vertCount];
                        if (meshUvsStaging.Length   < data.vertCount) meshUvsStaging   = new Vector2[data.vertCount];
                        NativeArray<int>.Copy(data.tris,    meshTrisStaging,  data.triCount);
                        NativeArray<Vector3>.Copy(data.verts, meshVertsStaging, data.vertCount);
                        NativeArray<Vector2>.Copy(data.uvs,   meshUvsStaging,   data.vertCount);
                    }

                    STPTMEUtils.ReadFourSBytesFromInt(packed,
                        out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
                    int mapsPerRow    = registry.maxX - registry.minX + 1;
                    int chunksPerMap  = registry.numberOfChunks * registry.numberOfChunks;
                    int lastChunkIdx  = registry.numberOfChunks - 1;

                    for (int nDy = -1; nDy <= 1; nDy++)
                    {
                        for (int nDx = -1; nDx <= 1; nDx++)
                        {
                            ArraySegment<TreeDecoder.DecodedTreeInstance> sourceSeg;
                            int sourceChunkPacked;
                            FaceId sourceChunkFace;
                            if (nDx == 0 && nDy == 0)
                            {
                                sourceSeg        = treesSeg;
                                sourceChunkPacked = packed;
                                sourceChunkFace   = face;
                            }
                            else
                            {
                                if (!STPTMEUtils.TryOffsetChunkUnpacked(
                                    mapX, mapY, chunkX, chunkY, face, nDx, nDy,
                                    registry.numberOfChunks, lastChunkIdx, registry.minX, registry.maxX,
                                    mapsPerRow, chunksPerMap, out ChunkKey neighborKey, out _))
                                    continue;

                                sourceSeg         = manager.GetDecodedTreesForChunk(neighborKey.packed, neighborKey.face);
                                sourceChunkPacked  = neighborKey.packed;
                                sourceChunkFace    = neighborKey.face;
                            }

                            // Compute the source chunk's storage slot once per nDx/nDy pair
                            // (used by the cache path to look up pre-baked UVs).
                            int sourceSlot = hasCacheForProjection
                                ? FaceIdUtility.GetStorageIndex(
                                    registry.globalIndexCalculator.GetIndex(sourceChunkPacked),
                                    sourceChunkFace)
                                : -1;

                            int sourceEnd = sourceSeg.Offset + sourceSeg.Count;
                            for (int ti = sourceSeg.Offset; ti < sourceEnd; ti++)
                            {
                                ref readonly var tree = ref sourceSeg.Array[ti];
                                if (tree.prototypeIndex >= protoReg.prototypes.Length) continue;
                                var proto = protoReg.prototypes[tree.prototypeIndex];
                                if (!proto.canopyOverlayEnabled || proto.canopyPaletteIndex < 0) continue;
                                if (proto.canopyPaletteIndex >= protoReg.canopyPalette.Length) continue;

                                // Per-prototype settings for this LOD
                                var protoMask = proto.GetCanopyMaskSettingsForLOD(lod);
                                if (protoMask == null || !protoMask.enabled) continue;
                                int   treeRadius = Mathf.Max(1, Mathf.RoundToInt(maskSize * Mathf.Clamp(protoMask.softRadius, 0.1f, 1f) * 0.6f));
                                float treeAlpha  = Mathf.Clamp01(protoMask.alphaMultiplier);
                                if (treeAlpha <= 0f) continue;

                                Color canopyColor = protoReg.canopyPalette[proto.canopyPaletteIndex].linear;

                                Vector2 treeUV;
                                int localTreeIndex = ti - sourceSeg.Offset;
                                if (hasCacheForProjection)
                                {
                                    // Fast path: O(1) array lookup, ~2 ns per tree.
                                    if (!registry.canopyUVCache.TryGetUV(sourceSlot, localTreeIndex, out treeUV))
                                        continue;
                                    // Shift own-chunk UV into current-chunk UV space.
                                    treeUV.x += nDx;
                                    treeUV.y += nDy;
                                }
                                else
                                {
                                    // Fallback: mesh projection (used before the cache is baked).
                                    if (!TryProjectPointToChunkUV(tree.worldPosition,
                                        data.triCount, meshTrisStaging,
                                        data.vertCount, meshVertsStaging, meshUvsStaging,
                                        out treeUV))
                                        continue;
                                }

                                float px = maskBorder + treeUV.x * (maskSize - 1);
                                float py = maskBorder + treeUV.y * (maskSize - 1);
                                if (px < -treeRadius || px > (maskTileSize - 1 + treeRadius)
                                    || py < -treeRadius || py > (maskTileSize - 1 + treeRadius))
                                    continue;

                                for (int dy = -treeRadius; dy <= treeRadius; dy++)
                                {
                                    for (int dx = -treeRadius; dx <= treeRadius; dx++)
                                    {
                                        float dist = Mathf.Sqrt(dx * dx + dy * dy) / (float)treeRadius;
                                        if (dist > 1f) continue;
                                        int sx = Mathf.RoundToInt(px + dx);
                                        int sy = Mathf.RoundToInt(py + dy);
                                        if (sx < 0 || sx >= maskTileSize || sy < 0 || sy >= maskTileSize) continue;
                                        float contribution = (1f - dist * dist) * treeAlpha;
                                        int midx = sy * maskTileSize + sx;
                                        accumR[midx] += canopyColor.r * contribution;
                                        accumG[midx] += canopyColor.g * contribution;
                                        accumB[midx] += canopyColor.b * contribution;
                                        accumA[midx] += contribution;
                                    }
                                }
                            }
                        }
                    }

                    for (int i = 0; i < pixelCount; i++)
                    {
                        float alpha = accumA[i];
                        if (alpha <= 1e-5f) continue;

                        float invAlpha = 1f / alpha;
                        float r = Mathf.Clamp01(accumR[i] * invAlpha);
                        float g = Mathf.Clamp01(accumG[i] * invAlpha);
                        float b = Mathf.Clamp01(accumB[i] * invAlpha);
                        float a = Mathf.Clamp01(alpha);
                        maskPixels[i] = new Color(r, g, b, a);
                    }
                }

                if (anyCanopy && dominantCanopyType >= 0)
                {
                    float canopyMode = useMask ? 2f : 1f;
                    for (int i = 0; i < data.vertCount; i++)
                        batchUvs[vertCount + i] = new Vector2(dominantCanopyType, canopyMode);
                }

                if (useMask && anyCanopy && maskPixels != null)
                {
                    if (!s_maskTileCache.ContainsKey(cacheKey))
                        s_maskTileCache[cacheKey] = maskPixels;

                    pendingMaskTiles.Add(new CanopyMaskTile
                    {
                        pixels = maskPixels,
                        size = maskTileSize,
                        interiorSize = maskSize,
                        border = maskBorder,
                        vertOffset = vertCount,
                        vertCount = data.vertCount
                    });
                }
            }

            vertCount += data.vertCount;
            triCount += data.triCount;

            // Cache edges for skirt stitching BEFORE disposing the per-chunk data. Batched
            // chunks share a single Renderer, so edge.sourceRenderer points to the batch.
            // Skirt material/MPB will then come from the batch material — visually OK since
            // the batched material is the shared atlas-bound terrain shader.
            registry.PendBatchedEdgeCache(packed, face, lod, ref data,
                (int)sliceIndex, (byte)tier, uvOffsetScale,
                allocatedNormalSlice, allocatedNormalTier, normalUvOffsetScale);

            data.Dispose();
        }

        public void Flush()
        {
            if (vertCount == 0) return;

            // Pack pending canopy mask tiles into an atlas; returns null if none.
            Texture2D canopyAtlas = null;
            if (pendingMaskTiles.Count > 0)
            {
                canopyAtlas = PackCanopyMaskAtlas();
                pendingMaskTiles.Clear();
            }

            registry.CreateBatchedChunk(batchVerts, batchTris, batchUvs, batchNormals, batchUv1, batchUv2, batchUv3, batchUv4, vertCount, triCount, batchEntries, canopyAtlas);
            vertCount = 0;
            triCount = 0;
            batchEntries.Clear();
        }

        /// <summary>
        /// Packs all pending canopy mask tiles into a single atlas texture.
        /// Returns the atlas Texture2D, or null if no tiles. Caller is responsible
        /// for assigning it to the per-batch MaterialPropertyBlock.
        /// </summary>
        private Texture2D PackCanopyMaskAtlas()
        {
            var protoReg = ChunkManager.Instance?.TreePrototypes;
            if (protoReg == null) return null;

            int atlasSize = Mathf.Clamp(protoReg.canopyMaskAtlasSize, 32, 4096);
            var atlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, mipChain: false, linear: true);
            atlas.filterMode = FilterMode.Bilinear;
            atlas.wrapMode = TextureWrapMode.Clamp;
            atlas.name = "CanopyMaskAtlas";

            // Clear atlas to black (zero alpha)
            var clearPixels = new Color32[atlasSize * atlasSize];
            atlas.SetPixels32(clearPixels);

            int cursorX = 0;
            int cursorY = 0;
            int rowHeight = 0;

            for (int i = 0; i < pendingMaskTiles.Count; i++)
            {
                var tile = pendingMaskTiles[i];
                if (tile.size > atlasSize)
                {
                    Debug.LogWarning($"[ChunkBatcher] Canopy mask tile {i} is {tile.size}px, larger than atlas {atlasSize}px. Increase canopyMaskAtlasSize or reduce maskSize/softRadius.");
                    break;
                }

                if (cursorX + tile.size > atlasSize)
                {
                    cursorX = 0;
                    cursorY += rowHeight;
                    rowHeight = 0;
                }

                int tileX = cursorX;
                int tileY = cursorY;

                if (tileX + tile.size > atlasSize || tileY + tile.size > atlasSize)
                {
                    Debug.LogWarning($"[ChunkBatcher] Canopy mask atlas overflow at tile {i}. atlas={atlasSize}px tile={tile.size}px rowY={tileY}px pendingTiles={pendingMaskTiles.Count}. Increase canopyMaskAtlasSize or reduce maskSize/softRadius.");
                    break;
                }

                atlas.SetPixels32(tileX, tileY, tile.size, tile.size, tile.pixels);
                cursorX += tile.size;
                if (tile.size > rowHeight) rowHeight = tile.size;

                // Write atlas UV into batchUv4 for each vertex in this tile
                float invAtlasSize = 1f / atlasSize;
                for (int vi = 0; vi < tile.vertCount; vi++)
                {
                    int idx = tile.vertOffset + vi;
                    Vector2 localUV = batchUv4[idx];
                    float pixelU = tile.border + 0.5f + localUV.x * (tile.interiorSize - 1);
                    float pixelV = tile.border + 0.5f + localUV.y * (tile.interiorSize - 1);
                    batchUv4[idx] = new Vector2(
                        (tileX + pixelU) * invAtlasSize,
                        (tileY + pixelV) * invAtlasSize);
                }
            }

            atlas.Apply(false, false);
            return atlas;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (batchVerts.IsCreated) batchVerts.Dispose();
            if (batchNormals.IsCreated) batchNormals.Dispose();
            if (batchTris.IsCreated) batchTris.Dispose();
            if (batchUvs.IsCreated) batchUvs.Dispose();
            if (batchUv1.IsCreated) batchUv1.Dispose();
            if (batchUv2.IsCreated) batchUv2.Dispose();
            if (batchUv3.IsCreated) batchUv3.Dispose();
            if (batchUv4.IsCreated) batchUv4.Dispose();
        }

        /// <summary>
        /// Returns true if the world-space point <paramref name="p"/> lies inside triangle
        /// (a, b, c) when orthogonally projected onto the triangle's plane.
        /// Uses Cramér-rule barycentric coordinates with a small epsilon on each
        /// coordinate to accept trees that land exactly on a shared edge.
        /// </summary>
        private static bool PointInTriangle3D(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float v, w;
            return PointInTriangle3DBary(p, a, b, c, out v, out w);
        }

    }  // end ChunkBatcher

    // ── Canopy UV projection helpers (public so ChunkManager's bake coroutine can call them) ──

    public static bool TryProjectPointToChunkUV(
        Vector3 p,
        int triCount, int[] tris,
        int vertCount, Vector3[] verts, Vector2[] uvs,
        out Vector2 uv)
    {
        uv = default;
        float bestPlaneDistSq = float.MaxValue;
        bool found = false;

        for (int t = 0; t < triCount; t += 3)
        {
            int li0 = tris[t];
            int li1 = tris[t + 1];
            int li2 = tris[t + 2];

            Vector3 a = verts[li0];
            Vector3 b = verts[li1];
            Vector3 c = verts[li2];
            Vector2 uv0 = uvs[li0];
            Vector2 uv1 = uvs[li1];
            Vector2 uv2 = uvs[li2];

            float v, w;
            if (PointInTriangle3DBary(p, a, b, c, out v, out w))
            {
                float u = 1f - v - w;
                uv = uv0 * u + uv1 * v + uv2 * w;
                return true;
            }

            if (!ProjectPointToTrianglePlaneBary(p, a, b, c, out v, out w, out float planeDistSq))
                continue;

            if (planeDistSq < bestPlaneDistSq)
            {
                bestPlaneDistSq = planeDistSq;
                float u = 1f - v - w;
                uv = uv0 * u + uv1 * v + uv2 * w;
                found = true;
            }
        }

        return found;
    }

    // Projects p onto the triangle plane and returns barycentrics for that planar point.
    private static bool ProjectPointToTrianglePlaneBary(Vector3 p, Vector3 a, Vector3 b, Vector3 c,
        out float v, out float w, out float planeDistSq)
    {
        v = 0f;
        w = 0f;
        planeDistSq = 0f;

        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 n = Vector3.Cross(ab, ac);
        float nSq = n.sqrMagnitude;
        if (nSq < 1e-8f)
            return false;

        Vector3 ap = p - a;
        float t = Vector3.Dot(ap, n) / nSq;
        Vector3 pp = ap - n * t;
        planeDistSq = (n * t).sqrMagnitude;

        float d00 = Vector3.Dot(ab, ab);
        float d01 = Vector3.Dot(ab, ac);
        float d11 = Vector3.Dot(ac, ac);
        float d20 = Vector3.Dot(pp, ab);
        float d21 = Vector3.Dot(pp, ac);
        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-8f)
            return false;

        v = (d11 * d20 - d01 * d21) / denom;
        w = (d00 * d21 - d01 * d20) / denom;
        return true;
    }

    // Same as PointInTriangle3D but also returns barycentric coordinates v, w.
    private static bool PointInTriangle3DBary(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float v, out float w)
    {
        v = 0f; w = 0f;
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 n  = Vector3.Cross(ab, ac);
        float   nSq = n.x * n.x + n.y * n.y + n.z * n.z;
        if (nSq < 1e-8f) return false;

        Vector3 ap = p - a;
        float   t  = (ap.x * n.x + ap.y * n.y + ap.z * n.z) / nSq;
        Vector3 pp = new Vector3(ap.x - t * n.x, ap.y - t * n.y, ap.z - t * n.z);

        float d00  = ab.x * ab.x + ab.y * ab.y + ab.z * ab.z;
        float d01  = ab.x * ac.x + ab.y * ac.y + ab.z * ac.z;
        float d11  = ac.x * ac.x + ac.y * ac.y + ac.z * ac.z;
        float d20  = pp.x * ab.x + pp.y * ab.y + pp.z * ab.z;
        float d21  = pp.x * ac.x + pp.y * ac.y + pp.z * ac.z;
        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-8f) return false;
        v = (d11 * d20 - d01 * d21) / denom;
        w = (d00 * d21 - d01 * d20) / denom;
        const float eps = -0.01f;
        return v >= eps && w >= eps && (v + w) <= 1.0f - eps;
    }

    // ============= GENERATION CYCLE ==============

    public void StartGenerationCycle(int newCenterChunk, FaceId newCenterFace,
    int generationVersion, HashSet<ChunkKey> ringPositions)
    {
        this.generationVersion = generationVersion;
        this.ringPositions = ringPositions;
        centerChunk = newCenterChunk;
        centerChunkFace = newCenterFace;
        centerFlatIdx = flatGridBFS.ChunkKeyToFlat(newCenterChunk, newCenterFace);
        
        int depthLimit = (chunkDistanceByLOD != null && chunkDistanceByLOD.Length > 0
            ? (int)chunkDistanceByLOD[chunkDistanceByLOD.Length - 1]
            : 0) + 2;
        if (depthLimit > halfSphereChunkDistance)
            depthLimit = halfSphereChunkDistance;
        StartCoroutine(RunGenerationCycle(newCenterChunk,
        newCenterFace,generationVersion,ringPositions, depthLimit));
    }

    public void SetCurrentCenter(int newCenterChunk, FaceId newCenterFace)
    {
        centerChunk = newCenterChunk;
        centerChunkFace = newCenterFace;
        centerFlatIdx = flatGridBFS.ChunkKeyToFlat(newCenterChunk, newCenterFace);
    }

    private IEnumerator RunGenerationCycle(int newCenterChunk,
    FaceId newCenterFace, int generationVersion, HashSet<ChunkKey> ringPositions,
    int bfsMaxDepth = 0, bool isSelfHealing = false)
    {
        if(!ChunkManager.Instance.isGenerationVersionValid(generationVersion))yield break;

        if (newCenterChunk != centerChunk || newCenterFace != centerChunkFace)
        {
            centerChunk = newCenterChunk;
            centerChunkFace = newCenterFace;
            centerFlatIdx = flatGridBFS.ChunkKeyToFlat(newCenterChunk, newCenterFace);
        }

        // Flat-grid BFS: single array read per neighbor, no coordinate math
        // bfsMaxDepth > 0: depth-limited (typically ~16 rings ≈ 961 chunks vs 55k full)
        // bfsMaxDepth == 0: unlimited (self-healing or reload)
        int bfsStart = centerFlatIdx;
        if (bfsMaxDepth > 0)
            flatGridBFS.RunBFS(bfsStart, bfsMaxDepth);
        else
            flatGridBFS.RunBFS(bfsStart);

        // Build flat bool[] for O(1) ring membership — indexed by flat BFS index
        int flatSize = flatGridBFS.totalCells;
        if (ringFlags == null || ringFlags.Length != flatSize)
            ringFlags = new bool[flatSize];
        else
            Array.Clear(ringFlags, 0, flatSize);
        foreach (var rk in ringPositions)
        {
            int ri = flatGridBFS.ChunkKeyToFlat(rk.packed, rk.face);
            ringFlags[ri] = true;
        }

        Queue<(int packed, FaceId face)> collisionGen = genCycleCollisionGen;
        Queue<(int packed, FaceId face, byte lod)> normalGen = genCycleNormalGen;
        Queue<(int packed, FaceId face, ChunkRecord chunk)> earlyRemovals = genCycleEarlyRemovals;
        Queue<(int packed, FaceId face, ChunkRecord chunk)> lateRemovals = genCycleLateRemovals;
        collisionGen.Clear(); normalGen.Clear(); earlyRemovals.Clear(); lateRemovals.Clear();

        bool treeActive = TreeRenderer.HasActiveSystem;

        if (treeActive)
            TreeRenderer.Instance.BeginBFSCullPass();

        for (int bi = 0; bi < flatGridBFS.resultCount; bi++)
        {
            // In self-healing mode, yield periodically during classification to spread 55k iterations
            if (isSelfHealing && bi > 0 && (bi & 2047) == 0)
            {
                yield return null;
                if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
            }

            int flatIdx = flatGridBFS.resultBuffer[bi];
            int pairPacked = flatGridBFS.GetPacked(flatIdx);
            FaceId pairFace = flatGridBFS.GetFace(flatIdx);

            // bi == 0 is always the BFS start (center chunk); ring membership from flat-indexed flags
            bool isCollision = bi == 0 || ringFlags[flatIdx];

            // Storage index needed for chunk slot / angular distance / tree ops
            int pairStorageIdx = flatGridBFS.GetStorageIndex(flatIdx);

            // Compute distance once, reuse for LOD and trees
            int distFromCenter = isCollision ? 0 :
                flatGridBFS.GetBFSDepth(flatIdx);

            byte correctLOD = isCollision ? (byte)0 : 
                STPTMEUtils.LODFromDistance(distFromCenter, chunkDistanceByLOD, maxLOD);

            if (treeActive)
            {
                bool isInPriorityRing = ringFlags[flatIdx];
                TreeRenderer.Instance.RefreshDistance(pairPacked, pairFace, distFromCenter, isInPriorityRing);
            }

            ChunkSlot existing = chunks[GetStorageIndex(pairPacked, pairFace)];

            if(!existing.IsEmpty)
            {
                bool hasCorrectLod = existing.HasLod(correctLOD, out ChunkRecord correctChunk);

                if(hasCorrectLod)
                {
                    // Heal missing tree registration: chunk is at correct LOD but trees were
                    // never registered (e.g. prior cycle aborted before CreateBatchedChunk ran).
                    // Distance-only refreshes are handled earlier in the BFS loop via RefreshDistance.
                    if (treeActive && !TreeRenderer.Instance.HasChunkData(pairPacked, pairFace))
                    {
                        bool isInPriorityRing = ringFlags[flatIdx];
                        TreeRenderer.Instance.RegisterChunk(pairPacked, pairFace, correctLOD, distFromCenter, isInPriorityRing);
                    }

                    // Stale LOD duplicates (if any) are cleaned up naturally when
                    // CreateChunk creates a replacement — no explicit removal needed.
                }
                else
                {
                    if(isCollision)
                    {
                        collisionGen.Enqueue((pairPacked, pairFace));
                    }
                    else
                    {
                        // Queue replacement generation only. The old LOD stays visible until
                        // Phase 4's CreateChunk/CreateBatchedChunk naturally supersedes it,
                        // avoiding visual holes and eliminating sync removal overhead.
                        normalGen.Enqueue((pairPacked, pairFace, correctLOD));
                    }
                }
            }
            else
            {
                if(isCollision)
                {
                    collisionGen.Enqueue((pairPacked, pairFace));
                }
                else
                {
                    normalGen.Enqueue((pairPacked, pairFace, correctLOD));
                }
            }
        }

        // After the BFS: evict trees for any chunk not stamped by this pass.
        // This catches spawn-pole chunks (and any other isolated chunks) that the BFS
        // above never reached, which RefreshDistance could therefore never clean up.
        if (TreeRenderer.HasActiveSystem)
            TreeRenderer.Instance.FlushUnvisitedChunks();

        // Half-sphere cleanup: remove terrain chunks that exist beyond the BFS horizon.
        // In the self-healing pass (depth = halfSphereChunkDistance), the BFS visits every
        // chunk that should exist. Any populated slot NOT visited is beyond half-sphere and
        // must be removed. This sweep is O(totalCells) but runs only during self-healing.
        if (isSelfHealing)
        {
            for (int f = 0; f < flatGridBFS.totalCells; f++)
            {
                if (flatGridBFS.WasVisited(f)) continue;
                int storageIdx = flatGridBFS.GetStorageIndex(f);
                if (chunks[storageIdx].IsEmpty) continue;

                int stPacked = flatGridBFS.GetPacked(f);
                FaceId stFace = flatGridBFS.GetFace(f);

                // Queue all records in this slot for late removal
                for (int ri = 0; ri < chunks[storageIdx].Count; ri++)
                    lateRemovals.Enqueue((stPacked, stFace, chunks[storageIdx][ri]));
            }
        }

        // Legacy tree collider ring removed — handled by ImpostorRenderer.
        // End of collision ring update.

        //Phase 1: early removals
        if(earlyRemovals.Count > 0)
        {
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
            yield return StartCoroutine(RemoveChunksFromMeshes(earlyRemovals, generationVersion));
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
        }

        //Phase 3: collision chunks
        // Process center chunk first without yielding — tree colliders must be
        // available immediately so the player never walks through trees.
        if (collisionGen.Count > 0)
        {
            var centerItem = collisionGen.Dequeue();
            ChunkManager.MeshData centerMeshData = default;

            bool centerSyncOk = ChunkManager.Instance.TryGenerateChunkOnlyMeshDataSync(
                centerItem.packed, centerItem.face, 0, out centerMeshData);

            if (!centerSyncOk)
            {
                yield return ChunkManager.Instance.StartGenChunkOnlyMeshData(
                    centerItem.packed, centerItem.face, 0,
                    data => { centerMeshData = data; });
                if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion))
                {
                    centerMeshData.Dispose();
                    yield break;
                }
            }

            if (centerMeshData.isValid)
            {
                CreateChunk(centerItem.packed, centerItem.face, 0, ref centerMeshData);
            }
            else
            {
                centerMeshData.Dispose();
            }
            // No yield here — proceed to remaining collision chunks immediately
        }

        // Remaining collision chunks (ring, not center)
        if (collisionGen.Count > 0)
        {
            yield return null;
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
        }
        while (collisionGen.Count > 0)
        {
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;

            var item = collisionGen.Dequeue();
            ChunkManager.MeshData meshData = default;

            bool syncOk = ChunkManager.Instance.TryGenerateChunkOnlyMeshDataSync(item.packed, item.face, 0, out meshData);

            if (!syncOk)
            {
                yield return ChunkManager.Instance.StartGenChunkOnlyMeshData(item.packed, item.face, 0,
                    data => { meshData = data; });
                if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion))
                {
                    meshData.Dispose();
                    yield break;
                }
            }

            if (meshData.isValid)
            {
                CreateChunk(item.packed, item.face, 0, ref meshData);
            }
            else
            {
                meshData.Dispose();
            }

            yield return null;
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
        }

        // normalGen is already in BFS order (ascending distance from center).
        // LOD is a non-decreasing function of distance, so BFS order = LOD order.
        //
        // Phase B: group normalGen by sector key BEFORE consumption. This keeps
        // BFS order (and therefore LOD ordering) intact within each sector while
        // grouping all chunks of one sector contiguously, so the batcher emits
        // spatially compact batches whose bounding spheres can actually be
        // frustum-culled. Without this, BFS order produces ring-shaped batches around
        // the player that no frustum plane can ever miss.
        //
        // Implementation: O(N + B) counting sort over a dense sector key with B=384
        // buckets, replacing an O(N log N) List<T>.Sort that allocated ~1.7 MB and
        // burned ~450 ms per cycle through delegate-dispatched comparisons on a 32 B
        // tuple. SoA arrays are pre-grown and reused — zero GC after warm-up.
        //
        // When debugDisableBatching is on, every chunk goes through the standalone
        // CreateChunk path (IsInNonBatchedRing is forced true) and sector grouping is
        // moot. Skip the sort entirely and drain normalGen straight into Phase 4.
        int planCount = normalGen.Count;
        if (!debugDisableBatching && planCount > 0)
        {
            EnsurePlanCapacity(planCount);
            Array.Clear(sectorBucketCount, 0, sectorBucketCount.Length);

            // Pass 1: drain normalGen → BFS staging arrays, compute dense sector key,
            // increment per-bucket count. sectorBucketCursor doubles as a "first seen"
            // sentinel here (-1 = not yet recorded in bucketEmissionOrder, ≥0 = already
            // recorded). It is overwritten with real cursor values in the prefix-sum pass
            // below before pass 2 reads it.
            for (int b = 0; b < sectorBucketCursor.Length; b++)
                sectorBucketCursor[b] = -1;
            int orderCount = 0;
            for (int i = 0; i < planCount; i++)
            {
                var p = normalGen.Dequeue();
                bfsPacked[i] = p.packed;
                bfsFace[i]   = (byte)p.face;
                bfsLod[i]    = p.lod;
                ushort key   = DenseSectorKey(p.packed, p.face);
                bfsKey[i]    = key;
                sectorBucketCount[key]++;
                if (sectorBucketCursor[key] < 0)
                {
                    sectorBucketCursor[key] = 0; // mark seen; real cursor set below
                    bucketEmissionOrder[orderCount++] = key;
                }
            }

            // Prefix sum in BFS-discovery order → write-cursor for each bucket.
            // This is the fix: buckets are emitted in the order their first member
            // was discovered by the BFS, i.e. ascending distance from the player.
            // Iterating sectorBucketCount in numerical order would emit bucket 0
            // (Up face, sector (0,0)) first regardless of player position, producing
            // the "reversed load order" symptom on side faces and at edges of Up.
            int running = 0;
            for (int o = 0; o < orderCount; o++)
            {
                int b = bucketEmissionOrder[o];
                sectorBucketCursor[b] = running;
                running += sectorBucketCount[b];
            }

            // Pass 2: scatter into sorted SoA. Stable: items in the same bucket keep
            // their relative BFS order (and therefore LOD-monotonic order).
            for (int i = 0; i < planCount; i++)
            {
                ushort key = bfsKey[i];
                int dst = sectorBucketCursor[key]++;
                sortedPacked[dst] = bfsPacked[i];
                sortedFace[dst]   = bfsFace[i];
                sortedLod[dst]    = bfsLod[i];
                sortedKey[dst]    = key;
            }
        }

        // Phase 4: Standard chunks (batched)
        using (var batcher = new ChunkBatcher(this, maxVertsPerOuterChunkMesh))
        {
            int opsThisFrame = 0;
            int workThisFrame = 0;
            int currentSectorKey = -1;
            int planIdx = 0;

            // When batching is disabled we iterate normalGen directly (planCount items)
            // — no sector key tracking needed, every chunk takes the standalone path.
            while (planIdx < planCount)
            {
                if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion))
                {
                    // Don't flush — abandon stale data
                    yield break;
                }

                int packed;
                FaceId face;
                byte lod;
                int itemSectorKey;

                if (debugDisableBatching)
                {
                    var dq = normalGen.Dequeue();
                    packed = dq.packed;
                    face   = dq.face;
                    lod    = dq.lod;
                    itemSectorKey = -1; // unused
                }
                else
                {
                    packed = sortedPacked[planIdx];
                    face   = (FaceId)sortedFace[planIdx];
                    lod    = sortedLod[planIdx];
                    itemSectorKey = sortedKey[planIdx];

                    // Sector boundary: flush the in-flight batch so the next batch is
                    // confined to one sector. Skip on the very first item.
                    if (currentSectorKey != -1 && itemSectorKey != currentSectorKey)
                        batcher.Flush();
                    currentSectorKey = itemSectorKey;
                }
                planIdx++;

                // Budget check: if adding this chunk's expected verts would exceed the
                // work-per-frame limit, yield first (unless nothing was generated yet).
                int expectedVerts = STPTMEUtils.chunkVertCount(lod);
                if (workThisFrame > 0 && workThisFrame + expectedVerts > maxChunkGenWorkPerFrame)
                {
                    workThisFrame = 0;
                    opsThisFrame = 0;
                    yield return null;
                    if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
                }

                ChunkManager.MeshData meshData = default;

                bool syncOk = ChunkManager.Instance.TryGenerateChunkOnlyMeshDataSync(packed, face, lod, out meshData);

                if (!syncOk)
                {
                    // Cell not cached — async load. Don't flush the batcher; it holds
                    // persistent NativeArrays and can keep accumulating after the yield.
                    yield return ChunkManager.Instance.StartGenChunkOnlyMeshData(packed, face, lod,
                        data => { meshData = data; });
                    if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion))
                    {
                        meshData.Dispose();
                        yield break;
                    }
                    opsThisFrame = 0;
                    workThisFrame = 0;
                }

                if (!meshData.isValid) { meshData.Dispose(); continue; }

                workThisFrame += meshData.vertCount;

                if (IsInNonBatchedRing(newCenterChunk, newCenterFace, packed, face))
                {
                    CreateChunk(packed, face, lod, ref meshData);
                    // Non-batched standalone chunk — limit to 1 per frame
                    opsThisFrame = 0;
                    workThisFrame = 0;
                    yield return null;
                    if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
                }
                else
                {
                    batcher.Add(packed, face, lod, ref meshData);

                    opsThisFrame++;
                    if (opsThisFrame >= maxChunkGenOpsPerFrame)
                    {
                        opsThisFrame = 0;
                        workThisFrame = 0;
                        yield return null;
                        if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
                    }
                }
            }

            batcher.Flush();
        } // batcher.Dispose() called here — frees persistent NativeArrays

        // Phase 5: Late removals
        if (lateRemovals.Count > 0)
        {
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
            yield return StartCoroutine(RemoveChunksFromMeshes(lateRemovals, generationVersion));
        }

        // Phase 6: Process any pending batch rebuilds (from RemoveChunkImmediate calls during CreateBatchedChunk)
        if (pendingBatchRebuilds.Count > 0)
        {
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
            yield return StartCoroutine(ProcessPendingBatchRebuilds(generationVersion));
        }

        if(chunkMaterialManager != null)
        {
            chunkMaterialManager.CommitDeferredReleases();
        }

        ChunkManager.Instance.ManageLoadedHeightmaps();

        // Notify the tree collider pool only after the full generation cycle is complete.
        // Earlier notification is too soon: some ring chunks may still be missing cached
        // cell/tree data, causing collider assignment to partially fail and then never retry
        // if the chunk remains in the ring.
// Legacy tree collider update removed — handled by ImpostorRenderer.

        // Chain self-healing pass: re-run with full BFS depth to clean up stale chunks
        // beyond the depth-limited radius. Delayed 500 frames to avoid competing with
        // normal chunk generation. Yields during classification to avoid frame spikes.
        // Depth is capped at halfSphereChunkDistance — no chunk beyond half the sphere
        // is ever visible, so there's nothing to heal or generate past that.
        if (!isSelfHealing && bfsMaxDepth > 0)
        {
            for (int delay = 0; delay < 500; delay++)
            {
                yield return null;
            }
            if (!ChunkManager.Instance.isGenerationVersionValid(generationVersion)) yield break;
            yield return StartCoroutine(RunGenerationCycle(newCenterChunk, newCenterFace,
                generationVersion, ringPositions, halfSphereChunkDistance, true));
        }
    }

    public void ReloadAll()
    {
        // Destroy all chunk GameObjects
        foreach(Transform child in chunkPoolParent)
        {
            Destroy(child.gameObject);
        }
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].Clear();
            if (ImpostorRenderer.Instance != null)
            ImpostorRenderer.Instance.SetChunkLOD(i, 255);
        }
        chunksByPool.Clear();
        batchesBeingRebuilt.Clear();
        pendingBatchRebuilds.Clear();
        chunkPool.Clear(); // Pooled GOs were children of chunkPoolParent, already destroyed above

        // Skirts: their GameObjects are also children of chunkPoolParent and got destroyed above.
        skirts.Clear();
        skirtPool.Clear();
        for (int i = 0; i < edgeCache.Length; i++) edgeCache[i] = default;

        // Clear tree rendering data so stale trees don't remain visible
        if (TreeRenderer.HasActiveSystem)
            TreeRenderer.Instance.ClearAll();

        // Bump ChunkManager's version so the new cycle passes isGenerationVersionValid checks
        ChunkManager.Instance.chunkGenerationVersion++;
        generationVersion = ChunkManager.Instance.chunkGenerationVersion;

        StartGenerationCycle(centerChunk, centerChunkFace, generationVersion, ringPositions);
    }

    /// <summary>
    /// Called by ChunkBatcher.Add to cache a sub-chunk's edge data before its NativeArrays
    /// are disposed. The source renderer is unknown until the batch GameObject is created,
    /// so it's left null here and patched in CreateBatchedChunk.
    /// </summary>
    private void PendBatchedEdgeCache(int packed, FaceId face, byte lod, ref ChunkManager.MeshData data,
        int splatSlice, byte splatTier, Vector4 uvOS,
        int normalSlice, byte normalTier, Vector4 normalUvOS)
    {
        CacheChunkEdges(packed, face, lod, null, ref data);
        int storageIdx = GetStorageIndex(packed, face);
        ref EdgeData ec = ref edgeCache[storageIdx];
        ec.splatSliceIndex = splatSlice;
        ec.splatTier = splatTier;
        ec.uvOffsetScale = uvOS;
        ec.normalSliceIndex = normalSlice;
        ec.normalTier = normalTier;
        ec.normalUvOffsetScale = normalUvOS;
    }

    // =================== SKIRT STITCHING ===================
    // A "skirt" is a thin standalone mesh that bridges the seam between two adjacent chunks
    // whose edges have different vertex counts. Two adjacent chunks' edge vertices coincide
    // at corners but not in between when their resolutions differ (different LOD, or same
    // LOD with different per-cell dsSteps). Without a skirt, T-junction gaps appear.

    /// <summary>
    /// Caches the four edge arrays of a freshly built mesh into edgeCache.
    /// Must be called BEFORE the underlying NativeArrays are disposed.
    /// </summary>
    private void CacheChunkEdges(int packed, FaceId face, byte lod, Renderer renderer,
        ref ChunkManager.MeshData data)
    {
        if (data.edgeWidth == 0 || data.edgeHeight == 0) return;
        int storageIdx = GetStorageIndex(packed, face);

        int w = data.edgeWidth;
        int h = data.edgeHeight;

        var ed = new EdgeData
        {
            vertsRight = new Vector3[h],
            vertsLeft = new Vector3[h],
            vertsUp = new Vector3[w],
            vertsDown = new Vector3[w],
            normalsRight = new Vector3[h],
            normalsLeft = new Vector3[h],
            normalsUp = new Vector3[w],
            normalsDown = new Vector3[w],
            origVertsRight = new Vector3[h],
            origVertsLeft = new Vector3[h],
            origVertsUp = new Vector3[w],
            origVertsDown = new Vector3[w],
            origNormalsRight = new Vector3[h],
            origNormalsLeft = new Vector3[h],
            origNormalsUp = new Vector3[w],
            origNormalsDown = new Vector3[w],
            innerVertsRight = new Vector3[h],
            innerVertsLeft = new Vector3[h],
            innerVertsUp = new Vector3[w],
            innerVertsDown = new Vector3[w],
            uvsRight = new Vector2[h],
            uvsLeft = new Vector2[h],
            uvsUp = new Vector2[w],
            uvsDown = new Vector2[w],
            lod = lod,
            stitchedFlags = 0,
            edgeWidth = data.edgeWidth,
            edgeHeight = data.edgeHeight,
            sourceRenderer = renderer
        };

        bool hasUvs = data.uvs.IsCreated && data.uvs.Length >= w * h;
        // Inner-row indices clamp to the opposite edge if the chunk is only 2 verts wide/tall.
        int innerDownRow = (h >= 2 ? 1 : 0) * w;
        int innerUpRow = (h >= 2 ? h - 2 : h - 1) * w;
        int innerLeftCol = w >= 2 ? 1 : 0;
        int innerRightCol = w >= 2 ? w - 2 : w - 1;

        // Bottom (y=0) and Top (y=h-1)
        int topRow = (h - 1) * w;
        for (int x = 0; x < w; x++)
        {
            Vector3 vd = data.verts[x];
            Vector3 nd = data.normals[x];
            Vector3 vu = data.verts[topRow + x];
            Vector3 nu = data.normals[topRow + x];
            ed.vertsDown[x] = vd;       ed.origVertsDown[x] = vd;
            ed.normalsDown[x] = nd;     ed.origNormalsDown[x] = nd;
            ed.vertsUp[x] = vu;         ed.origVertsUp[x] = vu;
            ed.normalsUp[x] = nu;       ed.origNormalsUp[x] = nu;
            ed.innerVertsDown[x] = data.verts[innerDownRow + x];
            ed.innerVertsUp[x] = data.verts[innerUpRow + x];
            if (hasUvs)
            {
                ed.uvsDown[x] = data.uvs[x];
                ed.uvsUp[x] = data.uvs[topRow + x];
            }
        }
        // Left (x=0) and Right (x=w-1)
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            Vector3 vl = data.verts[rowBase];
            Vector3 nl = data.normals[rowBase];
            Vector3 vr = data.verts[rowBase + (w - 1)];
            Vector3 nr = data.normals[rowBase + (w - 1)];
            ed.vertsLeft[y] = vl;       ed.origVertsLeft[y] = vl;
            ed.normalsLeft[y] = nl;     ed.origNormalsLeft[y] = nl;
            ed.vertsRight[y] = vr;      ed.origVertsRight[y] = vr;
            ed.normalsRight[y] = nr;    ed.origNormalsRight[y] = nr;
            ed.innerVertsLeft[y] = data.verts[rowBase + innerLeftCol];
            ed.innerVertsRight[y] = data.verts[rowBase + innerRightCol];
            if (hasUvs)
            {
                ed.uvsLeft[y] = data.uvs[rowBase];
                ed.uvsRight[y] = data.uvs[rowBase + (w - 1)];
            }
        }

        edgeCache[storageIdx] = ed;
    }

    /// <summary>Reverse direction: right↔left, up↔down.</summary>
    private static int OppositeDir(int dir) => dir switch
    {
        DIR_RIGHT => DIR_LEFT, DIR_LEFT => DIR_RIGHT, DIR_UP => DIR_DOWN, _ => DIR_UP
    };

    /// <summary>
    /// Same-face neighbor walker. Crosses cell (heightmap) boundaries within a face but does
    /// NOT cross face seams (cross-face skirts have non-trivial edge orientation and are
    /// skipped for now).
    /// </summary>
    private bool TryGetSameFaceNeighbor(int packed, int dir, out int nPacked)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte hX, out sbyte hY, out sbyte cX, out sbyte cY);
        int newCX = cX, newCY = cY, newHX = hX, newHY = hY;
        switch (dir)
        {
            case DIR_RIGHT: newCX++; if (newCX >= numberOfChunks) { newCX = 0; newHX++; } break;
            case DIR_LEFT:  newCX--; if (newCX < 0) { newCX = numberOfChunks - 1; newHX--; } break;
            case DIR_UP:    newCY++; if (newCY >= numberOfChunks) { newCY = 0; newHY++; } break;
            case DIR_DOWN:  newCY--; if (newCY < 0) { newCY = numberOfChunks - 1; newHY--; } break;
        }
        if (newHX < minX || newHX > maxX || newHY < minX || newHY > maxX)
        {
            nPacked = 0;
            return false;
        }
        nPacked = STPTMEUtils.WriteFourSBytesInInt((sbyte)newHX, (sbyte)newHY, (sbyte)newCX, (sbyte)newCY);
        return true;
    }

    /// <summary>
    /// For the chunk at (packed, face), inspects all 4 same-face neighbours and reconciles the
    /// shared seam:
    ///   - Both sides LOD 0 with mismatched edge vert counts: STITCH the higher-res side
    ///     (snap its edge verts onto the line segment defined by the lower-res side's edge).
    ///     This produces a watertight, walkable seam with no T-junction. No skirt is created.
    ///   - At least one side LOD &gt; 0 with mismatched edge counts: build a visual skirt
    ///     (no collider — those seams are far from the camera and never collided with).
    ///   - Edge counts match: destroy any stale skirt; ensure no stitch on either side.
    /// Stitching also runs against the neighbour's opposite edge: if THIS chunk is the lower-res
    /// side, the higher-res neighbour (which already exists) gets retroactively stitched.
    /// </summary>
    private void RebuildSeamsForChunk(int packed, FaceId face)
    {
        int selfIdx = GetStorageIndex(packed, face);
        ref EdgeData self = ref edgeCache[selfIdx];
        if (self.vertsRight == null) return;

        for (int dir = 0; dir < 4; dir++)
        {
            if (!TryGetSameFaceNeighbor(packed, dir, out int nPacked)) continue;
            int nIdx = GetStorageIndex(nPacked, face);
            ref EdgeData nb = ref edgeCache[nIdx];
            if (nb.vertsRight == null) continue;

            int oppDir = OppositeDir(dir);
            // Compare against ORIGINAL (pre-stitch) lengths to determine which side is denser.
            // After we restore-on-mismatch below, the live arrays equal the orig arrays anyway,
            // but using orig avoids surprises if a future change preserves stale state.
            Vector3[] selfOrig = self.GetOrigVerts(dir);
            Vector3[] nbOrig = nb.GetOrigVerts(oppDir);
            int nSelf = selfOrig.Length;
            int nNb = nbOrig.Length;

            var key = SkirtKey.Make(packed, face, nPacked, face);
            byte selfLod = self.lod;
            byte nbLod = nb.lod;

            if (nSelf == nNb)
            {
                // Edge counts match — clear any prior seam treatment.
                if ((self.stitchedFlags & (1 << dir)) != 0) UnstitchEdge(packed, face, dir);
                if ((nb.stitchedFlags & (1 << oppDir)) != 0) UnstitchEdge(nPacked, face, oppDir);
                if (skirts.TryGetValue(key, out var staleObj))
                {
                    skirts.Remove(key);
                    ReturnSkirtToPool(staleObj);
                }
                continue;
            }

            bool bothLod0 = selfLod == 0 && nbLod == 0;
            if (bothLod0)
            {
                // STITCH: snap higher-res edge onto lower-res edge segment. The skirt (if any)
                // is no longer needed; destroy it.
                if (skirts.TryGetValue(key, out var staleObj))
                {
                    skirts.Remove(key);
                    ReturnSkirtToPool(staleObj);
                }
                bool selfIsHigher = nSelf > nNb;
                if (selfIsHigher)
                {
                    // Ensure the OTHER side isn't currently stitched against us (shouldn't be,
                    // since lower-res can't be stitched to higher-res, but defensive).
                    if ((nb.stitchedFlags & (1 << oppDir)) != 0) UnstitchEdge(nPacked, face, oppDir);
                    StitchEdge(packed, face, dir, nPacked, face, oppDir);
                }
                else
                {
                    if ((self.stitchedFlags & (1 << dir)) != 0) UnstitchEdge(packed, face, dir);
                    StitchEdge(nPacked, face, oppDir, packed, face, dir);
                }
                continue;
            }

            // SKIRT (visual only): at least one side is non-LOD0. Drop any stale stitches and
            // build/refresh the bridge mesh.
            if ((self.stitchedFlags & (1 << dir)) != 0) UnstitchEdge(packed, face, dir);
            if ((nb.stitchedFlags & (1 << oppDir)) != 0) UnstitchEdge(nPacked, face, oppDir);

            bool selfIsHigherRes = nSelf > nNb;
            int srcCacheIdx = selfIsHigherRes ? selfIdx : nIdx;
            int srcDir = selfIsHigherRes ? dir : oppDir;
            // Pull live verts/normals (which equal orig here since we just unstitched if needed).
            BuildOrUpdateSkirt(key,
                self.GetVerts(dir), self.GetNormals(dir),
                nb.GetVerts(oppDir), nb.GetNormals(oppDir),
                edgeCache[srcCacheIdx].GetUvs(srcDir), selfIsHigherRes,
                ref edgeCache[srcCacheIdx]);
        }
    }

    /// <summary>
    /// Destroys any skirts attached to the chunk at (packed, face), unstitches any neighbour
    /// edge that was stitched against this chunk, and clears its edge cache. Called from
    /// RemoveChunkImmediate so adjacent chunks don't keep skirts or stitches referencing
    /// stale edge data.
    /// </summary>
    private void DropChunkEdgesAndSkirts(int packed, FaceId face)
    {
        int selfIdx = GetStorageIndex(packed, face);
        if (edgeCache[selfIdx].vertsRight == null) return;

        for (int dir = 0; dir < 4; dir++)
        {
            if (!TryGetSameFaceNeighbor(packed, dir, out int nPacked)) continue;
            int oppDir = OppositeDir(dir);
            int nIdx = GetStorageIndex(nPacked, face);
            // If the neighbour's opposite edge was stitched against us, restore it.
            if (edgeCache[nIdx].vertsRight != null &&
                (edgeCache[nIdx].stitchedFlags & (1 << oppDir)) != 0)
            {
                UnstitchEdge(nPacked, face, oppDir);
            }
            // And drop any skirt for this pair.
            var key = SkirtKey.Make(packed, face, nPacked, face);
            if (skirts.TryGetValue(key, out var obj))
            {
                skirts.Remove(key);
                ReturnSkirtToPool(obj);
            }
        }
        edgeCache[selfIdx] = default;
    }

    private void ReturnSkirtToPool(GameObject obj)
    {
        if (obj == null) return;
        // Skirts no longer carry colliders, but defensively drop any leftover from earlier code.
        MeshCollider mc = obj.GetComponent<MeshCollider>();
        if (mc != null) Destroy(mc);
        obj.SetActive(false);
        skirtPool.Push(obj);
    }

    // =================== EDGE STITCHING ===================
    // For LOD0\u2013LOD0 seams with mismatched edge vert counts, instead of building a skirt we
    // snap the denser side's edge verts onto the straight segments of the sparser side. The
    // sparser side's edge IS its rendered surface (triangles between successive corner verts
    // are flat segments), so the result is a perfectly continuous, walkable seam.
    //
    // Limitation: this only applies to non-batched chunks (single-chunk-per-renderer). LOD0
    // chunks are always within the non-batched ring in normal config; if a LOD0\u2013LOD0 mismatch
    // somehow occurs in the batched ring, it falls through to skirt rendering (no collider).

    /// <summary>
    /// Snap the higher-res chunk's edge verts onto the line segment defined by the lower-res
    /// neighbour's edge. Recomputes edge-row normals from the new positions and patches the
    /// mesh + collider. Updates edgeCache live arrays; orig arrays remain pristine.
    /// </summary>
    private void StitchEdge(int higherPacked, FaceId higherFace, int higherDir,
        int lowerPacked, FaceId lowerFace, int lowerDir)
    {
        int hIdx = GetStorageIndex(higherPacked, higherFace);
        int lIdx = GetStorageIndex(lowerPacked, lowerFace);
        ref EdgeData hi = ref edgeCache[hIdx];
        ref EdgeData lo = ref edgeCache[lIdx];
        if (hi.sourceRenderer == null || lo.sourceRenderer == null) return;

        // Restrict to non-batched (single chunk per GameObject). Batched chunks share a mesh
        // with siblings; modifying just one chunk's edge requires per-chunk vertOffset
        // tracking that we don't bother with here.
        var hiObj = hi.sourceRenderer.gameObject;
        if (!chunksByPool.TryGetValue(hiObj, out var hiEntries) || hiEntries.Count != 1) return;

        Vector3[] hVerts = hi.GetVerts(higherDir);
        Vector3[] lVerts = lo.GetVerts(lowerDir);
        if (hVerts == null || lVerts == null) return;
        int nH = hVerts.Length;
        int nL = lVerts.Length;
        if (nH <= nL) return; // higher must actually be denser

        var mf = hiObj.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;
        Mesh mesh = mf.sharedMesh;

        // Reuse pristine endpoints — they coincide with the lower side's corner verts already.
        // Interior verts: parametric position along the lower segment.
        Vector3[] origH = hi.GetOrigVerts(higherDir);
        Vector3[] inner = hi.GetInnerVerts(higherDir);
        float invH = 1f / (nH - 1);
        int lLast = nL - 1;
        Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;

        // Write new verts into the live array.
        hVerts[0] = lVerts[0];
        hVerts[nH - 1] = lVerts[nL - 1];
        for (int i = 1; i < nH - 1; i++)
        {
            float pos = i * invH * lLast;
            int j = (int)pos;
            if (j >= lLast) j = lLast - 1;
            float a = pos - j;
            hVerts[i] = Vector3.LerpUnclamped(lVerts[j], lVerts[j + 1], a);
        }

        // Recompute edge-row normals: cross(along-edge tangent, inner-row connector).
        // Endpoints fall back to sphere outward direction.
        Vector3[] hNormals = hi.GetNormals(higherDir);
        for (int i = 0; i < nH; i++)
        {
            if (i == 0 || i == nH - 1)
            {
                Vector3 outward = hVerts[i] - sphereCenter;
                hNormals[i] = outward.sqrMagnitude > 1e-12f ? outward.normalized : Vector3.up;
                continue;
            }
            Vector3 tEdge = hVerts[i + 1] - hVerts[i - 1];
            Vector3 tCross = inner[i] - hVerts[i];
            Vector3 n = Vector3.Cross(tEdge, tCross);
            float mag = n.magnitude;
            if (mag > 1e-8f)
            {
                n /= mag;
                Vector3 outward = hVerts[i] - sphereCenter;
                if (Vector3.Dot(n, outward) < 0f) n = -n;
                hNormals[i] = n;
            }
            else
            {
                Vector3 outward = hVerts[i] - sphereCenter;
                hNormals[i] = outward.sqrMagnitude > 1e-12f ? outward.normalized : Vector3.up;
            }
        }

        // Apply to the mesh: pull verts/normals lists, patch edge entries, set back.
        if (stitchVertList == null) stitchVertList = new List<Vector3>();
        if (stitchNormalList == null) stitchNormalList = new List<Vector3>();
        mesh.GetVertices(stitchVertList);
        mesh.GetNormals(stitchNormalList);
        WriteEdgeIntoVertNormalLists(higherDir, hi.edgeWidth, hi.edgeHeight,
            hVerts, hNormals, stitchVertList, stitchNormalList);
        mesh.SetVertices(stitchVertList);
        mesh.SetNormals(stitchNormalList);
        mesh.RecalculateBounds();

        var mc = hiObj.GetComponent<MeshCollider>();
        if (mc != null)
        {
            // Force PhysX to rebake from the modified mesh.
            mc.sharedMesh = null;
            mc.sharedMesh = mesh;
        }

        hi.stitchedFlags |= (byte)(1 << higherDir);
    }

    /// <summary>
    /// Restore an edge to its pristine pre-stitch state and patch the mesh + collider. Safe to
    /// call when not stitched (no-op).
    /// </summary>
    private void UnstitchEdge(int packed, FaceId face, int dir)
    {
        int idx = GetStorageIndex(packed, face);
        ref EdgeData ed = ref edgeCache[idx];
        if (ed.sourceRenderer == null) return;
        if ((ed.stitchedFlags & (1 << dir)) == 0) return;

        var obj = ed.sourceRenderer.gameObject;
        if (!chunksByPool.TryGetValue(obj, out var entries) || entries.Count != 1)
        {
            // Inconsistent (e.g. chunk became batched between stitch and unstitch). Just clear
            // the flag so we don't leak state.
            ed.stitchedFlags &= (byte)~(1 << dir);
            return;
        }

        Vector3[] live = ed.GetVerts(dir);
        Vector3[] orig = ed.GetOrigVerts(dir);
        Vector3[] liveN = ed.GetNormals(dir);
        Vector3[] origN = ed.GetOrigNormals(dir);
        Array.Copy(orig, live, orig.Length);
        Array.Copy(origN, liveN, origN.Length);

        var mf = obj.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            Mesh mesh = mf.sharedMesh;
            if (stitchVertList == null) stitchVertList = new List<Vector3>();
            if (stitchNormalList == null) stitchNormalList = new List<Vector3>();
            mesh.GetVertices(stitchVertList);
            mesh.GetNormals(stitchNormalList);
            WriteEdgeIntoVertNormalLists(dir, ed.edgeWidth, ed.edgeHeight,
                live, liveN, stitchVertList, stitchNormalList);
            mesh.SetVertices(stitchVertList);
            mesh.SetNormals(stitchNormalList);
            mesh.RecalculateBounds();

            var mc = obj.GetComponent<MeshCollider>();
            if (mc != null)
            {
                mc.sharedMesh = null;
                mc.sharedMesh = mesh;
            }
        }

        ed.stitchedFlags &= (byte)~(1 << dir);
    }

    /// <summary>
    /// Patch the row/column corresponding to <paramref name="dir"/> in the supplied vert/normal
    /// lists with the values from <paramref name="edgeVerts"/>/<paramref name="edgeNormals"/>.
    /// Vertex layout is row-major: index = y * w + x.
    /// </summary>
    private static void WriteEdgeIntoVertNormalLists(int dir, int w, int h,
        Vector3[] edgeVerts, Vector3[] edgeNormals,
        List<Vector3> verts, List<Vector3> normals)
    {
        switch (dir)
        {
            case DIR_DOWN:
                for (int x = 0; x < w; x++) { verts[x] = edgeVerts[x]; normals[x] = edgeNormals[x]; }
                break;
            case DIR_UP:
            {
                int topRow = (h - 1) * w;
                for (int x = 0; x < w; x++) { verts[topRow + x] = edgeVerts[x]; normals[topRow + x] = edgeNormals[x]; }
                break;
            }
            case DIR_LEFT:
                for (int y = 0; y < h; y++) { int idx = y * w; verts[idx] = edgeVerts[y]; normals[idx] = edgeNormals[y]; }
                break;
            case DIR_RIGHT:
                for (int y = 0; y < h; y++) { int idx = y * w + (w - 1); verts[idx] = edgeVerts[y]; normals[idx] = edgeNormals[y]; }
                break;
        }
    }

    /// <summary>
    /// Builds (or rebuilds) the skirt GameObject at the given key. Uses a "zipper" triangle
    /// strip between two edge vertex arrays of differing lengths, matching parametric position
    /// along the shared edge.
    /// Rendered double-sided (each triangle emitted with both windings) because the seam can
    /// be visible from either neighbour depending on which side has the higher peak at that
    /// point along the edge — a single winding can't be correct everywhere.
    /// UVs come from the higher-res neighbour's cached edge UVs so the skirt samples the
    /// matching edge strip of that chunk's splatmap slice (instead of stretching the whole
    /// slice across [0,1] and pulling in the wrong layers).
    /// </summary>
    private void BuildOrUpdateSkirt(SkirtKey key,
        Vector3[] edgeA, Vector3[] normalsA,
        Vector3[] edgeB, Vector3[] normalsB,
        Vector2[] srcEdgeUvs, bool srcIsA,
        ref EdgeData srcEdge)
    {
        int nA = edgeA.Length;
        int nB = edgeB.Length;
        if (nA < 2 || nB < 2) return;
        if (srcEdgeUvs == null || srcEdgeUvs.Length < 2) return;

        int vertCount = nA + nB;
        int forwardTris = nA + nB - 2;
        int triCount = forwardTris * 6; // ×2 for double-sided (forward + reversed winding)
        if (skirtVertBuf.Length < vertCount)
        {
            skirtVertBuf = new Vector3[vertCount];
            skirtNormalBuf = new Vector3[vertCount];
            skirtUvBuf = new Vector2[vertCount];
        }
        if (skirtTriBuf.Length < triCount) skirtTriBuf = new int[triCount];

        // UVs: both sides of the skirt sample the higher-res neighbour's edge UV strip so
        // the skirt visually continues that chunk's splat. The source side uses its own UVs
        // directly; the other side interpolates the source UV array along the shared
        // parametric edge.
        int srcN = srcIsA ? nA : nB;
        int otherN = srcIsA ? nB : nA;
        int srcLast = srcN - 1;
        int otherLast = otherN - 1;
        int srcUvLast = srcEdgeUvs.Length - 1;

        // Layout: A verts at [0..nA-1], B verts at [nA..nA+nB-1].
        for (int i = 0; i < nA; i++)
        {
            skirtVertBuf[i] = edgeA[i];
            skirtNormalBuf[i] = normalsA[i];
        }
        for (int j = 0; j < nB; j++)
        {
            skirtVertBuf[nA + j] = edgeB[j];
            skirtNormalBuf[nA + j] = normalsB[j];
        }

        // Source-side UVs: copy directly from the cached edge UVs (lengths should match,
        // but clamp defensively in case of a per-cell-resolution mismatch).
        for (int s = 0; s < srcN; s++)
        {
            int srcIdx = s <= srcUvLast ? s : srcUvLast;
            int dst = srcIsA ? s : nA + s;
            skirtUvBuf[dst] = srcEdgeUvs[srcIdx];
        }
        // Other-side UVs: interpolate along the source UV strip by parametric position.
        for (int o = 0; o < otherN; o++)
        {
            float t = (float)o / otherLast;
            float pos = t * srcUvLast;
            int i0 = (int)pos;
            int i1 = i0 + 1 <= srcUvLast ? i0 + 1 : srcUvLast;
            float frac = pos - i0;
            int dst = srcIsA ? nA + o : o;
            skirtUvBuf[dst] = Vector2.LerpUnclamped(srcEdgeUvs[i0], srcEdgeUvs[i1], frac);
        }

        // Zipper: walk both edges in parametric space, advance whichever side's next vertex
        // sits at a smaller t-value.
        int iA = 0, iB = 0, ti = 0;
        float invA = 1f / (nA - 1);
        float invB = 1f / (nB - 1);
        while (iA < nA - 1 || iB < nB - 1)
        {
            bool advanceA;
            if (iA >= nA - 1)       advanceA = false;
            else if (iB >= nB - 1)  advanceA = true;
            else                    advanceA = (iA + 1) * invA <= (iB + 1) * invB;

            int a, b, c;
            if (advanceA)
            {
                a = iA + 1; b = nA + iB; c = iA;
                iA++;
            }
            else
            {
                a = iA; b = nA + iB + 1; c = nA + iB;
                iB++;
            }
            // Forward winding
            skirtTriBuf[ti++] = a;
            skirtTriBuf[ti++] = b;
            skirtTriBuf[ti++] = c;
            // Reversed winding (back face) — same vertices, flipped order.
            skirtTriBuf[ti++] = a;
            skirtTriBuf[ti++] = c;
            skirtTriBuf[ti++] = b;
        }

        // Acquire GameObject (existing skirt or pool/new).
        GameObject obj;
        Mesh mesh;
        MeshRenderer mr;
        if (skirts.TryGetValue(key, out obj))
        {
            // Rebuild in place. Drop any existing collider — we'll re-add if needed.
            MeshCollider oldMc = obj.GetComponent<MeshCollider>();
            if (oldMc != null) Destroy(oldMc);
            mesh = obj.GetComponent<MeshFilter>().sharedMesh;
            mesh.Clear();
            mr = obj.GetComponent<MeshRenderer>();
            obj.SetActive(true);
        }
        else if (skirtPool.Count > 0)
        {
            obj = skirtPool.Pop();
            mesh = obj.GetComponent<MeshFilter>().sharedMesh;
            mesh.Clear();
            mr = obj.GetComponent<MeshRenderer>();
            obj.SetActive(true);
            skirts[key] = obj;
        }
        else
        {
            obj = new GameObject($"Skirt_{key.packedA}_{key.packedB}_{key.faceA}");
            obj.transform.SetParent(chunkPoolParent, false);
            mesh = new Mesh();
            obj.AddComponent<MeshFilter>().mesh = mesh;
            mr = obj.AddComponent<MeshRenderer>();
            skirts[key] = obj;
        }

        // Upload geometry. Use array overloads (no NativeArrays needed for these tiny meshes).
        mesh.SetVertices(skirtVertBuf, 0, vertCount);
        mesh.SetNormals(skirtNormalBuf, 0, vertCount);
        mesh.SetUVs(0, skirtUvBuf, 0, vertCount);
        mesh.SetTriangles(skirtTriBuf, 0, triCount, 0);
        mesh.RecalculateBounds();

        // Material + MPB: always use the non-batched material so the shader reads splat
        // metadata from the MaterialPropertyBlock (via _UVOffsetScale, _SplatSliceIndex, etc.)
        // rather than from per-vertex UV1/UV2/UV3 which skirt meshes don't carry.
        if (chunkMaterialManager != null && srcEdge.splatSliceIndex >= 0)
        {
            mr.sharedMaterial = chunkMaterialManager.SharedMaterial;
            if (skirtMpb == null) skirtMpb = new MaterialPropertyBlock();
            else skirtMpb.Clear();
            skirtMpb.SetFloat("_SplatSliceIndex", srcEdge.splatSliceIndex);
            skirtMpb.SetFloat("_SplatTier", srcEdge.splatTier);
            skirtMpb.SetVector("_UVOffsetScale", srcEdge.uvOffsetScale);
            if (srcEdge.normalSliceIndex >= 0)
            {
                skirtMpb.SetFloat("_NormalSliceIndex", srcEdge.normalSliceIndex);
                skirtMpb.SetFloat("_NormalTier", srcEdge.normalTier);
                skirtMpb.SetVector("_NormalUVOffsetScale", srcEdge.normalUvOffsetScale);
            }
            mr.SetPropertyBlock(skirtMpb);
        }
        else
        {
            mr.sharedMaterial = tempMat;
            mr.SetPropertyBlock(null);
        }
    }

}

/*
Re: removing GameObject gameObject from ChunkRecord — it's used as the primary identifier in RemoveFromSetByLodAndBatch and RemoveFromSetByBatch. Replacing it with an int batch ID would require a mapping layer (batch ID ↔ GameObject) and touching every call site. The savings would be shrinking the struct from 24 to 16 bytes. Worth considering later but high complexity for moderate gain.
This would also make converting sliceIndex to ushort from int maybe worth it, possibly saving even more
*/