#if false
using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Manages a pool of tree trunk/canopy collider GameObjects and assigns them to decoded
/// tree positions in the collision ring (chunks near the player).
///
/// Design summary
/// ──────────────
/// • One PooledColliderObject per (prototype, shapeIndex). Each carries the exact
///   Unity collider component described by the corresponding TreeColliderShape.
/// • Physical shapes (TreeColliderPurpose.Physical) → isTrigger = false.
///   Trigger shapes (TreeColliderPurpose.Trigger)   → isTrigger = true.
///   The two purposes are kept in separate sub-pools so each can later have its own
///   MonoBehaviour, PhysicsMaterial, or layer without any structural change.
/// • Idle objects sit under _poolRoot, collider.enabled = false.
///   Active objects are moved to the tree's world position and collider.enabled = true.
/// • Pool target = averageTreesPer9Chunks (from baked stats) × headroom × shapeCount.
///   A minimum floor prevents fully emptying any pool.
/// • If the live pool runs dry the manager starts an async grow coroutine.
///   If the idle count stays above target it starts an async shrink coroutine.
/// </summary>
public class TreeColliderManager : MonoBehaviour
{
    // ─── Inspector ───────────────────────────────────────────────────────────

    [Tooltip("Drag the TreePrototypeRegistry ScriptableObject here.")]
    [SerializeField] private TreePrototypeRegistry prototypeRegistry;

    [Tooltip("Multiplier applied to the baked average to obtain pool target size. " +
             "1.25 = 25 % headroom above the global average.")]
    [SerializeField] private float headroomMultiplier = 1.25f;

    [Tooltip("Minimum number of idle collider objects kept per sub-pool at all times, " +
             "even in regions with no trees.")]
    [SerializeField] private int minimumFloor = 50;

    [Tooltip("How many objects to create per frame during async grow.")]
    [SerializeField] private int growBatchPerFrame = 10;

    [Tooltip("How many objects to destroy per frame during async shrink.")]
    [SerializeField] private int shrinkBatchPerFrame = 5;

    [Tooltip("Frames the idle count must remain above target before shrink begins.")]
    [SerializeField] private int shrinkGracePeriodFrames = 120;

    // ─── Sub-pool key ────────────────────────────────────────────────────────

    /// <summary>Uniquely identifies one collider sub-pool.</summary>
    private readonly struct SubPoolKey : System.IEquatable<SubPoolKey>
    {
        public readonly byte prototypeIndex;
        public readonly byte shapeIndex;

        public SubPoolKey(byte proto, byte shape) { prototypeIndex = proto; shapeIndex = shape; }
        public bool Equals(SubPoolKey other) => prototypeIndex == other.prototypeIndex && shapeIndex == other.shapeIndex;
        public override bool Equals(object obj) => obj is SubPoolKey k && Equals(k);
        public override int GetHashCode() => (prototypeIndex << 8) | shapeIndex;
    }

    // ─── Sub-pool ────────────────────────────────────────────────────────────

    private class SubPool
    {
        public readonly SubPoolKey key;
        public readonly TreeColliderShape shape;
        public int targetCount;
        public int shrinkIdleFrames;

        // Idle objects: collider.enabled = false, parented to poolRoot.
        public readonly Stack<PooledColliderObject> idle = new Stack<PooledColliderObject>();
        // Active objects: collider.enabled = true, placed at tree positions.
        public readonly List<PooledColliderObject> active = new List<PooledColliderObject>();

        public SubPool(SubPoolKey key, TreeColliderShape shape, int targetCount)
        {
            this.key = key;
            this.shape = shape;
            this.targetCount = targetCount;
        }

        public int TotalCount => idle.Count + active.Count;
    }

    // ─── Pooled object ───────────────────────────────────────────────────────

    private class PooledColliderObject
    {
        public readonly GameObject go;
        public readonly Collider collider; // CapsuleCollider or MeshCollider
        public int packed;
        public FaceId face;

        public PooledColliderObject(GameObject go, Collider col) { this.go = go; collider = col; }
    }

    // ─── State ───────────────────────────────────────────────────────────────

    private Dictionary<SubPoolKey, SubPool> _pools = new Dictionary<SubPoolKey, SubPool>();
    private Transform _poolRoot;

    // Baked stats
    private float[] _averageTreesPer9Chunks; // indexed by prototypeIndex
    private bool _statsLoaded;

    // Decoded tree cache per active chunk.
    private Dictionary<ChunkKey, TreeDecoder.DecodedTreeInstance[]> _chunkTreeCache
        = new Dictionary<ChunkKey, TreeDecoder.DecodedTreeInstance[]>();

