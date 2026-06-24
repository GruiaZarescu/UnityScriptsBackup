#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using CustomTypes;

/// <summary>
/// Editor tool that reads a root container transform and writes one CellObjectGroup
/// binary file per original terrain group, mirroring the MeshSaver / TreeBaker pattern.
///
/// File naming: StreamingAssets/MapAssets/CellObjects/CellObjectGroup_{prefix}_{tgX}_{tgY}.bytes
///
/// FILE FORMAT
/// ============
/// ObjectGroupHeader  (64 bytes)
///   magic            ulong   8   0x005031_4A424F50_5453 ("STPOBJ1\0" LE)
///   formatVersion    ushort  2   = 1
///   headerSize       ushort  2   = 64
///   flags            uint    4   bit 0 = hasObjects
///   face             byte    1
///   origTerrainX     byte    1
///   origTerrainY     byte    1
///   subdPow2         byte    1
///   subCellCount     ushort  2
///   chunksPerAxis    ushort  2
///   totalObjects     uint    4
///   [36 bytes padding]
///
/// SubCellObjectEntry × subCellCount  (32 bytes each)
///   mapX             sbyte   1
///   mapY             sbyte   1
///   reserved         ushort  2
///   objectCount      uint    4
///   objIndexOffset   uint    4   absolute file offset to chunk index table
///   objDataOffset    uint    4   absolute file offset to object data
///   [16 bytes reserved]
///
/// Chunk index table  (chunksPerAxis² × 8 bytes each)
///   startIndex       uint    4
///   count            ushort  2
///   reserved         ushort  2
///
//// Object instance  (41 bytes each)
///   prototypeIndex   byte    1
///   posX             float   4
///   posY             float   4
///   posZ             float   4
///   rotX             float   4   world-space quaternion
///   rotY             float   4
///   rotZ             float   4
///   rotW             float   4
///   scaleX           float   4
///   scaleY           float   4
///   scaleZ           float   4
///   lodLevel         byte    1
///   [4 bytes reserved]
/// </summary>
public class MapObjectBaker : MonoBehaviour
{
    private const ulong OBJ_MAGIC          = 0x005031_4A424F505453UL; // "STPOBJ1\0"
    private const ushort OBJ_FORMAT_VERSION = 1;
    private const int    OBJ_HEADER_SIZE    = 64;
    private const int    SUBCELL_ENTRY_SIZE = 32;
    private const int    CHUNK_INDEX_SIZE   = 8;
    private const int    OBJECT_SIZE        = 40;

    [SerializeField, Tooltip("Root transform whose direct children are prefab instances to bake.")]
    private Transform container;

    [SerializeField, Tooltip("Unified prototype registry — resolves prefab GUIDs to prototypeIndex at bake time.")]
    private MapObjectPrototypeRegistry prototypeRegistry;

    // ── Internal per-bake state ─────────────────────────────────────────────

    private struct BakedObject
    {
        public byte    prototypeIndex;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    // Keyed by (face, tgX, tgY) → per-subcell → per-chunk → list of objects
    private Dictionary<(FaceId face, int tgX, int tgY),
        Dictionary<Vector2SByte, List<BakedObject>[]>> _groups;

    // ── Entry point ─────────────────────────────────────────────────────────

    [ContextMenu("Bake Map Objects")]
    public void BakeMapObjects()
    {
        
        var settings           = TerrainManagementSettings.Instance;
        int   subdivPow2       = 1 << settings.heightmapSubdivisions;
        int   numberOfChunks   = settings.numberOfChunks;
        sbyte minX             = settings.minX;
        sbyte maxX             = settings.maxX;
        float sphereRadius     = settings.sphereRadius;
        Vector3 sphereCenter   = settings.sphereCenter;
        float terrainSize      = settings.terrainSize;
        float tilingFactor     = settings.tilingFactor;
        float chunkSize        = terrainSize / tilingFactor;   // world units per chunk side
        int   faceSpanInChunks = (maxX - minX + 1) * numberOfChunks;
        float faceWorldSize    = (maxX - minX + 1) * (terrainSize / subdivPow2);

        _groups = new Dictionary<(FaceId, int, int),
            Dictionary<Vector2SByte, List<BakedObject>[]>>();

        int skipped = 0;

        // ── Build GUID → prototypeIndex lookup ──────────────────────────
        var guidToIndex = new System.Collections.Generic.Dictionary<string, byte>();
        if (prototypeRegistry != null && prototypeRegistry.entries != null)
        {
            for (int i = 0; i < prototypeRegistry.entries.Length && i <= 255; i++)
            {
                var p = prototypeRegistry.entries[i];
                if (p == null || p.sourcePrefab == null) continue;
                string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(p.sourcePrefab));
                if (!string.IsNullOrEmpty(guid))
                    guidToIndex[guid] = (byte)i;
            }
        }

