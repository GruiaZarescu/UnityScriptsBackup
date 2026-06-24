using UnityEngine;
using System.Collections.Generic;
using System.IO;
using CustomTypes;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Editor-only: Extracts tree instances from Unity terrains, quantizes to 6-byte polar format,
/// and provides data for combined cell file serialization.
/// </summary>
public static class TreeBaker
{
    // ===== FORMAT CONSTANTS =====
    public const ushort CELL_FORMAT_VERSION = 1;
    public const int CELL_HEADER_SIZE = 64;
    public const int TREE_INDEX_ENTRY_SIZE = 8;
    public const int TREE_INSTANCE_SIZE = 8;

    // ===== DATA STRUCTURES =====

    /// <summary>
    /// Compact 8-byte tree instance stored per chunk.
    /// Position is polar relative to chunk center on sphere tangent plane.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct STPTMETreeInstance
    {
        public byte spin;           // 0-255 → 0-360° polar angle from chunk-north tangent
        public byte distance;       // 0-255 → 0 to maxPolarDistance (chunk diagonal/2)
        public byte widthScale;     // 0-255 → [scaleMin, scaleMax]
        public byte heightScale;    // 0-255 → [scaleMin, scaleMax]
        public byte rotation;       // 0-255 → 0-360° yaw rotation around tree's up axis
        public byte prototypeIndex; // Index into prototype registry
        public ushort heightOffset; // 0-65535 → terrain height (0 to maxHeight)

        public STPTMETreeInstance(byte spin, byte distance, byte widthScale, byte heightScale, byte rotation, byte prototypeIndex, ushort heightOffset)
        {
            this.spin = spin;
            this.distance = distance;
            this.widthScale = widthScale;
            this.heightScale = heightScale;
            this.rotation = rotation;
            this.prototypeIndex = prototypeIndex;
            this.heightOffset = heightOffset;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(spin);
            writer.Write(distance);
            writer.Write(widthScale);
            writer.Write(heightScale);
            writer.Write(rotation);
            writer.Write(prototypeIndex);
            writer.Write(heightOffset);
        }
    }

    /// <summary>
    /// Per-chunk tree index entry for O(1) lookup.
    /// </summary>
    public struct TreeIndexEntry
    {
        public uint startIndex;     // Start index into tree instance stream
        public ushort count;        // Number of trees in this chunk
        public ushort reserved;     // Future: flags, biome bits, etc.

        public void Write(BinaryWriter writer)
        {
            writer.Write(startIndex);
            writer.Write(count);
            writer.Write(reserved);
        }
    }

    /// <summary>
    /// Combined cell file header (64 bytes).
    /// </summary>
    public struct CellHeader
    {
        public ulong magic;             // "STPCELL1" = 0x314C4C4543505453
        public ushort formatVersion;
        public ushort headerSize;
        public uint flags;              // Bit 0: has trees, Bit 1: has splatmap, etc.
        public sbyte mapX;
        public sbyte mapY;
        public byte isTop;
        public byte reserved0;
        public ushort heightResX;
        public ushort heightResY;
        public ushort chunkCountPerAxis;
        public ushort totalChunks;
        public uint totalTreeCount;
        public uint heightOffset;
        public uint heightSize;
        public uint treeIndexOffset;
        public uint treeIndexSize;
        public uint treeDataOffset;
        public uint treeDataSize;
        // Remaining bytes to reach 64: 64 - 8 - 2 - 2 - 4 - 1 - 1 - 1 - 1 - 2 - 2 - 2 - 2 - 4 - 4 - 4 - 4 - 4 - 4 - 4 = 8 bytes reserved
        // Actually let me recalculate: 8+2+2+4+1+1+1+1+2+2+2+2+4+4+4+4+4+4+4 = 56, so 8 bytes reserved

        public const ulong MAGIC = 0x314C4C4543505453; // "STPCELL1" little-endian

