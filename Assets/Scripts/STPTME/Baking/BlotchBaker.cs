using UnityEngine;
using System.Collections.Generic;
using System.IO;
using CustomTypes;

#if UNITY_EDITOR

/// <summary>
/// Editor-only: Extracts blotch data from Unity terrain trees and provides
/// serialization helpers for the group cell file blotch section.
///
/// Each Unity terrain tree whose prototypeIndex maps to a blotch-enabled
/// entry (in MapObjectPrototypeRegistry) is
/// serialized as a BlotchData instead of an STPTMETreeInstance.
/// The tree acts as a "flag" — its position marks the blotch center.
/// Blotch parameters (radius, density, conflictCategory) come from the
/// prototype entry, not from the tree instance itself.
/// </summary>
public static class BlotchBaker
{
    public const int BLOTCH_INSTANCE_SIZE = 16; // BlotchData is 16 bytes
    private static HashSet<int> _warnedMissingDefault = new HashSet<int>();

    // Cached Unity-prototype-index -> registry-array-index map, one per registry instance.
    // Rebuilt lazily; invalidated whenever ResetBakeWarnings() is called (start of a bake run),
    // so edits made to unityTerrainPrototypeIndex between bakes are always picked up.
    private static readonly Dictionary<MapObjectPrototypeRegistry, Dictionary<int, int>> _unityIndexCache
        = new Dictionary<MapObjectPrototypeRegistry, Dictionary<int, int>>();

    /// <summary>
    /// Resolves a Unity terrain tree prototype index to its corresponding entry's index in
    /// THIS registry's array, via each entry's explicit unityTerrainPrototypeIndex field.
    /// Returns -1 if no entry claims that Unity prototype (e.g. it's genuinely unmapped) or if
    /// two entries claim the same one (a real authoring error — logged once, not silently
    /// resolved to whichever happened to be found first).
    /// </summary>
    public static int ResolveRegistryIndexForUnityPrototype(MapObjectPrototypeRegistry registry, int unityProtoIdx)
    {
        if (registry == null || registry.entries == null || unityProtoIdx < 0) return -1;

        if (!_unityIndexCache.TryGetValue(registry, out var map))
        {
            map = new Dictionary<int, int>();
            for (int i = 0; i < registry.entries.Length; i++)
            {
                var e = registry.entries[i];
                if (e == null || e.unityTerrainPrototypeIndex < 0) continue;

                if (map.TryGetValue(e.unityTerrainPrototypeIndex, out int existing))
                {
                    Debug.LogError($"[BlotchBaker] Registry entries \"{registry.entries[existing].name}\" (index {existing}) " +
                        $"and \"{e.name}\" (index {i}) both claim Unity tree prototype {e.unityTerrainPrototypeIndex}. " +
                        "Only one can be correct — fix unityTerrainPrototypeIndex on one of them.");
                    continue; // keep the first mapping; don't let a duplicate silently overwrite it
                }
                map[e.unityTerrainPrototypeIndex] = i;
            }
            _unityIndexCache[registry] = map;
        }

        return map.TryGetValue(unityProtoIdx, out int registryIdx) ? registryIdx : -1;
    }

    /// <summary>Call once at the start of a bake run so the missing-default warning fires
    /// at most once per prototype per run, not once per tree.</summary>
    public static void ResetBakeWarnings()
    {
        _warnedMissingDefault.Clear();
        _unityIndexCache.Clear(); // also refreshes the Unity-index mapping for this run
    }

