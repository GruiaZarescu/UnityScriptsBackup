using UnityEngine;
using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CustomTypes;

// Splatmap group magic constant defined locally to avoid cross-assembly dependency on TextureBaker.
// Must match TextureBaker.SPLATMAP_GROUP_MAGIC.
public static class SplatmapGroupConstants
{
    public const ulong MAGIC = 0x3150475450545353; // "SSTPGS01" little-endian
    public const int HEADER_SIZE = 64;
    public const int SUBCELL_ENTRY_SIZE = 24;
}

/// <summary>
/// Runtime splatmap data provider. Mirrors heightmap caching in ChunkManager.
/// Loads splatmap tiles from StreamingAssets on demand (sync/async),
/// caches by (map, tier, face) key, provides layer metadata.
/// DOES NOT own GPU textures — that is ChunkMaterialManager's job (M3).
/// 
/// Reads from grouped SplatmapGroup_*.bytes files (one per original terrain) to keep
/// file count manageable when heightmapSubdivisions > 1. Individual Splatmap_*.bytes
/// files are also supported as a fallback for legacy bakes.
/// 
/// Dependencies: maxLOD from TerrainManagementSettings, tierCount (default 4).
/// No dependency on the editor-only TextureBaker class.
/// 
/// Cache: maintains a small LRU of recently accessed group files so that repeated
/// splatmap loads within the same terrain group don't re-parse the group header.
/// </summary>
public class TextureStreamer
{
    // ===== STRUCTS =====

    /// <summary>
    /// CPU-side splatmap tile for one heightmap cell at one LOD tier.
    /// Pixel layout: pixelData[y * width * layerCount + x * layerCount + layer] = weight byte [0,255].
    /// Includes border pixels on each side for seam-free filtering.
    /// </summary>
    public struct SplatmapTile
    {
        public byte[] pixelData;
        public ushort width;
        public ushort height;
        public byte layerCount;
        public float borderPixels;

        public readonly bool IsValid => pixelData != null && pixelData.Length > 0;
    }

    /// <summary>
    /// CPU-side world-space normal map tile for one heightmap cell at one normal tier.
    /// Pixel layout: pixelData[y * width * 3 + x * 3 + channel] in RGB8, where each
    /// channel encodes one component of the world-space normal as (n*0.5 + 0.5)*255.
    /// Includes border pixels on each side for seam-free filtering.
    /// </summary>
    public struct NormalTile
    {
        public byte[] pixelData;
        public ushort width;
        public ushort height;
        public float borderPixels;

        public readonly bool IsValid => pixelData != null && pixelData.Length > 0;
    }

    /// <summary>
    /// Per-layer metadata read from LayerMeta.bytes.
    /// Loaded once at init, never evicted.
    /// </summary>
    public struct TerrainLayerMeta
    {
        public Vector2 tileSize;
        public Vector2 tileOffset;
        public float metallic;
        public float smoothness;
        public string name;
    }

    // ===== CACHE =====

    private Dictionary<(Vector2SByte map, byte tier, FaceId face), SplatmapTile> splatmapCache
        = new Dictionary<(Vector2SByte, byte, FaceId), SplatmapTile>();

    // Heightmap-derived world-space normal maps. Independent tier system from splatmaps —
    // configured by NormalMeta.bytes baked alongside the normal files.
    private Dictionary<(Vector2SByte map, byte tier, FaceId face), NormalTile> normalCache
        = new Dictionary<(Vector2SByte, byte, FaceId), NormalTile>();

    // ===== CONFIG =====
    private byte maxLOD;
    private byte tierCount;
    private byte[] lodToTier; // lodToTier[lod] = tier index
    private string splatmapFolder;
    private string layerFolder;
    private string normalFolder;

    // ===== NORMAL MAP CONFIG (loaded from NormalMeta.bytes) =====
    private bool hasHeightmapNormals;
    private byte normalTierCount;
    private byte[] normalLodToTier;          // normalLodToTier[lod] = normal tier index
    private int[] normalTierResolutions;     // per-tier source resolution (without border)
    private byte normalBorderPixels;

    // ===== LAYER DATA (loaded once at init) =====