        public void Write(BinaryWriter writer)
        {
            writer.Write(MAGIC);
            writer.Write(formatVersion);
            writer.Write(headerSize);
            writer.Write(flags);
            writer.Write(mapX);
            writer.Write(mapY);
            writer.Write(isTop);
            writer.Write(reserved0);
            writer.Write(heightResX);
            writer.Write(heightResY);
            writer.Write(chunkCountPerAxis);
            writer.Write(totalChunks);
            writer.Write(totalTreeCount);
            writer.Write(heightOffset);
            writer.Write(heightSize);
            writer.Write(treeIndexOffset);
            writer.Write(treeIndexSize);
            writer.Write(treeDataOffset);
            writer.Write(treeDataSize);
            // Write 8 bytes of reserved/padding
            writer.Write((ulong)0);
        }
    }

    /// <summary>
    /// Accumulator for building cell data before serialization.
    /// </summary>
    public class CellBuildBuffer
    {
        public Vector2SByte cell;
        public FaceId face;
        public bool isTop;
        public ushort heightResX;
        public ushort heightResY;
        public ushort[,] heights;
        public int numberOfChunks;
        public List<STPTMETreeInstance>[] treesPerChunk; // [chunkFlatIndex]
        public bool hasValidChunks;

        public CellBuildBuffer(Vector2SByte cell, FaceId face, int numberOfChunks)
        {
            this.cell = cell;
            this.face = face;
            this.isTop = face == FaceId.Up;
            this.numberOfChunks = numberOfChunks;
            treesPerChunk = new List<STPTMETreeInstance>[numberOfChunks * numberOfChunks];
            for (int i = 0; i < treesPerChunk.Length; i++)
                treesPerChunk[i] = new List<STPTMETreeInstance>();
        }

        public void AddTree(int chunkFlatIndex, STPTMETreeInstance tree)
        {
            if (chunkFlatIndex >= 0 && chunkFlatIndex < treesPerChunk.Length)
                treesPerChunk[chunkFlatIndex].Add(tree);
        }

        /// <summary>
        /// Deterministically shuffle each chunk's tree list using Fisher-Yates.
        /// Seed is based on cell and chunk coordinates for repeatability.
        /// </summary>
        public void ShuffleAllChunks()
        {
            for (int chunkIdx = 0; chunkIdx < treesPerChunk.Length; chunkIdx++)
            {
                var list = treesPerChunk[chunkIdx];
                if (list.Count <= 1) continue;

                int chunkX = chunkIdx % numberOfChunks;
                int chunkY = chunkIdx / numberOfChunks;
                int seed = (cell.x * 31337 + cell.y * 137) ^ (chunkX * 7919 + chunkY * 6271);
                seed ^= ((int)face + 1) * 0x11111111;
                
                System.Random rng = new System.Random(seed);
                
                // Fisher-Yates shuffle
                for (int i = list.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    var temp = list[i];
                    list[i] = list[j];
                    list[j] = temp;
                }
            }
        }

        public int GetTotalTreeCount()
        {
            int total = 0;
            foreach (var list in treesPerChunk)
                total += list.Count;
            return total;
        }
    }

    // ===== GROUP CELL FILE FORMAT =====
    // Groups multiple subcells (from one original pre-subdivision terrain) into a single file.
    // Reduces I/O overhead when heightmapSubdivisions is high (e.g. 13,824 files → 216).
    //
    // Layout:
    //   [GroupHeader: 64 bytes]
    //   [SubCellEntry × subCellCount: 32 bytes each]
    //   [SubCell 0 data: heights, treeIndex, treeData]
    //   [SubCell 1 data: ...]
    //   ...

    public const ulong GROUP_MAGIC = 0x3250524754505453; // "STPGRP02" little-endian
    public const ushort GROUP_FORMAT_VERSION = 1;
    public const int GROUP_HEADER_SIZE = 64;
    public const int SUBCELL_ENTRY_SIZE = 32;