        // ── Assign each container child to a chunk ──────────────────────────────
        for (int i = 0; i < container.childCount; i++)
        {
            Transform child = container.GetChild(i);
            GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromOriginalSource(child.gameObject);
            if (prefabAsset == null)
            {
                Debug.LogWarning($"[MapObjectBaker] '{child.name}' is not a prefab instance — skipped.", child.gameObject);
                skipped++; continue;
            }

            string assetPath = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning($"[MapObjectBaker] Prefab source for '{child.name}' has no asset path — skipped.", child.gameObject);
                skipped++; continue;
            }

            string prefabGuid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!guidToIndex.TryGetValue(prefabGuid, out byte protoIdx))
            {
                Debug.LogWarning($"[MapObjectBaker] No registry entry for prefab '{prefabAsset.name}' — skipped.");
                skipped++; continue;
            }

            FaceId face = FaceIdUtility.GetClosestFace(child.position, sphereCenter);

            if (!FaceIdUtility.TryProjectWorldPointToFacePlane(
                    child.position, face, faceWorldSize, sphereCenter, out Vector2 plane))
            {
                Debug.LogWarning($"[MapObjectBaker] Could not project '{child.name}' onto face {face} — skipped.");
                skipped++; continue;
            }

            int globalChunkX = Mathf.Clamp(Mathf.FloorToInt(plane.x / chunkSize), 0, faceSpanInChunks - 1);
            int globalChunkY = Mathf.Clamp(Mathf.FloorToInt(plane.y / chunkSize), 0, faceSpanInChunks - 1);

            sbyte heightmapX = (sbyte)(minX + (globalChunkX / numberOfChunks));
            sbyte heightmapY = (sbyte)(minX + (globalChunkY / numberOfChunks));
            sbyte chunkX     = (sbyte)(globalChunkX % numberOfChunks);
            sbyte chunkY     = (sbyte)(globalChunkY % numberOfChunks);

            int tgX = (heightmapX - minX) / subdivPow2;
            int tgY = (heightmapY - minX) / subdivPow2;

            var groupKey = (face, tgX, tgY);
            if (!_groups.TryGetValue(groupKey, out var cellDict))
            {
                cellDict = new Dictionary<Vector2SByte, List<BakedObject>[]>();
                _groups[groupKey] = cellDict;
            }

            var cellKey = new Vector2SByte(heightmapX, heightmapY);
            if (!cellDict.TryGetValue(cellKey, out List<BakedObject>[] chunks))
            {
                chunks = new List<BakedObject>[numberOfChunks * numberOfChunks];
                cellDict[cellKey] = chunks;
            }

            int chunkFlat = chunkY * numberOfChunks + chunkX;
            if (chunks[chunkFlat] == null)
                chunks[chunkFlat] = new List<BakedObject>();