    private TerrainLayerMeta[] layerMetas;
    private byte[][] layerDiffuseData;  // [layerIndex] = raw RGBA32 bytes
    private byte[][] layerNormalData;   // [layerIndex] = raw RGBA32 bytes, null if not baked
    private int layerTextureResolution;
    private bool hasNormalMaps;

    // ===== PROPERTIES =====

    public int LayerCount => layerMetas != null ? layerMetas.Length : 0;
    public TerrainLayerMeta[] LayerMetas => layerMetas;
    public int LayerTextureResolution => layerTextureResolution;
    public bool HasNormalMaps => hasNormalMaps;
    public byte TierCount => tierCount;
    public int CachedTileCount => splatmapCache.Count;

    // Heightmap normal map accessors
    public bool HasHeightmapNormals => hasHeightmapNormals;
    public byte NormalTierCount => normalTierCount;
    public int CachedNormalTileCount => normalCache.Count;
    public Dictionary<(Vector2SByte map, byte tier, FaceId face), NormalTile>.KeyCollection CachedNormalKeys
        => normalCache.Keys;

    /// <summary>
    /// Exposes cached keys for eviction scanning by ChunkManager.
    /// </summary>
    public Dictionary<(Vector2SByte map, byte tier, FaceId face), SplatmapTile>.KeyCollection CachedKeys
        => splatmapCache.Keys;

    // ===== SPLATMAP GROUP CACHE =====
    // Parsed group file data cached per group key to avoid re-parsing the header
    // on every per-cell tile load within the same original terrain group.
    // The group file contains a subcell index and concatenated per-cell blobs.
    private class GroupCacheEntry
    {
        public struct SubCellEntry
        {
            public sbyte mapX;
            public sbyte mapY;
            public uint dataOffset;
            public uint dataSize;
        }
        public SubCellEntry[] entries;
        // The raw file path; used for equality checking with the current splatmap folder.
        public string filePath;
    }
    private Dictionary<(int tgX, int tgY, FaceId face), GroupCacheEntry> splatmapGroupCache
        = new Dictionary<(int, int, FaceId), GroupCacheEntry>();

    // ===== DERIVED BAKE-TIME PARAMETERS (set from ChunkManager at Init) =====
    // Needed to derive group file paths from cell coordinates (same formula as CellReader).
    private sbyte minX;
    private int subdivisionsPowerOf2;

    /// <summary>
    /// Stores the bake-time parameters needed to resolve group file paths.
    /// Must be called once during initialization, after Init().
    /// </summary>
    public void SetBakeParams(sbyte minX, int subdivisionsPowerOf2)
    {
        this.minX = minX;
        this.subdivisionsPowerOf2 = subdivisionsPowerOf2;
    }