    /// <summary>
    /// Data for a single subcell within a group, used during bake.
    /// </summary>
    public struct SubCellData
    {
        public CellBuildBuffer buffer;
        public bool[] validChunks;
        public byte dsSteps;
        public List<BlotchData> blotches; // null = no blotches
    }

#if UNITY_EDITOR

    // ===== QUANTIZATION =====

    /// <summary>
    /// Quantizes a Unity TreeInstance to our compact 7-byte format.
    /// </summary>
    public static STPTMETreeInstance QuantizeTree(
        Vector3 treeWorldPos,
        Vector3 chunkCenter,
        Vector3 tangentNorth,
        Vector3 tangentEast,
        float maxPolarDistance,
        float treeWidthScale,
        float treeHeightScale,
        float treeRotation,
        int prototypeIndex,
        float terrainHeight,
        float maxHeight,
        float scaleMin = 0.5f,
        float scaleMax = 2.0f)
    {
        // Project tree position onto chunk's tangent plane
        Vector3 offset = treeWorldPos - chunkCenter;
        float eastComponent = Vector3.Dot(offset, tangentEast);
        float northComponent = Vector3.Dot(offset, tangentNorth);

        // Convert to polar
        float radius = Mathf.Sqrt(eastComponent * eastComponent + northComponent * northComponent);
        float angle = Mathf.Atan2(eastComponent, northComponent); // Angle from north, clockwise
        if (angle < 0) angle += Mathf.PI * 2f;

        // Quantize
        byte spin = (byte)Mathf.Clamp(Mathf.RoundToInt(angle / (Mathf.PI * 2f) * 255f), 0, 255);
        byte distance = (byte)Mathf.Clamp(Mathf.RoundToInt(radius / maxPolarDistance * 255f), 0, 255);
        
        // Scale quantization: map [scaleMin, scaleMax] to [0, 255]
        float widthNorm = Mathf.InverseLerp(scaleMin, scaleMax, treeWidthScale);
        float heightNorm = Mathf.InverseLerp(scaleMin, scaleMax, treeHeightScale);
        byte widthScale = (byte)Mathf.Clamp(Mathf.RoundToInt(widthNorm * 255f), 0, 255);
        byte heightScale = (byte)Mathf.Clamp(Mathf.RoundToInt(heightNorm * 255f), 0, 255);

        // Rotation: Unity's rotation is in radians, convert to 0-255
        float rotNorm = treeRotation / (Mathf.PI * 2f);
        if (rotNorm < 0) rotNorm += 1f;
        byte rotation = (byte)Mathf.Clamp(Mathf.RoundToInt(rotNorm * 255f), 0, 255);

        byte protoIdx = (byte)Mathf.Clamp(prototypeIndex, 0, 255);
        
        // Height: normalize terrain height to 0-65535 (16-bit precision)
        ushort heightOffset = (ushort)Mathf.Clamp(Mathf.RoundToInt(terrainHeight / maxHeight * 65535f), 0, 65535);

        return new STPTMETreeInstance(spin, distance, widthScale, heightScale, rotation, protoIdx, heightOffset);
    }

    /// <summary>
    /// Computes chunk center on sphere and tangent vectors.
    /// Must match runtime decoding exactly.
    /// </summary>
    public static void ComputeChunkCenterAndTangents(
        Vector3 corner00, Vector3 corner10, Vector3 corner01, Vector3 corner11,
        Vector3 sphereCenter, float sphereRadius,
        out Vector3 chunkCenter, out Vector3 tangentNorth, out Vector3 tangentEast)
    {
        // Bilinear interpolation at center (u=0.5, v=0.5)
        Vector3 centerFlat = 0.25f * (corner00 + corner10 + corner01 + corner11);
        
        // Project onto sphere
        Vector3 dir = (centerFlat - sphereCenter).normalized;
        chunkCenter = sphereCenter + dir * sphereRadius;

        // Compute tangent plane basis
        // North: roughly towards +Y in world, but orthogonal to sphere normal
        Vector3 up = Vector3.up;
        tangentEast = Vector3.Cross(up, dir).normalized;
        if (tangentEast.sqrMagnitude < 0.001f)
        {
            // Handle pole case
            tangentEast = Vector3.Cross(Vector3.forward, dir).normalized;
        }
        tangentNorth = Vector3.Cross(dir, tangentEast).normalized;
    }

