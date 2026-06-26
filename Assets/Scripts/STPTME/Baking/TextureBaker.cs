using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomTypes;


#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Editor-only tool: Extracts terrain splatmaps and layer data, downsamples per LOD tier,
/// serializes to StreamingAssets for runtime streaming.
/// 
/// Splatmap file format (per heightmap cell):
///   [Header]  - 32 bytes, versioned
///   [TierDescriptors] - 12 bytes each
///   [PixelData] - raw uint8 weights per tier
///   
/// Layer file format (shared across all terrains):
///   [Header] - versioned
///   [PerLayerMeta] - tiling, offset, texture filenames
///   [LayerTextures] - raw RGBA pixel data per layer per resolution
/// </summary>

public class TextureBaker
{
    // ===== FORMAT CONSTANTS =====
    private const ushort SPLATMAP_FORMAT_VERSION = 1;
    private const ushort LAYER_FORMAT_VERSION = 1;

    // Header: version(2) + layerCount(1) + tierCount(1) + baseResX(2) + baseResY(2) 
    //       + channelsPerPixel(1) + bytesPerChannel(1) + compressionFlags(1) + padding(1)
    //       + checksum(4) + reserved(16) = 32 bytes
    private const int SPLATMAP_HEADER_SIZE = 32;

    // Per-tier descriptor: resX(2) + resY(2) + dataOffset(4) + dataSize(4) = 12 bytes
    private const int TIER_DESCRIPTOR_SIZE = 12;

    // ===== UNIFORM SPLATMAP TRACKING =====
    // Threshold: splatmap is considered "uniform" if one layer covers >= this fraction of the area
    // Set to 1.0f to only skip baking for truly single-layer splatmaps (preserves roads, paths, etc.)
    private const float UNIFORM_THRESHOLD = 1.0f;

    // Per-face uniform classification files written alongside splatmap groups.
    // Format: magic=0x4C43525350 ("SRCL" LE), version=1, cellCount, then per-cell dominantLayer (sbyte).
    // The sbyte is the dominant layer index (0..3) for uniform cells, or -1 for non-uniform.
    private const ulong UNIFORM_CLASS_MAGIC = 0x4C43525350; // "SRCL" little-endian
    private const ushort UNIFORM_CLASS_VERSION = 1;
    private const string UNIFORM_CLASS_SUFFIX = "UniformClassification_";

    // ===== SPLATMAP GROUP FILE FORMAT =====
    // Groups multiple per-cell splatmaps (from one original pre-subdivision terrain) into a single file.
    // Mirrors the CellGroup file grouping in CellFileBaking so file count doesn't explode with subdivisions.
    // Layout:
    //   [GroupHeader: 64 bytes]
    //   [SubCellEntry x subCellCount: 24 bytes each]
    //   [SubCell 0 data: full tier+data blob (same format as individual Splatmap_*.bytes)]
    //   [SubCell 1 data: ...]
    public const ulong SPLATMAP_GROUP_MAGIC = 0x3150475450545353; // "SSTPGS01" little-endian
    public const ushort SPLATMAP_GROUP_FORMAT_VERSION = 1;
    public const int SPLATMAP_GROUP_HEADER_SIZE = 64;
    public const int SPLATMAP_GROUP_SUBCELL_ENTRY_SIZE = 24;

    // ===== SETTINGS =====
    /// <summary>
    /// Defines the splatmap resolution for each LOD tier.
    /// Index = tier index, Value = target resolution (per heightmap cell, before adding border).
    /// Use 0 for "native resolution" (terrain's alphamapResolution / subdivisionsPowerOf2).
    /// Use positive values for absolute pixel counts (e.g., 256).
    /// Use negative fractions to specify resolution as a fraction of native (e.g., -2 = half, -4 = quarter).
    /// This fractional mode is used when heightmapSubdivisions changes the native cell size.
    /// </summary>
    [Serializable]
    public struct TextureBakeSettings
    {
        /// <summary>
        /// Per-tier target resolutions. Index 0 = closest chunks, last = farthest.
        ///  0  → native subdivided alphamap resolution (current default)
        ///  256  → absolute pixel count (256×256)
        /// -2  → half of native resolution, -4 = quarter, etc.
        /// Negative values are converted to absolute at bake time based on actual cell size.
        /// </summary>
        public int[] tierResolutions;
        /// </summary>
        public byte[] lodToTier;

        /// <summary>
        /// Resolution at which to bake terrain layer diffuse textures.
        /// These are shared and loaded once at startup, so can afford to be larger.
        /// Typical: 512 or 1024.
        /// </summary>
        public int layerTextureResolution;

        /// <summary>
        /// Whether to also bake normal maps for terrain layers (if they exist).
        /// </summary>
        public bool bakeNormalMaps;

        /// <summary>
        /// Border overlap in pixels at tier 0 resolution. Scaled down proportionally for lower tiers.
        /// 1 is sufficient for bilinear filtering. 2 provides safety for wider filter kernels.
        /// </summary>
        public int borderPixels;

        // ========== HEIGHTMAP-DERIVED NORMAL MAPS ==========
        // These are SEPARATE from layer normal maps (bakeNormalMaps above).
        // They store world-space surface normals derived from the heightmap geometry,
        // used in the shader for per-pixel NdotL (curvature) shading. They have their
        // OWN tier system that does NOT need to match splatmap tiers.

        /// <summary>
        /// Whether to bake heightmap-derived world-space normal maps (Phase 2 NdotL system).
        /// </summary>
        public bool bakeHeightmapNormals;

        /// <summary>
        /// Per-normal-tier output resolution. Index = normal tier index. Length = number of unique normal tiers.
        /// Example: [256, 128, 16, 8, 4] — 5 tiers from highest detail to lowest.
        /// </summary>
        public int[] normalTierResolutions;

        /// <summary>
        /// Which mesh LOD maps to which normal tier index. Length = maxLOD+1.
        /// Example for maxLOD=6 with normalTierResolutions of length 5: [0, 0, 1, 1, 2, 3, 4]
        /// means LOD 0-1 → tier 0 (256), LOD 2-3 → tier 1 (128), LOD 4 → tier 2 (16), etc.
        /// </summary>
        public byte[] lodToNormalTier;

        /// <summary>
        /// Border overlap in pixels for normal maps at tier 0 resolution.
        /// Same role as borderPixels for splatmaps — provides edge safety for filtering.
        /// </summary>
        public int normalBorderPixels;

        public static TextureBakeSettings Default(byte maxLOD)
        {
            // Build 4 tiers using fractional values relative to native cell size.
            // Negative values = fraction of the native cell size:
            //   -1 = full native, -2 = half, -4 = quarter, -8 = eighth
            int tierCount = 4;
            int[] resolutions = new int[] { -1, -2, -4, -8 };
            
            byte[] lodMap = new byte[maxLOD + 1];
            for (int i = 0; i <= maxLOD; i++)
            {
                // Distribute LODs evenly across tiers
                lodMap[i] = (byte)Mathf.Min(i / Mathf.Max(1, (maxLOD + 1) / tierCount), tierCount - 1);
            }

            // Default normal tier mapping: distribute mesh LODs across 4 normal tiers,
            // but skew toward higher detail at low LOD (closest chunks get sharpest normals).
            // For the user's example case (maxLOD=6), this produces something like
            // [0, 0, 1, 1, 2, 3, 3] → 4 tiers used.
            int normalTierCount = 4;
            int[] normalResolutions = new int[] { 256, 128, 64, 32 };
            byte[] normalLodMap = new byte[maxLOD + 1];
            int normalLodsPerTier = Mathf.Max(1, (maxLOD + 1) / normalTierCount);
            for (int i = 0; i <= maxLOD; i++)
                normalLodMap[i] = (byte)Mathf.Min(i / normalLodsPerTier, normalTierCount - 1);

            return new TextureBakeSettings
            {
                tierResolutions = resolutions,
                lodToTier = lodMap,
                layerTextureResolution = 512,
                bakeNormalMaps = false,
                borderPixels = 1,
                bakeHeightmapNormals = true,
                normalTierResolutions = normalResolutions,
                lodToNormalTier = normalLodMap,
                normalBorderPixels = 1
            };
        }
    }

    #if UNITY_EDITOR

