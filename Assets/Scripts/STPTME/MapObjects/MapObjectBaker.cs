#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using CustomTypes;
using STPTME.MapObjects;

/// <summary>
/// Editor tool that reads MapObjectDatabase and writes one CellObjectGroup binary file per
/// original terrain group, mirroring the MeshSaver / CellFileBaking pattern.
///
/// File naming: StreamingAssets/MapAssets/CellObjects/CellObjectGroup_{prefix}_{tgX}_{tgY}.bytes
///
/// FILE FORMAT (v2 — compact, two-convention)
/// ===========================================
/// Entries are split by MapObjectDatabase.AnchorMode at bake time:
///   TerrainSurface — position/orientation reconstructed at load time from chunk context
///     (never stored on disk) + a heading/tilt pair instead of a full quaternion. 20 bytes.
///   WorldFixed     — raw position + a smallest-three-compressed quaternion. 28 bytes.
/// See MapObjectCompactFormat for the exact packing math and rationale.
///
/// v1 files (uniform 46-byte records, no anchor-mode split) are REJECTED by the reader with
/// a clear "please rebake" error rather than silently misparsed — same policy as the earlier
/// OBJECT_SIZE fix, which taught us that silently reading a stale layout produces corrupted,
/// hard-to-diagnose data rather than an obvious failure.
///
/// ObjectGroupHeader  (64 bytes)
///   magic                ulong   8   0x005031_4A424F50_5453 ("STPOBJ1\0" LE)
///   formatVersion        ushort  2   = 2
///   headerSize           ushort  2   = 64
///   flags                uint    4   bit 0 = hasObjects
///   face                 byte    1
///   origTerrainX         byte    1
///   origTerrainY         byte    1
///   subdPow2             byte    1
///   subCellCount         ushort  2
///   chunksPerAxis        ushort  2
///   totalTerrainObjects  uint    4
///   totalWorldObjects    uint    4
///   [32 bytes padding]
///
/// SubCellObjectEntry × subCellCount  (32 bytes each)
///   mapX               sbyte  1
///   mapY               sbyte  1
///   reserved           ushort 2
///   terrainObjCount    uint   4
///   terrainIdxOffset   uint   4   absolute file offset to terrain-anchored chunk index table
///   terrainDataOffset  uint   4   absolute file offset to terrain-anchored record data
///   worldObjCount      uint   4
///   worldIdxOffset     uint   4   absolute file offset to world-fixed chunk index table
///   worldDataOffset    uint   4   absolute file offset to world-fixed record data
///   [4 bytes reserved]
///
/// Chunk index table (both conventions use this identical 8-byte-per-chunk layout)
///   startIndex   uint    4
///   count        ushort  2
///   reserved     ushort  2
///
/// TerrainAnchoredRecord (20 bytes) — see MapObjectCompactFormat.TerrainAnchoredRecord
/// WorldFixedRecord      (28 bytes) — see MapObjectCompactFormat.WorldFixedRecord
/// </summary>
public class MapObjectBaker : MonoBehaviour
{
    private const ulong  OBJ_MAGIC          = 0x005031_4A424F505453UL; // "STPOBJ1\0"
    private const ushort OBJ_FORMAT_VERSION = 2;
    private const int    OBJ_HEADER_SIZE    = 64;
    private const int    SUBCELL_ENTRY_SIZE = 32;
    private const int    CHUNK_INDEX_SIZE   = 8;
    private const int    TERRAIN_RECORD_SIZE = 20; // id(4)+proto(2)+localPos(4)+headingTilt(4)+scale(6)
    private const int    WORLD_RECORD_SIZE   = 28; // id(4)+proto(2)+pos(12)+rot(4)+scale(6)

    [SerializeField, Tooltip("Unified prototype registry — resolves prefab GUIDs to prototypeIndex at bake time.")]
    private MapObjectPrototypeRegistry prototypeRegistry;

    [SerializeField, Tooltip("Live authoring database to export from.")]
    private MapObjectDatabase database;

    // ── Internal per-bake state ─────────────────────────────────────────────

    private struct BakedObject
    {
        public ulong id;
        public byte prototypeIndex;
        public MapObjectDatabase.AnchorMode anchorMode;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        /// <summary>Meaningful only for TerrainSurface entries — computed once at bake time by
        /// MapObjectChunkMath, same convention BlotchBaker already uses.</summary>
        public float localXMeters, localZMeters;
    }

