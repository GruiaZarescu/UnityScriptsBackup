using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CustomTypes;

/// <summary>
/// Runtime reader for CellObjectGroup binary files written by <see cref="MapObjectBaker"/>.
/// Mirrors <see cref="CellReader"/> in structure: synchronous load, dictionary cache,
/// zero-copy per-chunk query via <see cref="GetObjectsForChunk"/>.
/// </summary>
public class CellObjectReader
{
    // ── File constants (must match MapObjectBaker) ───────────────────────────
    private const ulong OBJ_MAGIC       = 0x005031_4A424F505453UL;
    private const int   OBJ_HEADER_SIZE = 64;
    private const int   SUBCELL_ENTRY_SIZE = 32;
    private const int   CHUNK_INDEX_SIZE   = 8;
    private const int   OBJECT_SIZE        = 41;

    // ── Per-object runtime representation ───────────────────────────────────

    public struct CellObjectInstance
    {
        public byte      prototypeIndex;
        public Vector3   position;
        public Quaternion rotation;
        public Vector3   scale;
        public byte      lodLevel;
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

    // ── Public query ─────────────────────────────────────────────────────────

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

        // Filter by LOD: count matching entries into a temporary buffer.
        // In practice most chunks will have only a handful of objects, so this is fine.
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

    // ── File loading ─────────────────────────────────────────────────────────

    private CellObjectData LoadCell(sbyte hmX, sbyte hmY, FaceId face)
    {
        int tgX    = (hmX - _minX) / _subdivPow2;
        int tgY    = (hmY - _minX) / _subdivPow2;
        string prefix = FaceIdUtility.GetFilePrefix(face);
        string path   = Path.Combine(
            Application.streamingAssetsPath,
            $"MapAssets/CellObjects/CellObjectGroup_{prefix}_{tgX}_{tgY}.bytes");
        Debug.Log($"[CellObjectReader] Looking for file: {path} — exists={File.Exists(path)}");

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

        // Header
        ulong magic = br.ReadUInt64();
        if (magic != OBJ_MAGIC)
        {
            Debug.LogError($"[CellObjectReader] Bad magic in '{path}'");
            return default;
        }
        br.ReadUInt16(); // formatVersion
        br.ReadUInt16(); // headerSize
        br.ReadUInt32(); // flags
        br.ReadByte();   // face
        br.ReadByte();   // origTerrainX
        br.ReadByte();   // origTerrainY
        br.ReadByte();   // subdivPow2
        ushort subCellCount  = br.ReadUInt16();
        ushort chunksPerAxis = br.ReadUInt16();
        br.ReadUInt32(); // totalObjects (skip; we only need our subcell)
        fs.Seek(OBJ_HEADER_SIZE, SeekOrigin.Begin);

        int totalChunks = chunksPerAxis * chunksPerAxis;

        // SubCellEntry scan — find the entry matching the requested cell
        var entries = new (sbyte mapX, sbyte mapY, uint objCount, uint idxOff, uint dataOff)[subCellCount];
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

        // Find our subcell
        int target = -1;
        for (int i = 0; i < subCellCount; i++)
            if (entries[i].mapX == targetHmX && entries[i].mapY == targetHmY)
            { target = i; break; }

        if (target == -1 || entries[target].objCount == 0)
            return default;

        var e = entries[target];

        Debug.Log($"[CellObjectReader] '{path}': subCellCount={subCellCount}, target hm=({targetHmX},{targetHmY})");
        for (int i = 0; i < subCellCount; i++)
            Debug.Log($"  subcell[{i}] map=({entries[i].mapX},{entries[i].mapY}) objCount={entries[i].objCount}");

        // Chunk index table
        var chunkIndex = new (uint start, ushort count)[totalChunks];
        fs.Seek(e.idxOff, SeekOrigin.Begin);
        for (int c = 0; c < totalChunks; c++)
        {
            chunkIndex[c].start = br.ReadUInt32(); // 4
            chunkIndex[c].count = br.ReadUInt16(); // 2
            br.ReadUInt16();                        // 2 reserved
        }

        for (int c = 0; c < chunkIndex.Length; c++)
    Debug.Log($"  chunkIndex[{c}] → start={chunkIndex[c].start} count={chunkIndex[c].count}");

        // Object data
        var objects = new CellObjectInstance[e.objCount];
        fs.Seek(e.dataOff, SeekOrigin.Begin);
        for (int i = 0; i < (int)e.objCount; i++)
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

        return new CellObjectData { objects = objects, chunkIndex = chunkIndex };
    }
}
