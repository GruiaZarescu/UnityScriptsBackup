using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Single centralized override table for blotch radius/density, replacing per-terrain
/// authoring components. One asset covers every terrain across all 6 faces.
///
/// Keyed by (face, terrain tile grid X/Y, position-seed hash) — NOT by seed hash alone,
/// since two different terrain tiles can produce identical position-seed hashes for trees
/// sitting at the same relative [0,1] spot on each tile. Terrain identity must be part
/// of the key to avoid silently cross-applying one tile's override to another's tree.
/// </summary>
[CreateAssetMenu(fileName = "BlotchOverrideDatabase", menuName = "STPTME/Blotch Override Database")]
public class BlotchOverrideDatabase : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public FaceId face;
        public sbyte terrainGridX;
        public sbyte terrainGridY;
        public uint seedHash;
        public float radius;
        public float density;
    }

    [System.Serializable]
    public struct PrototypeDefault
    {
        public int prototypeIndex;
        public float radius;
        public float density;
    }

    [SerializeField] private List<PrototypeDefault> prototypeDefaults = new List<PrototypeDefault>();
    private Dictionary<int, PrototypeDefault> _protoDefaultLookup;
    private bool _protoDefaultDirty = true;

    private Dictionary<int, PrototypeDefault> ProtoDefaultLookup
    {
        get
        {
            if (_protoDefaultDirty || _protoDefaultLookup == null)
            {
                _protoDefaultLookup = new Dictionary<int, PrototypeDefault>(prototypeDefaults.Count);
                foreach (var d in prototypeDefaults) _protoDefaultLookup[d.prototypeIndex] = d;
                _protoDefaultDirty = false;
            }
            return _protoDefaultLookup;
        }
    }

    public bool TryGetPrototypeDefault(int prototypeIndex, out PrototypeDefault entry)
        => ProtoDefaultLookup.TryGetValue(prototypeIndex, out entry);

    public void SetPrototypeDefault(int prototypeIndex, float radius, float density)
    {
        for (int i = 0; i < prototypeDefaults.Count; i++)
        {
            if (prototypeDefaults[i].prototypeIndex == prototypeIndex)
            {
                var e = prototypeDefaults[i]; e.radius = radius; e.density = density;
                prototypeDefaults[i] = e;
                _protoDefaultDirty = true;
                return;
            }
        }
        prototypeDefaults.Add(new PrototypeDefault { prototypeIndex = prototypeIndex, radius = radius, density = density });
        _protoDefaultDirty = true;
    }

    [Header("Migration (one-time, editor only)")]
    [SerializeField] private MapObjectPrototypeRegistry migrationSourceRegistry;

    #if UNITY_EDITOR
    [ContextMenu("Migrate Prototype Defaults From Registry")]
    public void MigrateFromRegistry()
    {
        if (migrationSourceRegistry?.entries == null)
        {
            Debug.LogWarning("[BlotchOverrideDatabase] Assign migrationSourceRegistry before running migration.");
            return;
        }
        for (int i = 0; i < migrationSourceRegistry.entries.Length; i++)
        {
            var e = migrationSourceRegistry.entries[i];
            if (e == null) continue;
            SetPrototypeDefault(i, e.blotchRadius, e.blotchDensity);
        }
        Debug.Log($"[BlotchOverrideDatabase] Migrated {migrationSourceRegistry.entries.Length} prototype defaults.");
    }
    #endif

    [SerializeField] private List<Entry> overrides = new List<Entry>();

    private Dictionary<(FaceId, sbyte, sbyte, uint), Entry> _lookup;
    private bool _dirty = true;

    private void OnValidate() {_dirty = true; _protoDefaultDirty = true;}

    private Dictionary<(FaceId, sbyte, sbyte, uint), Entry> Lookup
    {
        get
        {
            if (_dirty || _lookup == null)
            {
                _lookup = new Dictionary<(FaceId, sbyte, sbyte, uint), Entry>(overrides.Count);
                foreach (var o in overrides)
                    _lookup[(o.face, o.terrainGridX, o.terrainGridY, o.seedHash)] = o;
                _dirty = false;
            }
            return _lookup;
        }
    }

    public bool TryGetOverride(FaceId face, sbyte terrainGridX, sbyte terrainGridY, uint seedHash, out Entry entry)
        => Lookup.TryGetValue((face, terrainGridX, terrainGridY, seedHash), out entry);

    public void SetOverride(FaceId face, sbyte terrainGridX, sbyte terrainGridY, uint seedHash, float radius, float density)
    {
        for (int i = 0; i < overrides.Count; i++)
        {
            var e = overrides[i];
            if (e.face == face && e.terrainGridX == terrainGridX && e.terrainGridY == terrainGridY && e.seedHash == seedHash)
            {
                e.radius = radius;
                e.density = density;
                overrides[i] = e;
                _dirty = true;
                return;
            }
        }
        overrides.Add(new Entry { face = face, terrainGridX = terrainGridX, terrainGridY = terrainGridY,
            seedHash = seedHash, radius = radius, density = density });
        _dirty = true;
    }

    public void RemoveOverride(FaceId face, sbyte terrainGridX, sbyte terrainGridY, uint seedHash)
    {
        overrides.RemoveAll(e => e.face == face && e.terrainGridX == terrainGridX
            && e.terrainGridY == terrainGridY && e.seedHash == seedHash);
        _dirty = true;
    }

    public IReadOnlyList<Entry> AllOverrides => overrides;
}