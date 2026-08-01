using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CustomTypes;
using STPTME.MapObjects;

/// <summary>
/// Runtime reader for CellObjectGroup binary files written by <see cref="MapObjectBaker"/>
/// (v2, compact two-convention format — see MapObjectBaker's header comment for the exact
/// byte layout). Mirrors <see cref="CellReader"/> in structure: synchronous load, dictionary
/// cache, zero-copy per-chunk query via <see cref="GetObjectsForChunk"/>.
///
/// TerrainSurface records never store position on disk — this reader reconstructs it via
/// ChunkManager.Instance.ComputeExactInstanceDir/SampleTerrainHeight, the exact same math the
/// GPU blotch pipeline and prefab tree spawning already use. WorldFixed records are simpler:
/// raw position, compressed rotation.
///
/// Both conventions are unpacked into the SAME public CellObjectInstance shape callers already
/// expect (plain Vector3/Quaternion) — everything downstream of this reader (IMapObjectSource,
/// ChunkObjectLoader, MapPrefabStreamer) needs zero changes.
///
/// Also provides <see cref="LoadAllObjects"/>, a bulk scan of every group file — used once, at
/// scene load, by MapContentOrchestrator to find every object whose prototype should be
/// GPU-instanced.
/// </summary>
public class CellObjectReader
{
    // ── File constants (must match MapObjectBaker) ───────────────────────────
    private const ulong  OBJ_MAGIC           = 0x005031_4A424F505453UL;
    private const ushort OBJ_FORMAT_VERSION  = 2;
    private const int    OBJ_HEADER_SIZE     = 64;
    private const int    SUBCELL_ENTRY_SIZE  = 32;
    private const int    CHUNK_INDEX_SIZE    = 8;

    // ── Per-object runtime representation (unchanged shape — callers depend on this) ──

    public struct CellObjectInstance
    {
        public byte       prototypeIndex;
        public Vector3    position;
        public Quaternion rotation;
        public Vector3    scale;
        public byte       lodLevel; // always 0 — never stored on disk, see MapObjectBaker header
    }

    private struct SubCellEntry
    {
        public sbyte mapX, mapY;
        public uint terrainObjCount, terrainIdxOff, terrainDataOff;
        public uint worldObjCount, worldIdxOff, worldDataOff;
    }

    // ── Per-cell cached data ─────────────────────────────────────────────────

    private struct CellObjectData
    {
        public MapObjectCompactFormat.TerrainAnchoredRecord[] terrainRecords;
        public (uint start, ushort count)[] terrainChunkIndex;
        public MapObjectCompactFormat.WorldFixedRecord[] worldRecords;
        public (uint start, ushort count)[] worldChunkIndex;
    }

    // ── State ────────────────────────────────────────────────────────────────

    private readonly Dictionary<MapFaceKey, CellObjectData> _cache
        = new Dictionary<MapFaceKey, CellObjectData>();

    private int   _subdivPow2;
    private sbyte _minX;

    public void Init(int subdivPow2, sbyte minX)
    {
        _subdivPow2 = subdivPow2;
        _minX       = minX;
    }

    // ── Public query (per-chunk, lazy, used by ChunkObjectLoader streaming) ──