    /// <summary>
    /// Extracts blotches from all trees in the given terrain, writing them into
    /// the per-cell blotch lists on the provided dictionaries.
    ///
    /// Called after cell buffers are set up (no longer needs legacy TreeBaker ordering).
    /// processed trees into cellBuffers, because we piggyback on the same
    /// cell/chunk indexing logic.
    ///
    /// Trees whose prototypeIndex maps to an entry with blotchRadius >= 0
    /// are converted to BlotchData. The blotch replaces the tree in the
    /// procedural system; the tree is NOT added to the instanced tree list.
    /// </summary>
    public static void ExtractBlotchesFromTerrain(
    Terrain terrain,
    FaceId face,
    FaceContainerOrientation orientation,
    Dictionary<Vector2SByte, List<BlotchData>> blotchBuffers,
    int subdivisionsPowerOf2,
    int numberOfChunks,
    int tilingFactor,
    Vector3 sphereCenter,
    float sphereRadius,
    float maxHeight,
    float faceWorldSize,
    float planeTerrainOriginX,
    float planeTerrainOriginZ,
    sbyte cellKeyBaseX,
    sbyte cellKeyBaseZ,
    MapObjectPrototypeRegistry prototypeRegistry,
    float chunkSizeMeters,
    BlotchOverrideDatabase overrideDatabase = null,   // CHANGED from BlotchOverrideAuthoring
    sbyte terrainGridX = 0,                            // NEW — needed to key the lookup
    sbyte terrainGridY = 0)  
    {
        TerrainData td = terrain.terrainData;
        float terrainSize = td.size.x;
        TreeInstance[] trees = td.treeInstances;

        if (trees == null || trees.Length == 0) return;
        if (prototypeRegistry == null || prototypeRegistry.entries == null) return;

        

        float cellSize = terrainSize / subdivisionsPowerOf2;

        foreach (var tree in trees)
        {
            int unityProtoIdx = tree.prototypeIndex;

            // Translate Unity's tree-prototype index into THIS registry's array index. These
            // are independent numbering schemes: Unity's tree prototype list is fixed by what's
            // painted on the terrain, while the registry can contain object-pathway-only entries
            // (fences, buildings) anywhere in its array. Previously this used unityProtoIdx
            // directly as the registry index too, which happened to work only because no
            // object-only entries existed before any tree entries — inserting one shifted every
            // tree after it out of alignment, causing baked blotches to point at the wrong
            // registry entry entirely (e.g. a fence spawning where a tree should be).
            //
            // BlotchHash.PositionSeed below deliberately keeps using unityProtoIdx, NOT the
            // translated index — every existing per-tree override in BlotchOverrideDatabase was
            // authored against seeds computed from Unity's numbering, and changing that now
            // would desync every override already placed.
            int protoIdx = ResolveRegistryIndexForUnityPrototype(prototypeRegistry, unityProtoIdx);
            if (protoIdx < 0)
                continue; // no registry entry declares this Unity prototype as its own

            var proto = prototypeRegistry.entries[protoIdx];
            if (proto == null) continue;

            // Only convert trees whose prototype has blotch parameters.
            // A blotchRadius of 0 means single-instance (exact position tree).
            // A blotchRadius > 0 means procedural cluster.
            // We filter by checking if the entry has the blotch fields populated.
            // At minimum, use the entry if it has been configured (name is set).
            if (string.IsNullOrEmpty(proto.name))
                continue;

            // Re-orient tree position into plane space (matches cell/chunk indexing).
            FaceContainerOrientations.NormalizedWorldToPlane(orientation,
                tree.position.x, tree.position.z,
                out float npx, out float npz);

            float treePlaneRelX = npx * terrainSize;
            float treePlaneRelZ = npz * terrainSize;
            float treePlaneFaceX = planeTerrainOriginX + treePlaneRelX;
            float treePlaneFaceZ = planeTerrainOriginZ + treePlaneRelZ;

            // Cell coordinate.
            int cellLocalX = Mathf.Clamp(Mathf.FloorToInt(treePlaneRelX / cellSize), 0, subdivisionsPowerOf2 - 1);
            int cellLocalZ = Mathf.Clamp(Mathf.FloorToInt(treePlaneRelZ / cellSize), 0, subdivisionsPowerOf2 - 1);

            float chunkSize = cellSize / numberOfChunks;
            float cellRelX = treePlaneRelX - (cellLocalX * cellSize);
            float cellRelZ = treePlaneRelZ - (cellLocalZ * cellSize);

            int chunkLocalX = Mathf.Clamp(Mathf.FloorToInt(cellRelX / chunkSize), 0, numberOfChunks - 1);
            int chunkLocalZ = Mathf.Clamp(Mathf.FloorToInt(cellRelZ / chunkSize), 0, numberOfChunks - 1);


            Vector2SByte cellKey = new Vector2SByte(
                (sbyte)(cellKeyBaseX + cellLocalX),
                (sbyte)(cellKeyBaseZ + cellLocalZ)
            );

            if (!blotchBuffers.TryGetValue(cellKey, out var blotchList))
            {
                blotchList = new List<BlotchData>();
                blotchBuffers[cellKey] = blotchList;
            }

            // Build chunkPacked ID: (mapX<<24)|(mapY<<16)|(chunkX<<8)|chunkY
            // We're at the cell level here; the chunkX/chunkY will be resolved
            // at GPU time based on the blotch's local position.
            // For now, store map coordinates from the cell key.
            int cellPacked = STPTMEUtils.WriteFourSBytesInInt(cellKey.x, cellKey.y, (sbyte)chunkLocalX, (sbyte)chunkLocalZ);

            // Generate a deterministic seed from the tree instance index.
            // Since TreeInstance doesn't have a stable ID, we use position
            // hash so the blotch is stable across re-bakes.
            uint seed = BlotchHash.PositionSeed(tree.position, unityProtoIdx); // Unity's index space — see comment above

             // Compute chunk origin in face-plane space (cell origin + chunk offset inside the cell)
            float chunkOriginX = planeTerrainOriginX + cellLocalX * cellSize + chunkLocalX * chunkSize;
            float chunkOriginZ = planeTerrainOriginZ + cellLocalZ * cellSize + chunkLocalZ * chunkSize;

            // Quantize blotch center position relative to CHUNK origin (not cell)
            float localX = treePlaneFaceX - chunkOriginX;
            float localZ = treePlaneFaceZ - chunkOriginZ;

            // Clamp to CHUNK bounds — matches BlotchData's 16-bit / 75 m encoding.
            localX = Mathf.Clamp(localX, 0f, chunkSize);
            localZ = Mathf.Clamp(localZ, 0f, chunkSize);

            // Read blotch parameters from the prototype entry.
            byte conflictCategory = proto.conflictCategory;

            float blotchRadius, blotchDensity;
            BlotchOverrideDatabase.Entry blotchOverride = default;
            bool hasOverride = overrideDatabase != null &&
                overrideDatabase.TryGetOverride(face, terrainGridX, terrainGridY, seed, out blotchOverride);

            if (hasOverride)
            {
                blotchRadius = blotchOverride.radius;
                blotchDensity = blotchOverride.density;
            }
            else if (overrideDatabase != null && overrideDatabase.TryGetPrototypeDefault(protoIdx, out var protoDefault))
            {
                blotchRadius = protoDefault.radius;
                blotchDensity = protoDefault.density;
            }
            else
            {
                blotchRadius = 0f;
                blotchDensity = 0f;
                if (_warnedMissingDefault.Add(protoIdx))
                    Debug.LogWarning($"[BlotchBaker] No override or prototype default for registry entry {protoIdx} " +
                        $"(\"{proto.name}\", Unity tree prototype {unityProtoIdx}) " +
                        $"— baking as (radius=0, density=0). Set a default in BlotchOverrideDatabase.");
            }

            bool cullLODOverride = proto.cullLOD != 255;
            bool instanceAlways = proto.instanceAlways;

                var blotch = new BlotchData(
                chunkPacked: cellPacked,
                face: face,
                prototypeIndex: (byte)protoIdx,
                conflictCategory: conflictCategory,
                seed: seed,
                densityPerSqM: blotchDensity,
                radiusMeters: blotchRadius,
                localXMeters: localX,
                localZMeters: localZ,
                chunkSizeMeters: chunkSizeMeters
            );

            blotchList.Add(blotch);
        }
    }

    /// <summary>
    /// Writes a blotch data blob for one subcell into the BinaryWriter.
    /// Called from WriteGroupCellFile after the tree data section.
    /// </summary>
    public static void WriteBlotchSection(BinaryWriter writer, List<BlotchData> blotches)
    {
        int count = blotches?.Count ?? 0;
        writer.Write(count);

        if (count > 0)
        {
            foreach (var blotch in blotches)
            {
                writer.Write(blotch.chunkPacked);
                writer.Write(blotch.packedMeta);
                writer.Write(blotch.seedAndDensity);
                writer.Write(blotch.packedPos);
            }
        }
    }
}

#endif