    /// <summary>
    /// Computes the maximum polar distance for a chunk (half diagonal).
    /// </summary>
    public static float ComputeMaxPolarDistance(
        Vector3 corner00, Vector3 corner10, Vector3 corner01, Vector3 corner11,
        Vector3 chunkCenter, Vector3 tangentNorth, Vector3 tangentEast)
    {
        float maxDist = 0f;
        Vector3[] corners = { corner00, corner10, corner01, corner11 };
        
        foreach (var corner in corners)
        {
            Vector3 offset = corner - chunkCenter;
            float east = Vector3.Dot(offset, tangentEast);
            float north = Vector3.Dot(offset, tangentNorth);
            float dist = Mathf.Sqrt(east * east + north * north);
            if (dist > maxDist) maxDist = dist;
        }
        
        return maxDist;
    }

    /// <summary>
    /// Extracts and quantizes all trees from a terrain into the appropriate cell buffers.
    ///
    /// All cell/chunk indices and ProjectFacePlanePoint inputs operate in face-plane space.
    /// The caller passes in:
    ///  - <paramref name="orientation"/>: rotation that converts the source terrain's
    ///    container/world XZ axes into the face's plane (axisA, axisB) axes.
    ///  - <paramref name="planeTerrainOriginX"/>/<paramref name="planeTerrainOriginZ"/>:
    ///    plane-space coordinates of the terrain corner that maps to plane (0, 0).
    ///  - <paramref name="cellKeyBaseZ"/>: the sbyte cell-key
    ///    base for this terrain (= settings.minX + planeTerrainGridY * subdivisionsPowerOf2).
    /// </summary>
    public static void ExtractTreesFromTerrain(
        Terrain terrain,
        FaceId face,
        FaceContainerOrientation orientation,
        Dictionary<Vector2SByte, CellBuildBuffer> cellBuffers,
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
        sbyte cellKeyBaseZ)
    {
        TerrainData td = terrain.terrainData;
        float terrainSize = td.size.x;
        TreeInstance[] trees = td.treeInstances;

        if (trees == null || trees.Length == 0) return;

        float cellSize = terrainSize / subdivisionsPowerOf2;
        int chunksPerCell = numberOfChunks; // chunks per axis within a cell

        foreach (var tree in trees)
        {
            // Re-orient the tree's normalized container-space position into normalized plane-space.
            // After this point everything is in plane coordinates.
            FaceContainerOrientations.NormalizedWorldToPlane(orientation,
                tree.position.x, tree.position.z,
                out float npx, out float npz);

            // Plane-space tree position relative to the terrain's plane origin, then absolute on the face plane.
            float treePlaneRelX = npx * terrainSize;
            float treePlaneRelZ = npz * terrainSize;
            float treePlaneFaceX = planeTerrainOriginX + treePlaneRelX;
            float treePlaneFaceZ = planeTerrainOriginZ + treePlaneRelZ;

            // Cell coordinate (plane sub-cell within this terrain).
            int cellLocalX = Mathf.Clamp(Mathf.FloorToInt(treePlaneRelX / cellSize), 0, subdivisionsPowerOf2 - 1);
            int cellLocalZ = Mathf.Clamp(Mathf.FloorToInt(treePlaneRelZ / cellSize), 0, subdivisionsPowerOf2 - 1);

            Vector2SByte cellKey = new Vector2SByte(
                (sbyte)(cellKeyBaseX + cellLocalX),
                (sbyte)(cellKeyBaseZ + cellLocalZ)
            );

            if (!cellBuffers.TryGetValue(cellKey, out var cellBuffer))
            {
                continue; // Cell not valid (no valid chunks)
            }

            // Chunk within the cell (plane space).
            float cellPlaneStartX = planeTerrainOriginX + cellLocalX * cellSize;
            float cellPlaneStartZ = planeTerrainOriginZ + cellLocalZ * cellSize;
            float relToCellX = treePlaneFaceX - cellPlaneStartX;
            float relToCellZ = treePlaneFaceZ - cellPlaneStartZ;

            int chunkX = Mathf.Clamp(Mathf.FloorToInt(relToCellX * chunksPerCell / cellSize), 0, chunksPerCell - 1);
            int chunkZ = Mathf.Clamp(Mathf.FloorToInt(relToCellZ * chunksPerCell / cellSize), 0, chunksPerCell - 1);
            int chunkFlatIndex = chunkZ * chunksPerCell + chunkX;

            float chunkSize = cellSize / chunksPerCell;
            float chunkPlaneStartX = cellPlaneStartX + chunkX * chunkSize;
            float chunkPlaneStartZ = cellPlaneStartZ + chunkZ * chunkSize;

            // Chunk corners on sphere from plane coords (must match runtime's ProjectFacePlanePoint).
            Vector3[] corners = new Vector3[4];
            float[,] offsets = { {0,0}, {1,0}, {0,1}, {1,1} };
            for (int c = 0; c < 4; c++)
            {
                float cx = chunkPlaneStartX + offsets[c,0] * chunkSize;
                float cz = chunkPlaneStartZ + offsets[c,1] * chunkSize;
                corners[c] = FaceIdUtility.ProjectFacePlanePoint(face, cx, cz, faceWorldSize, sphereCenter, sphereRadius);
            }

            ComputeChunkCenterAndTangents(
                corners[0], corners[1], corners[2], corners[3],
                sphereCenter, sphereRadius,
                out Vector3 chunkCenter, out Vector3 tangentNorth, out Vector3 tangentEast);

            float maxPolarDist = ComputeMaxPolarDistance(
                corners[0], corners[1], corners[2], corners[3],
                chunkCenter, tangentNorth, tangentEast);

            Vector3 treeOnSphere = FaceIdUtility.ProjectFacePlanePoint(
                face, treePlaneFaceX, treePlaneFaceZ, faceWorldSize, sphereCenter, sphereRadius);

            float terrainHeight = tree.position.y * td.size.y;

            STPTMETreeInstance quantized = QuantizeTree(
                treeOnSphere,
                chunkCenter,
                tangentNorth,
                tangentEast,
                maxPolarDist,
                tree.widthScale,
                tree.heightScale,
                tree.rotation,
                tree.prototypeIndex,
                terrainHeight,
                maxHeight
            );

            cellBuffer.AddTree(chunkFlatIndex, quantized);
        }
    }