    /// <summary>
    /// Returns all objects (both conventions, merged) assigned to the chunk encoded in
    /// <paramref name="packed"/>, on the given <paramref name="face"/>, filtered to the given
    /// <paramref name="lodLevel"/> (always 0 in practice — see CellObjectInstance.lodLevel).
    /// Loads the file synchronously if not yet cached. Returns an empty span on miss.
    /// </summary>
    public ArraySegment<CellObjectInstance> GetObjectsForChunk(
        int packed, FaceId face, int numberOfChunks, byte lodLevel)
    {
        if (lodLevel != 0) return default; // nothing is ever stored at any other value

        STPTMEUtils.ReadFourSBytesFromInt(packed,
            out sbyte hmX, out sbyte hmY, out sbyte chunkX, out sbyte chunkY);

        var mapKey = new MapFaceKey(new Vector2SByte(hmX, hmY), face);

        if (!_cache.TryGetValue(mapKey, out CellObjectData data))
        {
            data = LoadCell(hmX, hmY, face);
            _cache[mapKey] = data;
        }

        int chunkFlat = chunkY * numberOfChunks + chunkX;

        int terrainCount = 0, worldCount = 0;
        (uint start, ushort count) terrainRange = default, worldRange = default;

        if (data.terrainChunkIndex != null && chunkFlat < data.terrainChunkIndex.Length)
        {
            terrainRange = data.terrainChunkIndex[chunkFlat];
            terrainCount = terrainRange.count;
        }
        if (data.worldChunkIndex != null && chunkFlat < data.worldChunkIndex.Length)
        {
            worldRange = data.worldChunkIndex[chunkFlat];
            worldCount = worldRange.count;
        }

        int total = terrainCount + worldCount;
        if (total == 0) return default;

        if (_filterBuffer == null || _filterBuffer.Length < total)
            _filterBuffer = new CellObjectInstance[total * 2];

        var settings = TerrainManagementSettings.Instance;
        float chunkSizeMeters = settings.terrainSize / settings.tilingFactor;
        Vector3 sphereCenter = settings.sphereCenter;
        float sphereRadius = settings.sphereRadius;

        int n = 0;
        for (int i = 0; i < terrainCount; i++)
        {
            var rec = data.terrainRecords[terrainRange.start + i];
            MapObjectCompactFormat.UnpackLocalPos(rec.packedLocalPos, chunkSizeMeters, out float lx, out float lz);
            Vector3 dir = ChunkManager.Instance.ComputeExactInstanceDir(packed, face, lx, lz);
            float height = ChunkManager.Instance.SampleTerrainHeight(packed, face, lx, lz);
            var entry = MapObjectCompactFormat.UnpackTerrainAnchored(rec, dir, sphereCenter, sphereRadius, height);

            _filterBuffer[n++] = new CellObjectInstance
            {
                prototypeIndex = (byte)entry.prototypeIndex,
                position = entry.worldPosition,
                rotation = entry.worldRotation,
                scale = entry.localScale,
                lodLevel = 0
            };
        }
        for (int i = 0; i < worldCount; i++)
        {
            var rec = data.worldRecords[worldRange.start + i];
            var entry = MapObjectCompactFormat.UnpackWorldFixed(rec);

            _filterBuffer[n++] = new CellObjectInstance
            {
                prototypeIndex = (byte)entry.prototypeIndex,
                position = entry.worldPosition,
                rotation = entry.worldRotation,
                scale = entry.localScale,
                lodLevel = 0
            };
        }

        return new ArraySegment<CellObjectInstance>(_filterBuffer, 0, n);
    }

    private CellObjectInstance[] _filterBuffer;

    public void Evict(Vector2SByte map, FaceId face)
        => _cache.Remove(new MapFaceKey(map, face));

    // ── Public bulk load (used once, at scene load, by MapContentOrchestrator) ──

    /// <summary>
    /// Scans every CellObjectGroup_*.bytes file and returns EVERY object across every subcell
    /// (both conventions), fully unpacked into world position/rotation. Each result item
    /// carries its own resolved (chunkPacked, face) so the caller doesn't need to re-derive
    /// chunk addressing.
    /// </summary>
    public static List<(int chunkPacked, FaceId face, CellObjectInstance instance)> LoadAllObjects(string folder)
    {
        var result = new List<(int, FaceId, CellObjectInstance)>();

        if (!Directory.Exists(folder))
        {
            Debug.LogWarning($"[CellObjectReader] CellObjects folder not found: {folder}");
            return result;
        }

        var settings = TerrainManagementSettings.Instance;
        float chunkSizeMeters = settings.terrainSize / settings.tilingFactor;
        Vector3 sphereCenter = settings.sphereCenter;
        float sphereRadius = settings.sphereRadius;

        string[] files = Directory.GetFiles(folder, "CellObjectGroup_*.bytes");
        foreach (string path in files)
        {
            try
            {
                LoadAllObjectsFromFile(path, result, chunkSizeMeters, sphereCenter, sphereRadius);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CellObjectReader] Bulk parse failed for '{Path.GetFileName(path)}': {ex.Message}");
            }
        }