    // Desired collision ring for the current generation target.
    private HashSet<ChunkKey> _desiredChunkKeys = new HashSet<ChunkKey>();

    // Chunks that currently have at least one active collider assigned.
    private HashSet<ChunkKey> _assignedChunkKeys = new HashSet<ChunkKey>();

    // Reusable list for SetDesiredCollisionRing removals
    private readonly List<ChunkKey> _ringToRemove = new List<ChunkKey>();

    // References injected by ChunkManager
    private ChunkManager _chunkManager;
    private Vector3 _sphereCenter;

    private bool _initialized;

    // ─── Initialization ──────────────────────────────────────────────────────

    /// <summary>
    /// Called by ChunkManager.Awake after settings are loaded.
    /// </summary>
    public void Initialize(ChunkManager chunkManager, TreePrototypeRegistry registry, string statsFilePath)
    {
        _chunkManager = chunkManager;
        prototypeRegistry = registry;
        _sphereCenter = chunkManager.transform.position; // overwritten below from settings

        // Read sphere center from settings (same source ChunkManager uses)
        var settings = TerrainManagementSettings.Instance;
        if (settings != null) _sphereCenter = settings.sphereCenter;

        // Create a hidden root for idle objects
        _poolRoot = new GameObject("[TreeColliderPool]").transform;
        _poolRoot.SetParent(transform, false);

        // Load baked density stats
        LoadStats(statsFilePath);

        // Build sub-pools for every prototype shape
        BuildPools();

        _initialized = true;
    }

    private void LoadStats(string path)
    {
        _averageTreesPer9Chunks = null;

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[TreeColliderManager] Stats file not found at {path}. Pool will use floor size only.");
            return;
        }

        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            uint magic = reader.ReadUInt32();
            if (magic != 0x54435354) // "TSCT"
            {
                Debug.LogWarning("[TreeColliderManager] Stats file has wrong magic. Ignoring.");
                return;
            }

            ushort version = reader.ReadUInt16();
            if (version != 1)
            {
                Debug.LogWarning($"[TreeColliderManager] Unknown stats version {version}. Ignoring.");
                return;
            }

            ushort protoCount = reader.ReadUInt16();
            int totalValidChunks = reader.ReadInt32();

            _averageTreesPer9Chunks = new float[protoCount];
            for (int i = 0; i < protoCount; i++)
            {
                reader.ReadInt32(); // totalTrees — not used at runtime
                _averageTreesPer9Chunks[i] = reader.ReadSingle(); // averageTreesPer9Chunks
            }