    /// <summary>
    /// Bakes all splatmap data and terrain layer textures for both hemispheres.
    /// Optionally bakes heightmap-derived world-space normal maps if enabled in settings.
    /// Call from MeshSaver or a custom editor button.
    /// </summary>
    public static void BakeAll(
        Transform[] faceContainers,
        FaceContainerOrientation[] faceOrientations,
        int heightmapSubdivisionsPowerOf2,
        int numberOfChunks,
        sbyte minX,sbyte maxX,
        TextureBakeSettings settings,
        Vector3 sphereCenter = default,
        float sphereRadius = 0f)
    {
        string splatmapFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "Splatmaps");
        string layerFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "TerrainLayers");
        string normalFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "Normals");
        
        // Clean old splatmap files (and their .meta files) before rebaking to prevent stale data
        if (Directory.Exists(splatmapFolder))
        {
            string[] oldFiles = Directory.GetFiles(splatmapFolder, "Splatmap_*.bytes");
            string[] oldMetas = Directory.GetFiles(splatmapFolder, "Splatmap_*.bytes.meta");
            int deleteCount = oldFiles.Length + oldMetas.Length;
            if (deleteCount > 0)
            {
                Debug.Log($"[TextureBaker] Deleting {oldFiles.Length} old splatmap files + {oldMetas.Length} .meta files before rebake.");
                foreach (string f in oldFiles)
                    File.Delete(f);
                foreach (string f in oldMetas)
                    File.Delete(f);
            }
        }

        if (!Directory.Exists(splatmapFolder)) Directory.CreateDirectory(splatmapFolder);
        if (!Directory.Exists(layerFolder)) Directory.CreateDirectory(layerFolder);
        if (settings.bakeHeightmapNormals)
        {
            // Wipe stale normal files before rebake
            if (Directory.Exists(normalFolder))
            {
                string[] oldFiles = Directory.GetFiles(normalFolder, "Normal_*.bytes");
                string[] oldMetas = Directory.GetFiles(normalFolder, "Normal_*.bytes.meta");
                int deleteCount = oldFiles.Length + oldMetas.Length;
                if (deleteCount > 0)
                {
                    Debug.Log($"[TextureBaker] Deleting {oldFiles.Length} old normal files + {oldMetas.Length} .meta files before rebake.");
                    foreach (string f in oldFiles) File.Delete(f);
                    foreach (string f in oldMetas) File.Delete(f);
                }
            }
            if (!Directory.Exists(normalFolder)) Directory.CreateDirectory(normalFolder);
        }
        
        // Collect terrains for all 6 faces
        List<TerrainInfo>[] faceTerrains = new List<TerrainInfo>[6];
        int totalTerrainCount = 0;
        Terrain referenceTerrain = null;
        
        for (int f = 0; f < 6; f++)
        {
            FaceContainerOrientation orientation = faceOrientations != null && f < faceOrientations.Length
                ? faceOrientations[f]
                : FaceContainerOrientations.Get((FaceId)f);
            faceTerrains[f] = CollectTerrains(f < faceContainers.Length ? faceContainers[f] : null, orientation);
            totalTerrainCount += faceTerrains[f].Count;
            if (referenceTerrain == null && faceTerrains[f].Count > 0)
                referenceTerrain = faceTerrains[f][0].terrain;
        }
        
        if (totalTerrainCount == 0)
        {
            Debug.LogError("[TextureBaker] No terrains found under any face container.");
            return;
        }
        
        // Step 1: Bake terrain layer textures (shared across all terrains)
        BakeTerrainLayers(referenceTerrain.terrainData, layerFolder, settings);

        // NOTE: Bake-time overlay system (TreeOverlayShape, overlay stamping) has been removed
        // and replaced by the runtime canopy overlay system. See ChunkBatcher.Add() for
        // runtime per-vertex canopy marking with alpha smoothing on LOD1+ chunks.

        // Step 2: Bake splatmaps per heightmap cell for each face.
        // Collect raw byte blobs keyed by (face, origTerrainX, origTerrainY) for grouped file output.
        int totalCells = totalTerrainCount * heightmapSubdivisionsPowerOf2 * heightmapSubdivisionsPowerOf2;
        int processed = 0;
        // cellSplatmapBlobs[face][(origTX, origTY)] = list of (mapX, mapY, byte[])
        Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>>[] faceBlobs
            = new Dictionary<(int, int), List<(sbyte, sbyte, byte[])>>[6];
        for (int f = 0; f < 6; f++)
            faceBlobs[f] = new Dictionary<(int, int), List<(sbyte, sbyte, byte[])>>();

        // Per-face uniform classification: maps cell coords to dominant layer (-1 = non-uniform).
        Dictionary<(sbyte mx, sbyte my), sbyte>[] faceClassifications
            = new Dictionary<(sbyte, sbyte), sbyte>[6];
        for (int f = 0; f < 6; f++)
            faceClassifications[f] = new Dictionary<(sbyte, sbyte), sbyte>();

        for (int f = 0; f < 6; f++)
        {
            if (faceTerrains[f].Count > 0)
            {
                FaceContainerOrientation orientation = faceOrientations != null && f < faceOrientations.Length
                    ? faceOrientations[f]
                    : FaceContainerOrientations.Get((FaceId)f);
                BakeFaceCollect(faceTerrains[f], (FaceId)f, orientation, heightmapSubdivisionsPowerOf2,
                    minX, settings, splatmapFolder, ref processed, totalCells, faceBlobs[f],
                    faceClassifications[f]);
            }
        }

        // Write grouped splatmap files (one per original terrain per face), then delete individuals.
        for (int f = 0; f < 6; f++)
        {
            string side = FaceIdUtility.GetFilePrefix((FaceId)f);
            foreach (var kvp in faceBlobs[f])
            {
                var (tx, ty) = kvp.Key;
                var entries = kvp.Value;
                string groupPath = Path.Combine(splatmapFolder, $"SplatmapGroup_{side}_{tx}_{ty}.bytes");
                WriteSplatmapGroupFile(groupPath, entries);
            }
        }

        // Delete individual splatmap files (they've been superseded by groups)
        if (Directory.Exists(splatmapFolder))
        {
            string[] oldIndiv = Directory.GetFiles(splatmapFolder, "Splatmap_*.bytes");
            foreach (string f in oldIndiv) File.Delete(f);
        }

        // Write per-face uniform classification files for runtime use.
        string classFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "ClassData");
        if (!Directory.Exists(classFolder)) Directory.CreateDirectory(classFolder);
        for (int f = 0; f < 6; f++)
        {
            if (faceClassifications[f].Count == 0) continue;
            string side = FaceIdUtility.GetFilePrefix((FaceId)f);
            string classPath = Path.Combine(classFolder, $"{UNIFORM_CLASS_SUFFIX}{side}.bytes");
            WriteUniformClassificationFile(classPath, faceClassifications[f]);
        }

        // Step 3: Bake heightmap-derived world-space normal maps (Phase 2 NdotL system)
        if (settings.bakeHeightmapNormals)
        {
            if (sphereRadius <= 0f)
            {
                Debug.LogError("[TextureBaker] bakeHeightmapNormals=true but sphereRadius<=0. Skipping normal bake.");
            }
            else
            {
                ValidateNormalSettings(settings);

                int normalProcessed = 0;
                int normalTotalCells = totalCells;
                for (int f = 0; f < 6; f++)
                {
                    if (faceTerrains[f].Count > 0)
                    {
                        FaceContainerOrientation orientation = faceOrientations != null && f < faceOrientations.Length
                            ? faceOrientations[f]
                            : FaceContainerOrientations.Get((FaceId)f);
                        BakeFaceNormals(faceTerrains[f], (FaceId)f, heightmapSubdivisionsPowerOf2,
                            minX, settings, normalFolder, sphereCenter, sphereRadius,
                            orientation, ref normalProcessed, normalTotalCells);
                    }
                }

                WriteNormalMeta(normalFolder, settings);
            }
        }
        
        EditorUtility.ClearProgressBar();
        AssetDatabase.Refresh();
        
        // Log uniform splatmap statistics
        LogUniformSplatmapStats();
    }

    private static void ValidateNormalSettings(TextureBakeSettings settings)
    {
        if (settings.normalTierResolutions == null || settings.normalTierResolutions.Length == 0)
            throw new InvalidOperationException("[TextureBaker] normalTierResolutions is null/empty.");
        if (settings.lodToNormalTier == null || settings.lodToNormalTier.Length == 0)
            throw new InvalidOperationException("[TextureBaker] lodToNormalTier is null/empty.");
        int maxTierIdx = settings.normalTierResolutions.Length - 1;
        for (int i = 0; i < settings.lodToNormalTier.Length; i++)
        {
            if (settings.lodToNormalTier[i] > maxTierIdx)
                throw new InvalidOperationException(
                    $"[TextureBaker] lodToNormalTier[{i}]={settings.lodToNormalTier[i]} exceeds " +
                    $"max tier index {maxTierIdx} (normalTierResolutions length).");
        }
    }

    public struct TerrainInfo
    {
        public Terrain terrain;
        public sbyte gridX;
        public sbyte gridY;
    }

    private static List<TerrainInfo> CollectTerrains(Transform container, FaceContainerOrientation orientation)
    {
        List<TerrainInfo> result = new List<TerrainInfo>();
        if (container == null) return result;
        
        Terrain[] terrains = container.GetComponentsInChildren<Terrain>();
        if (terrains.Length == 0) return result;

        // Match MeshSaver.CollectTerrainsForFace: derive 0-based grid indices
        // relative to the face's own minimum terrain position.
        float terrainSize = terrains[0].terrainData.size.x;
        float minPosX = float.MaxValue;
        float minPosZ = float.MaxValue;
        foreach (Terrain t in terrains)
        {
            Vector3 pos = t.GetPosition();
            minPosX = Mathf.Min(minPosX, pos.x);
            minPosZ = Mathf.Min(minPosZ, pos.z);
        }

        int gridSize = Mathf.RoundToInt(Mathf.Sqrt(terrains.Length));

        foreach (Terrain t in terrains)
        {
            Vector3 pos = t.GetPosition();
            int worldGridX = Mathf.RoundToInt((pos.x - minPosX) / terrainSize);
            int worldGridY = Mathf.RoundToInt((pos.z - minPosZ) / terrainSize);
            FaceContainerOrientations.GridWorldToPlane(orientation, worldGridX, worldGridY, gridSize, out int gridX, out int gridY);

            result.Add(new TerrainInfo
            {
                terrain = t,
                gridX = (sbyte)gridX,
                gridY = (sbyte)gridY
            });
        }
        
        return result;
    }

    /// <summary>
    /// Extracts a heightmap cell's splatmap region from the full terrain alphamap,
    /// including border pixels for seam-free filtering.
    /// Returns float[y, x, layer] with dimensions (cellSize + 2*border)^2.
    /// Border pixels that fall outside the terrain are clamped to edge values.
    /// </summary>
    private static float[,,] ExtractCellSplatmap(
        float[,,] fullAlphamap,
        int cellX, int cellY,
        int cellSize,
        int fullRes,
        int layerCount,
        int border)
    {
        int outSize = cellSize + 2 * border;
        float[,,] result = new float[outSize, outSize, layerCount];
        
        int startX = cellX * cellSize - border;
        int startY = cellY * cellSize - border;
        
        for (int y = 0; y < outSize; y++)
        {
            int srcY = Mathf.Clamp(startY + y, 0, fullRes - 1);
            for (int x = 0; x < outSize; x++)
            {
                int srcX = Mathf.Clamp(startX + x, 0, fullRes - 1);
                for (int layer = 0; layer < layerCount; layer++)
                {
                    result[y, x, layer] = fullAlphamap[srcY, srcX, layer];
                }
            }
        }
        
        return result;
    }

     /// <summary>
    /// Downsamples a splatmap to target resolution using area averaging.
    /// This is the correct filter for weight maps — maintains the property that
    /// weights sum to 1.0 at each pixel (unlike point sampling which can alias).
    /// Input: float[srcH, srcW, layers], Output: float[dstH, dstW, layers]
    /// </summary>
    private static float[,,] DownsampleSplatmap(float[,,] source, int dstW, int dstH, int layerCount)
    {
        int srcH = source.GetLength(0);
        int srcW = source.GetLength(1);
        
        if (dstW == srcW && dstH == srcH)
        {
            // No downsampling needed — clone
            float[,,] clone = new float[srcH, srcW, layerCount];
            Array.Copy(source, clone, source.Length);
            return clone;
        }
        
        float[,,] result = new float[dstH, dstW, layerCount];

        float[] layerSums = new float[layerCount];
        
        float scaleX = (float)srcW / dstW;
        float scaleY = (float)srcH / dstH;
        
        for (int dy = 0; dy < dstH; dy++)
        {
            int syMin = (int)(dy  * scaleY);
            int syMax = Mathf.Min((int)((dy+1)* scaleY),srcH);
            if(syMax <= syMin) syMax = syMin + 1; // Ensure at least one row

            for(int dx = 0; dx < dstW; dx++)
            {
                int sxMin = (int)(dx * scaleX);
                int sxMax = Mathf.Min((int)((dx + 1) * scaleX),srcW);
                if(sxMax <= sxMin) sxMax = sxMin + 1;

                for(int l=0; l< layerCount; l++)
                    layerSums[l] = 0f;
                
                int count = 0;
                for(int sy = syMin; sy < syMax; sy++)
                {
                    for(int sx = sxMin; sx<sxMax; sx++)
                    {
                        for(int l=0; l < layerCount; l++)
                            layerSums[l] += source[sy, sx, l];
                        count++;
                    }
                }

                float invCount = 1f / count;
                for(int l=0; l< layerCount;l++)
                    result[dy, dx, l] = layerSums[l] * invCount;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Writes a single cell's splatmap data into the given stream (all tiers).
    /// The stream bytes are the same format as an individual Splatmap_*.bytes file.
    /// Returns the dominant layer index for uniform detection, or -1 if non-uniform.
    /// </summary>
    private static int BakeCellSplatmapToStream(
        float[,,] cellSplatmap,
        int layerCount,
        sbyte mapX, sbyte mapY,
        FaceId face,
        TextureBakeSettings settings,
        Stream output)
    {
        // Uniform detection on the original (pre-downsampled) splatmap.
        int dominantLayer = DetectUniformSplatmap(cellSplatmap, layerCount);

        using (var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            int tierCount = settings.tierResolutions.Length;
            int baseSrcH = cellSplatmap.GetLength(0);
            int baseSrcW = cellSplatmap.GetLength(1);

            // Pre-compute all tier data
            byte[][] tierPixelData = new byte[tierCount][];
            int[] tierResXs = new int[tierCount];
            int[] tierResYs = new int[tierCount];

            // Resolve fractional tier resolutions before the loop
            // Negative values represent fractions of the native cell size (e.g., -2 = half, -4 = quarter).
            // 0 = native, positive = absolute pixel count.
            int nativeCellSize = baseSrcW - 2 * settings.borderPixels; // core (non-border) cell pixels
            int[] resolvedTierRes = new int[tierCount];
            for (int t = 0; t < tierCount; t++)
            {
                int tr = settings.tierResolutions[t];
                if (tr < 0)
                    resolvedTierRes[t] = Mathf.Max(1, nativeCellSize / (-tr));
                else
                    resolvedTierRes[t] = tr;
            }

            // Uniform detection: check if the splatmap is dominated by a single layer.
            // The result is used downstream to write per-face classification files.
            if (dominantLayer >= 0)
            {
                int dlKey = (int)face * 100 + dominantLayer;
                // Per-layer stats tracked via face*100+layer key for bake log.
            }

            for (int tier = 0; tier < tierCount; tier++)
            {
                int targetRes = resolvedTierRes[tier];
                int dstW, dstH;

                if (targetRes <= 0)
                {
                    dstW = baseSrcW;
                    dstH = baseSrcH;
                }
                else
                {
                    int borderAtThisTier = Mathf.Max(1, settings.borderPixels * targetRes /
                        (resolvedTierRes[0] <= 0 ? baseSrcW - 2 * settings.borderPixels : resolvedTierRes[0]));
                    dstW = targetRes + 2 * borderAtThisTier;
                    dstH = targetRes + 2 * borderAtThisTier;
                }

                float[,,] tierSource = cellSplatmap;
                float[,,] downsampled = DownsampleSplatmap(tierSource, dstW, dstH, layerCount);

                byte[] pixels = new byte[dstW * dstH * layerCount];
                int idx = 0;
                for (int y = 0; y < dstH; y++)
                {
                    for (int x = 0; x < dstW; x++)
                    {
                        int remaining = 255;
                        for (int l = 0; l < layerCount - 1; l++)
                        {
                            int val = Mathf.RoundToInt(downsampled[y, x, l] * 255f);
                            val = Mathf.Clamp(val, 0, remaining);
                            pixels[idx++] = (byte)val;
                            remaining -= val;
                        }
                        pixels[idx++] = (byte)Mathf.Clamp(remaining, 0, 255);
                    }
                }

                tierPixelData[tier] = pixels;
                tierResXs[tier] = (ushort)dstW;
                tierResYs[tier] = (ushort)dstH;
            }

            // Data offsets
            int headerAndDescriptorSize = SPLATMAP_HEADER_SIZE + tierCount * TIER_DESCRIPTOR_SIZE;
            uint[] dataOffsets = new uint[tierCount];
            uint[] dataSizes = new uint[tierCount];
            uint currentOffset = (uint)headerAndDescriptorSize;
            for (int tier = 0; tier < tierCount; tier++)
            {
                dataOffsets[tier] = currentOffset;
                dataSizes[tier] = (uint)tierPixelData[tier].Length;
                currentOffset += dataSizes[tier];
            }

            uint checksum = ComputeCRC32(tierPixelData);

            // === HEADER (32 bytes) ===
            bw.Write(SPLATMAP_FORMAT_VERSION);
            bw.Write((byte)layerCount);
            bw.Write((byte)tierCount);
            bw.Write((ushort)tierResXs[0]);
            bw.Write((ushort)tierResYs[0]);
            bw.Write((byte)layerCount);
            bw.Write((byte)1);
            bw.Write((byte)0);
            bw.Write((byte)settings.borderPixels);
            bw.Write(checksum);
            bw.Write(new byte[16]);

            // === TIER DESCRIPTORS ===
            for (int tier = 0; tier < tierCount; tier++)
            {
                bw.Write((ushort)tierResXs[tier]);
                bw.Write((ushort)tierResYs[tier]);
                bw.Write(dataOffsets[tier]);
                bw.Write(dataSizes[tier]);
            }

            // === PIXEL DATA ===
            for (int tier = 0; tier < tierCount; tier++)
                bw.Write(tierPixelData[tier]);
        }
        return dominantLayer;
    }

    /// <summary>
    /// Public entry point used by MeshSaver to extract per-face splatmap data into memory
    /// (without writing files) before terrains are destroyed. Returns the blobs dictionary
    /// keyed by (origTerrainX, origTerrainY) and the per-cell classifications dictionary.
    /// </summary>
    public static (Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>> blobs,
                   Dictionary<(sbyte mx, sbyte my), sbyte> classifications)
        BakeFaceSplatmaps(
            List<TerrainInfo> terrains,
            FaceId face,
            FaceContainerOrientation orientation,
            int subdivisionsPow2,
            sbyte minX,
            TextureBakeSettings settings)
    {
        var blobs = new Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>>();
        var classifications = new Dictionary<(sbyte mx, sbyte my), sbyte>();

        int totalCells = terrains.Count * subdivisionsPow2 * subdivisionsPow2;
        int processed = 0;

        BakeFaceCollect(terrains, face, orientation, subdivisionsPow2, minX, settings,
            null,
            ref processed, totalCells, blobs, classifications);

        EditorUtility.ClearProgressBar();

        return (blobs, classifications);
    }

    /// <summary>
    /// Collects splatmap blobs per original terrain group from one face.
    /// Populates <paramref name="blobsByTerrain"/> keyed by (origTX, origTY).
    /// Also populates <paramref name="classifications"/> with per-cell uniform detection results.
    /// </summary>
    private static void BakeFaceCollect(
        List<TerrainInfo> terrains,
        FaceId face,
        FaceContainerOrientation orientation,
        int subdivisionsPow2,
        sbyte minX,
        TextureBakeSettings settings,
        string outputFolder,
        ref int processed,
        int totalCells,
        Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>> blobsByTerrain,
        Dictionary<(sbyte mx, sbyte my), sbyte> classifications)
    {
        string side = FaceIdUtility.GetFilePrefix(face);

        foreach (var terrainInfo in terrains)
        {
            Terrain terrain = terrainInfo.terrain;
            TerrainData td = terrain.terrainData;
            int alphamapRes = td.alphamapResolution;
            int layerCount = td.alphamapLayers;

            float[,,] fullAlphamap = FaceContainerOrientations.OrientAlphamaps(
                td.GetAlphamaps(0, 0, alphamapRes, alphamapRes),
                orientation,
                alphamapRes,
                layerCount);

            int cellsPerTerrainAxis = subdivisionsPow2;
            int cellAlphamapSize = alphamapRes / cellsPerTerrainAxis;

            // Compute orig terrain key (same formula as MeshSaver cell grouping)
            int origTX = terrainInfo.gridX;
            int origTY = terrainInfo.gridY;
            var key = (origTX, origTY);

            if (!blobsByTerrain.ContainsKey(key))
                blobsByTerrain[key] = new List<(sbyte, sbyte, byte[])>();

            for (int cy = 0; cy < cellsPerTerrainAxis; cy++)
            {
                for (int cx = 0; cx < cellsPerTerrainAxis; cx++)
                {
                    sbyte mapX = (sbyte)(minX + terrainInfo.gridX * subdivisionsPow2 + cx);
                    sbyte mapY = (sbyte)(minX + terrainInfo.gridY * subdivisionsPow2 + cy);

                    float[,,] cellSplatmap = ExtractCellSplatmap(
                        fullAlphamap, cx, cy, cellAlphamapSize,
                        alphamapRes, layerCount, settings.borderPixels);

                    // Bake to memory stream
                    byte[] blob;
                    sbyte dominantLayer;
                    using (var ms = new MemoryStream())
                    {
                        int dl = BakeCellSplatmapToStream(cellSplatmap, layerCount, mapX, mapY, face, settings, ms);
                        blob = ms.ToArray();
                        dominantLayer = (sbyte)dl;
                    }

                    // Store classification for uniform cell detection
                    classifications[(mapX, mapY)] = dominantLayer;

                    blobsByTerrain[key].Add((mapX, mapY, blob));

                    processed++;
                    if ((processed & 31) == 0 || processed == totalCells)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar(
                            "Baking Splatmaps",
                            $"[{side}] Cell ({mapX},{mapY}) - {processed}/{totalCells}",
                            (float)processed / totalCells))
                        {
                            EditorUtility.ClearProgressBar();
                            return;
                        }
                    }
                }
            }

            // Clear terrainData reference to release internal alphamap textures.
            // These are persistent native textures held by TerrainData until explicitly released.
            if (terrain != null)
            {
                terrain.terrainData = null;
            }
        }
    }

    /// <summary>
    /// Writes a grouped splatmap file containing multiple per-cell splatmaps from one original terrain.
    /// Format: 64-byte header + 24-byte subcell entries + concatenated cell blobs.
    /// </summary>
    private static void WriteSplatmapGroupFile(
        string outputPath,
        List<(sbyte mx, sbyte my, byte[] data)> entries)
    {
        string tempPath = outputPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                int count = entries.Count;
                uint dataStart = (uint)(SPLATMAP_GROUP_HEADER_SIZE + count * SPLATMAP_GROUP_SUBCELL_ENTRY_SIZE);
                uint cursor = dataStart;

                uint[] offsets = new uint[count];
                uint[] sizes = new uint[count];
                for (int i = 0; i < count; i++)
                {
                    offsets[i] = cursor;
                    sizes[i] = (uint)entries[i].data.Length;
                    cursor += sizes[i];
                }

                // Header
                bw.Write(SPLATMAP_GROUP_MAGIC);
                bw.Write(SPLATMAP_GROUP_FORMAT_VERSION);
                bw.Write((ushort)SPLATMAP_GROUP_HEADER_SIZE);
                bw.Write(0u); // flags
                bw.Write((ushort)count);
                bw.Write(new byte[64 - 8 - 2 - 2 - 4 - 2]); // pad to 64

                // Subcell entries: mapX(1) mapY(1) reserved(2) dataOffset(4) dataSize(4) reserved(12) = 24
                for (int i = 0; i < count; i++)
                {
                    bw.Write(entries[i].mx);
                    bw.Write(entries[i].my);
                    bw.Write((ushort)0); // reserved
                    bw.Write(offsets[i]);
                    bw.Write(sizes[i]);
                    bw.Write(0u); // reserved
                    bw.Write(0u); // reserved
                    bw.Write(0u); // reserved (total padding to 24)
                }

                // Data blobs
                for (int i = 0; i < count; i++)
                    bw.Write(entries[i].data);
            }

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
    /// Writes a per-face uniform classification file for runtime use.
    /// Format: magic(8) + version(2) + cellCount(4) + [dominantLayer(1)] * cellCount.
    /// Each sbyte is the dominant layer index (0..3) for uniform cells, or -1 for non-uniform.
    /// </summary>
    private static void WriteUniformClassificationFile(
        string outputPath,
        Dictionary<(sbyte mx, sbyte my), sbyte> classifications)
    {
        string tempPath = outputPath + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(UNIFORM_CLASS_MAGIC);
                bw.Write(UNIFORM_CLASS_VERSION);
                bw.Write(classifications.Count); // cellCount

                foreach (var kvp in classifications)
                {
                    bw.Write(kvp.Key.mx);
                    bw.Write(kvp.Key.my);
                    bw.Write(kvp.Value);
                }
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);

            int uniformCount = classifications.Values.Count(v => v >= 0);
            int totalCount = classifications.Count;
            Debug.Log($"[TextureBaker] Wrote {outputPath}: {uniformCount}/{totalCount} " +
                      $"({(float)uniformCount / totalCount * 100f:F1}%) uniform cells.");
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes all cached per-face splatmap groups and uniform classification files to disk.
    /// Called by MeshSaver after terrains have been destroyed (data lives in the cached dictionaries).
    /// </summary>
    public static void WriteCachedSplatmaps(
        Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>>[] faceBlobs,
        Dictionary<(sbyte mx, sbyte my), sbyte>[] faceClassifications)
    {
        string splatmapFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "Splatmaps");
        if (!Directory.Exists(splatmapFolder)) Directory.CreateDirectory(splatmapFolder);

        for (int f = 0; f < 6; f++)
        {
            if (faceBlobs[f] == null || faceBlobs[f].Count == 0) continue;
            string side = FaceIdUtility.GetFilePrefix((FaceId)f);
            foreach (var kvp in faceBlobs[f])
            {
                var (tx, ty) = kvp.Key;
                var entries = kvp.Value;
                string groupPath = Path.Combine(splatmapFolder, $"SplatmapGroup_{side}_{tx}_{ty}.bytes");
                WriteSplatmapGroupFile(groupPath, entries);
            }
        }

        if (Directory.Exists(splatmapFolder))
        {
            string[] oldIndiv = Directory.GetFiles(splatmapFolder, "Splatmap_*.bytes");
            foreach (string f in oldIndiv) File.Delete(f);
        }

        string classFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "ClassData");
        if (!Directory.Exists(classFolder)) Directory.CreateDirectory(classFolder);
        for (int f = 0; f < 6; f++)
        {
            if (faceClassifications[f] == null || faceClassifications[f].Count == 0) continue;
            string side = FaceIdUtility.GetFilePrefix((FaceId)f);
            string classPath = Path.Combine(classFolder, $"{UNIFORM_CLASS_SUFFIX}{side}.bytes");
            WriteUniformClassificationFile(classPath, faceClassifications[f]);
        }

        Debug.Log("[TextureBaker] WriteCachedSplatmaps complete.");
    }

    /// <summary>
    /// Writes a single face's cached splatmap groups and uniform classification file to disk.
    /// Call this per-face immediately after extraction so memory can be freed.
    /// </summary>
    public static void WriteFaceSplatmaps(
        int faceIndex,
        Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>> blobs,
        Dictionary<(sbyte mx, sbyte my), sbyte> classifications)
    {
        string splatmapFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "Splatmaps");
        if (!Directory.Exists(splatmapFolder)) Directory.CreateDirectory(splatmapFolder);

        string side = FaceIdUtility.GetFilePrefix((FaceId)faceIndex);
        foreach (var kvp in blobs)
        {
            var (tx, ty) = kvp.Key;
            var entries = kvp.Value;
            string groupPath = Path.Combine(splatmapFolder, $"SplatmapGroup_{side}_{tx}_{ty}.bytes");
            WriteSplatmapGroupFile(groupPath, entries);
        }

        if (Directory.Exists(splatmapFolder))
        {
            string[] oldIndiv = Directory.GetFiles(splatmapFolder, "Splatmap_*.bytes");
            foreach (string f in oldIndiv) File.Delete(f);
        }

        if (classifications != null && classifications.Count > 0)
        {
            string classFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "ClassData");
            if (!Directory.Exists(classFolder)) Directory.CreateDirectory(classFolder);
            string classPath = Path.Combine(classFolder, $"{UNIFORM_CLASS_SUFFIX}{side}.bytes");
            WriteUniformClassificationFile(classPath, classifications);
        }
    }

    /// <summary>
    /// Detects if a splatmap is "uniform" - dominated by a single layer.
    /// Returns the dominant layer index, or -1 if not uniform.
    /// A splatmap is uniform if one layer covers >= UNIFORM_THRESHOLD of the area.
    /// </summary>
    private static int DetectUniformSplatmap(float[,,] splatmap, int layerCount)
    {
        int height = splatmap.GetLength(0);
        int width = splatmap.GetLength(1);
        int totalPixels = height * width;
        
        // Sum all weights per layer
        float[] layerTotals = new float[layerCount];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int l = 0; l < layerCount; l++)
                {
                    layerTotals[l] += splatmap[y, x, l];
                }
            }
        }
        
        // Find dominant layer
        int dominantLayer = -1;
        float maxTotal = 0f;
        for (int l = 0; l < layerCount; l++)
        {
            if (layerTotals[l] > maxTotal)
            {
                maxTotal = layerTotals[l];
                dominantLayer = l;
            }
        }
        
        // Check if dominant layer covers enough area
        // Splatmap weights are in [0,1] and sum to 1.0 per pixel
        // For uniform: maxTotal should equal totalPixels (each pixel has weight 1.0 for dominant layer)
        float coverage = maxTotal / totalPixels;
        return coverage >= UNIFORM_THRESHOLD ? dominantLayer : -1;
    }

    /// <summary>
    /// Logs uniform splatmap statistics at the end of baking.
    /// Now aggregated from per-face classification data.
    /// </summary>
    private static void LogUniformSplatmapStats() { }

     /// <summary>
    /// Bakes terrain layer diffuse (and optionally normal) textures to files,
    /// plus metadata about tiling and offsets.
    /// Accepts a TerrainData reference (live terrain) and extracts layers from it.
    /// </summary>
    public static void BakeTerrainLayers(
        TerrainData terrainData,
        string outputFolder,
        TextureBakeSettings settings)
    {
        TerrainLayer[] layers = terrainData != null ? terrainData.terrainLayers : null;
        BakeTerrainLayers(layers, outputFolder, settings);
    }

    /// <summary>
    /// Bakes terrain layer diffuse (and optionally normal) textures to files,
    /// plus metadata about tiling and offsets.
    /// Accepts a pre-extracted TerrainLayer[] array (no live TerrainData needed).
    /// </summary>
    public static void BakeTerrainLayers(
        TerrainLayer[] layers,
        string outputFolder,
        TextureBakeSettings settings)
    {
        if (layers == null || layers.Length == 0)
        {
            Debug.LogWarning("[TextureBaker] No terrain layers available.");
            return;
        }
        
        // === Write layer metadata ===
        string metaPath = Path.Combine(outputFolder, "LayerMeta.bytes");
        using (var fs = new FileStream(metaPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(LAYER_FORMAT_VERSION);          // uint16
            bw.Write((byte)layers.Length);            // uint8
            bw.Write((ushort)settings.layerTextureResolution); // uint16 - resolution all layer textures are baked at
            bw.Write(settings.bakeNormalMaps ? (byte)1 : (byte)0); // uint8
            bw.Write(new byte[10]);                  // reserved
            
            for (int i = 0; i < layers.Length; i++)
            {
                TerrainLayer layer = layers[i];
                
                // Tiling
                bw.Write(layer.tileSize.x);          // float
                bw.Write(layer.tileSize.y);          // float
                bw.Write(layer.tileOffset.x);        // float
                bw.Write(layer.tileOffset.y);        // float
                
                // Metallic / smoothness (for future PBR)
                bw.Write(layer.metallic);            // float
                bw.Write(layer.smoothness);          // float
                
                // Layer name for debugging
                string layerName = layer.name ?? $"Layer_{i}";
                bw.Write(layerName);                 // string (BinaryWriter length-prefixed)
            }
        }
        
        // === Bake each layer's diffuse texture ===
        int res = settings.layerTextureResolution;
        
        for (int i = 0; i < layers.Length; i++)
        {
            TerrainLayer layer = layers[i];
            
            // Diffuse
            if (layer.diffuseTexture != null)
            {
                byte[] rgba = BakeTextureToRGBA(layer.diffuseTexture, res, res, isLinear: false); // Diffuse is sRGB
                string diffusePath = Path.Combine(outputFolder, $"Layer_{i}_diffuse.bytes");
                File.WriteAllBytes(diffusePath, rgba);
            }
            else
            {
                Debug.LogWarning($"[TextureBaker] Layer {i} ({layer.name}) has no diffuse texture.");
            }
            
            // Normal (optional)
            if (settings.bakeNormalMaps && layer.normalMapTexture != null)
            {
                byte[] rgba = BakeTextureToRGBA(layer.normalMapTexture, res, res, isLinear: true); // Normal is Linear
                string normalPath = Path.Combine(outputFolder, $"Layer_{i}_normal.bytes");
                File.WriteAllBytes(normalPath, rgba);
            }
            
            EditorUtility.DisplayProgressBar("Baking Layer Textures",
                $"Layer {i}/{layers.Length}: {layer.name}", (float)i / layers.Length);
        }

        EditorUtility.ClearProgressBar();

        // Release texture memory WITHOUT nulling the persistent references on the
        // actual TerrainLayer assets. Setting diffuseTexture=null modifies the .terrainlayer
        // asset file, permanently breaking the texture link until manually re-assigned.
        // Instead, unload the texture from memory while keeping the reference intact.
        for (int i = 0; i < layers.Length; i++)
        {
            if (layers[i] != null)
            {
                if (layers[i].diffuseTexture != null)
                    Resources.UnloadAsset(layers[i].diffuseTexture);
                if (layers[i].normalMapTexture != null)
                    Resources.UnloadAsset(layers[i].normalMapTexture);
            }
        }
    }

    /// <summary>
    /// Reads a texture at any compression/format and re-renders it to a target resolution
    /// as raw RGBA32 bytes. Uses RenderTexture blit for format-agnostic reading.
    /// </summary>
    private static byte[] BakeTextureToRGBA(Texture2D source, int width, int height, bool isLinear)
    {
        // We cannot always read pixels directly from compressed/GPU textures.
        // Blit to a temporary RenderTexture, then ReadPixels into a clean Texture2D.
        
        var readWriteFlag = isLinear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
        RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWriteFlag);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        
        // Blit source to RT (handles format conversion and resize)
        Graphics.Blit(source, rt);
        
        // Read back
        Texture2D readable = new Texture2D(width, height, TextureFormat.RGBA32, isLinear);
        readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        readable.Apply();
        
        // Copy the data BEFORE destroying the texture - GetRawTextureData returns a reference
        // that becomes invalid once the texture is destroyed
        byte[] rawBytes = new byte[width * height * 4];
        readable.GetRawTextureData().CopyTo(rawBytes, 0);
        
        // Cleanup
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        UnityEngine.Object.DestroyImmediate(readable);
        
        return rawBytes;
    }

    /// <summary>
    /// For cells at terrain edges, patches border pixels by sampling from neighboring terrains.
    /// Call after initial extraction to fix edges that were clamped.
    /// </summary>
    public static void PatchBordersFromNeighbors(
        float[,,] cellSplatmap,
        int cellSize,
        int border,
        int layerCount,
        float[,,] neighborLeft,   // null if no neighbor
        float[,,] neighborRight,
        float[,,] neighborUp,
        float[,,] neighborDown)
    {
        int outSize = cellSize + 2 * border;
        
        // Left border: columns [0, border)
        if (neighborLeft != null)
        {
            int neighborW = neighborLeft.GetLength(1);
            for (int y = 0; y < outSize; y++)
            {
                int srcY = Mathf.Clamp(y, 0, neighborLeft.GetLength(0) - 1);
                for (int x = 0; x < border; x++)
                {
                    int srcX = neighborW - border + x;
                    srcX = Mathf.Clamp(srcX, 0, neighborW - 1);
                    for (int l = 0; l < layerCount; l++)
                        cellSplatmap[y, x, l] = neighborLeft[srcY, srcX, l];
                }
            }
        }
        
        // Right border: columns [outSize - border, outSize)
        if (neighborRight != null)
        {
            for (int y = 0; y < outSize; y++)
            {
                int srcY = Mathf.Clamp(y, 0, neighborRight.GetLength(0) - 1);
                for (int x = 0; x < border; x++)
                {
                    int dstX = outSize - border + x;
                    int srcX = Mathf.Clamp(x, 0, neighborRight.GetLength(1) - 1);
                    for (int l = 0; l < layerCount; l++)
                        cellSplatmap[y, dstX, l] = neighborRight[srcY, srcX, l];
                }
            }
        }
        
        // Bottom border: rows [0, border)
        if (neighborDown != null)
        {
            int neighborH = neighborDown.GetLength(0);
            for (int y = 0; y < border; y++)
            {
                int srcY = neighborH - border + y;
                srcY = Mathf.Clamp(srcY, 0, neighborH - 1);
                for (int x = 0; x < outSize; x++)
                {
                    int srcX = Mathf.Clamp(x, 0, neighborDown.GetLength(1) - 1);
                    for (int l = 0; l < layerCount; l++)
                        cellSplatmap[y, x, l] = neighborDown[srcY, srcX, l];
                }
            }
        }
        
        // Top border: rows [outSize - border, outSize)
        if (neighborUp != null)
        {
            for (int y = 0; y < border; y++)
            {
                int dstY = outSize - border + y;
                int srcY = Mathf.Clamp(y, 0, neighborUp.GetLength(0) - 1);
                for (int x = 0; x < outSize; x++)
                {
                    int srcX = Mathf.Clamp(x, 0, neighborUp.GetLength(1) - 1);
                    for (int l = 0; l < layerCount; l++)
                        cellSplatmap[dstY, x, l] = neighborUp[srcY, srcX, l];
                }
            }
        }
    }

    // ===== CRC32 UTILITY =====
    private static readonly uint[] crc32Table;
    static TextureBaker()
    {
        crc32Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            crc32Table[i] = crc;
        }
    }

    private static uint ComputeCRC32(byte[][] dataSets)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte[] data in dataSets)
        {
            for (int i = 0; i < data.Length; i++)
                crc = (crc >> 8) ^ crc32Table[(crc ^ data[i]) & 0xFF];
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Validates that all terrains share the same terrain layers.
    /// Call before bake to catch configuration errors.
    /// </summary>
    public static bool ValidateTerrainLayers(Transform[] faceContainers)
    {
        TerrainLayer[] reference = null;
        
        foreach (Transform container in faceContainers)
        {
            if (container == null) continue;
            foreach (Transform child in container)
            {
                Terrain t = child.GetComponent<Terrain>();
                if (t == null) continue;
                
                TerrainLayer[] layers = t.terrainData.terrainLayers;
                if (reference == null)
                {
                    reference = layers;
                    continue;
                }
                
                if (layers.Length != reference.Length)
                {
                    Debug.LogError($"[TextureBaker] Terrain {t.name} has {layers.Length} layers but reference has {reference.Length}. All terrains must share the same layers.");
                    return false;
                }
                
                for (int i = 0; i < layers.Length; i++)
                {
                    if (layers[i] != reference[i])
                    {
                        Debug.LogError($"[TextureBaker] Terrain {t.name} layer {i} differs from reference terrain. All terrains must share the same layer set.");
                        return false;
                    }
                }
            }
        }
        
        if (reference == null)
        {
            Debug.LogError("[TextureBaker] No terrains found.");
            return false;
        }
        
        return true;
    }

    // ===========================================================================
    // ===== HEIGHTMAP-DERIVED WORLD-SPACE NORMAL MAPS (Phase 2 NdotL system) ====
    // ===========================================================================

    private const ushort NORMAL_FORMAT_VERSION = 1;
    private const ushort NORMAL_META_VERSION = 1;
    // Same layout as splatmap (header 32 + tier descriptors 12 each), but channels=3 (RGB8).
    private const int NORMAL_HEADER_SIZE = 32;
    private const int NORMAL_TIER_DESCRIPTOR_SIZE = 12;

    /// <summary>
    /// Bakes world-space normal maps for every cell on a face, at every unique normal tier resolution.
    /// Combines all terrain heightmaps into a single contiguous face height grid for cross-cell sampling,
    /// then projects each output texel onto the sphere and computes the surface normal via finite differences.
    /// </summary>
    private static void BakeFaceNormals(
        List<TerrainInfo> terrains,
        FaceId face,
        int subdivisionsPow2,
        sbyte minX,
        TextureBakeSettings settings,
        string outputFolder,
        Vector3 sphereCenter,
        float sphereRadius,
        FaceContainerOrientation orientation,
        ref int processed,
        int totalCells)
    {
        string side = FaceIdUtility.GetFilePrefix(face);

        // Build a single contiguous height grid for the entire face.
        BuildFaceHeightGrid(terrains, out float[,] faceHeights, out float terrainSize,
            out int terrainGridSize, out int heightRes, out float bakedMaxHeight, orientation);
        if (faceHeights == null) return;

        float faceWorldSize = terrainGridSize * terrainSize;
        float pixelDistance = terrainSize / (heightRes - 1);
        int facePixelsPerAxis = terrainGridSize * (heightRes - 1) + 1;

        // Precompute face axes once.
        FaceIdUtility.GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);

        // One cell occupies cellSize plane units = faceWorldSize / cellsPerAxis.
        int cellsPerFaceAxis = terrainGridSize * subdivisionsPow2;
        float cellSize = faceWorldSize / cellsPerFaceAxis;

        foreach (var terrainInfo in terrains)
        {
            for (int cy = 0; cy < subdivisionsPow2; cy++)
            {
                for (int cx = 0; cx < subdivisionsPow2; cx++)
                {
                    sbyte mapX = (sbyte)(minX + terrainInfo.gridX * subdivisionsPow2 + cx);
                    sbyte mapY = (sbyte)(minX + terrainInfo.gridY * subdivisionsPow2 + cy);

                    int cellI = terrainInfo.gridX * subdivisionsPow2 + cx;
                    int cellJ = terrainInfo.gridY * subdivisionsPow2 + cy;
                    float cellPlaneStartX = cellI * cellSize;
                    float cellPlaneStartY = cellJ * cellSize;

                    BakeCellNormalsToFile(
                        face, mapX, mapY,
                        cellPlaneStartX, cellPlaneStartY, cellSize,
                        faceHeights, facePixelsPerAxis, pixelDistance, bakedMaxHeight,
                        faceWorldSize, sphereCenter, sphereRadius,
                        localUp, axisA, axisB,
                        settings, outputFolder);

                    processed++;
                    // Throttle progress bar updates (see BakeFace for rationale).
                    if ((processed & 31) == 0 || processed == totalCells)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar(
                            "Baking Heightmap Normals",
                            $"[{side}] Cell ({mapX},{mapY}) - {processed}/{totalCells}",
                            (float)processed / totalCells))
                        {
                            EditorUtility.ClearProgressBar();
                            return;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Combines per-terrain heightmaps into a single contiguous grid covering the whole face.
    /// Output values are world-space heights (terrain.size.y * normalized height in [0,1]).
    /// Shared edge samples are kept consistent (last column/row of one terrain == first of the next).
    /// </summary>
    private static void BuildFaceHeightGrid(
        List<TerrainInfo> terrains,
        out float[,] faceHeights,
        out float terrainSize,
        out int terrainGridSize,
        out int heightRes,
        out float bakedMaxHeight,
        FaceContainerOrientation orientation)
    {
        faceHeights = null; terrainSize = 0f; terrainGridSize = 0; heightRes = 0; bakedMaxHeight = 0f;
        if (terrains.Count == 0) return;

        TerrainData td0 = terrains[0].terrain.terrainData;
        terrainSize = td0.size.x;
        bakedMaxHeight = td0.size.y;
        heightRes = td0.heightmapResolution;
        terrainGridSize = Mathf.RoundToInt(Mathf.Sqrt(terrains.Count));

        int facePixelsPerAxis = terrainGridSize * (heightRes - 1) + 1;
        faceHeights = new float[facePixelsPerAxis, facePixelsPerAxis];

        foreach (var ti in terrains)
        {
            TerrainData td = ti.terrain.terrainData;
            float[,] localHeights = FaceContainerOrientations.OrientHeights(
                td.GetHeights(0, 0, heightRes, heightRes),
                orientation,
                heightRes);
            int baseX = ti.gridX * (heightRes - 1);
            int baseY = ti.gridY * (heightRes - 1);
            for (int y = 0; y < heightRes; y++)
            {
                int dstY = baseY + y;
                for (int x = 0; x < heightRes; x++)
                {
                    int dstX = baseX + x;
                    faceHeights[dstY, dstX] = localHeights[y, x] * bakedMaxHeight;
                }
            }
        }
    }

    /// <summary>
    /// Bilinear-samples world-space height at a fractional pixel position in the face grid.
    /// Clamps to grid edges (terrain edges are face boundaries — no extrapolation needed).
    /// </summary>
    private static float SampleFaceHeightBilinear(float[,] faceHeights, int facePixels, float fx, float fy)
    {
        float cx = Mathf.Clamp(fx, 0f, facePixels - 1f);
        float cy = Mathf.Clamp(fy, 0f, facePixels - 1f);
        int x0 = (int)cx; int y0 = (int)cy;
        int x1 = Mathf.Min(x0 + 1, facePixels - 1);
        int y1 = Mathf.Min(y0 + 1, facePixels - 1);
        float tx = cx - x0; float ty = cy - y0;
        float h00 = faceHeights[y0, x0];
        float h10 = faceHeights[y0, x1];
        float h01 = faceHeights[y1, x0];
        float h11 = faceHeights[y1, x1];
        float h0 = h00 + (h10 - h00) * tx;
        float h1 = h01 + (h11 - h01) * tx;
        return h0 + (h1 - h0) * ty;
    }

    /// <summary>
    /// Writes one cell's normal file containing every normal tier as RGB8 (world-space normal*0.5+0.5).
    /// </summary>
    private static void BakeCellNormalsToFile(
        FaceId face, sbyte mapX, sbyte mapY,
        float cellPlaneStartX, float cellPlaneStartY, float cellSize,
        float[,] faceHeights, int facePixelsPerAxis, float pixelDistance, float bakedMaxHeight,
        float faceWorldSize, Vector3 sphereCenter, float sphereRadius,
        Vector3 localUp, Vector3 axisA, Vector3 axisB,
        TextureBakeSettings settings, string outputFolder)
    {
        string side = FaceIdUtility.GetFilePrefix(face);
        string filename = $"Normal_{side}_{mapX}_{mapY}.bytes";
        string path = Path.Combine(outputFolder, filename);

        int tierCount = settings.normalTierResolutions.Length;
        int border = Mathf.Max(0, settings.normalBorderPixels);

        byte[][] tierPixelData = new byte[tierCount][];
        ushort[] tierResXs = new ushort[tierCount];
        ushort[] tierResYs = new ushort[tierCount];

        // Gradient step in plane units used for finite-difference normal computation.
        // Half a heightmap pixel keeps the gradient bandlimited to the source heightmap's
        // Nyquist frequency, preventing the normal map from inventing detail above the source.
        float gradStepPlane = 0.5f * pixelDistance;

        // Hoisted invariants. ProjectPlanePointToSurface used to recompute these per call.
        float invPixelDistance = pixelDistance > 0f ? 1f / pixelDistance : 0f;
        float twoOverFaceSize = faceWorldSize > 0f ? 2f / faceWorldSize : 0f;
        int facePixelsMinusOne = facePixelsPerAxis - 1;
        float upX = localUp.x, upY = localUp.y, upZ = localUp.z;
        float aAx = axisA.x, aAy = axisA.y, aAz = axisA.z;
        float aBx = axisB.x, aBy = axisB.y, aBz = axisB.z;
        float scX = sphereCenter.x, scY = sphereCenter.y, scZ = sphereCenter.z;

        for (int tier = 0; tier < tierCount; tier++)
        {
            int R = settings.normalTierResolutions[tier];
            int totalSize = R + 2 * border;
            float pixelPlane = cellSize / R;

            byte[] pixels = new byte[totalSize * totalSize * 3];
            int idx = 0;

            for (int py = 0; py < totalSize; py++)
            {
                float planeY = cellPlaneStartY + (py - border + 0.5f) * pixelPlane;
                for (int px = 0; px < totalSize; px++)
                {
                    float planeX = cellPlaneStartX + (px - border + 0.5f) * pixelPlane;

                    // Sample 4 neighbors in plane space (inlined ProjectPlanePointToSurface).
                    float pxmX, pxmY, pxmZ;
                    float pxpX, pxpY, pxpZ;
                    float pymX, pymY, pymZ;
                    float pypX, pypY, pypZ;

                    ProjectInline(planeX - gradStepPlane, planeY,
                        faceHeights, facePixelsMinusOne, invPixelDistance, twoOverFaceSize,
                        sphereRadius, scX, scY, scZ, upX, upY, upZ, aAx, aAy, aAz, aBx, aBy, aBz,
                        out pxmX, out pxmY, out pxmZ);
                    ProjectInline(planeX + gradStepPlane, planeY,
                        faceHeights, facePixelsMinusOne, invPixelDistance, twoOverFaceSize,
                        sphereRadius, scX, scY, scZ, upX, upY, upZ, aAx, aAy, aAz, aBx, aBy, aBz,
                        out pxpX, out pxpY, out pxpZ);
                    ProjectInline(planeX, planeY - gradStepPlane,
                        faceHeights, facePixelsMinusOne, invPixelDistance, twoOverFaceSize,
                        sphereRadius, scX, scY, scZ, upX, upY, upZ, aAx, aAy, aAz, aBx, aBy, aBz,
                        out pymX, out pymY, out pymZ);
                    ProjectInline(planeX, planeY + gradStepPlane,
                        faceHeights, facePixelsMinusOne, invPixelDistance, twoOverFaceSize,
                        sphereRadius, scX, scY, scZ, upX, upY, upZ, aAx, aAy, aAz, aBx, aBy, aBz,
                        out pypX, out pypY, out pypZ);

                    float txX = pxpX - pxmX, txY = pxpY - pxmY, txZ = pxpZ - pxmZ;
                    float tyX = pypX - pymX, tyY = pypY - pymY, tyZ = pypZ - pymZ;

                    float nX = txY * tyZ - txZ * tyY;
                    float nY = txZ * tyX - txX * tyZ;
                    float nZ = txX * tyY - txY * tyX;

                    float magSq = nX * nX + nY * nY + nZ * nZ;
                    if (magSq <= 1e-16f)
                    {
                        // Degenerate — fall back to face-local up (radial outward from sphere center
                        // for any point on this face is within ~45° of localUp).
                        nX = upX; nY = upY; nZ = upZ;
                    }
                    else
                    {
                        float invMag = 1f / Mathf.Sqrt(magSq);
                        nX *= invMag; nY *= invMag; nZ *= invMag;
                        // Make sure the normal points outward. We use the average of the 4 already-
                        // projected neighbor points as a cheap surrogate for the center surface point,
                        // then dot against (avg - sphereCenter) — the true radial outward direction.
                        // Using localUp here is incorrect: on steep slopes near face corners the
                        // normal can deviate >90° from localUp even when correct, which would flip
                        // it and reverse NdotL shading.
                        float avgX = 0.25f * (pxmX + pxpX + pymX + pypX) - scX;
                        float avgY = 0.25f * (pxmY + pxpY + pymY + pypY) - scY;
                        float avgZ = 0.25f * (pxmZ + pxpZ + pymZ + pypZ) - scZ;
                        if (nX * avgX + nY * avgY + nZ * avgZ < 0f)
                        {
                            nX = -nX; nY = -nY; nZ = -nZ;
                        }
                    }

                    // Encode world-space normal RGB8 ([-1,1] → [0,255]) using a fast clamp+round.
                    int r = (int)((nX * 0.5f + 0.5f) * 255f + 0.5f);
                    int g = (int)((nY * 0.5f + 0.5f) * 255f + 0.5f);
                    int b = (int)((nZ * 0.5f + 0.5f) * 255f + 0.5f);
                    if (r < 0) r = 0; else if (r > 255) r = 255;
                    if (g < 0) g = 0; else if (g > 255) g = 255;
                    if (b < 0) b = 0; else if (b > 255) b = 255;
                    pixels[idx++] = (byte)r;
                    pixels[idx++] = (byte)g;
                    pixels[idx++] = (byte)b;
                }
            }

            tierPixelData[tier] = pixels;
            tierResXs[tier] = (ushort)totalSize;
            tierResYs[tier] = (ushort)totalSize;
        }

        // Layout: header (32) + tierCount * descriptors (12) + raw RGB8 pixel data per tier.
        int headerAndDescriptorSize = NORMAL_HEADER_SIZE + tierCount * NORMAL_TIER_DESCRIPTOR_SIZE;
        uint[] dataOffsets = new uint[tierCount];
        uint[] dataSizes = new uint[tierCount];
        uint currentOffset = (uint)headerAndDescriptorSize;
        for (int tier = 0; tier < tierCount; tier++)
        {
            dataOffsets[tier] = currentOffset;
            dataSizes[tier] = (uint)tierPixelData[tier].Length;
            currentOffset += dataSizes[tier];
        }
        uint checksum = ComputeCRC32(tierPixelData);

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            // === HEADER (32 bytes) — same shape as splatmap, channels=3 ===
            bw.Write(NORMAL_FORMAT_VERSION);    // uint16 - offset 0
            bw.Write((byte)1);                  // uint8  - offset 2 (layerCount = 1, single normal map)
            bw.Write((byte)tierCount);          // uint8  - offset 3
            bw.Write(tierResXs[0]);             // uint16 - offset 4
            bw.Write(tierResYs[0]);             // uint16 - offset 6
            bw.Write((byte)3);                  // uint8  - offset 8 (channels per pixel = RGB)
            bw.Write((byte)1);                  // uint8  - offset 9 (bytes per channel)
            bw.Write((byte)0);                  // uint8  - offset 10 (compression flags)
            bw.Write((byte)border);             // uint8  - offset 11 (border pixels at tier 0)
            bw.Write(checksum);                 // uint32 - offset 12
            bw.Write(new byte[16]);             // 16 bytes reserved - offset 16..31

            // === TIER DESCRIPTORS ===
            for (int tier = 0; tier < tierCount; tier++)
            {
                bw.Write(tierResXs[tier]);
                bw.Write(tierResYs[tier]);
                bw.Write(dataOffsets[tier]);
                bw.Write(dataSizes[tier]);
            }

            // === PIXEL DATA ===
            for (int tier = 0; tier < tierCount; tier++)
                bw.Write(tierPixelData[tier]);
        }
    }

    /// <summary>
    /// Aggressively-inlined per-pixel surface projection for the normal bake. Equivalent to
    /// ProjectPlanePointToSurface but avoids method-call overhead, redundant Vector3 allocation,
    /// and the per-pixel division `1/pixelDistance` and `1/faceWorldSize` that the call-based
    /// version performed. Bilinear-samples height directly from <paramref name="faceHeights"/>.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ProjectInline(
        float planeX, float planeY,
        float[,] faceHeights, int facePixelsMinusOne,
        float invPixelDistance, float twoOverFaceSize,
        float sphereRadius,
        float scX, float scY, float scZ,
        float upX, float upY, float upZ,
        float aAx, float aAy, float aAz,
        float aBx, float aBy, float aBz,
        out float ox, out float oy, out float oz)
    {
        // Bilinear height sample.
        float fx = planeX * invPixelDistance;
        float fy = planeY * invPixelDistance;
        if (fx < 0f) fx = 0f; else if (fx > facePixelsMinusOne) fx = facePixelsMinusOne;
        if (fy < 0f) fy = 0f; else if (fy > facePixelsMinusOne) fy = facePixelsMinusOne;
        int x0 = (int)fx; int y0 = (int)fy;
        int x1 = x0 + 1; if (x1 > facePixelsMinusOne) x1 = facePixelsMinusOne;
        int y1 = y0 + 1; if (y1 > facePixelsMinusOne) y1 = facePixelsMinusOne;
        float tx = fx - x0; float ty = fy - y0;
        float h00 = faceHeights[y0, x0];
        float h10 = faceHeights[y0, x1];
        float h01 = faceHeights[y1, x0];
        float h11 = faceHeights[y1, x1];
        float h0 = h00 + (h10 - h00) * tx;
        float h1 = h01 + (h11 - h01) * tx;
        float h = h0 + (h1 - h0) * ty;

        // Cube-to-sphere projection.
        float ax = planeX * twoOverFaceSize - 1f;
        float by = planeY * twoOverFaceSize - 1f;
        float cubeX = upX + ax * aAx + by * aBx;
        float cubeY = upY + ax * aAy + by * aBy;
        float cubeZ = upZ + ax * aAz + by * aBz;
        float invMag = 1f / Mathf.Sqrt(cubeX * cubeX + cubeY * cubeY + cubeZ * cubeZ);
        float r = sphereRadius + h;
        float dxs = cubeX * invMag * r;
        float dys = cubeY * invMag * r;
        float dzs = cubeZ * invMag * r;
        ox = scX + dxs;
        oy = scY + dys;
        oz = scZ + dzs;
    }

    /// <summary>
    /// Projects a 2D plane point to 3D world space (sphere + height) and computes the surface
    /// normal via central differences in plane space. The result is a unit normal in world space.
    /// </summary>
    private static Vector3 ComputeWorldNormalAtPlanePoint(
        float planeX, float planeY, float gradStepPlane,
        float[,] faceHeights, int facePixelsPerAxis, float pixelDistance,
        float faceWorldSize, Vector3 sphereCenter, float sphereRadius,
        Vector3 localUp, Vector3 axisA, Vector3 axisB)
    {
        // Sample 4 neighbors in plane space.
        Vector3 pXm = ProjectPlanePointToSurface(planeX - gradStepPlane, planeY,
            faceHeights, facePixelsPerAxis, pixelDistance,
            faceWorldSize, sphereCenter, sphereRadius, localUp, axisA, axisB);
        Vector3 pXp = ProjectPlanePointToSurface(planeX + gradStepPlane, planeY,
            faceHeights, facePixelsPerAxis, pixelDistance,
            faceWorldSize, sphereCenter, sphereRadius, localUp, axisA, axisB);
        Vector3 pYm = ProjectPlanePointToSurface(planeX, planeY - gradStepPlane,
            faceHeights, facePixelsPerAxis, pixelDistance,
            faceWorldSize, sphereCenter, sphereRadius, localUp, axisA, axisB);
        Vector3 pYp = ProjectPlanePointToSurface(planeX, planeY + gradStepPlane,
            faceHeights, facePixelsPerAxis, pixelDistance,
            faceWorldSize, sphereCenter, sphereRadius, localUp, axisA, axisB);

        Vector3 tx = pXp - pXm;
        Vector3 ty = pYp - pYm;
        Vector3 n = Vector3.Cross(tx, ty);

        float mag = n.magnitude;
        if (mag <= 1e-8f)
        {
            // Degenerate — fall back to sphere radial direction.
            Vector3 self = ProjectPlanePointToSurface(planeX, planeY,
                faceHeights, facePixelsPerAxis, pixelDistance,
                faceWorldSize, sphereCenter, sphereRadius, localUp, axisA, axisB);
            Vector3 r = self - sphereCenter;
            float rm = r.magnitude;
            return rm > 1e-6f ? r / rm : localUp;
        }
        n /= mag;

        // Make sure the normal points outward from the sphere center.
        Vector3 selfPt = ProjectPlanePointToSurface(planeX, planeY,
            faceHeights, facePixelsPerAxis, pixelDistance,
            faceWorldSize, sphereCenter, sphereRadius, localUp, axisA, axisB);
        if (Vector3.Dot(n, selfPt - sphereCenter) < 0f) n = -n;
        return n;
    }

    /// <summary>
    /// Projects a 2D plane point onto the sphere face surface, displaced by the bilinear-sampled height.
    /// </summary>
    private static Vector3 ProjectPlanePointToSurface(
        float planeX, float planeY,
        float[,] faceHeights, int facePixelsPerAxis, float pixelDistance,
        float faceWorldSize, Vector3 sphereCenter, float sphereRadius,
        Vector3 localUp, Vector3 axisA, Vector3 axisB)
    {
        float fx = planeX / pixelDistance;
        float fy = planeY / pixelDistance;
        float h = SampleFaceHeightBilinear(faceHeights, facePixelsPerAxis, fx, fy);

        float percentX = faceWorldSize > 0f ? planeX / faceWorldSize : 0f;
        float percentY = faceWorldSize > 0f ? planeY / faceWorldSize : 0f;
        float ax = (percentX - 0.5f) * 2f;
        float by = (percentY - 0.5f) * 2f;

        float cubeX = localUp.x + ax * axisA.x + by * axisB.x;
        float cubeY = localUp.y + ax * axisA.y + by * axisB.y;
        float cubeZ = localUp.z + ax * axisA.z + by * axisB.z;
        float invMag = 1f / Mathf.Sqrt(cubeX * cubeX + cubeY * cubeY + cubeZ * cubeZ);
        float dx = cubeX * invMag, dy = cubeY * invMag, dz = cubeZ * invMag;

        float r = sphereRadius + h;
        return new Vector3(sphereCenter.x + dx * r, sphereCenter.y + dy * r, sphereCenter.z + dz * r);
    }

    /// <summary>
    /// Writes NormalMeta.bytes describing the per-tier resolutions and per-LOD tier mapping.
    /// Read at runtime by TextureStreamer to build the matching mapping/cache structures.
    /// </summary>
    private static void WriteNormalMeta(string outputFolder, TextureBakeSettings settings)
    {
        string path = Path.Combine(outputFolder, "NormalMeta.bytes");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(NORMAL_META_VERSION);                              // uint16
            bw.Write((byte)settings.normalTierResolutions.Length);      // uint8 tier count
            bw.Write((byte)settings.lodToNormalTier.Length);            // uint8 maxLOD+1
            bw.Write((byte)Mathf.Max(0, settings.normalBorderPixels));  // uint8 border
            bw.Write(new byte[5]);                                      // reserved (alignment)

            for (int i = 0; i < settings.normalTierResolutions.Length; i++)
                bw.Write((ushort)settings.normalTierResolutions[i]);
            for (int i = 0; i < settings.lodToNormalTier.Length; i++)
                bw.Write(settings.lodToNormalTier[i]);
        }
    }

    // =====================================================================
    // ============= CANOPY OVERLAY (BAKE-TIME, RUNTIME-FREE) ==============
    // =====================================================================
    // Modifies splatmap weights at bake to fake far-distance tree canopies.
    // Strictly bake-side: no runtime data, no new file fields, no consumer changes.
    // The runtime reads identical splatmap bytes — they just contain extra color
    // contribution in tiers where trees are culled.
    // =====================================================================

    /// <summary>
    /// One canopy stamp in plane-pixel coordinates of a single terrain's full alphamap.
    /// </summary>
    private struct TreeStamp
    {
        public float pxFull;        // Plane-space pixel x within the terrain's full alphamap
        public float pyFull;        // Plane-space pixel y within the terrain's full alphamap
        public float radiusPx;      // Stamp radius in alphamap pixels
        public byte prototypeIndex;
    }

    // NOTE: Bake-time overlay system (OverlayBakeContext, BuildOverlayContext, etc.) has been removed.
    // Overlay functionality is now handled at runtime by ChunkBatcher.SmoothCanopyAlpha()
    // which marks tree coverage per-vertex and smooths alpha across LOD1+ chunks.

    /// <summary>
    /// Brute-force "closest point on the probability simplex with positions = layerColors".
    /// Enumerates all subsets of size 1, 2, 3 and keeps the lowest-residual constrained LS.
    /// Returns a length-N weight vector summing to 1 with at most 3 non-zero entries.
    /// </summary>
    private static float[] SolveSimplexClosest(Vector3 target, Vector3[] layerColors)
    {
        int N = layerColors.Length;
        float[] best = new float[N];
        // Initialize with a degenerate fallback: all weight on layer 0.
        if (N > 0) best[0] = 1f;
        float bestRes = N > 0 ? (layerColors[0] - target).sqrMagnitude : 0f;

        // Singletons
        for (int i = 1; i < N; i++)
        {
            float r = (layerColors[i] - target).sqrMagnitude;
            if (r < bestRes) { bestRes = r; Array.Clear(best, 0, N); best[i] = 1f; }
        }

        // Pairs
        for (int i = 0; i < N; i++)
        for (int j = i + 1; j < N; j++)
        {
            Vector3 a = layerColors[i], b = layerColors[j];
            Vector3 d = b - a;
            float dd = Vector3.Dot(d, d);
            if (dd < 1e-8f) continue;
            float u = Mathf.Clamp01(Vector3.Dot(target - a, d) / dd);
            Vector3 p = a + u * d;
            float r = (p - target).sqrMagnitude;
            if (r < bestRes)
            {
                bestRes = r;
                Array.Clear(best, 0, N);
                best[i] = 1f - u;
                best[j] = u;
            }
        }

        // Triples (interior of triangle face only — edges/verts already covered)
        for (int i = 0; i < N; i++)
        for (int j = i + 1; j < N; j++)
        for (int k = j + 1; k < N; k++)
        {
            Vector3 a = layerColors[i], b = layerColors[j], c = layerColors[k];
            Vector3 e1 = b - a, e2 = c - a;
            float A = Vector3.Dot(e1, e1);
            float B = Vector3.Dot(e1, e2);
            float D = Vector3.Dot(e2, e2);
            float det = A * D - B * B;
            if (Mathf.Abs(det) < 1e-8f) continue;
            Vector3 t = target - a;
            float E = Vector3.Dot(e1, t);
            float F = Vector3.Dot(e2, t);
            float u = (D * E - B * F) / det;
            float v = (A * F - B * E) / det;
            if (u < 0f || v < 0f || u + v > 1f) continue;
            Vector3 pp = a + u * e1 + v * e2;
            float r = (pp - target).sqrMagnitude;
            if (r < bestRes)
            {
                bestRes = r;
                Array.Clear(best, 0, N);
                best[i] = 1f - u - v;
                best[j] = u;
                best[k] = v;
            }
        }

        return best;
    }

    // NOTE: Bake-time tree stamp collection has been removed (replaced by runtime canopy system)

    private static List<TreeStamp> FilterStampsForCell(
        List<TreeStamp> terrainStamps,
        int cellX, int cellY, int cellSize, int border)
    {
        List<TreeStamp> result = null;
        // Extract region in full-alphamap pixel coords:
        //   [cellX*cellSize - border, (cellX+1)*cellSize + border)
        float x0 = cellX * cellSize - border;
        float y0 = cellY * cellSize - border;
        float x1 = (cellX + 1) * cellSize + border;
        float y1 = (cellY + 1) * cellSize + border;
        for (int i = 0; i < terrainStamps.Count; i++)
        {
            var s = terrainStamps[i];
            if (s.pxFull + s.radiusPx < x0) continue;
            if (s.pxFull - s.radiusPx > x1) continue;
            if (s.pyFull + s.radiusPx < y0) continue;
            if (s.pyFull - s.radiusPx > y1) continue;
            if (result == null) result = new List<TreeStamp>(8);
            // Translate to cell-with-border local coords.
            s.pxFull -= x0;
            s.pyFull -= y0;
            result.Add(s);
        }
        return result;
    }

    // NOTE: Bake-time overlay stamping methods have been removed (replaced by runtime canopy system)

    #endif
}
