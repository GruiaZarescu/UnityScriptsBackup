using System;
using System.Collections.Generic;
using UnityEngine;
using CustomTypes;

/// <summary>
/// Subscribes to <see cref="ChunkRegistry.OnChunkCreated"/> and
/// <see cref="ChunkRegistry.OnChunkRemoved"/> and instantiates / destroys the world
/// objects that belong to each chunk, keyed by LOD level.
///
/// When chunk LOD X is created, all <see cref="CellObjectReader.CellObjectInstance"/>
/// entries with <c>lodLevel == X</c> for that chunk are instantiated from the
/// <see cref="LegacyMapObjectRegistry"/>. When the chunk is removed, those instances
/// are destroyed.
/// </summary>
public class ChunkObjectLoader : MonoBehaviour
{

    [SerializeField, Tooltip("New unified registry for blotch parameters and instance rules. "
                           + "Used to decide if an object should be GPU-instanced vs GameObject at runtime.")]
    private MapObjectPrototypeRegistry prototypeRegistry;

    [SerializeField, Tooltip("Parent transform for all spawned objects. Leave null to use scene root.")]
    private Transform objectParent;

    // ── Runtime state ─────────────────────────────────────────────────────────

    private CellObjectReader _reader;
    private int    _numberOfChunks;

    // Key: packed-int chunk + face + lod encoded together.
    // Value: list of instantiated GameObjects for that chunk/lod.
    private readonly Dictionary<long, List<GameObject>> _spawned
        = new Dictionary<long, List<GameObject>>();

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Start()
    {

        var settings    = TerrainManagementSettings.Instance;
        _numberOfChunks = settings.numberOfChunks;

        _reader = new CellObjectReader();
        _reader.Init(1 << settings.heightmapSubdivisions, settings.minX);

        // Subscribe once ChunkManager/ChunkRegistry are guaranteed to exist.
        var chunkRegistry = ChunkManager.Instance?.chunkRegistry;
        if (chunkRegistry == null)
        {
            Debug.LogError("[ChunkObjectLoader] ChunkManager or ChunkRegistry not found.", this);
            enabled = false;
            return;
        }

        chunkRegistry.OnChunkCreated += HandleChunkCreated;
        chunkRegistry.OnChunkRemoved += HandleChunkRemoved;
    }

    private void OnDestroy()
    {
        var cr = ChunkManager.Instance?.chunkRegistry;
        if (cr != null)
        {
            cr.OnChunkCreated -= HandleChunkCreated;
            cr.OnChunkRemoved -= HandleChunkRemoved;
        }

        // Destroy all remaining spawned objects
        foreach (var list in _spawned.Values)
            DestroyList(list);
        _spawned.Clear();
    }

    // ── Chunk event handlers ──────────────────────────────────────────────────

    private void HandleChunkCreated(int packed, FaceId face, byte lod)
    {
        var segment = _reader.GetObjectsForChunk(packed, face, _numberOfChunks, lod);
        if (segment.Count == 0) return;

        var instances = new List<GameObject>(segment.Count);
        long key = EncodeKey(packed, face, lod);

        foreach (var obj in segment)
        {
            var entry = prototypeRegistry?.GetEntry(obj.prototypeIndex);
            if (entry == null)
            {
                Debug.LogWarning(
                    $"[ChunkObjectLoader] No MapObjectPrototypeRegistry entry for prototypeIndex={obj.prototypeIndex} — skipping.");
                continue;
            }

            // GPU instanced at this LOD — skip GameObject spawn.
            if (entry.IsInstancedAtLOD(lod))
                continue;

            GameObject prefab = entry.sourcePrefab;
            if (prefab == null)
            {
                Debug.LogWarning(
                    $"[ChunkObjectLoader] Entry[{obj.prototypeIndex}] '{entry.name}' has no sourcePrefab — skipping.");
                continue;
            }

            GameObject instance = Instantiate(prefab, obj.position, obj.rotation, objectParent);
            instance.transform.localScale = obj.scale;
            instances.Add(instance);
        }

        if (instances.Count > 0)
            _spawned[key] = instances;
    }

    private void HandleChunkRemoved(int packed, FaceId face, byte lod)
    {
        long key = EncodeKey(packed, face, lod);
        if (!_spawned.TryGetValue(key, out var list)) return;

        DestroyList(list);
        _spawned.Remove(key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Encodes (packed int32, FaceId 0-5, lod 0-255) into a unique long key.
    private static long EncodeKey(int packed, FaceId face, byte lod)
        => ((long)(uint)packed) | ((long)(byte)face << 32) | ((long)lod << 40);

    private static void DestroyList(List<GameObject> list)
    {
        foreach (GameObject go in list)
            if (go != null) Destroy(go);
        list.Clear();
    }
}
