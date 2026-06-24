using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Baked tree-to-UV mapping for fast canopy overlay painting.
///
/// Stores 2 bytes (quantized u, v in [0,255]) per tree, indexed by
/// (chunkStorageSlot, localTreeIndex). Eliminates all TryProjectPointToChunkUV calls
/// at runtime — replaces a 406ms-per-generation mesh-projection loop with a flat
/// array lookup costing ~2ns per tree.
///
/// Layout of the binary file at StreamingAssets/MapAssets/CanopyUVCache.bytes:
///   [0..7]   magic:      "SCPUVC1\0" (8 bytes, little-endian ulong)
///   [8..9]   version:    ushort = 1
///   [10..13] totalSlots: int  (= total chunk storage slots)
///   [14..17] totalTrees: int  (= sum of trees across all chunks)
///   [18..19] reserved:   ushort = 0
///   [20 .. 20+totalSlots*4-1]
///            offsets:    int[totalSlots]  — byte offset into uvData, or -1 (no trees)
///   [20+totalSlots*4 ..]
///            uvData:     byte[totalTrees*2] — (u,v) pairs packed, one per tree
///
/// Storage: 1M trees × 2 bytes = 2 MB of UV data.
/// </summary>
public class CanopyUVCache
{
    public const string ASSET_RELATIVE_PATH = "MapAssets/CanopyUVCache.bytes";

    private const ulong  MAGIC          = 0x3156435055504353UL; // "SCPUVC1\0" LE
    private const ushort FORMAT_VERSION = 1;

    // Indexed by FaceIdUtility.GetStorageIndex(globalIdx, face).
    // Value = byte offset into uvData where this chunk's pairs begin, or -1.
    private int[]  chunkOffsets;
    private byte[] uvData;

    public bool IsLoaded => chunkOffsets != null;

    // ── Runtime lookup ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the UV for tree [localTreeIndex] of chunk [chunkSlot] in that chunk's own
    /// [0,1]×[0,1] UV space. For trees from a neighbor chunk at offset (nDx, nDy) add
    /// new Vector2(nDx, nDy) to the result to convert to the current chunk's UV space.
    /// Returns false only if the cache wasn't baked or the index is out of range.
    /// </summary>
    public bool TryGetUV(int chunkSlot, int localTreeIndex, out Vector2 uv)
    {
        uv = default;
        if (chunkOffsets == null || (uint)chunkSlot >= (uint)chunkOffsets.Length)
            return false;

        int baseOffset = chunkOffsets[chunkSlot];
        if (baseOffset < 0)
            return false;

        int idx = baseOffset + localTreeIndex * 2;
        if (idx + 1 >= uvData.Length)
            return false;

        uv.x = uvData[idx]     * (1f / 255f);
        uv.y = uvData[idx + 1] * (1f / 255f);
        return true;
    }

    // ── I/O ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the cache from StreamingAssets. Returns null if the file is absent or corrupt.
    /// Logs a warning if the slot count doesn't match the current world (stale bake).
    /// </summary>
    public static CanopyUVCache LoadFromStreamingAssets(int expectedSlots)
    {
        string path = Path.Combine(Application.streamingAssetsPath, ASSET_RELATIVE_PATH);
        if (!File.Exists(path))
            return null;

        try
        {
            using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            ulong magic = reader.ReadUInt64();
            if (magic != MAGIC)
            {
                Debug.LogError("[CanopyUVCache] Bad magic — file corrupt or wrong format.");
                return null;
            }

            ushort version = reader.ReadUInt16();
            if (version != FORMAT_VERSION)
                Debug.LogWarning($"[CanopyUVCache] Version mismatch: file={version}, " +
                                 $"expected={FORMAT_VERSION}. Proceeding anyway.");

            int totalSlots = reader.ReadInt32();
            int totalTrees = reader.ReadInt32();
            reader.ReadUInt16(); // reserved

            if (totalSlots != expectedSlots)
                Debug.LogWarning($"[CanopyUVCache] Slot count mismatch: file={totalSlots}, " +
                                 $"expected={expectedSlots}. Cache may be stale — rebake.");

            var cache       = new CanopyUVCache();
            cache.chunkOffsets = new int[totalSlots];
            for (int i = 0; i < totalSlots; i++)
                cache.chunkOffsets[i] = reader.ReadInt32();

            cache.uvData = reader.ReadBytes(totalTrees * 2);

            Debug.Log($"[CanopyUVCache] Loaded: {totalSlots} slots, {totalTrees:N0} trees, " +
                      $"{cache.uvData.Length / 1024} KB.");
            return cache;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CanopyUVCache] Load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes cache data to the given absolute path. Called by the bake coroutine.
    /// </summary>
    public static void Save(string path, int[] offsets, byte[] uvData)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs     = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        writer.Write(MAGIC);
        writer.Write(FORMAT_VERSION);
        writer.Write(offsets.Length);     // totalSlots
        writer.Write(uvData.Length / 2);  // totalTrees
        writer.Write((ushort)0);          // reserved

        foreach (int o in offsets)
            writer.Write(o);

        writer.Write(uvData);
    }
}
