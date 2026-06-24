using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomTypes;
using System.Threading.Tasks;
using System.Collections.Concurrent;


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
    // Mirrors the CellGroup file grouping in TreeBaker so file count doesn't explode with subdivisions.
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
    /// 
    /// Now supports parallel processing across faces using Task.WhenAll.
    /// </summary>
    public static async Task BakeAllAsync(
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
        
        // Step 1: Bake terrain layer textures (shared across all terrains) - single threaded
        BakeTerrainLayers(referenceTerrain.terrainData, layerFolder, settings);

        // NOTE: Bake-time overlay system (TreeOverlayShape, overlay stamping) has been removed
        // and replaced by the runtime canopy overlay system. See ChunkBatcher.Add() for
        // runtime per-vertex canopy marking with alpha smoothing on LOD1+ chunks.

        // Step 2: Bake splatmaps per heightmap cell for each face - PARALLEL PROCESSING
        var bakeTasks = new List<Task>();
        
        int totalCells = totalTerrainCount * heightmapSubdivisionsPowerOf2 * heightmapSubdivisionsPowerOf2;
        int processed = 0;
        // Use ConcurrentDictionary for thread-safe access from parallel tasks
        var faceBlobs = new ConcurrentDictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>>[6];
        var faceClassifications = new ConcurrentDictionary<(sbyte mx, sbyte my), sbyte>[6];
        
        for (int f = 0; f < 6; f++)
        {
            if (faceTerrains[f].Count > 0)
            {
                int faceIndex = f; // Capture for closure
                var task = Task.Run(() => BakeFaceCollectAsync(
                    faceTerrains[faceIndex], 
                    (FaceId)faceIndex, 
                    faceOrientations != null && faceIndex < faceOrientations.Length
                        ? faceOrientations[faceIndex]
                        : FaceContainerOrientations.Get((FaceId)faceIndex),
                    heightmapSubdivisionsPowerOf2,
                    minX, settings, splatmapFolder, ref processed, totalCells, 
                    faceBlobs[faceIndex], faceClassifications[faceIndex]));
                bakeTasks.Add(task);
            }
        }

        // Wait for all faces to complete baking in parallel
        await Task.WhenAll(bakeTasks);

        // Write grouped splatmap files (one per original terrain per face) - can be done in parallel
        var writeTasks = new List<Task>();
        for (int f = 0; f < 6; f++)
        {
            if (faceBlobs[f] != null && faceBlobs[f].Count > 0)
            {
                int faceIndex = f; // Capture for closure
                writeTasks.Add(Task.Run(() => WriteFaceSplatmaps(faceIndex, faceBlobs[faceIndex], faceClassifications[faceIndex])));
            }
        }
        
        await Task.WhenAll(writeTasks);

        // Delete individual splatmap files (they've been superseded by groups)
        if (Directory.Exists(splatmapFolder))
        {
            string[] oldIndiv = Directory.GetFiles(splatmapFolder, "Splatmap_*.bytes");
            foreach (string f in oldIndiv) File.Delete(f);
        }

        // Step 3: Bake heightmap-derived world-space normal maps (Phase 2 NdotL system) - PARALLEL
        if (settings.bakeHeightmapNormals && sphereRadius > 0f)
        {
            ValidateNormalSettings(settings);

            int normalProcessed = 0;
            int normalTotalCells = totalCells;
            
            var normalTasks = new List<Task>();
            for (int f = 0; f < 6; f++)
            {
                if (faceTerrains[f].Count > 0)
                {
                    int faceIndex = f; // Capture for closure
                    normalTasks.Add(Task.Run(() => BakeFaceNormalsAsync(
                        faceTerrains[faceIndex], 
                        (FaceId)faceIndex,
                        heightmapSubdivisionsPowerOf2,
                        minX, settings, normalFolder, sphereCenter, sphereRadius,
                        faceOrientations != null && faceIndex < faceOrientations.Length
                            ? faceOrientations[faceIndex]
                            : FaceContainerOrientations.Get((FaceId)faceIndex),
                        ref normalProcessed, normalTotalCells)));
                }
            }
            
            await Task.WhenAll(normalTasks);

            WriteNormalMeta(normalFolder, settings);
        }
        
        EditorUtility.ClearProgressBar();
        AssetDatabase.Refresh();
        
        // Log uniform splatmap statistics - aggregated from parallel results
        LogUniformSplatmapStats(faceClassifications);
    }

    private static void ValidateNormalSettings(TextureBakeSettings settings) { /* ... */ }

    public struct TerrainInfo
    {
        public Terrain terrain;
        public sbyte gridX;
        public sbyte gridY;
    }

    private static List<TerrainInfo> CollectTerrains(Transform container, FaceContainerOrientation orientation)
    {
        // ... existing implementation unchanged ...
        return new List<TerrainInfo>();
    }

    /// <summary>
    /// Bakes splatmaps for a single face asynchronously. Called in parallel for each face.
    /// </summary>
    private static async Task BakeFaceCollectAsync(
        List<TerrainInfo> terrains,
        FaceId face,
        FaceContainerOrientation orientation,
        int subdivisionsPow2,
        sbyte minX,
        TextureBakeSettings settings,
        string outputFolder,
        ref int processed,
        int totalCells,
        ConcurrentDictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>> blobsByTerrain,
        ConcurrentDictionary<(sbyte mx, sbyte my), sbyte> classifications)
    {
        // ... similar to existing BakeFaceCollect but with async/await and thread-safe collections
        await Task.Yield(); // Allow other tasks to run
        
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

            blobsByTerrain.TryAdd(key, new List<(sbyte, sbyte, byte[])>());

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

                    // Store classification for uniform cell detection - thread-safe
                    classifications.TryAdd((mapX, mapY), dominantLayer);

                    var entriesList = blobsByTerrain.GetOrAdd(key, _ => new List<(sbyte, sbyte, byte[])>());
                    lock (entriesList) // Thread-safe addition to list
                    {
                        entriesList.Add((mapX, mapY, blob));
                    }

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
            if (terrain != null)
            {
                terrain.terrainData = null;
            }
        }
    }

    /// <summary>
    /// Bakes normals for a single face asynchronously. Called in parallel for each face.
    /// </summary>
    private static async Task BakeFaceNormalsAsync(
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
        // ... similar to existing BakeFaceNormals but with async/await and thread-safe progress tracking
        await Task.Yield(); // Allow other tasks to run
        
        string side = FaceIdUtility.GetFilePrefix(face);

        // Build a single contiguous height grid for the entire face.
        BuildFaceHeightGrid(terrains, out float[,] faceHeights, out float terrainSize,
            out int terrainGridSize, out int heightRes, out float bakedMaxHeight, orientation);
        if (faceHeights == null) return;

        // ... rest of normal baking logic with parallel cell processing using Parallel.For
    }

    private static void BuildFaceHeightGrid( /* ... */ ) { /* existing implementation */ }
    
    /// <summary>
    /// Writes a single face's cached splatmap groups and uniform classification file to disk.
    /// Call this per-face immediately after extraction so memory can be freed.
    /// </summary>
    public static void WriteFaceSplatmaps(
        int faceIndex,
        ConcurrentDictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>> blobs,
        ConcurrentDictionary<(sbyte mx, sbyte my), sbyte> classifications)
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

    private static void LogUniformSplatmapStats(ConcurrentDictionary<(sbyte mx, sbyte my), sbyte>[] faceClassifications) { /* ... */ }

    // ... rest of existing methods unchanged for now (BakeCellSplatmapToStream, WriteSplatmapGroupFile, etc.)
}
