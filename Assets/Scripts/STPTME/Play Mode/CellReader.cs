using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CustomTypes;

/// <summary>
/// Runtime reader for combined cell files (heightmaps + tree data).
/// Mirrors the format written by CellFileBaking.WriteCellFile.
/// 
/// File layout:
///   [CellHeader 64 bytes]
///   [Height section: ushort[resY, resX]]
///   [TreeIndex section: TreeIndexEntry[totalChunks]]
///   [TreeData section: STPTMETreeInstance[totalTreeCount]]
/// </summary>
public class CellReader
{
    // ===== CONSTANTS (must match CellFileBaking) =====
    public const ulong CELL_MAGIC = 0x314C4C4543505453; // "STPCELL1"
    public const int CELL_HEADER_SIZE = 64;
    public const int TREE_INDEX_ENTRY_SIZE = 8;
    public const int TREE_INSTANCE_SIZE = 8;
    // Group file constants (mirror CellFileBaking)
    public const ulong GROUP_MAGIC = 0x3250524754505453; // "STPGRP02"
    public const int GROUP_HEADER_SIZE = 64;

    // ===== RUNTIME DATA STRUCTURES =====

    /// <summary>
    /// Parsed cell file header.
    /// </summary>
    public struct CellHeader
    {
        public ulong magic;
        public ushort formatVersion;
        public ushort headerSize;
        public uint flags;
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

        public bool HasTrees => (flags & 1) != 0;
        public bool IsValid => magic == CELL_MAGIC;
        public FaceId Face => (FaceId)isTop;
        public FaceId LegacyFace => isTop != 0 ? FaceId.Up : FaceId.Down;
    }

    /// <summary>
    /// Per-chunk tree index entry for O(1) lookup.
    /// </summary>
    public struct TreeIndexEntry
    {
        public uint startIndex;
        public ushort count;
        public ushort reserved;
    }

    /// <summary>
    /// Compact 8-byte tree instance (matches CellFileBaking.STPTMETreeInstance).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct STPTMETreeInstance
    {
        public byte spin;
        public byte distance;
        public byte widthScale;
        public byte heightScale;
        public byte rotation;
        public byte prototypeIndex;
        public ushort heightOffset;  // Terrain height / maxHeight * 65535
    }

    /// <summary>
    /// Fully parsed cell data, cached per (map, face) key.
    /// </summary>
    public class CellData
    {
        public CellHeader header;
        public ushort[,] heights;           // [resY, resX]
        public TreeIndexEntry[] treeIndex;  // [totalChunks]
        public STPTMETreeInstance[] trees;  // [totalTreeCount]

        /// <summary>
        /// Returns tree instances for a specific chunk via ArraySegment (zero-copy).
        /// Returns empty segment if chunk has no trees or index is invalid.
        /// </summary>
        public ArraySegment<STPTMETreeInstance> GetTreesForChunk(int chunkFlatIndex)
        {
            if (trees == null || treeIndex == null || chunkFlatIndex < 0 || chunkFlatIndex >= treeIndex.Length)
                return new ArraySegment<STPTMETreeInstance>(Array.Empty<STPTMETreeInstance>());

            var entry = treeIndex[chunkFlatIndex];
            if (entry.count == 0 || entry.startIndex >= trees.Length)
                return new ArraySegment<STPTMETreeInstance>(Array.Empty<STPTMETreeInstance>());

            return new ArraySegment<STPTMETreeInstance>(trees, (int)entry.startIndex, entry.count);
        }
    }

    // ===== STATE =====

    private Dictionary<MapFaceKey, CellData> cellCache
        = new Dictionary<MapFaceKey, CellData>();

    private string cellFolderPath;
    private int subdivisionsPowerOf2;
    private sbyte minX;

    // ===== PROPERTIES =====

    public int CachedCellCount => cellCache.Count;
    public Dictionary<MapFaceKey, CellData>.KeyCollection CachedKeys => cellCache.Keys;

    // ===== INIT =====

    public void Init(int subdivisionsPowerOf2, sbyte minX)
    {
        cellFolderPath = Path.Combine(Application.streamingAssetsPath, "MapAssets/Cells");
        this.subdivisionsPowerOf2 = subdivisionsPowerOf2;
        this.minX = minX;
    }

    // ===== PATH HELPERS =====

    /// <summary>Legacy per-cell file path. Used as fallback when group files don't exist.</summary>
    private string GetLegacyCellFilePath(Vector2SByte map, FaceId face)
    {
        string prefix = FaceIdUtility.GetFilePrefix(face);
        return Path.Combine(cellFolderPath, $"Cell_{prefix}_{map.x}_{map.y}.bytes");
    }

