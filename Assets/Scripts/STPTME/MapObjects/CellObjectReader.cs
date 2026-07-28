using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CustomTypes;

/// <summary>
/// Runtime reader for CellObjectGroup binary files written by <see cref="MapObjectBaker"/>.
/// Mirrors <see cref="CellReader"/> in structure: synchronous load, dictionary cache,
/// zero-copy per-chunk query via <see cref="GetObjectsForChunk"/>.
///
/// Also provides <see cref="LoadAllObjects"/>, a bulk scan of every group file — used once,
/// at scene load, by MapContentOrchestrator to find every object whose prototype should be
/// GPU-instanced. This is a genuinely different traversal from the per-chunk lazy path
/// (every subcell, not just one target), so it shares only the header/subcell-entry parsing.
/// </summary>
public class CellObjectReader
{
    // ── File constants (must match MapObjectBaker) ───────────────────────────
    private const ulong OBJ_MAGIC       = 0x005031_4A424F505453UL;
    private const int   OBJ_HEADER_SIZE = 64;
    private const int   SUBCELL_ENTRY_SIZE = 32;
    private const int   CHUNK_INDEX_SIZE   = 8;
    private const int   OBJECT_SIZE        = 46; // matches MapObjectBaker's write layout exactly; not used for offset math here (fields read sequentially), kept for documentation parity.

    // ── Per-object runtime representation ───────────────────────────────────

    public struct CellObjectInstance
    {
        public byte      prototypeIndex;
        public Vector3   position;
        public Quaternion rotation;
        public Vector3   scale;
        public byte      lodLevel;
    }

    private struct SubCellEntry
    {
        public sbyte mapX, mapY;
        public uint objCount, idxOff, dataOff;
    }

    // ── Per-cell cached data ─────────────────────────────────────────────────

    private struct CellObjectData
    {
        /// <summary>Flat array of all instances in this cell, sorted by chunk.</summary>
        public CellObjectInstance[] objects;
        /// <summary>Per-chunk (startIndex, count). Length = chunksPerAxis².</summary>
        public (uint start, ushort count)[] chunkIndex;
    }

    // ── State ────────────────────────────────────────────────────────────────

    private readonly Dictionary<MapFaceKey, CellObjectData> _cache
        = new Dictionary<MapFaceKey, CellObjectData>();

    private int   _subdivPow2;
    private sbyte _minX;

    // ── Init ─────────────────────────────────────────────────────────────────

    public void Init(int subdivPow2, sbyte minX)
    {
        _subdivPow2 = subdivPow2;
        _minX       = minX;
    }

    // ── Public query (per-chunk, lazy, used by ChunkObjectLoader streaming) ──

