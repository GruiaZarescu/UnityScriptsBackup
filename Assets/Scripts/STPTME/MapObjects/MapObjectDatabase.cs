using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Single, persistent source of truth for all standalone map objects (buildings, fences,
/// props — anything that isn't a terrain-tree-authored blotch). Replaces the old
/// "place prefabs under a container, bake, delete container" workflow.
///
/// One shared asset covers the whole 6-face map, mirroring BlotchOverrideDatabase's pattern.
/// Identity is a stable ulong assigned once at Add() time — never derived from list index
/// or from position/rotation (which are expected to change via Update()).
///
/// Runtime (shipped builds) NEVER reads this asset — it only exists for editor-time
/// authoring and as the export source for MapObjectBaker. Shipped builds read the baked
/// CellObjectGroup_*.bytes files via CellObjectReader, same as before.
/// </summary>
[CreateAssetMenu(fileName = "MapObjectDatabase", menuName = "STPTME/Map Object Database")]
public class MapObjectDatabase : ScriptableObject
{
    [Serializable]
    public struct MapObjectEntry
    {
        public ulong id;
        public int prototypeIndex;
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public Vector3 localScale;
    }

    [SerializeField] private List<MapObjectEntry> entries = new List<MapObjectEntry>();
    [SerializeField] private ulong nextId = 1; // 0 reserved as "invalid / not from this database"

    public IReadOnlyList<MapObjectEntry> All => entries;
    public int Count => entries.Count;

    /// <summary>
    /// Bumped on every Add/Remove/Update. Consumers (e.g. LiveDatabaseObjectSource) compare
    /// this against a cached value to know when their per-chunk index needs rebuilding,
    /// instead of rebuilding every frame.
    /// </summary>
    public int Version { get; private set; }

    private Dictionary<ulong, int> _idToIndex;
    private bool _lookupDirty = true;

    private void OnValidate() => _lookupDirty = true;

    private Dictionary<ulong, int> IdToIndex
    {
        get
        {
            if (_lookupDirty || _idToIndex == null)
            {
                _idToIndex = new Dictionary<ulong, int>(entries.Count);
                for (int i = 0; i < entries.Count; i++)
                    _idToIndex[entries[i].id] = i;
                _lookupDirty = false;
            }
            return _idToIndex;
        }
    }

    public ulong Add(int prototypeIndex, Vector3 worldPos, Quaternion worldRot, Vector3 scale)
    {
        ulong id = nextId++;
        entries.Add(new MapObjectEntry
        {
            id = id,
            prototypeIndex = prototypeIndex,
            worldPosition = worldPos,
            worldRotation = worldRot,
            localScale = scale
        });
        _lookupDirty = true;
        Version++;
        MarkDirty();
        return id;
    }

    public bool Remove(ulong id)
    {
        if (!IdToIndex.TryGetValue(id, out int idx)) return false;
        entries.RemoveAt(idx);
        _lookupDirty = true;
        Version++;
        MarkDirty();
        return true;
    }

    public bool TryGet(ulong id, out MapObjectEntry entry)
    {
        if (IdToIndex.TryGetValue(id, out int idx))
        {
            entry = entries[idx];
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>Moves/rotates/rescales an existing entry without changing its identity.</summary>
    public bool UpdateDatabase(ulong id, Vector3 worldPos, Quaternion worldRot, Vector3 scale)
    {
        if (!IdToIndex.TryGetValue(id, out int idx)) return false;
        var e = entries[idx];
        e.worldPosition = worldPos;
        e.worldRotation = worldRot;
        e.localScale = scale;
        entries[idx] = e;
        Version++;
        MarkDirty();
        return true;
    }

    private void MarkDirty()
    {
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif
    }
}