        return result;
    }

    private static void LoadAllObjectsFromFile(string path, List<(int, FaceId, CellObjectInstance)> result,
        float chunkSizeMeters, Vector3 sphereCenter, float sphereRadius)
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

            var terrainChunkIndex = e.terrainObjCount > 0 ? ReadChunkIndex(fs, br, e.terrainIdxOff, totalChunks) : null;
            var terrainRecords = e.terrainObjCount > 0 ? ReadTerrainRecords(fs, br, e.terrainDataOff, e.terrainObjCount) : null;
            var worldChunkIndex = e.worldObjCount > 0 ? ReadChunkIndex(fs, br, e.worldIdxOff, totalChunks) : null;
            var worldRecords = e.worldObjCount > 0 ? ReadWorldRecords(fs, br, e.worldDataOff, e.worldObjCount) : null;

            for (int c = 0; c < totalChunks; c++)
            {
                sbyte chunkX = (sbyte)(c % chunksPerAxis);
                sbyte chunkY = (sbyte)(c / chunksPerAxis);
                int packed = STPTMEUtils.WriteFourSBytesInInt(e.mapX, e.mapY, chunkX, chunkY);

                if (terrainChunkIndex != null)
                {
                    var (start, count) = terrainChunkIndex[c];
                    for (int i = 0; i < count; i++)
                    {
                        var rec = terrainRecords[start + i];
                        MapObjectCompactFormat.UnpackLocalPos(rec.packedLocalPos, chunkSizeMeters, out float lx, out float lz);
                        Vector3 dir = ChunkManager.Instance.ComputeExactInstanceDir(packed, (FaceId)face, lx, lz);
                        float height = ChunkManager.Instance.SampleTerrainHeight(packed, (FaceId)face, lx, lz);
                        var entry = MapObjectCompactFormat.UnpackTerrainAnchored(rec, dir, sphereCenter, sphereRadius, height);

                        result.Add((packed, (FaceId)face, new CellObjectInstance
                        {
                            prototypeIndex = (byte)entry.prototypeIndex,
                            position = entry.worldPosition, rotation = entry.worldRotation, scale = entry.localScale,
                            lodLevel = 0
                        }));
                    }
                }

                if (worldChunkIndex != null)
                {
                    var (start, count) = worldChunkIndex[c];
                    for (int i = 0; i < count; i++)
                    {
                        var rec = worldRecords[start + i];
                        var entry = MapObjectCompactFormat.UnpackWorldFixed(rec);

                        result.Add((packed, (FaceId)face, new CellObjectInstance
                        {
                            prototypeIndex = (byte)entry.prototypeIndex,
                            position = entry.worldPosition, rotation = entry.worldRotation, scale = entry.localScale,
                            lodLevel = 0
                        }));
                    }
                }
            }
        }
    }

    // ── File loading (per-chunk lazy path) ──────────────────────────────────

    private CellObjectData LoadCell(sbyte hmX, sbyte hmY, FaceId face)
    {
        int tgX = (hmX - _minX) / _subdivPow2;
        int tgY = (hmY - _minX) / _subdivPow2;
        string prefix = FaceIdUtility.GetFilePrefix(face);
        string path = Path.Combine(
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

        if (target == -1)
            return default;

        var e = entries[target];
        var data = new CellObjectData();

        if (e.terrainObjCount > 0)
        {
            data.terrainChunkIndex = ReadChunkIndex(fs, br, e.terrainIdxOff, totalChunks);
            data.terrainRecords = ReadTerrainRecords(fs, br, e.terrainDataOff, e.terrainObjCount);
        }
        if (e.worldObjCount > 0)
        {
            data.worldChunkIndex = ReadChunkIndex(fs, br, e.worldIdxOff, totalChunks);
            data.worldRecords = ReadWorldRecords(fs, br, e.worldDataOff, e.worldObjCount);
        }

        return data;
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
        ushort formatVersion = br.ReadUInt16();
        if (formatVersion != OBJ_FORMAT_VERSION)
        {
            Debug.LogError($"[CellObjectReader] '{path}' is format v{formatVersion}, but this reader only " +
                $"supports v{OBJ_FORMAT_VERSION}. Please re-bake map objects (MapObjectBaker > Bake Map Objects) " +
                "— this file's layout is incompatible, not just outdated data.");
            return false;
        }
        br.ReadUInt16(); // headerSize
        br.ReadUInt32(); // flags
        face = br.ReadByte();
        br.ReadByte();   // origTerrainX
        br.ReadByte();   // origTerrainY
        br.ReadByte();   // subdivPow2
        subCellCount  = br.ReadUInt16();
        chunksPerAxis = br.ReadUInt16();
        br.ReadUInt32(); // totalTerrainObjects
        br.ReadUInt32(); // totalWorldObjects
        br.BaseStream.Seek(OBJ_HEADER_SIZE, SeekOrigin.Begin);
        return true;
    }

    private static SubCellEntry[] ReadSubCellEntries(BinaryReader br, ushort subCellCount)
    {
        var entries = new SubCellEntry[subCellCount];
        for (int i = 0; i < subCellCount; i++)
        {
            entries[i].mapX = br.ReadSByte();              // 1
            entries[i].mapY = br.ReadSByte();               // 1
            br.ReadUInt16();                                  // 2 reserved
            entries[i].terrainObjCount = br.ReadUInt32();       // 4
            entries[i].terrainIdxOff   = br.ReadUInt32();        // 4
            entries[i].terrainDataOff  = br.ReadUInt32();         // 4
            entries[i].worldObjCount   = br.ReadUInt32();          // 4
            entries[i].worldIdxOff     = br.ReadUInt32();            // 4
            entries[i].worldDataOff    = br.ReadUInt32();             // 4
            br.ReadUInt32();                                            // 4 reserved → total 32
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

    private static MapObjectCompactFormat.TerrainAnchoredRecord[] ReadTerrainRecords(
        FileStream fs, BinaryReader br, uint dataOff, uint count)
    {
        var records = new MapObjectCompactFormat.TerrainAnchoredRecord[count];
        fs.Seek(dataOff, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            records[i].id = br.ReadUInt32();                 // 4
            records[i].prototypeIndex = br.ReadUInt16();       // 2
            records[i].packedLocalPos = br.ReadUInt32();        // 4
            records[i].packedHeadingTilt = br.ReadUInt32();      // 4
            records[i].scaleX = br.ReadUInt16();                  // 2
            records[i].scaleY = br.ReadUInt16();                   // 2
            records[i].scaleZ = br.ReadUInt16();                    // 2
        }
        return records;
    }

    private static MapObjectCompactFormat.WorldFixedRecord[] ReadWorldRecords(
        FileStream fs, BinaryReader br, uint dataOff, uint count)
    {
        var records = new MapObjectCompactFormat.WorldFixedRecord[count];
        fs.Seek(dataOff, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            records[i].id = br.ReadUInt32();             // 4
            records[i].prototypeIndex = br.ReadUInt16();   // 2
            records[i].posX = br.ReadSingle();              // 4
            records[i].posY = br.ReadSingle();               // 4
            records[i].posZ = br.ReadSingle();                // 4
            records[i].packedRotation = br.ReadUInt32();        // 4
            records[i].scaleX = br.ReadUInt16();                  // 2
            records[i].scaleY = br.ReadUInt16();                   // 2
            records[i].scaleZ = br.ReadUInt16();                    // 2
        }
        return records;
    }
}