using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CustomTypes;

/// <summary>
/// Runtime reader for the blotch data section inside group cell files.
///
/// Unlike CellReader (per-cell on-demand loading), this reader loads ALL
/// blotch data at once during scene initialization. The resulting array
/// is uploaded to the GPU as the permanent GlobalBlotchBuffer.
///
/// Group cell file layout (from TreeBaker.WriteGroupCellFile):
///   [GroupHeader 64 bytes]
///   [SubCellEntry × subCellCount: 32 bytes each]
///   For each subcell:
///     [Height data: ushort[heightResY, heightResX]]
///     [Tree index: TreeIndexEntry[totalChunks]]
///     [Tree data: STPTMETreeInstance[totalTreeCount]]
///     [Blotch count: int]                          ← NEW
///     [Blotch data: BlotchData × blotchCount]      ← NEW
/// </summary>
public static class CellBlotchReader
{
    // Must match TreeBaker group file constants
    private const ulong GROUP_MAGIC = 0x3250524754505453; // "STPGRP02"
    private const ushort GROUP_FORMAT_VERSION = 1;
    private const int GROUP_HEADER_SIZE = 64;
    private const int SUBCELL_ENTRY_SIZE = 32;
    private const int TREE_INSTANCE_SIZE = 8;
    private const int TREE_INDEX_ENTRY_SIZE = 8;
    private const int BLOTCH_BYTE_SIZE = 16; // BlotchData is 16 bytes

    /// <summary>
    /// Loads all BlotchData from every group cell file across every face.
    /// Returns a single flat array suitable for uploading to the GPU.
    /// </summary>
    /// <param name="cellsFolder">
    /// Path to StreamingAssets/MapAssets/Cells/
    /// </param>
    public static BlotchData[] LoadAllBlotches(string cellsFolder)
    {
        if (!Directory.Exists(cellsFolder))
        {
            Debug.LogWarning($"[CellBlotchReader] Cells folder not found: {cellsFolder}");
            return Array.Empty<BlotchData>();
        }

        string[] files = Directory.GetFiles(cellsFolder, "CellGroup_*.bytes");
        if (files.Length == 0)
        {
            Debug.LogWarning("[CellBlotchReader] No group cell files found.");
            return Array.Empty<BlotchData>();
        }

        var allBlotches = new List<BlotchData>(1024 * 1024); // start with ~1M capacity

        foreach (string filePath in files)
        {
            try
            {
                LoadBlotchesFromFile(filePath, allBlotches);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CellBlotchReader] Failed to read {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        return allBlotches.ToArray();
    }

    private static void LoadBlotchesFromFile(string filePath, List<BlotchData> result)
    {
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        using (var reader = new BinaryReader(fs))
        {
            // --- Read group header (64 bytes) ---
            ulong magic = reader.ReadUInt64();
            if (magic != GROUP_MAGIC) return;

            ushort version = reader.ReadUInt16();
            if (version != GROUP_FORMAT_VERSION) return;

            ushort headerSize = reader.ReadUInt16(); // should be GROUP_HEADER_SIZE
            uint flags = reader.ReadUInt32();         // reserved
            byte face = reader.ReadByte();            // FaceId
            byte origTerrainX = reader.ReadByte();
            byte origTerrainY = reader.ReadByte();
            byte subdPow2 = reader.ReadByte();        // subdivisionsPowerOf2
            ushort subCellCount = reader.ReadUInt16();
            ushort chunksPerCellAxis = reader.ReadUInt16();

            // Skip remaining header padding (64 - 24 = 40 bytes)
            reader.BaseStream.Seek(GROUP_HEADER_SIZE - 24, SeekOrigin.Current);

            // --- Read subcell index entries (32 bytes each) ---
            // Store offsets so we can skip to data sections.
            var subcellOffsets = new (uint heightOff, uint heightSize, uint treeIndexOff, uint treeDataOff, uint treeCount)[subCellCount];

            for (int si = 0; si < subCellCount; si++)
            {
                sbyte cellX = reader.ReadSByte();
                sbyte cellY = reader.ReadSByte();
                byte dsSteps = reader.ReadByte();
                byte reserved = reader.ReadByte();
                ushort heightResX = reader.ReadUInt16();
                ushort heightResY = reader.ReadUInt16();
                uint treeCount = reader.ReadUInt32();
                uint heightOffset = reader.ReadUInt32();
                uint heightSize = reader.ReadUInt32();
                uint treeIndexOffset = reader.ReadUInt32();
                uint treeDataOffset = reader.ReadUInt32();
                uint reserved2 = reader.ReadUInt32();

                // Calculate offset of the blotch section:
                // height data ends at heightOffset + heightSize
                // tree index ends at treeIndexOffset + treeIndexSize (chunksPerCellAxis² × 8)
                uint treeIndexSize = (uint)(chunksPerCellAxis * chunksPerCellAxis * TREE_INDEX_ENTRY_SIZE);
                // tree data ends at treeDataOffset + treeDataSize (treeCount × 8)
                uint treeDataSize = treeCount * TREE_INSTANCE_SIZE;
                // blotch count starts right after tree data
                uint blotchCountOffset = treeDataOffset + treeDataSize;

                subcellOffsets[si] = (heightOffset, heightSize, treeIndexOffset, blotchCountOffset, treeCount);
            }

            // --- Read blotch sections ---
            for (int si = 0; si < subCellCount; si++)
            {
                var off = subcellOffsets[si];

                // Seek to blotch count position (immediately after tree data).
                // If the seek position is at or past the end of the stream, the file
                // was written by an older version of TreeBaker that doesn't include
                // the blotch section — skip silently.
                if (off.treeDataOff >= (ulong)reader.BaseStream.Length)
                    continue;
                reader.BaseStream.Seek((long)off.treeDataOff, SeekOrigin.Begin);

                // Guard: need at least 4 bytes for the blotch count.
                if (reader.BaseStream.Length - reader.BaseStream.Position < 4)
                    continue;

                int blotchCount = reader.ReadInt32();
                if (blotchCount <= 0) continue;

                for (int bi = 0; bi < blotchCount; bi++)
                {
                    int chunkPacked = reader.ReadInt32();
                    uint packedMeta = reader.ReadUInt32();
                    uint seedAndDensity = reader.ReadUInt32();
                    uint packedPos = reader.ReadUInt32();

                    // Reconstruct BlotchData from the raw fields we just read.
                    // Use the 16-byte struct layout directly — BlotchData has [StructLayout(Pack=1)].
                    // We read the 4 uints and build the struct.
                    BlotchData bd = new BlotchData(
                        chunkPacked: chunkPacked,
                        face: (FaceId)(packedMeta & 0xFF),
                        prototypeIndex: (byte)((packedMeta >> 8) & 0xFF),
                        conflictCategory: (byte)((packedMeta >> 16) & 0xFF),
                        seed: seedAndDensity & 0xFFFF,
                        densityPerSqM: ((seedAndDensity >> 16) & 0xFF) * 0.5f,
                        radiusMeters: ((seedAndDensity >> 24) & 0xFF) * 0.25f,
                        localXMeters: (packedPos & 0xFFFF) / 65535f * 75f,
                        localZMeters: ((packedPos >> 16) & 0xFFFF) / 65535f * 75f,
                        chunkSizeMeters: 75f,
                        cullLODOverride: (packedMeta & (1u << 24)) != 0,
                        instanceAlways: (packedMeta & (1u << 25)) != 0
                    );
                    result.Add(bd);
                }
            }
        }
    }
}