    /// <summary>Group file path: one file per original unsubdivided terrain.</summary>
    private string GetGroupFilePath(Vector2SByte map, FaceId face)
    {
        int tgX = (map.x - minX) / subdivisionsPowerOf2;
        int tgY = (map.y - minX) / subdivisionsPowerOf2;
        string prefix = FaceIdUtility.GetFilePrefix(face);
        return Path.Combine(cellFolderPath, $"CellGroup_{prefix}_{tgX}_{tgY}.bytes");
    }

    // ===== SYNC LOADING =====

    /// <summary>
    /// Returns cached cell data or loads synchronously from disk.
    /// Tries group file first, falls back to legacy per-cell file.
    /// </summary>
    public CellData GetOrLoadSync(Vector2SByte map, FaceId face)
    {
        var key = new MapFaceKey(map, face);
        if (cellCache.TryGetValue(key, out CellData cached))
            return cached;

        // Try group file (new format).
        string groupPath = GetGroupFilePath(map, face);
        if (File.Exists(groupPath))
        {
            var subcells = ParseGroupFileSync(groupPath, face);
            if (subcells != null)
            {
                foreach (var (m, d) in subcells)
                    cellCache[new MapFaceKey(m, face)] = d;
            }
            cellCache.TryGetValue(key, out CellData result);
            return result;
        }

        // Fallback: legacy per-cell file.
        string legacyPath = GetLegacyCellFilePath(map, face);
        CellData data = LoadCellFileSync(legacyPath);
        if (data != null)
            cellCache[key] = data;
        return data;
    }

    /// <summary>
    /// Returns cached cell data, or null if not loaded.
    /// </summary>
    public CellData GetCachedOrDefault(Vector2SByte map, FaceId face)
    {
        cellCache.TryGetValue(new MapFaceKey(map, face), out CellData cached);
        return cached;
    }

    /// <summary>
    /// Extracts just the heightmap at the requested LOD level.
    /// Returns null if cell doesn't exist or can't be loaded.
    /// </summary>
    public ushort[,] GetHeights(Vector2SByte map, byte lod, FaceId face, bool sync)
    {
        CellData data = sync ? GetOrLoadSync(map, face) : GetCachedOrDefault(map, face);
        if (data == null) return null;

        if (lod > 0)
            return STPTMEUtils.GetHeightsLodUshort(data.heights, lod);
        
        return data.heights;
    }

    /// <summary>
    /// Returns tree instances for a chunk identified by packed index.
    /// Unpacks the index to resolve map + local chunk flat index.
    /// Returns empty segment if cell not cached or chunk has no trees.
    /// </summary>
    /// <param name="packed">Packed chunk index (mapX|mapY|chunkX|chunkY)</param>
    /// <param name="face">Logical face identity</param>
    /// <param name="numberOfChunks">Chunks per axis within a cell (for flat index calc)</param>
    public ArraySegment<STPTMETreeInstance> GetTreesForPackedChunk(int packed, FaceId face, int numberOfChunks)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        var map = new Vector2SByte(mapX, mapY);
        
        CellData data = GetCachedOrDefault(map, face);
        if (data == null)
            return new ArraySegment<STPTMETreeInstance>(Array.Empty<STPTMETreeInstance>());

