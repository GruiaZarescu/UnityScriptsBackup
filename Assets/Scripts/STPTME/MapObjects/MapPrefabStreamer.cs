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
    MapObjectDatabase sourceDatabase = null,
    Quaternion? explicitRotation = null)
    {
        STPTMEUtils.ReadFourSBytesFromInt(chunkPacked, out var map1, out var map2, out var chunk1, out var chunk2);

        MapObjectPrototypeRegistry.MapObjectPrototypeEntry entry = prototypeRegistry?.GetEntry(prototypeIndex);

        if (entry?.sourcePrefab == null)
        {
            Debug.LogWarning($"[MapPrefabStreamer] No prefab for prototypeIndex={prototypeIndex}");
            return null;
        }

        GameObject obj = GetOrCreatePooledObject(prototypeIndex, entry.sourcePrefab);
        if (obj == null)
        {
            Debug.LogWarning($"[MapPrefabStreamer] Failed to spawn object for prototypeIndex={prototypeIndex}");
            return null;
        }

        Transform t = obj.transform;
        t.position = worldPosition;

        // Blotch-derived content (trees etc.) has no meaningful authored orientation — it
        // wants "upright along the sphere radial, spun by a per-instance yaw", which is what
        // the rotationDeg path below builds. Map objects are different: they carry a REAL
        // authored quaternion (pitch and roll included — e.g. a fence tilted to follow a
        // slope), and rebuilding that from yaw alone silently discards everything but the
        // yaw. explicitRotation lets those callers pass the true orientation through intact.
        if (explicitRotation.HasValue)
        {
            t.rotation = explicitRotation.Value;
        }
        else
        {
            Vector3 radialUp = (worldPosition - sphereCenter).normalized;
            t.rotation = Quaternion.FromToRotation(Vector3.up, radialUp) * Quaternion.Euler(0f, rotationDeg, 0f);
        }

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
        metaComponent.sourceDatabase = sourceDatabase;

        // Must run AFTER transform is final and AFTER parenting. Awake() alone is insufficient:
        // pooled reuse finds an existing MapObjectMetadata, so AddComponent (and therefore Awake)
        // never fires again, leaving a stale collider from a previous spawn's position/layer.
        metaComponent.EnsurePickCollider();

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

    public class MapObjectMetadata : MonoBehaviour
    {
        public byte prototypeIndex;
        public uint seed;
        public ulong id;
        public MapObjectDatabase sourceDatabase;

        public static bool ShowAuthoringGizmos = false;
        public static bool SnapToGroundEnabled = false;

        /// <summary>Scales the pick sphere's radius relative to full mesh bounds. 1.0 = old
        /// behavior (exactly bounds.extents.magnitude); lower values shrink it for less visual
        /// clutter in dense clusters, at the cost of a smaller "click through gaps" margin.</summary>
        public static float PickSphereScale = 0.6f;

        /// <summary>When false, pick spheres are neither drawn nor used as a raycast fallback —
        /// picking only ever hits actual visible mesh geometry ("mesh collider only" mode).
        /// The collider itself still exists (so toggling back on works instantly); this only
        /// gates the gizmo draw and the fallback raycast pass in TryPickMapObject.</summary>
        public static bool PickSpheresEnabled = true;

        private const string PickChildName = "__PickCollider";

        private void Awake()
        {
            EnsurePickCollider();
        }

        /// <summary>
        /// Creates or re-syncs the bounding-sphere pick collider. Safe to call repeatedly —
        /// call it on every spawn, not just Awake, since pooled reuse skips Awake entirely.
        /// </summary>
        public void EnsurePickCollider()
        {
            // id == 0 means this object never came from MapObjectDatabase — a blotch-derived
            // tree, not a placed map object. Every spawned prefab gets this component
            // regardless of origin (SpawnObject attaches it unconditionally), so without this
            // check every tree in view would also grow a full-canopy-sized pick sphere the
            // moment the authoring dashboard is open — which is what was actually cluttering
            // the scene, not sphere size itself.
            if (id == 0) return;

            Transform pick = transform.Find(PickChildName);
            if (pick == null)
            {
                var pickObj = new GameObject(PickChildName);
                pickObj.transform.SetParent(transform, false);
                var sc = pickObj.AddComponent<SphereCollider>();
                sc.isTrigger = true;
                pick = pickObj.transform;
            }

            // Bounds computed from renderers only — exclude the pick child itself so it can
            // never feed its own size back into the calculation.
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            bool any = false;
            Bounds b = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].transform.IsChildOf(pick)) continue;
                if (!any) { b = renderers[i].bounds; any = true; }
                else b.Encapsulate(renderers[i].bounds);
            }
            if (!any) return;

            pick.position = b.center;

            var sphere = pick.GetComponent<SphereCollider>();
            if (sphere != null)
            {
                // Collider radius is in LOCAL space, so divide out the parent's scale —
                // otherwise a scaled prefab gets a sphere scaled twice over.
                float maxScale = Mathf.Max(Mathf.Abs(transform.lossyScale.x),
                                Mathf.Max(Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z)));
                if (maxScale < 0.0001f) maxScale = 1f;
                sphere.radius = (b.extents.magnitude / maxScale) * PickSphereScale;
            }

            int pickLayer = LayerMask.NameToLayer("MapObjectPicking");
            if (pickLayer < 0)
                Debug.LogWarning("[MapObjectMetadata] Layer 'MapObjectPicking' not found — add it in " +
                    "Project Settings > Tags and Layers. Pick raycasts will not work.");
            else
                pick.gameObject.layer = pickLayer;   // re-asserted every call, fixes colliders made before the layer existed
        }

    #if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!ShowAuthoringGizmos || !PickSpheresEnabled) return;
            if (id == 0) return; // blotch-derived tree, not a placed map object — see EnsurePickCollider

            // Derived live from renderer bounds rather than read off the cached child, so the
            // gizmo is correct even if the collider hasn't been re-synced yet.
            Renderer[] renderers = GetComponentsInChildren<Renderer>();
            Transform pick = transform.Find(PickChildName);
            bool any = false;
            Bounds b = default;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (pick != null && renderers[i].transform.IsChildOf(pick)) continue;
                if (!any) { b = renderers[i].bounds; any = true; }
                else b.Encapsulate(renderers[i].bounds);
            }
            if (!any) return;

            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireSphere(b.center, b.extents.magnitude * PickSphereScale);
        }

        private void OnDrawGizmosSelected()
        {
            if (!ShowAuthoringGizmos) return;
            if (id == 0) return; // blotch-derived tree — nothing here to select/edit as a map object
            UnityEditor.Handles.Label(transform.position + Vector3.up * 1.5f,
                $"id={id}  {(sourceDatabase != null ? "live" : "baked")}");
        }
    #endif
    }

}