    /// <summary>
    /// Returns all objects assigned to the chunk encoded in <paramref name="packed"/>,
    /// on the given <paramref name="face"/>, filtered to the given <paramref name="lodLevel"/>.
    /// Loads the file synchronously if not yet cached. Returns an empty span on miss.
    /// </summary>
    public ArraySegment<CellObjectInstance> GetObjectsForChunk(
        int packed, FaceId face, int numberOfChunks, byte lodLevel)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed,
            out sbyte hmX, out sbyte hmY, out sbyte chunkX, out sbyte chunkY);

        var mapKey = new MapFaceKey(new Vector2SByte(hmX, hmY), face);

        if (!_cache.TryGetValue(mapKey, out CellObjectData data))
        {
            data = LoadCell(hmX, hmY, face);
            _cache[mapKey] = data;
        }

        if (data.objects == null || data.chunkIndex == null)
            return default;

        int chunkFlat = chunkY * numberOfChunks + chunkX;
        if (chunkFlat >= data.chunkIndex.Length)
            return default;

        var (start, count) = data.chunkIndex[chunkFlat];
        if (count == 0) return default;

        if (_filterBuffer == null || _filterBuffer.Length < count)
            _filterBuffer = new CellObjectInstance[count * 2];

        int n = 0;
        for (int i = (int)start; i < (int)start + count; i++)
            if (data.objects[i].lodLevel == lodLevel)
                _filterBuffer[n++] = data.objects[i];

        return new ArraySegment<CellObjectInstance>(_filterBuffer, 0, n);
    }

    private CellObjectInstance[] _filterBuffer;

    /// <summary>Evicts the cached data for a cell (mirrors CellReader.Evict).</summary>
    public void Evict(Vector2SByte map, FaceId face)
        => _cache.Remove(new MapFaceKey(map, face));

    // ── Public bulk load (used once, at scene load, by MapContentOrchestrator) ──

    /// <summary>
    /// Scans every CellObjectGroup_*.bytes file in <paramref name="folder"/> and returns
    /// EVERY object across every subcell — not just one target, unlike the per-chunk path.
    /// Each result item carries its own resolved (chunkPacked, face) so the caller doesn't
    /// need to re-derive chunk addressing.
    /// </summary>
    public static List<(int chunkPacked, FaceId face, CellObjectInstance instance)> LoadAllObjects(string folder)
    {
        var result = new List<(int, FaceId, CellObjectInstance)>();

        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[CellObjectReader] CellObjects folder not found: {folder}");
            return result;
        }

        string[] files = Directory.GetFiles(folder, "CellObjectGroup_*.bytes");
        foreach (string path in files)
        {
            try
            {
                LoadAllObjectsFromFile(path, result);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CellObjectReader] Bulk parse failed for '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        return result;
    }

    private static void LoadAllObjectsFromFile(string path, List<(int, FaceId, CellObjectInstance)> result)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        if (!ReadHeader(br, path, out byte face, out ushort subCellCount, out ushort chunksPerAxis))
            return;

        SubCellEntry[] entries = ReadSubCellEntries(br, subCellCount);
        int totalChunks = chunksPerAxis * chunksPerAxis;

        for (int si = 0; si < subCellCount; si++)
        {
            var e = entries[si];
            if (e.objCount == 0) continue;

            var chunkIndex = ReadChunkIndex(fs, br, e.idxOff, totalChunks);
            var objects = ReadObjects(fs, br, e.dataOff, e.objCount);

            for (int c = 0; c < totalChunks; c++)
            {
                var (start, count) = chunkIndex[c];
                if (count == 0) continue;

                sbyte chunkX = (sbyte)(c % chunksPerAxis);
                sbyte chunkY = (sbyte)(c / chunksPerAxis);
                int packed = STPTMEUtils.WriteFourSBytesInInt(e.mapX, e.mapY, chunkX, chunkY);

                for (int i = (int)start; i < (int)start + count; i++)
                    result.Add((packed, (FaceId)face, objects[i]));
            }
        }
    }

    // ── File loading (per-chunk lazy path) ──────────────────────────────────

    private CellObjectData LoadCell(sbyte hmX, sbyte hmY, FaceId face)
    {
        int tgX    = (hmX - _minX) / _subdivPow2;
        int tgY    = (hmY - _minX) / _subdivPow2;
        string prefix = FaceIdUtility.GetFilePrefix(face);
        string path   = Path.Combine(
            Application.streamingAssetsPath,
            $"MapAssets/CellObjects/CellObjectGroup_{prefix}_{tgX}_{tgY}.bytes");

        if (!File.Exists(path))
            return default;

        try
        {
            return ParseGroupFile(path, hmX, hmY);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CellObjectReader] Failed to parse '{path}': {ex.Message}");
            return default;
        }
    }

    private static CellObjectData ParseGroupFile(string path, sbyte targetHmX, sbyte targetHmY)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        if (!ReadHeader(br, path, out _, out ushort subCellCount, out ushort chunksPerAxis))
            return default;

        SubCellEntry[] entries = ReadSubCellEntries(br, subCellCount);
        int totalChunks = chunksPerAxis * chunksPerAxis;

        int target = -1;
        for (int i = 0; i < subCellCount; i++)
            if (entries[i].mapX == targetHmX && entries[i].mapY == targetHmY)
            { target = i; break; }

        if (target == -1 || entries[target].objCount == 0)
            return default;

        var e = entries[target];
        var chunkIndex = ReadChunkIndex(fs, br, e.idxOff, totalChunks);
        var objects = ReadObjects(fs, br, e.dataOff, e.objCount);

        return new CellObjectData { objects = objects, chunkIndex = chunkIndex };
    }

    // ── Shared parsing helpers ───────────────────────────────────────────────

    private static bool ReadHeader(BinaryReader br, string path, out byte face, out ushort subCellCount, out ushort chunksPerAxis)
    {
        face = 0; subCellCount = 0; chunksPerAxis = 0;

        ulong magic = br.ReadUInt64();
        if (magic != OBJ_MAGIC)
        {
            Debug.LogError($"[CellObjectReader] Bad magic in '{path}'");
            return false;
        }
        br.ReadUInt16(); // formatVersion
        br.ReadUInt16(); // headerSize
        br.ReadUInt32(); // flags
        face = br.ReadByte();
        br.ReadByte();   // origTerrainX
        br.ReadByte();   // origTerrainY
        br.ReadByte();   // subdivPow2
        subCellCount  = br.ReadUInt16();
        chunksPerAxis = br.ReadUInt16();
        br.ReadUInt32(); // totalObjects
        br.BaseStream.Seek(OBJ_HEADER_SIZE, SeekOrigin.Begin);
        return true;
    }

    private static SubCellEntry[] ReadSubCellEntries(BinaryReader br, ushort subCellCount)
    {
        var entries = new SubCellEntry[subCellCount];
        for (int i = 0; i < subCellCount; i++)
        {
            entries[i].mapX    = br.ReadSByte();   // 1
            entries[i].mapY    = br.ReadSByte();   // 1
            br.ReadUInt16();                        // 2 reserved
            entries[i].objCount = br.ReadUInt32(); // 4
            entries[i].idxOff   = br.ReadUInt32(); // 4
            entries[i].dataOff  = br.ReadUInt32(); // 4
            for (int p = 0; p < 16; p++) br.ReadByte(); // 16 reserved
        }
        return entries;
    }

    private static (uint start, ushort count)[] ReadChunkIndex(FileStream fs, BinaryReader br, uint idxOff, int totalChunks)
    {
        var chunkIndex = new (uint start, ushort count)[totalChunks];
        fs.Seek(idxOff, SeekOrigin.Begin);
        for (int c = 0; c < totalChunks; c++)
        {
            chunkIndex[c].start = br.ReadUInt32(); // 4
            chunkIndex[c].count = br.ReadUInt16(); // 2
            br.ReadUInt16();                        // 2 reserved
        }
        return chunkIndex;
    }

    private static CellObjectInstance[] ReadObjects(FileStream fs, BinaryReader br, uint dataOff, uint objCount)
    {
        var objects = new CellObjectInstance[objCount];
        fs.Seek(dataOff, SeekOrigin.Begin);
        for (int i = 0; i < (int)objCount; i++)
        {
            objects[i].prototypeIndex = br.ReadByte();                 // 1
            objects[i].position = new Vector3(
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); // 12
            objects[i].rotation = new Quaternion(
                br.ReadSingle(), br.ReadSingle(),
                br.ReadSingle(), br.ReadSingle()); // 16
            objects[i].scale = new Vector3(
                br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); // 12
            objects[i].lodLevel = br.ReadByte();   // 1
            br.ReadByte(); br.ReadByte(); br.ReadByte(); br.ReadByte(); // 4 reserved
        }
        return objects;
    }
}