    /// <summary>
    /// Writes a combined cell file with height and tree data.
    /// </summary>
    public static void WriteCellFile(
        string outputPath,
        CellBuildBuffer buffer,
        bool[] validChunks)
    {
        string tempPath = outputPath + ".tmp";
        
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs))
            {
                // Shuffle trees before writing
                buffer.ShuffleAllChunks();
                
                int totalTrees = buffer.GetTotalTreeCount();
                int totalChunks = buffer.numberOfChunks * buffer.numberOfChunks;
                
                // Calculate offsets
                uint heightOffset = (uint)CELL_HEADER_SIZE;
                uint heightSize = (uint)(buffer.heightResX * buffer.heightResY * 2); // ushort = 2 bytes
                
                uint treeIndexOffset = heightOffset + heightSize;
                uint treeIndexSize = (uint)(totalChunks * TREE_INDEX_ENTRY_SIZE);
                
                uint treeDataOffset = treeIndexOffset + treeIndexSize;
                uint treeDataSize = (uint)(totalTrees * TREE_INSTANCE_SIZE);

                // Build and write header
                CellHeader header = new CellHeader
                {
                    formatVersion = CELL_FORMAT_VERSION,
                    headerSize = CELL_HEADER_SIZE,
                    flags = totalTrees > 0 ? 1u : 0u, // Bit 0 = has trees
                    mapX = buffer.cell.x,
                    mapY = buffer.cell.y,
                    isTop = (byte)buffer.face,
                    reserved0 = 0,
                    heightResX = buffer.heightResX,
                    heightResY = buffer.heightResY,
                    chunkCountPerAxis = (ushort)buffer.numberOfChunks,
                    totalChunks = (ushort)totalChunks,
                    totalTreeCount = (uint)totalTrees,
                    heightOffset = heightOffset,
                    heightSize = heightSize,
                    treeIndexOffset = treeIndexOffset,
                    treeIndexSize = treeIndexSize,
                    treeDataOffset = treeDataOffset,
                    treeDataSize = treeDataSize
                };
                
                header.Write(writer);

                // Write height section
                for (int z = 0; z < buffer.heightResY; z++)
                {
                    for (int x = 0; x < buffer.heightResX; x++)
                    {
                        writer.Write(buffer.heights[z, x]);
                    }
                }

                // Write tree index section
                uint currentStartIndex = 0;
                for (int chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++)
                {
                    var entry = new TreeIndexEntry
                    {
                        startIndex = currentStartIndex,
                        count = (ushort)buffer.treesPerChunk[chunkIdx].Count,
                        reserved = 0
                    };
                    entry.Write(writer);
                    currentStartIndex += (uint)buffer.treesPerChunk[chunkIdx].Count;
                }

                // Write tree data section (concatenated, pre-shuffled)
                for (int chunkIdx = 0; chunkIdx < totalChunks; chunkIdx++)
                {
                    foreach (var tree in buffer.treesPerChunk[chunkIdx])
                    {
                        tree.Write(writer);
                    }
                }
            }

            // Atomic replace
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes a group cell file containing multiple subcells from one original terrain.
    /// </summary>
    public static void WriteGroupCellFile(
        string outputPath,
        SubCellData[] subcells,
        byte face,
        byte origTerrainX,
        byte origTerrainY,
        byte subdPow2,
        ushort chunksPerCellAxis)
    {
        string tempPath = outputPath + ".tmp";

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs))
            {
                ushort subCellCount = (ushort)subcells.Length;
                int totalChunks = chunksPerCellAxis * chunksPerCellAxis;

                // Shuffle trees in all subcells before writing.
                foreach (var sc in subcells)
                    sc.buffer.ShuffleAllChunks();

                // --- Compute data layout ---
                uint dataStart = (uint)(GROUP_HEADER_SIZE + subCellCount * SUBCELL_ENTRY_SIZE);
                uint cursor = dataStart;

                uint[] heightOffsets = new uint[subCellCount];
                uint[] heightSizes = new uint[subCellCount];
                uint[] treeIndexOffsets = new uint[subCellCount];
                uint[] treeDataOffsets = new uint[subCellCount];
                uint[] treeCounts = new uint[subCellCount];

                for (int i = 0; i < subCellCount; i++)
                {
                    var buf = subcells[i].buffer;
                    uint hSize = (uint)(buf.heightResX * buf.heightResY * 2);
                    uint treeCount = (uint)buf.GetTotalTreeCount();
                    uint tiSize = (uint)(totalChunks * TREE_INDEX_ENTRY_SIZE);
                    uint tdSize = (uint)(treeCount * TREE_INSTANCE_SIZE);

                    heightOffsets[i] = cursor;
                    heightSizes[i] = hSize;
                    cursor += hSize;

                    treeIndexOffsets[i] = cursor;
                    cursor += tiSize;

                    treeDataOffsets[i] = cursor;
                    cursor += tdSize;

                    // Blotch data: count (int) + 16 bytes per BlotchData.
                    int blotchCount = subcells[i].blotches?.Count ?? 0;
                    cursor += (uint)(4 + blotchCount * 16);

                    treeCounts[i] = treeCount;
                }

                // --- Write group header (64 bytes) ---
                writer.Write(GROUP_MAGIC);                      // 8
                writer.Write(GROUP_FORMAT_VERSION);              // 2
                writer.Write((ushort)GROUP_HEADER_SIZE);         // 2
                writer.Write(0u);                                // flags: 4
                writer.Write(face);                              // 1
                writer.Write(origTerrainX);                      // 1
                writer.Write(origTerrainY);                      // 1
                writer.Write(subdPow2);                          // 1
                writer.Write(subCellCount);                      // 2
                writer.Write(chunksPerCellAxis);                 // 2
                // 24 bytes written, pad to 64
                for (int p = 0; p < 5; p++) writer.Write(0uL);  // 40

                // --- Write subcell index (32 bytes each) ---
                for (int i = 0; i < subCellCount; i++)
                {
                    var buf = subcells[i].buffer;
                    writer.Write(buf.cell.x);                    // 1
                    writer.Write(buf.cell.y);                    // 1
                    writer.Write(subcells[i].dsSteps);           // 1
                    writer.Write((byte)0);                       // reserved: 1
                    writer.Write(buf.heightResX);                // 2
                    writer.Write(buf.heightResY);                // 2
                    writer.Write(treeCounts[i]);                  // 4
                    writer.Write(heightOffsets[i]);               // 4
                    writer.Write(heightSizes[i]);                 // 4
                    writer.Write(treeIndexOffsets[i]);            // 4
                    writer.Write(treeDataOffsets[i]);             // 4
                    writer.Write(0u);                             // reserved2: 4
                    // 32 bytes total
                }

                // --- Write data sections per subcell ---
                for (int i = 0; i < subCellCount; i++)
                {
                    var buf = subcells[i].buffer;

                    // Heights
                    for (int z = 0; z < buf.heightResY; z++)
                        for (int x = 0; x < buf.heightResX; x++)
                            writer.Write(buf.heights[z, x]);

                    // Tree index
                    uint currentStartIndex = 0;
                    for (int ci = 0; ci < totalChunks; ci++)
                    {
                        var entry = new TreeIndexEntry
                        {
                            startIndex = currentStartIndex,
                            count = (ushort)buf.treesPerChunk[ci].Count,
                            reserved = 0
                        };
                        entry.Write(writer);
                        currentStartIndex += (uint)buf.treesPerChunk[ci].Count;
                    }

                    // Tree data
                    for (int ci = 0; ci < totalChunks; ci++)
                        foreach (var tree in buf.treesPerChunk[ci])
                            tree.Write(writer);

                    // Blotch data (appended after tree data, preceded by count)
                    var blotches = subcells[i].blotches;
                    int blotchCount = blotches?.Count ?? 0;
                    writer.Write(blotchCount);
                    if (blotchCount > 0)
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

            // Atomic replace
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Validates a written cell file.
    /// </summary>
    public static bool ValidateCellFile(string path, out string error)
    {
        error = null;
        
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                // Check magic
                ulong magic = reader.ReadUInt64();
                if (magic != CellHeader.MAGIC)
                {
                    error = $"Invalid magic: expected 0x{CellHeader.MAGIC:X}, got 0x{magic:X}";
                    return false;
                }

                ushort version = reader.ReadUInt16();
                if (version != CELL_FORMAT_VERSION)
                {
                    error = $"Unsupported version: {version}";
                    return false;
                }

                ushort headerSize = reader.ReadUInt16();
                reader.ReadUInt32(); // flags
                reader.ReadSByte(); // mapX
                reader.ReadSByte(); // mapY
                reader.ReadByte(); // isTop
                reader.ReadByte(); // reserved0
                ushort heightResX = reader.ReadUInt16();
                ushort heightResY = reader.ReadUInt16();
                reader.ReadUInt16(); // chunkCountPerAxis
                ushort totalChunks = reader.ReadUInt16();
                uint totalTreeCount = reader.ReadUInt32();
                uint heightOffset = reader.ReadUInt32();
                uint heightSize = reader.ReadUInt32();
                uint treeIndexOffset = reader.ReadUInt32();
                uint treeIndexSize = reader.ReadUInt32();
                uint treeDataOffset = reader.ReadUInt32();
                uint treeDataSize = reader.ReadUInt32();

                // Validate sizes
                uint expectedHeightSize = (uint)(heightResX * heightResY * 2);
                if (heightSize != expectedHeightSize)
                {
                    error = $"Height size mismatch: expected {expectedHeightSize}, got {heightSize}";
                    return false;
                }

                uint expectedTreeIndexSize = (uint)(totalChunks * TREE_INDEX_ENTRY_SIZE);
                if (treeIndexSize != expectedTreeIndexSize)
                {
                    error = $"Tree index size mismatch: expected {expectedTreeIndexSize}, got {treeIndexSize}";
                    return false;
                }

                uint expectedTreeDataSize = (uint)(totalTreeCount * TREE_INSTANCE_SIZE);
                if (treeDataSize != expectedTreeDataSize)
                {
                    error = $"Tree data size mismatch: expected {expectedTreeDataSize}, got {treeDataSize}";
                    return false;
                }

                // Validate file length
                long expectedLength = treeDataOffset + treeDataSize;
                if (fs.Length < expectedLength)
                {
                    error = $"File truncated: expected at least {expectedLength} bytes, got {fs.Length}";
                    return false;
                }

                return true;
            }
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ===== COLLIDER STATS =====

    /// <summary>File written to StreamingAssets/MapAssets/TreeColliderStats.bytes at bake time.
    /// Consumed at runtime by TreeColliderManager to size the capsule collider pool.</summary>
    public const uint COLLIDER_STATS_MAGIC = 0x54435354; // "TSCT"
    public const ushort COLLIDER_STATS_VERSION = 1;

    /// <summary>
    /// Writes per-prototype tree density stats used to size the runtime collider pool.
    /// For each prototype, records: total tree count across all valid chunks,
    /// and the derived averageTreesPer9Chunks = (total / validChunks) * 9.
    /// The pool manager multiplies averageTreesPer9Chunks by a headroom factor
    /// to obtain its initial target capacity.
    /// </summary>
    /// <param name="totalTreesPerPrototype">Summed tree count per prototypeIndex across all cells and hemispheres.</param>
    /// <param name="totalValidChunks">Total number of valid (on-sphere) chunks across all cells / hemispheres.</param>
    /// <param name="outputPath">Absolute path to write the .bytes file.</param>
    public static void WriteColliderStats(int[] totalTreesPerPrototype, int totalValidChunks, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        using var writer = new BinaryWriter(File.Open(outputPath, FileMode.Create));

        writer.Write(COLLIDER_STATS_MAGIC);          // uint  4
        writer.Write(COLLIDER_STATS_VERSION);        // ushort 2
        writer.Write((ushort)totalTreesPerPrototype.Length); // ushort 2
        writer.Write(totalValidChunks);              // int   4

        for (int i = 0; i < totalTreesPerPrototype.Length; i++)
        {
            int total = totalTreesPerPrototype[i];
            float avg = totalValidChunks > 0 ? (total / (float)totalValidChunks) * 9f : 0f;
            writer.Write(total); // int   4
            writer.Write(avg);   // float 4
        }
    }

#endif
}