    /// <summary>
    /// Initialize the streamer. Call from ChunkManager.Awake after settings are loaded.
    /// Loads layer metadata and layer textures from StreamingAssets (one-time).
    /// Builds the LOD-to-tier mapping from maxLOD and tierCount using the same formula
    /// as the bake tool, without depending on the editor-only TextureBaker class.
    /// </summary>
    /// <param name="maxLOD">From TerrainManagementSettings (ChunkManager already has this)</param>
    /// <param name="tierCount">Number of LOD tiers baked (default 4, matching TextureBaker default)</param>
    public void Init(byte maxLOD, byte tierCount = 4)
    {
        this.maxLOD = maxLOD;
        this.tierCount = tierCount;//Maybe custom tiers in the future, non formula derived, stored only once in meta files? For now, keep it simple and formulaic, matching the bake tool's default behavior.

        splatmapFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "Splatmaps");
        layerFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "TerrainLayers");
        normalFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "Normals");

        // Build LOD → tier mapping (same formula as bake tool's Default settings)
        // LODs are distributed evenly across tiers:
        //   lodToTier[i] = min(i / max(1, (maxLOD+1)/tierCount), tierCount-1)
        lodToTier = new byte[maxLOD + 1];
        int lodsPerTier = Mathf.Max(1, (maxLOD + 1) / tierCount);
        for (int i = 0; i <= maxLOD; i++)
        {
            lodToTier[i] = (byte)Mathf.Min(i / lodsPerTier, tierCount - 1);
        }

        LoadLayerMetadata();
        LoadLayerTextures();
        LoadNormalMetadata();
    }

    // ===== TIER LOOKUP =====

    /// <summary>
    /// Returns the splatmap tier index for a given LOD level.
    /// Multiple LODs map to the same tier, so cache hit rate is higher than heightmaps.
    /// </summary>
    public byte GetTierForLOD(byte lod)
    {
        if (lod >= lodToTier.Length) return (byte)(tierCount - 1);
        return lodToTier[lod];
    }

    /// <summary>
    /// Returns the heightmap-normal tier index for a given LOD level.
    /// The mapping is INDEPENDENT of the splatmap tier mapping (configurable per LOD via
    /// TextureBakeSettings.lodToNormalTier), so multiple chunk LODs can share normal tiers
    /// at different rates than splat tiers.
    /// </summary>
    public byte GetNormalTierForLOD(byte lod)
    {
        if (!hasHeightmapNormals || normalLodToTier == null || normalLodToTier.Length == 0)
            return 0;
        if (lod >= normalLodToTier.Length) return (byte)(normalTierCount - 1);
        return normalLodToTier[lod];
    }

    // ===== SYNC LOADING =====

    /// <summary>
    /// Returns cached tile or loads synchronously from disk. Use for collision chunks.
    /// Mirrors ChunkManager.GetOrLoadHeightmap(sync: true).
    /// </summary>
    public SplatmapTile GetOrLoadSync(Vector2SByte map, byte tier, FaceId face)
    {
        var key = (map, tier, face);
        if (splatmapCache.TryGetValue(key, out SplatmapTile cached))
            return cached;

        // Try loading from a group file (with individual file fallback)
        SplatmapTile tile;
        if (TryGetSplatmapBytes(map, face, out byte[] blob))
            tile = LoadTierFromBlob(blob, tier);
        else
            tile = default;

        if (tile.IsValid)
            splatmapCache[key] = tile;
        return tile;
    }

    public bool IsCached(Vector2SByte map, byte tier, FaceId face)
    {
        return splatmapCache.ContainsKey((map, tier, face));
    }

    public void Evict(Vector2SByte map, byte tier, FaceId face)
    {
        splatmapCache.Remove((map, tier, face));
    }

    // ===== NORMAL MAP LOADING =====

    /// <summary>
    /// Returns cached normal tile or loads synchronously from disk. Returns default (invalid)
    /// if heightmap normals weren't baked.
    /// </summary>
    public NormalTile GetOrLoadNormalSync(Vector2SByte map, byte tier, FaceId face)
    {
        if (!hasHeightmapNormals) return default;

        var key = (map, tier, face);
        if (normalCache.TryGetValue(key, out NormalTile cached))
            return cached;

        NormalTile tile = LoadNormalTierFromFile(GetNormalPath(map, face), tier);
        if (tile.IsValid)
            normalCache[key] = tile;
        return tile;
    }

    public bool IsNormalCached(Vector2SByte map, byte tier, FaceId face)
    {
        return normalCache.ContainsKey((map, tier, face));
    }

    public void EvictNormal(Vector2SByte map, byte tier, FaceId face)
    {
        normalCache.Remove((map, tier, face));
    }

    // ===== LAYER DATA ACCESS =====

    /// <summary>
    /// Returns raw RGBA32 bytes for a layer's diffuse texture, or null if unavailable.
    /// Loaded once at Init. ChunkMaterialManager uploads these to GPU Texture2DArray.
    /// After GPU upload, caller may call ReleaseLayerCPUData() to free CPU memory.
    /// </summary>
    public byte[] GetLayerDiffuseData(int layerIndex)
    {
        if (layerDiffuseData == null || layerIndex < 0 || layerIndex >= layerDiffuseData.Length)
            return null;
        return layerDiffuseData[layerIndex];
    }

    /// <summary>
    /// Returns raw RGBA32 bytes for a layer's normal map, or null if not baked/unavailable.
    /// </summary>
    public byte[] GetLayerNormalData(int layerIndex)
    {
        if (layerNormalData == null || layerIndex < 0 || layerIndex >= layerNormalData.Length)
            return null;
        return layerNormalData[layerIndex];
    }

    /// <summary>
    /// Frees CPU-side layer texture byte arrays after they have been uploaded to GPU.
    /// Layer metadata (tiling, offsets) is retained.
    /// </summary>
    public void ReleaseLayerCPUData()
    {
        layerDiffuseData = null;
        layerNormalData = null;
    }

    // ===== INTERNAL: FILE I/O =====

    /// <summary>
    /// Resolves the best available file for a splatmap cell, trying the grouped file first.
    /// The group file stores multiple per-cell blobs concatenated; this method parses the
    /// group index into a cache, then seeks to the correct cell's blob and returns the bytes
    /// as if they were a standalone file (caller then calls LoadTierFromStream).
    /// Returns the raw file path to a standalone file as a fallback.
    /// </summary>
    private bool TryGetSplatmapBytes(Vector2SByte map, FaceId face, out byte[] blob)
    {
        blob = null;

        // 1) Derive group key (same formula as CellReader)
        int tgX = (map.x - minX) / subdivisionsPowerOf2;
        int tgY = (map.y - minX) / subdivisionsPowerOf2;
        var groupKey = (tgX, tgY, face);

        // 2) Try to parse group file
        if (!splatmapGroupCache.TryGetValue(groupKey, out GroupCacheEntry groupEntry))
        {
            string groupPath = GetSplatmapGroupPath(tgX, tgY, face);
            if (File.Exists(groupPath))
            {
                groupEntry = ParseSplatmapGroupFile(groupPath);
                if (groupEntry != null)
                    splatmapGroupCache[groupKey] = groupEntry;
                else
                    return false;
            }
            else
            {
                // Fallback: individual file (tried down below)
                string indivPath = GetLegacySplatmapPath(map, face);
                if (File.Exists(indivPath))
                {
                    blob = File.ReadAllBytes(indivPath);
                    return true;
                }
                return false;
            }
        }

        // 3) Find this specific cell in the group entry
        for (int i = 0; i < groupEntry.entries.Length; i++)
        {
            if (groupEntry.entries[i].mapX == map.x && groupEntry.entries[i].mapY == map.y)
            {
                // Read the cell's blob from the group file
                using (var fs = new FileStream(groupEntry.filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    blob = new byte[groupEntry.entries[i].dataSize];
                    fs.Seek(groupEntry.entries[i].dataOffset, SeekOrigin.Begin);
                    fs.Read(blob, 0, blob.Length);
                }
                return true;
            }
        }

        return false;
    }

    private string GetSplatmapGroupPath(int tgX, int tgY, FaceId face)
    {
        string side = FaceIdUtility.GetFilePrefix(face);
        return Path.Combine(splatmapFolder, $"SplatmapGroup_{side}_{tgX}_{tgY}.bytes");
    }

    private string GetLegacySplatmapPath(Vector2SByte map, FaceId face)
    {
        string side = FaceIdUtility.GetFilePrefix(face);
        return Path.Combine(splatmapFolder, $"Splatmap_{side}_{map.x}_{map.y}.bytes");
    }

    /// <summary>
    /// Parses a grouped splatmap file header and returns the subcell index.
    /// Thread-safe, no Unity API calls.
    /// Format: 64-byte header + 24-byte subcell entries + concatenated cell blobs.
    /// </summary>
    private static GroupCacheEntry ParseSplatmapGroupFile(string filePath)
    {
        try
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                ulong magic = br.ReadUInt64();
                if (magic != SplatmapGroupConstants.MAGIC)
                {
                    Debug.LogError($"[TextureStreamer] Invalid splatmap group magic in {filePath}");
                    return null;
                }
                br.ReadUInt16(); // formatVersion
                br.ReadUInt16(); // headerSize
                br.ReadUInt32(); // flags
                ushort count = br.ReadUInt16();
                // skip remaining header padding to reach subcell entries
                fs.Seek(SplatmapGroupConstants.HEADER_SIZE, SeekOrigin.Begin);

                var entry = new GroupCacheEntry
                {
                    entries = new GroupCacheEntry.SubCellEntry[count],
                    filePath = filePath
                };

                for (int i = 0; i < count; i++)
                {
                    sbyte mx = br.ReadSByte();
                    sbyte my = br.ReadSByte();
                    br.ReadUInt16(); // reserved
                    uint dataOffset = br.ReadUInt32();
                    uint dataSize = br.ReadUInt32();
                    br.ReadUInt32(); // reserved
                    br.ReadUInt32(); // reserved
                    br.ReadUInt32(); // reserved (total padding to 24)
                    entry.entries[i] = new GroupCacheEntry.SubCellEntry
                    {
                        mapX = mx,
                        mapY = my,
                        dataOffset = dataOffset,
                        dataSize = dataSize
                    };
                }

                return entry;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TextureStreamer] Error parsing splatmap group {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads a single tier's pixel data from a byte array containing a full splatmap file
    /// (individual format). The buffer is the same format as a standalone Splatmap_*.bytes file.
    /// </summary>
    private static SplatmapTile LoadTierFromBlob(byte[] blob, byte tier)
    {
        if (blob == null || blob.Length < 32)
            return default;

        using (var ms = new MemoryStream(blob))
        using (var br = new BinaryReader(ms))
        {
            // === Header (32 bytes) ===
            ushort version = br.ReadUInt16();       // offset 0
            byte layerCount = br.ReadByte();        // offset 2
            byte fileTierCount = br.ReadByte();     // offset 3
            ushort baseResX = br.ReadUInt16();      // offset 4
            br.ReadUInt16();                        // offset 6  baseResY
            br.ReadByte();                          // offset 8  channelsPerPixel
            br.ReadByte();                          // offset 9  bytesPerChannel
            br.ReadByte();                          // offset 10 compressionFlags
            byte tier0Border = br.ReadByte();       // offset 11
            br.ReadUInt32();                        // offset 12 checksum
            br.ReadBytes(16);                       // offset 16..31 reserved

            if (tier >= fileTierCount)
            {
                Debug.LogError($"[TextureStreamer] Requested tier {tier} but blob has {fileTierCount} tiers");
                return default;
            }

            int descriptorOffset = 32 + tier * 12;
            ms.Seek(descriptorOffset, SeekOrigin.Begin);

            ushort resX = br.ReadUInt16();
            ushort resY = br.ReadUInt16();
            uint dataOffset = br.ReadUInt32();
            uint dataSize = br.ReadUInt32();

            ms.Seek(dataOffset, SeekOrigin.Begin);
            byte[] pixelData = br.ReadBytes((int)dataSize);

            float actualBorder = (baseResX > 0 && resX != baseResX)
                ? (float)tier0Border * resX / baseResX
                : tier0Border;

            return new SplatmapTile
            {
                pixelData = pixelData,
                width = resX,
                height = resY,
                layerCount = layerCount,
                borderPixels = actualBorder
            };
        }
    }

    /// <summary>
    /// Reads a single tier's pixel data from a splatmap file (individual format).
    /// Synchronous, thread-safe. Seeks directly to the target tier's data offset.
    /// 
    /// File format (written by TextureBaker):
    ///   [Header 32 bytes] version(2) layerCount(1) tierCount(1) baseResX(2) baseResY(2)
    ///                     channelsPerPixel(1) bytesPerChannel(1) compressionFlags(1) borderPixels(1)
    ///                     checksum(4) reserved(16)
    ///   [TierDescriptors 12 bytes each] resX(2) resY(2) dataOffset(4) dataSize(4)
    ///   [PixelData] raw uint8 weights per tier
    /// </summary>
    private static SplatmapTile LoadTierFromFile(string filePath, byte tier)
    {
        if (!File.Exists(filePath))
            return default;

        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        using (BinaryReader br = new BinaryReader(fs))
        {
            // === Header (32 bytes) ===
            ushort version = br.ReadUInt16();       // offset 0
            byte layerCount = br.ReadByte();        // offset 2
            byte fileTierCount = br.ReadByte();     // offset 3
            ushort baseResX = br.ReadUInt16();      // offset 4  tier 0 width (needed for border scaling)
            br.ReadUInt16();                        // offset 6  baseResY (unused here)
            br.ReadByte();                          // offset 8  channelsPerPixel (unused)
            br.ReadByte();                          // offset 9  bytesPerChannel (unused)
            br.ReadByte();                          // offset 10 compressionFlags (unused)
            byte tier0Border = br.ReadByte();       // offset 11 border pixels at tier 0
            br.ReadUInt32();                        // offset 12 checksum (unused at load)
            br.ReadBytes(16);                       // offset 16..31 reserved

            if (tier >= fileTierCount)
            {
                Debug.LogError($"[TextureStreamer] Requested tier {tier} but file has {fileTierCount} tiers: {filePath}");
                return default;
            }

            // === Seek to target tier descriptor (skip others) ===
            // Descriptors start at byte 32, each is 12 bytes
            int descriptorOffset = 32 + tier * 12;
            fs.Seek(descriptorOffset, SeekOrigin.Begin);

            ushort resX = br.ReadUInt16();
            ushort resY = br.ReadUInt16();
            uint dataOffset = br.ReadUInt32();
            uint dataSize = br.ReadUInt32();

            // === Read pixel data at the tier's offset ===
            fs.Seek(dataOffset, SeekOrigin.Begin);
            byte[] pixelData = br.ReadBytes((int)dataSize);

            // The bake tool downsamples the entire (core+border) image uniformly.
            // The actual border in the destination is fractional: tier0Border * dstSize / srcSize.
            // Using the exact float avoids sub-pixel UV shifts at heightmap boundaries.
            float actualBorder = (baseResX > 0 && resX != baseResX)
                ? (float)tier0Border * resX / baseResX
                : tier0Border;

            return new SplatmapTile
            {
                pixelData = pixelData,
                width = resX,
                height = resY,
                layerCount = layerCount,
                borderPixels = actualBorder
            };
        }
    }

    /// <summary>
    /// Async wrapper around LoadTierFromFile. Runs file I/O on a thread pool thread.
    /// No Unity API calls inside — fully thread-safe.
    /// </summary>
    private static Task<SplatmapTile> LoadTierFromFileAsync(string filePath, byte tier)
    {
        return Task.Run(() => LoadTierFromFile(filePath, tier));
    }

    // ===== INTERNAL: LAYER METADATA =====

    /// <summary>
    /// Reads LayerMeta.bytes: per-layer tiling, offset, metallic, smoothness, name.
    /// Called once during Init.
    /// 
    /// Format: version(2) count(1) resolution(2) hasNormals(1) reserved(10)
    ///         then per layer: tileSize(8) tileOffset(8) metallic(4) smoothness(4) name(string)
    /// </summary>
    private void LoadLayerMetadata()
    {
        string metaPath = Path.Combine(layerFolder, "LayerMeta.bytes");
        if (!File.Exists(metaPath))
        {
            Debug.LogWarning("[TextureStreamer] LayerMeta.bytes not found. Layer data unavailable.");
            layerMetas = Array.Empty<TerrainLayerMeta>();
            return;
        }

        using (FileStream fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read))
        using (BinaryReader br = new BinaryReader(fs))
        {
            ushort version = br.ReadUInt16();
            byte count = br.ReadByte();
            layerTextureResolution = br.ReadUInt16();
            hasNormalMaps = br.ReadByte() == 1;
            br.ReadBytes(10); // reserved

            layerMetas = new TerrainLayerMeta[count];
            for (int i = 0; i < count; i++)
            {
                layerMetas[i] = new TerrainLayerMeta
                {
                    tileSize = new Vector2(br.ReadSingle(), br.ReadSingle()),
                    tileOffset = new Vector2(br.ReadSingle(), br.ReadSingle()),
                    metallic = br.ReadSingle(),
                    smoothness = br.ReadSingle(),
                    name = br.ReadString() // BinaryWriter length-prefixed string
                };
            }
        }
    }

    /// <summary>
    /// Reads Layer_N_diffuse.bytes (and optionally Layer_N_normal.bytes) into byte arrays.
    /// Called once during Init. Data is held until ReleaseLayerCPUData() or Dispose().
    /// </summary>
    private void LoadLayerTextures()
    {
        if (layerMetas == null || layerMetas.Length == 0)
            return;

        int count = layerMetas.Length;
        layerDiffuseData = new byte[count][];
        layerNormalData = new byte[count][];

        for (int i = 0; i < count; i++)
        {
            string diffusePath = Path.Combine(layerFolder, $"Layer_{i}_diffuse.bytes");
            if (File.Exists(diffusePath))
                layerDiffuseData[i] = File.ReadAllBytes(diffusePath);

            if (hasNormalMaps)
            {
                string normalPath = Path.Combine(layerFolder, $"Layer_{i}_normal.bytes");
                if (File.Exists(normalPath))
                    layerNormalData[i] = File.ReadAllBytes(normalPath);
            }
        }
    }

    // ===== INTERNAL: NORMAL MAP I/O =====

    private string GetNormalPath(Vector2SByte map, FaceId face)
    {
        string side = FaceIdUtility.GetFilePrefix(face);
        return Path.Combine(normalFolder, $"Normal_{side}_{map.x}_{map.y}.bytes");
    }

    /// <summary>
    /// Loads NormalMeta.bytes (per-tier resolutions, per-LOD tier mapping).
    /// Sets hasHeightmapNormals = false silently if the file is missing (normal bake disabled).
    /// </summary>
    private void LoadNormalMetadata()
    {
        string metaPath = Path.Combine(normalFolder, "NormalMeta.bytes");
        if (!File.Exists(metaPath))
        {
            hasHeightmapNormals = false;
            return;
        }

        using (FileStream fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read))
        using (BinaryReader br = new BinaryReader(fs))
        {
            ushort version = br.ReadUInt16();
            byte tCount = br.ReadByte();
            byte lodArrayLen = br.ReadByte();
            normalBorderPixels = br.ReadByte();
            br.ReadBytes(5); // reserved

            normalTierCount = tCount;
            normalTierResolutions = new int[tCount];
            for (int i = 0; i < tCount; i++)
                normalTierResolutions[i] = br.ReadUInt16();

            normalLodToTier = new byte[lodArrayLen];
            for (int i = 0; i < lodArrayLen; i++)
                normalLodToTier[i] = br.ReadByte();

            hasHeightmapNormals = true;
        }
    }

    /// <summary>
    /// Reads a single tier's RGB8 normal pixel data from a normal file. Synchronous.
    /// File format mirrors splatmap (32-byte header + 12-byte tier descriptors), with channels=3.
    /// </summary>
    private static NormalTile LoadNormalTierFromFile(string filePath, byte tier)
    {
        if (!File.Exists(filePath))
            return default;

        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
        using (BinaryReader br = new BinaryReader(fs))
        {
            // === Header (32 bytes) ===
            ushort version = br.ReadUInt16();       // offset 0
            byte layerCount = br.ReadByte();        // offset 2 (=1 for normals)
            byte fileTierCount = br.ReadByte();     // offset 3
            ushort baseResX = br.ReadUInt16();      // offset 4 tier 0 width
            br.ReadUInt16();                        // offset 6 baseResY
            br.ReadByte();                          // offset 8 channelsPerPixel (=3)
            br.ReadByte();                          // offset 9 bytesPerChannel
            br.ReadByte();                          // offset 10 compression flags
            byte tier0Border = br.ReadByte();       // offset 11
            br.ReadUInt32();                        // offset 12 checksum
            br.ReadBytes(16);                       // offset 16..31 reserved

            if (tier >= fileTierCount)
            {
                Debug.LogError($"[TextureStreamer] Requested normal tier {tier} but file has {fileTierCount} tiers: {filePath}");
                return default;
            }

            int descriptorOffset = 32 + tier * 12;
            fs.Seek(descriptorOffset, SeekOrigin.Begin);

            ushort resX = br.ReadUInt16();
            ushort resY = br.ReadUInt16();
            uint dataOffset = br.ReadUInt32();
            uint dataSize = br.ReadUInt32();

            fs.Seek(dataOffset, SeekOrigin.Begin);
            byte[] pixelData = br.ReadBytes((int)dataSize);

            // Normal maps use a fixed border for ALL tiers (unlike splatmaps
            // which scale border proportionally). Use tier0Border directly.
            float actualBorder = tier0Border;

            return new NormalTile
            {
                pixelData = pixelData,
                width = resX,
                height = resY,
                borderPixels = actualBorder
            };
        }
    }

    // ===== CLEANUP =====

    /// <summary>
    /// Releases all cached data. Call on scene unload / destroy.
    /// </summary>
    public void Dispose()
    {
        splatmapCache.Clear();
        normalCache.Clear();
        layerDiffuseData = null;
        layerNormalData = null;
        layerMetas = null;
        lodToTier = null;
        normalLodToTier = null;
        normalTierResolutions = null;
    }
}