            chunks[chunkFlat].Add(new BakedObject
            {
                prototypeIndex = protoIdx,
                position = child.position,
                rotation = child.rotation,
                scale    = child.localScale,
            });
        }

        // ── Write files ──────────────────────────────────────────────────────
        string outFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets/CellObjects");
        if (!Directory.Exists(outFolder))
            Directory.CreateDirectory(outFolder);

        int filesWritten = 0;

        foreach (var kvp in _groups)
        {
            var (face, tgX, tgY) = kvp.Key;
            var cellDict         = kvp.Value;
            string prefix        = FaceIdUtility.GetFilePrefix(face);
            string filePath      = Path.Combine(outFolder, $"CellObjectGroup_{prefix}_{tgX}_{tgY}.bytes");

            WriteGroupFile(filePath, face, tgX, tgY, cellDict,
                (byte)subdivPow2, (ushort)numberOfChunks, minX);
            filesWritten++;
        }

        _groups = null;
        AssetDatabase.Refresh();

        Debug.Log($"[MapObjectBaker] Baked {container.childCount - skipped} object(s) into {filesWritten} file(s)." +
                  (skipped > 0 ? $" {skipped} skipped." : string.Empty));
    }

    // ── Binary writer ────────────────────────────────────────────────────────

    private static void WriteGroupFile(
        string filePath,
        FaceId face, int tgX, int tgY,
        Dictionary<Vector2SByte, List<BakedObject>[]> cellDict,
        byte subdivPow2, ushort chunksPerAxis,
        sbyte minX)
    {
        int subCellCount  = cellDict.Count;
        int totalChunks   = chunksPerAxis * chunksPerAxis;

        // Pre-calculate all object arrays sorted per-chunk per-cell, and the total.
        // We need offsets before writing the subcell index, so a two-pass approach is used.
        var cellKeys = new List<Vector2SByte>(cellDict.Keys);

        uint totalObjects = 0;
        foreach (var entry in cellDict.Values)
            foreach (var chunkList in entry)
                if (chunkList != null) totalObjects += (uint)chunkList.Count;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ── ObjectGroupHeader (64 bytes) ─────────────────────────────────────
        bw.Write(OBJ_MAGIC);                         // 8
        bw.Write(OBJ_FORMAT_VERSION);                // 2
        bw.Write((ushort)OBJ_HEADER_SIZE);           // 2
        bw.Write((uint)(totalObjects > 0 ? 1u : 0u)); // 4  flags
        bw.Write((byte)face);                        // 1
        bw.Write((byte)tgX);                         // 1
        bw.Write((byte)tgY);                         // 1
        bw.Write(subdivPow2);                        // 1
        bw.Write((ushort)subCellCount);              // 2
        bw.Write(chunksPerAxis);                     // 2
        bw.Write(totalObjects);                      // 4
        for (int p = 0; p < 36; p++) bw.Write((byte)0); // 36 padding → total 64

        // ── Layout calculation ───────────────────────────────────────────────
        // SubCellEntry section starts right after header.
        long subcellEntryBase = OBJ_HEADER_SIZE;
        long dataStart        = subcellEntryBase + (long)subCellCount * SUBCELL_ENTRY_SIZE;

        // For each cell: [chunk index table (totalChunks × 8)] [objects (n × 56)]
        var cellOffsets = new (long indexOffset, long dataOffset, uint objectCount)[subCellCount];

        long cursor = dataStart;
        for (int i = 0; i < subCellCount; i++)
        {
            List<BakedObject>[] chunks = cellDict[cellKeys[i]];
            uint cellObjCount = 0;
            foreach (var c in chunks) if (c != null) cellObjCount += (uint)c.Count;

            cellOffsets[i] = (
                indexOffset:  cursor,
                dataOffset:   cursor + (long)totalChunks * CHUNK_INDEX_SIZE,
                objectCount:  cellObjCount
            );
            cursor += (long)totalChunks * CHUNK_INDEX_SIZE + (long)cellObjCount * OBJECT_SIZE;
        }

        // ── SubCellObjectEntry × subCellCount (32 bytes each) ───────────────
        for (int i = 0; i < subCellCount; i++)
        {
            Vector2SByte key = cellKeys[i];
            bw.Write(key.x);                                        // 1
            bw.Write(key.y);                                        // 1
            bw.Write((ushort)0);                                    // 2 reserved
            bw.Write(cellOffsets[i].objectCount);                   // 4
            bw.Write((uint)cellOffsets[i].indexOffset);             // 4
            bw.Write((uint)cellOffsets[i].dataOffset);              // 4
            for (int p = 0; p < 16; p++) bw.Write((byte)0);        // 16 reserved → total 32
        }

        // ── Per-cell data sections ───────────────────────────────────────────
        for (int i = 0; i < subCellCount; i++)
        {
            List<BakedObject>[] chunks = cellDict[cellKeys[i]];

            // Build per-chunk startIndex (running count into this cell's object array)
            uint runningIndex = 0;

            // Chunk index table
            for (int c = 0; c < totalChunks; c++)
            {
                List<BakedObject> list = chunks[c];
                ushort count = list != null ? (ushort)Mathf.Min(list.Count, ushort.MaxValue) : (ushort)0;
                bw.Write(runningIndex);          // 4
                bw.Write(count);                 // 2
                bw.Write((ushort)0);             // 2 reserved
                runningIndex += count;
            }

            // Object instances
            for (int c = 0; c < totalChunks; c++)
            {
                List<BakedObject> list = chunks[c];
                if (list == null) continue;
                foreach (BakedObject obj in list)
                {
                    bw.Write(obj.prototypeIndex); // 1
                    bw.Write(obj.position.x);     // 4
                    bw.Write(obj.position.y);     // 4
                    bw.Write(obj.position.z);     // 4
                    bw.Write(obj.rotation.x);     // 4
                    bw.Write(obj.rotation.y);     // 4
                    bw.Write(obj.rotation.z);     // 4
                    bw.Write(obj.rotation.w);     // 4
                    bw.Write(obj.scale.x);        // 4
                    bw.Write(obj.scale.y);        // 4
                    bw.Write(obj.scale.z);        // 4
                    bw.Write((byte)0);            // 5 reserved (was lodLevel + 4 pad)
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                }
            }
        }
    }
}
#endif