    // Keyed by (face, tgX, tgY) → per-subcell → per-chunk → list of objects (mixed anchor
    // modes; split into terrain/world sections only at write time, see WriteGroupFile).
    private Dictionary<(FaceId face, int tgX, int tgY),
        Dictionary<Vector2SByte, List<BakedObject>[]>> _groups;

    [ContextMenu("Bake Map Objects")]
    public void BakeMapObjects()
    {
        if (database == null)
        {
            Debug.LogError("[MapObjectBaker] No MapObjectDatabase assigned. Cannot bake.");
            return;
        }

        var settings         = TerrainManagementSettings.Instance;
        int   subdivPow2     = 1 << settings.heightmapSubdivisions;
        int   numberOfChunks = settings.numberOfChunks;
        sbyte minX           = settings.minX;
        sbyte maxX           = settings.maxX;
        Vector3 sphereCenter = settings.sphereCenter;
        float terrainSize    = settings.terrainSize;
        float tilingFactor   = settings.tilingFactor;
        float chunkSize      = terrainSize / tilingFactor;
        float faceWorldSize  = settings.faceWorldSize; // see ChunkManager.GetFaceWorldSize for why this must not be reinvented locally

        _groups = new Dictionary<(FaceId, int, int), Dictionary<Vector2SByte, List<BakedObject>[]>>();

        int skipped = 0;
        int protoCount = (prototypeRegistry != null && prototypeRegistry.entries != null)
            ? prototypeRegistry.entries.Length : int.MaxValue;

        foreach (var entry in database.All)
        {
            if (entry.prototypeIndex < 0 || entry.prototypeIndex >= protoCount)
            {
                Debug.LogWarning($"[MapObjectBaker] Entry id={entry.id} has out-of-range prototypeIndex={entry.prototypeIndex} — skipped.");
                skipped++; continue;
            }

            if (!MapObjectChunkMath.TryResolve(entry.worldPosition, sphereCenter, chunkSize, faceWorldSize,
                    numberOfChunks, minX, maxX, out var addr))
            {
                Debug.LogWarning($"[MapObjectBaker] Could not project entry id={entry.id} onto any face — skipped.");
                skipped++; continue;
            }

            int tgX = (addr.heightmapX - minX) / subdivPow2;
            int tgY = (addr.heightmapY - minX) / subdivPow2;

            var groupKey = (addr.face, tgX, tgY);
            if (!_groups.TryGetValue(groupKey, out var cellDict))
            {
                cellDict = new Dictionary<Vector2SByte, List<BakedObject>[]>();
                _groups[groupKey] = cellDict;
            }

            var cellKey = new Vector2SByte(addr.heightmapX, addr.heightmapY);
            if (!cellDict.TryGetValue(cellKey, out List<BakedObject>[] chunks))
            {
                chunks = new List<BakedObject>[numberOfChunks * numberOfChunks];
                cellDict[cellKey] = chunks;
            }

            int chunkFlat = addr.chunkY * numberOfChunks + addr.chunkX;
            if (chunks[chunkFlat] == null)
                chunks[chunkFlat] = new List<BakedObject>();

            chunks[chunkFlat].Add(new BakedObject
            {
                id             = entry.id,
                prototypeIndex = (byte)entry.prototypeIndex,
                anchorMode     = entry.anchorMode,
                position       = entry.worldPosition,
                rotation       = entry.worldRotation,
                scale          = entry.localScale,
                localXMeters   = addr.localXMeters,
                localZMeters   = addr.localZMeters,
            });
        }

        string outFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets/CellObjects");
        if (!Directory.Exists(outFolder))
            Directory.CreateDirectory(outFolder);

        // This bake is a full, non-incremental rebuild from the live database — it should own
        // the whole output folder. Without this, a cell that had objects in a PREVIOUS bake
        // but has none in the CURRENT database never gets rewritten (there's nothing to write),
        // so its old file — possibly a stale format version — silently survives forever and
        // gets picked up by LoadAllObjects on the next scene load.
        string[] staleFiles = Directory.GetFiles(outFolder, "CellObjectGroup_*.bytes");
        foreach (string stale in staleFiles)
            File.Delete(stale);
        if (staleFiles.Length > 0)
            Debug.Log($"[MapObjectBaker] Cleared {staleFiles.Length} existing CellObjectGroup file(s) before rebake.");

        int filesWritten = 0;
        foreach (var kvp in _groups)
        {
            var (face, tgX, tgY) = kvp.Key;
            var cellDict         = kvp.Value;
            string prefix        = FaceIdUtility.GetFilePrefix(face);
            string filePath      = Path.Combine(outFolder, $"CellObjectGroup_{prefix}_{tgX}_{tgY}.bytes");

            WriteGroupFile(filePath, face, tgX, tgY, cellDict,
                (byte)subdivPow2, (ushort)numberOfChunks, sphereCenter, chunkSize);
            filesWritten++;
        }

        _groups = null;
        AssetDatabase.Refresh();

        Debug.Log($"[MapObjectBaker] Baked {database.Count - skipped} object(s) into {filesWritten} file(s), " +
            $"format v{OBJ_FORMAT_VERSION}." + (skipped > 0 ? $" {skipped} skipped." : string.Empty));
    }

