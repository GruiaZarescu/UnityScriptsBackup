using System.Collections.Generic;
using UnityEngine;
using CustomTypes;

/// <summary>
/// Per-terrain authoring override table for blotch radius/density. Lets multiple trees
/// sharing the same MapObjectPrototypeRegistry entry (same mesh) produce differently
/// sized/dense blotches, without needing duplicate prototype entries.
///
/// Keyed by BlotchHash.PositionSeed(tree.position, tree.prototypeIndex) — NOT by tree
/// array index, since Unity's TreeInstance array is not stable across terrain edits.
/// BlotchBaker must compute the identical hash at bake time for lookups to match.
/// </summary>
public class BlotchOverrideAuthoring : MonoBehaviour
{
    [Tooltip("The terrain this override table applies to. If left empty, uses the Terrain " +
             "component on this GameObject.")]
    [SerializeField] private Terrain terrain;

    [System.Serializable]
    public struct Entry
    {
        public uint seedHash;
        public float radius;
        public float density;
    }

    [Tooltip("Per-tree overrides. Populated via the scene view gizmo workflow or manually.")]
    [SerializeField] private List<Entry> overrides = new List<Entry>();

    [Header("Gizmo Display")]
    [SerializeField] private bool showGizmos = true;
    [SerializeField] private Color overriddenColor = Color.cyan;
    [SerializeField] private Color defaultColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);

    // Optional: assign so gizmos can show the EFFECTIVE radius/density (override or
    // prototype default) rather than only drawing overridden trees.
    [SerializeField] private MapObjectPrototypeRegistry registryForGizmoPreview;

    private Dictionary<uint, Entry> _lookup;
    private bool _lookupDirty = true;

    private Terrain ResolvedTerrain => terrain != null ? terrain : GetComponent<Terrain>();

    private void OnValidate() { _lookupDirty = true; }

    private Dictionary<uint, Entry> Lookup
    {
        get
        {
            if (_lookupDirty || _lookup == null)
            {
                _lookup = new Dictionary<uint, Entry>(overrides.Count);
                foreach (var o in overrides) _lookup[o.seedHash] = o;
                _lookupDirty = false;
            }
            return _lookup;
        }
    }

    /// <summary>Looks up an override by the shared position-seed hash. Called by BlotchBaker.</summary>
    public bool TryGetOverride(uint seedHash, out Entry entry) => Lookup.TryGetValue(seedHash, out entry);

    /// <summary>Adds or updates the override for a given seed hash.</summary>
    public void SetOverride(uint seedHash, float radius, float density)
    {
        for (int i = 0; i < overrides.Count; i++)
        {
            if (overrides[i].seedHash == seedHash)
            {
                var e = overrides[i];
                e.radius = radius;
                e.density = density;
                overrides[i] = e;
                _lookupDirty = true;
                return;
            }
        }
        overrides.Add(new Entry { seedHash = seedHash, radius = radius, density = density });
        _lookupDirty = true;
    }

    public void RemoveOverride(uint seedHash)
    {
        overrides.RemoveAll(e => e.seedHash == seedHash);
        _lookupDirty = true;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!showGizmos) return;
        var t = ResolvedTerrain;
        if (t == null || t.terrainData == null) return;

        var trees = t.terrainData.treeInstances;
        var size = t.terrainData.size;
        Vector3 terrainPos = t.GetPosition();

        foreach (var tree in trees)
        {
            uint seed = BlotchHash.PositionSeed(tree.position, tree.prototypeIndex);
            Vector3 worldPos = terrainPos + Vector3.Scale(tree.position, size);

            bool hasOverride = Lookup.TryGetValue(seed, out Entry ov);
            float radius = hasOverride ? ov.radius : 0f;
            bool haveDefaultRadius = false;

            if (!hasOverride && registryForGizmoPreview != null
                && tree.prototypeIndex >= 0 && tree.prototypeIndex < registryForGizmoPreview.entries.Length)
            {
                var proto = registryForGizmoPreview.entries[tree.prototypeIndex];
                if (proto != null) { radius = proto.blotchRadius; haveDefaultRadius = true; }
            }

            Gizmos.color = hasOverride ? overriddenColor : defaultColor;

            if (radius > 0.01f)
            {
                Gizmos.DrawWireSphere(worldPos, radius);
            }
            else
            {
                // Point-instance (radius 0) or unknown default — small marker so it's still visible.
                Gizmos.DrawWireCube(worldPos, Vector3.one * 0.5f);
            }

#if UNITY_EDITOR
            if (hasOverride)
            {
                UnityEditor.Handles.Label(worldPos + Vector3.up * (radius + 0.5f),
                    $"r={ov.radius:F1}m  d={ov.density:F1}/m²");
            }
#endif
        }
    }
#endif
}