            _statsLoaded = true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[TreeColliderManager] Failed to read stats file: {ex.Message}");
        }
    }

    private void BuildPools()
    {
        if (prototypeRegistry == null || prototypeRegistry.prototypes == null) return;

        for (byte pi = 0; pi < prototypeRegistry.prototypes.Length; pi++)
        {
            var entry = prototypeRegistry.prototypes[pi];
            if (entry == null || !entry.HasColliders) continue;

            for (byte si = 0; si < entry.colliderShapes.Length; si++)
            {
                var shape = entry.colliderShapes[si];
                if (shape == null) continue;

                int target = ComputeTarget(pi);
                var key = new SubPoolKey(pi, si);
                var pool = new SubPool(key, shape, target);
                _pools[key] = pool;

                // Pre-warm to floor
                int warmCount = Mathf.Max(minimumFloor, target);
                for (int n = 0; n < warmCount; n++)
                    pool.idle.Push(CreatePooledObject(pool));
            }
        }
    }

    private int ComputeTarget(byte prototypeIndex)
    {
        float avg = 0f;
        if (_statsLoaded && _averageTreesPer9Chunks != null && prototypeIndex < _averageTreesPer9Chunks.Length)
            avg = _averageTreesPer9Chunks[prototypeIndex];

        return Mathf.Max(minimumFloor, Mathf.CeilToInt(avg * headroomMultiplier));
    }

    // ─── Pool object factory / destructor ────────────────────────────────────

    private PooledColliderObject CreatePooledObject(SubPool pool)
    {
        var go = new GameObject($"TreeCol_{pool.key.prototypeIndex}_{pool.key.shapeIndex}");
        go.transform.SetParent(_poolRoot, false);

        Collider col;
        if (pool.shape.type == TreeColliderType.Capsule)
        {
            var cap = go.AddComponent<CapsuleCollider>();
            cap.radius  = pool.shape.radius;
            cap.height  = pool.shape.height;
            cap.center  = pool.shape.center;
            cap.direction = (int)pool.shape.axis;
            cap.isTrigger = pool.shape.purpose == TreeColliderPurpose.Trigger;
            cap.enabled = false;
            col = cap;
        }
        else // Mesh
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = pool.shape.colliderMesh;
            mc.convex = pool.shape.convex;
            mc.isTrigger = pool.shape.purpose == TreeColliderPurpose.Trigger && pool.shape.convex;
            mc.enabled = false;
            col = mc;
        }

        return new PooledColliderObject(go, col);
    }

    // ─── Collision ring update ────────────────────────────────────────────────

    /// <summary>
    /// Called by ChunkRegistry whenever the collision ring changes (new center / rebuild).
    /// <paramref name="ringPositions"/> is the set of ChunkKeys generated by
    /// GenerateRings. <paramref name="centerChunk"/> / <paramref name="centerFace"/> are the
    /// player's current chunk (which is collision-eligible but not in ringPositions).
    /// </summary>
    public void SetDesiredCollisionRing(
        int centerChunk, FaceId centerFace,
        HashSet<ChunkKey> ringPositions)
    {
        if (!_initialized) return;

        // Rebuild _desiredChunkKeys in-place — no new HashSet allocation
        _desiredChunkKeys.Clear();
        _desiredChunkKeys.Add(new ChunkKey(centerChunk, centerFace));
        if (ringPositions != null)
        {
            foreach (var ck in ringPositions)
                _desiredChunkKeys.Add(ck);
        }

        // Return colliders for chunks that left the ring
        _ringToRemove.Clear();
        foreach (var key in _assignedChunkKeys)
            if (!_desiredChunkKeys.Contains(key))
                _ringToRemove.Add(key);

        foreach (var key in _ringToRemove)
            ReturnChunk(key);
    }

    /// <summary>
    /// Reconciles the currently stored desired collision ring against the live assigned colliders.
    /// Use this after generation phases complete to heal any missed assignments.
    /// </summary>
    public void OnCollisionRingChanged(
        int centerChunk, FaceId centerFace,
        HashSet<ChunkKey> ringPositions)
    {
        if (!_initialized) return;

        SetDesiredCollisionRing(centerChunk, centerFace, ringPositions);

        // Reconcile every desired chunk, not only newly-entered ones.
        // Reason: assignment can legitimately fail temporarily if a cell wasn't cached yet
        // when an earlier generation pass ran. If the chunk remains in the ring, it still
        // needs another chance to acquire colliders once data is available.
        foreach (var key in _desiredChunkKeys)
        {
            if (_assignedChunkKeys.Contains(key))
                ReturnChunk(key);
            AssignChunk(key);
        }

        // Check whether any pool needs to grow or shrink
        foreach (var pool in _pools.Values)
            CheckPoolResize(pool);
    }

    /// <summary>
    /// Called as soon as a collision chunk becomes available. If that chunk is part of the
    /// desired collision ring, assign tree colliders immediately instead of waiting for the
    /// whole generation cycle to finish.
    /// </summary>
    public void OnCollisionChunkReady(int packed, FaceId face)
    {
        if (!_initialized) return;

        var key = new ChunkKey(packed, face);
        if (!_desiredChunkKeys.Contains(key)) return;

        if (_assignedChunkKeys.Contains(key))
            ReturnChunk(key);

        AssignChunk(key);
    }

    // ─── Per-chunk assign / return ───────────────────────────────────────────

    private void AssignChunk(ChunkKey chunkKey)
    {
        int packed = chunkKey.packed;
        FaceId face = chunkKey.face;

        // Decode trees (or use cache)
        if (!_chunkTreeCache.TryGetValue(chunkKey, out var trees))
        {
            var segment = _chunkManager.GetDecodedTreesForChunk(packed, face);
            if (segment.Count == 0)
            {
                trees = Array.Empty<TreeDecoder.DecodedTreeInstance>();
            }
            else
            {
                trees = new TreeDecoder.DecodedTreeInstance[segment.Count];
                segment.CopyTo(trees);
            }
            _chunkTreeCache[chunkKey] = trees;
        }

        if (trees == null || trees.Length == 0) return;

        bool assignedAny = false;

        foreach (var tree in trees)
        {
            byte pi = tree.prototypeIndex;
            var entry = prototypeRegistry?.GetPrototype(pi);
            if (entry == null || !entry.HasColliders) continue;

            for (byte si = 0; si < entry.colliderShapes.Length; si++)
            {
                var key = new SubPoolKey(pi, si);
                if (!_pools.TryGetValue(key, out var pool)) continue;

                PooledColliderObject obj = pool.idle.Count > 0
                    ? pool.idle.Pop()
                    : CreatePooledObject(pool); // emergency allocation; grow will follow

                PlaceCollider(obj, pool.shape, entry, tree);
                obj.packed = packed;
                obj.face = face;
                pool.active.Add(obj);
                assignedAny = true;
            }
        }

        if (assignedAny)
            _assignedChunkKeys.Add(chunkKey);
    }

    private void ReturnChunk(ChunkKey chunkKey)
    {
        _chunkTreeCache.Remove(chunkKey);
        _assignedChunkKeys.Remove(chunkKey);

        foreach (var pool in _pools.Values)
        {
            for (int i = pool.active.Count - 1; i >= 0; i--)
            {
                var obj = pool.active[i];
                if (obj.packed != chunkKey.packed || obj.face != chunkKey.face) continue;

                pool.active.RemoveAt(i);
                obj.collider.enabled = false;
                obj.go.transform.SetParent(_poolRoot, false);
                obj.go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                obj.go.transform.localScale = Vector3.one;
                pool.idle.Push(obj);
            }
        }
    }

    /// <summary>Returns all active colliders of every pool to idle immediately.</summary>
    private void ReturnAll()
    {
        foreach (var pool in _pools.Values)
        {
            foreach (var obj in pool.active)
            {
                obj.collider.enabled = false;
                obj.go.transform.SetParent(_poolRoot, false);
                obj.go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                obj.go.transform.localScale = Vector3.one;
            }
            foreach (var obj in pool.active)
                pool.idle.Push(obj);
            pool.active.Clear();
        }
        _chunkTreeCache.Clear();
        _assignedChunkKeys.Clear();
        _desiredChunkKeys.Clear();
    }

    // ─── Collider placement ──────────────────────────────────────────────────

    private void PlaceCollider(PooledColliderObject obj, TreeColliderShape shape,
        TreePrototypeRegistry.TreePrototypeEntry proto,
        in TreeDecoder.DecodedTreeInstance tree)
    {
        var t = obj.go.transform;

        // Compute the tree's full world-space orientation:
        // 1. Align local Y with the sphere surface normal (tree grows outward from sphere center)
        // 2. Apply the tree's own yaw rotation around that normal
        Vector3 surfaceNormal = (tree.worldPosition - _sphereCenter).normalized;
        Quaternion alignToSurface = Quaternion.FromToRotation(Vector3.up, surfaceNormal);
        Quaternion yawRotation   = Quaternion.AngleAxis(tree.rotationRadians * Mathf.Rad2Deg, surfaceNormal);
        Quaternion treeRotation  = yawRotation * alignToSurface;
        Quaternion shapeRotation = Quaternion.Euler(shape.localEulerAngles);
        Quaternion meshCorrection = Quaternion.identity;
        Quaternion fullRotation;

        // Interpret shape.center as the collider's pivot position in tree-local space.
        // Apply that offset in tree space first, then apply the shape-local rotation around
        // that shifted center. This prevents tilted branch colliders from orbiting around
        // the trunk base when their center is offset away from the root.
        Vector3 scaledCenterOffset;
        Vector3 finalScale = tree.Scale;
        // Base anchor of the collider mesh in its local space (mesh branch only).
        // Used to anchor the collider mesh's base to the target point, matching
        // TreeRenderer.ComputeTreeMatrix's use of cachedMeshBaseAnchor. Zero for
        // capsules (their dimensions are author-defined and centre-based).
        Vector3 colliderMeshBaseAnchor = Vector3.zero;

        if (shape.type == TreeColliderType.Capsule)
        {
            fullRotation = treeRotation * shapeRotation;

            // Scale capsule dimensions by the tree's individual scale factors.
            // The shape stores the base (scale=1) dimensions; apply the tree's
            // widthScale and heightScale so the collider fits the rendered mesh.
            var cap = (CapsuleCollider)obj.collider;
            int axis = (int)shape.axis;
            float w = tree.widthScale;
            float h = tree.heightScale;

            cap.radius    = shape.radius * w;
            cap.height    = shape.height * h;
            // Center offset: axis component scales by height, others by width.
            Vector3 sc = new Vector3(w, axis == 1 ? h : w, axis == 2 ? h : w);
            scaledCenterOffset = Vector3.Scale(shape.center, sc);
            cap.center = Vector3.zero;
        }
        else
        {
            Mesh mesh = shape.colliderMesh != null ? shape.colliderMesh : proto?.GetMeshForLOD(0);
            Vector3 meshBoundsSize = mesh != null ? mesh.bounds.size : Vector3.one;

            bool isZOriented = meshBoundsSize.z > meshBoundsSize.y * 1.5f;
            float meshHeight = isZOriented ? meshBoundsSize.z : meshBoundsSize.y;
            float meshWidth = Mathf.Max(meshBoundsSize.x, isZOriented ? meshBoundsSize.y : meshBoundsSize.z);

            meshHeight = Mathf.Max(meshHeight, 0.01f);
            meshWidth = Mathf.Max(meshWidth, 0.01f);

            meshCorrection = isZOriented ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
            fullRotation = treeRotation * shapeRotation * meshCorrection;

            // Compute the collider mesh's base anchor in its own local space, mirroring
            // TreePrototypeRegistry.CacheMeshData so the collider mesh is anchored by its
            // base (not its bounds centre) to the tree position. Without this the collider
            // mesh sits centred on tree.worldPosition while the render mesh sits with its
            // base there — colliders appear shifted downward by half the mesh height.
            if (mesh != null)
            {
                Bounds cb = mesh.bounds;
                Vector3 cc = cb.center;
                if (isZOriented)
                {
                    float baseZ = cc.z <= 0f ? cb.max.z : cb.min.z;
                    colliderMeshBaseAnchor = new Vector3(cc.x, cc.y, baseZ);
                }
                else
                {
                    colliderMeshBaseAnchor = new Vector3(cc.x, cb.min.y, cc.z);
                }
            }

            // Match TreeRenderer normalization so collider size follows the visible tree.
            if (proto != null)
            {
                if (isZOriented)
                {
                    finalScale = new Vector3(
                        (proto.baseWidth / meshWidth) * tree.widthScale,
                        (proto.baseWidth / meshWidth) * tree.widthScale,
                        (proto.baseHeight / meshHeight) * tree.heightScale
                    );
                }
                else
                {
                    finalScale = new Vector3(
                        (proto.baseWidth / meshWidth) * tree.widthScale,
                        (proto.baseHeight / meshHeight) * tree.heightScale,
                        (proto.baseWidth / meshWidth) * tree.widthScale
                    );
                }
            }

            scaledCenterOffset = Vector3.Scale(shape.center, tree.Scale);
        }

        // Anchor the collider mesh's base to the tree position, matching
        // TreeRenderer.ComputeTreeMatrix: position - rotation * Scale(baseAnchor, scale).
        // For capsules colliderMeshBaseAnchor is zero, so this is a no-op.
        Vector3 baseAnchorOffset = fullRotation * Vector3.Scale(colliderMeshBaseAnchor, finalScale);
        Vector3 worldCenter = tree.worldPosition + treeRotation * scaledCenterOffset - baseAnchorOffset;
        t.SetPositionAndRotation(worldCenter, fullRotation);
        t.localScale = finalScale;
        obj.collider.enabled = true;
    }

    // ─── Pool resize ─────────────────────────────────────────────────────────

    private void CheckPoolResize(SubPool pool)
    {
        int total = pool.TotalCount;
        if (total < pool.targetCount)
        {
            StartCoroutine(GrowPool(pool, pool.targetCount - total));
            return;
        }

        if (pool.idle.Count > pool.targetCount && pool.active.Count == 0)
        {
            pool.shrinkIdleFrames++;
            if (pool.shrinkIdleFrames >= shrinkGracePeriodFrames)
            {
                pool.shrinkIdleFrames = 0;
                int excess = pool.idle.Count - Mathf.Max(minimumFloor, pool.targetCount);
                if (excess > 0)
                    StartCoroutine(ShrinkPool(pool, excess));
            }
        }
        else
        {
            pool.shrinkIdleFrames = 0;
        }
    }

    private IEnumerator GrowPool(SubPool pool, int needed)
    {
        int created = 0;
        while (created < needed)
        {
            int batch = Mathf.Min(growBatchPerFrame, needed - created);
            for (int i = 0; i < batch; i++)
                pool.idle.Push(CreatePooledObject(pool));
            created += batch;
            yield return null;
        }
    }

    private IEnumerator ShrinkPool(SubPool pool, int excess)
    {
        int destroyed = 0;
        while (destroyed < excess && pool.idle.Count > minimumFloor)
        {
            int batch = Mathf.Min(shrinkBatchPerFrame, Mathf.Min(excess - destroyed, pool.idle.Count - minimumFloor));
            for (int i = 0; i < batch; i++)
                Destroy(pool.idle.Pop().go);
            destroyed += batch;
            yield return null;
        }
    }

    // ─── Lifecycle ───────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        if (!_initialized) return;
        ReturnAll();
        foreach (var pool in _pools.Values)
            foreach (var obj in pool.idle)
                if (obj?.go != null) Destroy(obj.go);
    }
}
#endif