    // ── Binary writer ────────────────────────────────────────────────────────

    private static void WriteGroupFile(
        string filePath,
        FaceId face, int tgX, int tgY,
        Dictionary<Vector2SByte, List<BakedObject>[]> cellDict,
        byte subdivPow2, ushort chunksPerAxis,
        Vector3 sphereCenter, float chunkSizeMeters)
    {
        int subCellCount = cellDict.Count;
        int totalChunks  = chunksPerAxis * chunksPerAxis;
        var cellKeys     = new List<Vector2SByte>(cellDict.Keys);

        // Pack every record up front and split by anchor mode, per cell per chunk. Packing is
        // pure math (no file I/O yet) — this just gets us fixed-size records so offsets can be
        // computed in a single pass afterward, same two-pass shape the old writer already used.
        var terrainByCell = new List<MapObjectCompactFormat.TerrainAnchoredRecord>[subCellCount][];
        var worldByCell   = new List<MapObjectCompactFormat.WorldFixedRecord>[subCellCount][];

        uint totalTerrainObjects = 0, totalWorldObjects = 0;

        for (int i = 0; i < subCellCount; i++)
        {
            var chunks = cellDict[cellKeys[i]];
            var tChunks = new List<MapObjectCompactFormat.TerrainAnchoredRecord>[totalChunks];
            var wChunks = new List<MapObjectCompactFormat.WorldFixedRecord>[totalChunks];

            for (int c = 0; c < totalChunks; c++)
            {
                var list = chunks[c];
                if (list == null) continue;

                foreach (var obj in list)
                {
                    var entry = new MapObjectDatabase.MapObjectEntry
                    {
                        id = obj.id, prototypeIndex = obj.prototypeIndex,
                        worldPosition = obj.position, worldRotation = obj.rotation, localScale = obj.scale,
                        anchorMode = obj.anchorMode
                    };

                    if (obj.anchorMode == MapObjectDatabase.AnchorMode.WorldFixed)
                    {
                        if (wChunks[c] == null) wChunks[c] = new List<MapObjectCompactFormat.WorldFixedRecord>();
                        wChunks[c].Add(MapObjectCompactFormat.PackWorldFixed(entry));
                        totalWorldObjects++;
                    }
                    else
                    {
                        if (tChunks[c] == null) tChunks[c] = new List<MapObjectCompactFormat.TerrainAnchoredRecord>();
                        tChunks[c].Add(MapObjectCompactFormat.PackTerrainAnchored(
                            entry, sphereCenter, obj.localXMeters, obj.localZMeters, chunkSizeMeters));
                        totalTerrainObjects++;
                    }
                }
            }
            terrainByCell[i] = tChunks;
            worldByCell[i] = wChunks;
        }

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ── ObjectGroupHeader (64 bytes) ─────────────────────────────────────
        bw.Write(OBJ_MAGIC);                                                         // 8
        bw.Write(OBJ_FORMAT_VERSION);                                                // 2
        bw.Write((ushort)OBJ_HEADER_SIZE);                                           // 2
        bw.Write((uint)((totalTerrainObjects + totalWorldObjects) > 0 ? 1u : 0u));   // 4  flags
        bw.Write((byte)face);                                                        // 1
        bw.Write((byte)tgX);                                                         // 1
        bw.Write((byte)tgY);                                                         // 1
        bw.Write(subdivPow2);                                                        // 1
        bw.Write((ushort)subCellCount);                                              // 2
        bw.Write(chunksPerAxis);                                                     // 2
        bw.Write(totalTerrainObjects);                                               // 4
        bw.Write(totalWorldObjects);                                                 // 4
        for (int p = 0; p < 32; p++) bw.Write((byte)0);                              // 32 padding → total 64

        // ── Layout calculation ───────────────────────────────────────────────
        long subcellEntryBase = OBJ_HEADER_SIZE;
        long dataStart = subcellEntryBase + (long)subCellCount * SUBCELL_ENTRY_SIZE;

        var cellOffsets = new (long terrainIdxOff, long terrainDataOff, uint terrainCount,
                                long worldIdxOff, long worldDataOff, uint worldCount)[subCellCount];

        long cursor = dataStart;
        for (int i = 0; i < subCellCount; i++)
        {
            uint terrainCount = 0, worldCount = 0;
            foreach (var c in terrainByCell[i]) if (c != null) terrainCount += (uint)c.Count;
            foreach (var c in worldByCell[i])   if (c != null) worldCount   += (uint)c.Count;

            long terrainIdxOff = cursor;
            long terrainDataOff = terrainIdxOff + (long)totalChunks * CHUNK_INDEX_SIZE;
            long worldIdxOff = terrainDataOff + (long)terrainCount * TERRAIN_RECORD_SIZE;
            long worldDataOff = worldIdxOff + (long)totalChunks * CHUNK_INDEX_SIZE;

            cellOffsets[i] = (terrainIdxOff, terrainDataOff, terrainCount, worldIdxOff, worldDataOff, worldCount);
            cursor = worldDataOff + (long)worldCount * WORLD_RECORD_SIZE;
        }

        // ── SubCellObjectEntry × subCellCount (32 bytes each) ───────────────
        for (int i = 0; i < subCellCount; i++)
        {
            Vector2SByte key = cellKeys[i];
            var off = cellOffsets[i];
            bw.Write(key.x);                             // 1
            bw.Write(key.y);                              // 1
            bw.Write((ushort)0);                            // 2 reserved
            bw.Write(off.terrainCount);                      // 4
            bw.Write((uint)off.terrainIdxOff);                 // 4
            bw.Write((uint)off.terrainDataOff);                 // 4
            bw.Write(off.worldCount);                            // 4
            bw.Write((uint)off.worldIdxOff);                       // 4
            bw.Write((uint)off.worldDataOff);                        // 4
            bw.Write((uint)0);                                        // 4 reserved → total 32
        }

        // ── Per-cell data sections ───────────────────────────────────────────
        for (int i = 0; i < subCellCount; i++)
        {
            WriteChunkSection(bw, terrainByCell[i], totalChunks,
                (w, rec) =>
                {
                    w.Write(rec.id); w.Write(rec.prototypeIndex);
                    w.Write(rec.packedLocalPos); w.Write(rec.packedHeadingTilt);
                    w.Write(rec.scaleX); w.Write(rec.scaleY); w.Write(rec.scaleZ);
                });

            WriteChunkSection(bw, worldByCell[i], totalChunks,
                (w, rec) =>
                {
                    w.Write(rec.id); w.Write(rec.prototypeIndex);
                    w.Write(rec.posX); w.Write(rec.posY); w.Write(rec.posZ);
                    w.Write(rec.packedRotation);
                    w.Write(rec.scaleX); w.Write(rec.scaleY); w.Write(rec.scaleZ);
                });
        }
    }

    /// <summary>Writes one chunk-index table followed by one flat record array, for either
    /// convention — shared so the two sections can never accidentally drift in structure.</summary>
    private static void WriteChunkSection<T>(BinaryWriter bw, List<T>[] chunks, int totalChunks, Action<BinaryWriter, T> writeRecord)
    {
        uint runningIndex = 0;
        for (int c = 0; c < totalChunks; c++)
        {
            var list = chunks[c];
            ushort count = list != null ? (ushort)Mathf.Min(list.Count, ushort.MaxValue) : (ushort)0;
            bw.Write(runningIndex);   // 4
            bw.Write(count);          // 2
            bw.Write((ushort)0);      // 2 reserved
            runningIndex += count;
        }

        for (int c = 0; c < totalChunks; c++)
        {
            var list = chunks[c];
            if (list == null) continue;
            foreach (T rec in list)
                writeRecord(bw, rec);
        }
    }
}
#endif