        int chunkFlatIndex = chunkY * numberOfChunks + chunkX;
        return data.GetTreesForChunk(chunkFlatIndex);
    }

    /// <summary>
    /// Sync variant of GetTreesForPackedChunk — synchronously loads the cell from disk if it
    /// is not already cached. Intended for use in the CanopyUVCache bake pass only; blocks
    /// the calling thread while loading.
    /// </summary>
    public ArraySegment<STPTMETreeInstance> GetTreesForPackedChunkSync(int packed, FaceId face, int numberOfChunks)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        var map = new Vector2SByte(mapX, mapY);

        CellData data = GetOrLoadSync(map, face);
        if (data == null)
            return new ArraySegment<STPTMETreeInstance>(Array.Empty<STPTMETreeInstance>());

        int chunkFlatIndex = chunkY * numberOfChunks + chunkX;
        return data.GetTreesForChunk(chunkFlatIndex);
    }

    // ===== ASYNC LOADING =====

    /// <summary>
    /// Coroutine that loads a cell asynchronously. Tries group file first, fallback to legacy.
    /// </summary>
    public IEnumerator LoadCellCoroutine(Vector2SByte map, FaceId face, Action<CellData> onLoaded)
    {
        var key = new MapFaceKey(map, face);
        if (cellCache.TryGetValue(key, out CellData cached))
        {
            onLoaded?.Invoke(cached);
            yield break;
        }

        string groupPath = GetGroupFilePath(map, face);
        bool groupExists = File.Exists(groupPath);
        string filePath = groupExists ? groupPath : GetLegacyCellFilePath(map, face);

        if (groupExists)
        {
            var task = Task.Run(() => ParseGroupFileSync(filePath, face));
            while (!task.IsCompleted)
                yield return null;

            if (task.IsCanceled || task.IsFaulted)
            {
                Debug.LogError($"[CellReader] Failed to load group {filePath}: {task.Exception}");
                onLoaded?.Invoke(null);
            }
            else
            {
                var subcells = task.Result;
                if (subcells != null)
                {
                    foreach (var (m, d) in subcells)
                        cellCache[new MapFaceKey(m, face)] = d;
                }
                cellCache.TryGetValue(key, out CellData result);
                onLoaded?.Invoke(result);
            }
        }
        else
        {
            var task = Task.Run(() => LoadCellFileSync(filePath));
            while (!task.IsCompleted)
                yield return null;

            if (task.IsCanceled || task.IsFaulted)
            {
                Debug.LogError($"[CellReader] Failed to load {filePath}: {task.Exception}");
                onLoaded?.Invoke(null);
            }
            else
            {
                CellData data = task.Result;
                if (data != null)
                    cellCache[key] = data;
                onLoaded?.Invoke(data);
            }
        }
    }

    // ===== CACHE MANAGEMENT =====

    public bool IsCached(Vector2SByte map, FaceId face)
    {
        return cellCache.ContainsKey(new MapFaceKey(map, face));
    }

    public void Evict(Vector2SByte map, FaceId face)
    {
        cellCache.Remove(new MapFaceKey(map, face));
    }

    // ===== INTERNAL: FILE PARSING =====

    /// <summary>
    /// Reads and parses a cell file. Thread-safe (no Unity API calls).
    /// </summary>
    private static CellData LoadCellFileSync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                // Read header
                CellHeader header = ReadHeader(br);
                if (!header.IsValid)
                {
                    Debug.LogError($"[CellReader] Invalid magic in {filePath}");
                    return null;
                }

                CellData data = new CellData { header = header };

                // Read heights
                fs.Seek(header.heightOffset, SeekOrigin.Begin);
                data.heights = new ushort[header.heightResY, header.heightResX];
                for (int y = 0; y < header.heightResY; y++)
                {
                    for (int x = 0; x < header.heightResX; x++)
                    {
                        data.heights[y, x] = br.ReadUInt16();
                    }
                }

                // Read tree index
                if (header.HasTrees && header.treeIndexSize > 0)
                {
                    fs.Seek(header.treeIndexOffset, SeekOrigin.Begin);
                    data.treeIndex = new TreeIndexEntry[header.totalChunks];
                    for (int i = 0; i < header.totalChunks; i++)
                    {
                        data.treeIndex[i] = new TreeIndexEntry
                        {
                            startIndex = br.ReadUInt32(),
                            count = br.ReadUInt16(),
                            reserved = br.ReadUInt16()
                        };
                    }

                    // Read tree data
                    fs.Seek(header.treeDataOffset, SeekOrigin.Begin);
                    data.trees = new STPTMETreeInstance[header.totalTreeCount];
                    for (int i = 0; i < header.totalTreeCount; i++)
                    {
                            data.trees[i] = new STPTMETreeInstance
                        {
                            spin = br.ReadByte(),
                            distance = br.ReadByte(),
                            widthScale = br.ReadByte(),
                            heightScale = br.ReadByte(),
                            rotation = br.ReadByte(),
                            prototypeIndex = br.ReadByte(),
                            heightOffset = br.ReadUInt16()
                        };
                    }

                }
                else
                {
                    data.treeIndex = Array.Empty<TreeIndexEntry>();
                    data.trees = Array.Empty<STPTMETreeInstance>();
                }

                return data;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CellReader] Error reading {filePath}: {ex.Message}");
            return null;
        }
    }

    private static CellHeader ReadHeader(BinaryReader br)
    {
        return new CellHeader
        {
            magic = br.ReadUInt64(),
            formatVersion = br.ReadUInt16(),
            headerSize = br.ReadUInt16(),
            flags = br.ReadUInt32(),
            mapX = br.ReadSByte(),
            mapY = br.ReadSByte(),
            isTop = br.ReadByte(),
            reserved0 = br.ReadByte(),
            heightResX = br.ReadUInt16(),
            heightResY = br.ReadUInt16(),
            chunkCountPerAxis = br.ReadUInt16(),
            totalChunks = br.ReadUInt16(),
            totalTreeCount = br.ReadUInt32(),
            heightOffset = br.ReadUInt32(),
            heightSize = br.ReadUInt32(),
            treeIndexOffset = br.ReadUInt32(),
            treeIndexSize = br.ReadUInt32(),
            treeDataOffset = br.ReadUInt32(),
            treeDataSize = br.ReadUInt32()
            // Skip 8 bytes reserved
        };
    }

    // ===== GROUP FILE PARSING =====

    /// <summary>
    /// Parses a group cell file and returns all subcells. Thread-safe (no Unity API, no cache writes).
    /// Returns null on failure.
    /// </summary>
    private static List<(Vector2SByte map, CellData data)> ParseGroupFileSync(string filePath, FaceId face)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                // Read group header
                ulong magic = br.ReadUInt64();
                if (magic != GROUP_MAGIC)
                {
                    Debug.LogError($"[CellReader] Invalid group magic in {filePath}");
                    return null;
                }
                br.ReadUInt16(); // formatVersion
                br.ReadUInt16(); // headerSize
                br.ReadUInt32(); // flags
                br.ReadByte();   // face
                br.ReadByte();   // origTerrainX
                br.ReadByte();   // origTerrainY
                br.ReadByte();   // subdPow2
                ushort subCellCount = br.ReadUInt16();
                ushort chunksPerCellAxis = br.ReadUInt16();
                // Skip remaining header padding
                fs.Seek(GROUP_HEADER_SIZE, SeekOrigin.Begin);

                int totalChunks = chunksPerCellAxis * chunksPerCellAxis;

                // Read subcell index
                var entries = new (sbyte mapX, sbyte mapY, byte dsSteps,
                    ushort resX, ushort resY, uint treeCount,
                    uint heightOff, uint heightSize,
                    uint treeIdxOff, uint treeDataOff)[subCellCount];

                for (int i = 0; i < subCellCount; i++)
                {
                    entries[i].mapX = br.ReadSByte();
                    entries[i].mapY = br.ReadSByte();
                    entries[i].dsSteps = br.ReadByte();
                    br.ReadByte(); // reserved
                    entries[i].resX = br.ReadUInt16();
                    entries[i].resY = br.ReadUInt16();
                    entries[i].treeCount = br.ReadUInt32();
                    entries[i].heightOff = br.ReadUInt32();
                    entries[i].heightSize = br.ReadUInt32();
                    entries[i].treeIdxOff = br.ReadUInt32();
                    entries[i].treeDataOff = br.ReadUInt32();
                    br.ReadUInt32(); // reserved2
                }

                // Parse each subcell into a CellData
                var result = new List<(Vector2SByte, CellData)>(subCellCount);

                for (int i = 0; i < subCellCount; i++)
                {
                    var e = entries[i];
                    var cellData = new CellData();

                    cellData.header = new CellHeader
                    {
                        magic = CELL_MAGIC,
                        flags = e.treeCount > 0 ? 1u : 0u,
                        mapX = e.mapX,
                        mapY = e.mapY,
                        isTop = (byte)face,
                        heightResX = e.resX,
                        heightResY = e.resY,
                        chunkCountPerAxis = chunksPerCellAxis,
                        totalChunks = (ushort)totalChunks,
                        totalTreeCount = e.treeCount
                    };

                    // Heights
                    fs.Seek(e.heightOff, SeekOrigin.Begin);
                    cellData.heights = new ushort[e.resY, e.resX];
                    for (int y = 0; y < e.resY; y++)
                        for (int x = 0; x < e.resX; x++)
                            cellData.heights[y, x] = br.ReadUInt16();

                    // Trees
                    if (e.treeCount > 0)
                    {
                        fs.Seek(e.treeIdxOff, SeekOrigin.Begin);
                        cellData.treeIndex = new TreeIndexEntry[totalChunks];
                        for (int t = 0; t < totalChunks; t++)
                        {
                            cellData.treeIndex[t] = new TreeIndexEntry
                            {
                                startIndex = br.ReadUInt32(),
                                count = br.ReadUInt16(),
                                reserved = br.ReadUInt16()
                            };
                        }

                        fs.Seek(e.treeDataOff, SeekOrigin.Begin);
                        cellData.trees = new STPTMETreeInstance[e.treeCount];
                        for (int t = 0; t < (int)e.treeCount; t++)
                        {
                            cellData.trees[t] = new STPTMETreeInstance
                            {
                                spin = br.ReadByte(),
                                distance = br.ReadByte(),
                                widthScale = br.ReadByte(),
                                heightScale = br.ReadByte(),
                                rotation = br.ReadByte(),
                                prototypeIndex = br.ReadByte(),
                                heightOffset = br.ReadUInt16()
                            };
                        }
                    }
                    else
                    {
                        cellData.treeIndex = Array.Empty<TreeIndexEntry>();
                        cellData.trees = Array.Empty<STPTMETreeInstance>();
                    }

                    result.Add((new Vector2SByte(e.mapX, e.mapY), cellData));
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CellReader] Error reading group {filePath}: {ex.Message}");
            return null;
        }
    }
}
