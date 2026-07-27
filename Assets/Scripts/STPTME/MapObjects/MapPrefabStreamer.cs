using UnityEngine;
using System.Collections.Generic;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Handles streaming and pooling of LOD0 GameObjects for map objects.
    /// Used by ChunkObjectLoader when a blob or cell object requires instantiation
    /// (typically LOD0 and non-instanced LOD1+ objects).
    /// 
    /// Separates object spawning logic from orchestration, enabling:
    /// - Object pooling and reuse
    /// - Async prefab loading (SRP)
    /// - Hierarchical organization (parent per chunk)
    /// - Batch activation/deactivation
    /// </summary>
    public class MapPrefabStreamer : MonoBehaviour
    {
        [SerializeField] private MapObjectPrototypeRegistry prototypeRegistry;
        [SerializeField] private bool useObjectPooling = true;
        [SerializeField] private int poolSizePerPrototype = 50;

        public ulong id;

        // Per-prototype object pools
        private Dictionary<int, Queue<GameObject>> objectPools;

        // Track active instances per chunk for cleanup — key = EncodeKey(packed, face, lod)
        private Dictionary<long, List<GameObject>> activeInstancesByChunk;

        // ===== LIFECYCLE =====

        private void Awake()
        {
            objectPools = new Dictionary<int, Queue<GameObject>>();
            activeInstancesByChunk = new Dictionary<long, List<GameObject>>();

            // Pre-populate pools for all prototype entries
            if (useObjectPooling && prototypeRegistry != null)
            {
                for (int i = 0; i < prototypeRegistry.entries.Length; i++)
                {
                    var entry = prototypeRegistry.entries[i];
                    if (entry?.sourcePrefab != null)
                    {
                        InitializePool(i, entry.sourcePrefab, poolSizePerPrototype);
                    }
                }
            }
        }

        // ===== POOL MANAGEMENT =====

        private void InitializePool(int prototypeIndex, GameObject prefab, int count)
        {
            //Debug.Log($"[MapPrefabStreamer] Initializing pool for prototypeIndex={prototypeIndex} with {count} instances of prefab '{prefab.name}'");
            var pool = new Queue<GameObject>(count);
            for (int i = 0; i < count; i++)
            {
                GameObject obj = Instantiate(prefab);
                obj.name = $"{prefab.name}_{i}";
                obj.SetActive(false);
                pool.Enqueue(obj);
            }
            objectPools[prototypeIndex] = pool;
        }

        private GameObject GetOrCreatePooledObject(int prototypeIndex, GameObject prefab)
        {
            if (!useObjectPooling || prefab == null)
                return Instantiate(prefab);

            if (!objectPools.TryGetValue(prototypeIndex, out var pool))
            {
                InitializePool(prototypeIndex, prefab, poolSizePerPrototype);
                pool = objectPools[prototypeIndex];
            }

            GameObject obj;
            if (pool.Count > 0)
            {
                obj = pool.Dequeue();
                obj.SetActive(true);
            }
            else
            {
                // Pool exhausted, create new (will return to pool later)
                obj = Instantiate(prefab);
                obj.name = $"{prefab.name}_PoolOverflow";
                obj.SetActive(true);
            }

            return obj;
        }

        private void ReturnToPool(int prototypeIndex, GameObject obj)
        {
            if (!useObjectPooling || !objectPools.TryGetValue(prototypeIndex, out var pool))
            {
                Destroy(obj);
                return;
            }

            obj.SetActive(false);
            obj.transform.parent = null;
            pool.Enqueue(obj);
        }

        // ===== SPAWN / DESPAWN =====

        /// <summary>
        /// Spawns or retrieves a pooled object for the given prototype at the world position.
        /// Registers it under the chunk for later cleanup.
        /// </summary>

        public GameObject SpawnObject(
    int prototypeIndex,
    Transform parentTransform,
    int chunkPacked,
    FaceId face,
    byte chunkLOD,
    Vector3 worldPosition,
    float rotationDeg,
    float heightScale,
    uint seed,
    Vector3 sphereCenter,
    float widthScale = 1f,
    ulong mapObjectId = 0,
    MapObjectDatabase sourceDatabase = null)   // NEW
    {
        //Final stage of streaming pipeline, CPU/Object branch. Start of debug to see where it's broken.
        STPTMEUtils.ReadFourSBytesFromInt(chunkPacked,out var map1, out var map2, out var chunk1, out var chunk2);

        MapObjectPrototypeRegistry.MapObjectPrototypeEntry entry = prototypeRegistry?.GetEntry(prototypeIndex);

        if (entry?.sourcePrefab == null)
        {
            Debug.LogWarning($"[MapPrefabStreamer] No prefab for prototypeIndex={prototypeIndex}");
            return null;
        }

        GameObject obj = GetOrCreatePooledObject(prototypeIndex, entry.sourcePrefab);
        if (obj == null) {
            Debug.LogWarning($"[MapPrefabStreamer] Failed to spawn object for prototypeIndex={prototypeIndex}");
            return null;
        }

        Transform t = obj.transform;
        t.position = worldPosition;
        Vector3 radialUp = (worldPosition - sphereCenter).normalized;
        t.rotation = Quaternion.FromToRotation(Vector3.up, radialUp) * Quaternion.Euler(0f, rotationDeg, 0f);
        Vector3 baseScale = entry.sourcePrefab.transform.localScale;
        t.localScale = new Vector3(baseScale.x * widthScale, baseScale.y * heightScale, baseScale.z * widthScale);
        obj.name = "prefab " + entry.sourcePrefab.name;
        obj.SetActive(true);

        if (parentTransform != null)
            t.SetParent(parentTransform);

        long chunkKey = EncodeChunkKey(chunkPacked, face, chunkLOD);
        if (!activeInstancesByChunk.TryGetValue(chunkKey, out var instances))
        {
            instances = new List<GameObject>();
            activeInstancesByChunk[chunkKey] = instances;
        }
        instances.Add(obj);

        var metaComponent = obj.GetComponent<MapObjectMetadata>() ?? obj.AddComponent<MapObjectMetadata>();
        metaComponent.prototypeIndex = (byte)prototypeIndex;
        metaComponent.seed = seed;
        metaComponent.id = mapObjectId;
        metaComponent.sourceDatabase = sourceDatabase;   // NEW — null for baked-file spawns, real asset for live-authoring spawns
        return obj;
    }

        /// <summary>
        /// Despawns all objects for the given chunk, returning them to the pool.
        /// </summary>
        public void DespawnChunkObjects(int chunkPacked, FaceId face, byte chunkLOD)
        {
            long chunkKey = EncodeChunkKey(chunkPacked, face, chunkLOD);

            if (activeInstancesByChunk.TryGetValue(chunkKey, out var instances))
            {
                foreach (var obj in instances)
                {
                    var meta = obj.GetComponent<MapObjectMetadata>();
                    int prototypeIndex = meta?.prototypeIndex ?? -1;
                    ReturnToPool(prototypeIndex, obj);
                }
                activeInstancesByChunk.Remove(chunkKey);
            }
        }

        // ===== HELPERS =====

        private long EncodeChunkKey(int chunkPacked, FaceId face, byte lod)
        {
            return ((long)(uint)chunkPacked << 16) | ((uint)face << 8) | lod;
        }

        public void ClearAll()
        {
            foreach (var instances in activeInstancesByChunk.Values)
                foreach (var obj in instances)
                    Destroy(obj);

            activeInstancesByChunk.Clear();

            foreach (var pool in objectPools.Values)
                foreach (var obj in pool)
                    Destroy(obj);

            objectPools.Clear();
        }
    }

    /// <summary>
    /// Lightweight metadata attached to spawned objects for runtime queries.
    /// </summary>
    [System.Serializable]
    public class MapObjectMetadata : MonoBehaviour
    {
        public byte prototypeIndex;
        public uint seed;
        public ulong id;

        public MapObjectDatabase sourceDatabase; // null if spawned from a baked file, not live-editable

        public static bool ShowAuthoringGizmos = false; // toggled by MapObjectAuthoringWindow

        private void Awake()
        {
            EnsurePickCollider();
        }

        private void EnsurePickCollider()
        {
            if (transform.Find("__PickCollider") != null) return;

            var renderers = GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

            var pickObj = new GameObject("__PickCollider");
            pickObj.transform.SetParent(transform, false);
            pickObj.transform.position = b.center;

            int pickLayer = LayerMask.NameToLayer("MapObjectPicking");
            if (pickLayer < 0)
                Debug.LogWarning("[MapObjectMetadata] Layer 'MapObjectPicking' not found — add it in " +
                    "Project Settings > Tags and Layers, or pick raycasts will fall back to Default.");
            pickObj.layer = pickLayer >= 0 ? pickLayer : gameObject.layer;

            var sphere = pickObj.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = b.extents.magnitude; // encloses full renderer bounds regardless of mesh holes
        }

        private void OnDrawGizmos()
        {
            if (!ShowAuthoringGizmos) return;
            var pick = transform.Find("__PickCollider");
            if (pick == null) return;
            var sc = pick.GetComponent<SphereCollider>();
            if (sc == null) return;

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireSphere(pick.position, sc.radius);
        }

        #if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!ShowAuthoringGizmos) return;
            UnityEditor.Handles.Label(transform.position + Vector3.up * 1.5f,
                $"id={id}  proto={(sourceDatabase != null ? "live" : "baked")}");
        }
        #endif

    }

}

