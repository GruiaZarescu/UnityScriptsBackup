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

    /// <summary>Call once at the start of a bake run so the missing-default warning fires
    /// at most once per prototype per run, not once per tree.</summary>
    public static void ResetBakeWarnings() => _warnedMissingDefault.Clear();

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
            int protoIdx = tree.prototypeIndex;
            if (protoIdx < 0 || protoIdx >= prototypeRegistry.entries.Length)
                continue;

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
            uint seed = BlotchHash.PositionSeed(tree.position, tree.prototypeIndex);

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
                    Debug.LogWarning($"[BlotchBaker] No override or prototype default for prototype {protoIdx} " +
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