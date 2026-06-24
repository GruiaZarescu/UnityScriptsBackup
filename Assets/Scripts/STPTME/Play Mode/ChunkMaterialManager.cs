using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using CustomTypes;

/// <summary>
/// GPU-side texture management for terrain chunks.
/// 
/// Responsibilities:
///   - Owns the single shared terrain Material (all chunk renderers reference it)
///   - Manages Texture2DArrays: one set per LOD tier for splatmaps, one for layer diffuse
///   - Allocates/releases splatmap slices with ref-counting and per-tier free-lists
///   - Binds per-renderer state via MaterialPropertyBlock (slice index, tier, UV offset/scale)
///
/// SRP batcher strategy:
///   All Texture2DArrays are bound globally on the shared Material — per-renderer
///   MaterialPropertyBlock only sets int/vector properties (no textures), so renderers
///   at the same LOD tier batch together.
///
/// Thread safety: NOT thread-safe. All methods must be called from the main thread.
///
/// Lifecycle:
///   ChunkManager.Awake  → Init(streamer, shader)
///   ChunkRegistry       → AllocateSlice / AllocateAndBind / BindToRenderer / ReleaseSlice
///   ChunkManager.OnDestroy → Dispose()
/// </summary>
public class ChunkMaterialManager
{
    // ========== TYPES ==========

    private struct SliceRecord
    {
        public Vector2SByte map;
        public byte tier;
        public FaceId face;
        public int refCount;
    }

    // ========== SHADER PROPERTY IDS ==========
    // Cached once. SplatPropIds[group][tier] maps to _SplatmapArray{group}_T{tier}.
    // Group 0 covers layers 0-3, Group 1 covers layers 4-7.

    private static readonly int[][] SplatPropIds; // [group][tier]
    private static readonly int Prop_LayerDiffuseArray;
    private static readonly int Prop_SplatSliceIndex;
    private static readonly int Prop_SplatTier;
    private static readonly int Prop_UVOffsetScale;
    private static readonly int Prop_LayerCount;
    private static readonly int Prop_SplatGroupCount;
    private static readonly int Prop_LayerTiling;
    // ----- Uniform splatmap classification -----
    private static readonly int Prop_UniformDominantLayer;
    // ----- Heightmap normal map property IDs (Phase 2) -----
    private static readonly int[] NormalArrayPropIds; // [tier]
    private static readonly int Prop_NormalSliceIndex;
    private static readonly int Prop_NormalTier;
    private static readonly int Prop_NormalUVOffsetScale;
    private const int MAX_NORMAL_TIER_SLOTS = 4; // shader supports T0..T3

    static ChunkMaterialManager()
    {
        SplatPropIds = new int[2][];
        SplatPropIds[0] = new int[]
        {
            Shader.PropertyToID("_SplatmapArray_T0"),
            Shader.PropertyToID("_SplatmapArray_T1"),
            Shader.PropertyToID("_SplatmapArray_T2"),
            Shader.PropertyToID("_SplatmapArray_T3")
        };
        SplatPropIds[1] = new int[]
        {
            Shader.PropertyToID("_SplatmapArray1_T0"),
            Shader.PropertyToID("_SplatmapArray1_T1"),
            Shader.PropertyToID("_SplatmapArray1_T2"),
            Shader.PropertyToID("_SplatmapArray1_T3")
        };

        Prop_LayerDiffuseArray = Shader.PropertyToID("_LayerDiffuseArray");
        Prop_SplatSliceIndex   = Shader.PropertyToID("_SplatSliceIndex");
        Prop_SplatTier         = Shader.PropertyToID("_SplatTier");
        Prop_UVOffsetScale     = Shader.PropertyToID("_UVOffsetScale");
        Prop_LayerCount        = Shader.PropertyToID("_LayerCount");
        Prop_SplatGroupCount   = Shader.PropertyToID("_SplatGroupCount");
        Prop_LayerTiling       = Shader.PropertyToID("_LayerTiling");
        Prop_UniformDominantLayer = Shader.PropertyToID("_UniformDominantLayer");

        NormalArrayPropIds = new int[]
        {
            Shader.PropertyToID("_NormalmapArray_T0"),
            Shader.PropertyToID("_NormalmapArray_T1"),
            Shader.PropertyToID("_NormalmapArray_T2"),
            Shader.PropertyToID("_NormalmapArray_T3")
        };
        Prop_NormalSliceIndex = Shader.PropertyToID("_NormalSliceIndex");
        Prop_NormalTier       = Shader.PropertyToID("_NormalTier");
        Prop_NormalUVOffsetScale = Shader.PropertyToID("_NormalUVOffsetScale");
    }

    // ========== STATE ==========

    // Shared material — assigned to all chunk renderers via renderer.sharedMaterial
    private Material sharedMaterial;
    private Material sharedBatchedMaterial; 

    public Material SharedMaterial => sharedMaterial;
    public Material SharedBatchedMaterial => sharedBatchedMaterial;

    // Layer diffuse textures (static, uploaded once at Init, never modified)
    private Texture2DArray layerDiffuseArray;

    // Splatmap arrays: [tier][splatGroup]. Each Texture2DArray holds RGBA32 slices
    // at the tier's resolution. Created lazily on first allocation for each tier.
    private Texture2DArray[][] splatmapArrays;

    // Per-tier metadata
    private int[] tierWidths;            // 0 = tier not yet initialized
    private int[] tierHeights;
    private int[] tierSliceCapacity;     // current array depth per tier
    private Stack<int>[] freeSlices;     // per-tier free-list of available slice indices
    private List<SliceRecord>[] sliceRecords; // [tier] → indexed by sliceIndex

    // Staging textures for efficient per-slice GPU upload (one per tier, reused)
    private Texture2D[] stagingTextures;

    // Pre-allocated RGBA reshape buffer per tier (avoids GC on each upload)
    private byte[][] reshapeBuffers;

    // Forward lookup: (map, tier, isTop) → sliceIndex within that tier
    private Dictionary<(Vector2SByte, byte, FaceId), int> keyToSlice;

    // 1×1 placeholder array for uninitialized tier slots on the material
    private Texture2DArray dummyArray;

    // ----- Heightmap normal map state (Phase 2, parallel to splatmap state) -----
    // Independent tier/slice allocator. Cannot share splatmap slices because the
    // per-LOD tier mapping for normals is configured separately.
    private byte normalTierCount;
    private bool normalsEnabled;
    private Texture2DArray[] normalArrays;          // [tier]
    private int[] normalTierWidths;
    private int[] normalTierHeights;
    private int[] normalTierSliceCapacity;
    private Stack<int>[] normalFreeSlices;
    private List<SliceRecord>[] normalSliceRecords;
    private Texture2D[] normalStagingTextures;
    private Dictionary<(Vector2SByte, byte, FaceId), int> normalKeyToSlice;
    private Texture2DArray dummyNormalArray;

    // Config (set once in Init, never changes)
    private byte tierCount;
    private int layerCount;
    private int splatGroupCount;     // ceil(layerCount / 4), capped at 2
    private int initialSliceCapacity;

    // ========== UNIFORM SPLATMAP CLASSIFICATION ==========

    /// <summary>
    /// Per-cell uniform classification loaded from baked files.
    /// Keyed by (face, mx, my) -> dominantLayer (0..3 = uniform, -1 = not uniform).
    /// </summary>
    private Dictionary<(sbyte face, sbyte mx, sbyte my), sbyte> uniformCellClassifications;
    private bool uniformClassificationEnabled;
    private const ulong UNIFORM_CLASS_MAGIC = 0x4C43525350; // "SRCL" little-endian
    private const ushort UNIFORM_CLASS_VERSION = 1;
    private const string UNIFORM_CLASS_SUFFIX = "UniformClassification_";

    // ========== SHADER PROPERTY IDS ==========

    // ========== PROPERTIES ==========

    /// <summary>
    /// The single shared Material used by all terrain chunk renderers.
    /// Assign to renderer.sharedMaterial (NOT .material) to avoid cloning.
    /// </summary>
    public int LayerCount => layerCount;
    public int SplatGroupCount => splatGroupCount;
    public byte TierCount => tierCount;
    public bool NormalsEnabled => normalsEnabled;
    public byte NormalTierCount => normalTierCount;

    // ========== INIT ==========

    /// <summary>
    /// Initialize GPU resources. Call from ChunkManager.Awake() after TextureStreamer.Init().
    /// Creates shared Material, uploads layer diffuse textures, prepares per-tier structures.
    /// </summary>
    /// <param name="streamer">Initialized TextureStreamer (provides layer data and config)</param>
    /// <param name="terrainShader">Custom terrain blend shader, or null for URP/Lit fallback</param>
    /// <param name="initialSliceCapacity">Initial splatmap array depth per tier (doubles on overflow)</param>
    public void Init(TextureStreamer streamer, Shader terrainShader, int initialSliceCapacity = 64)
    {
        this.tierCount = streamer.TierCount;
        this.layerCount = streamer.LayerCount;
        this.splatGroupCount = (layerCount + 3) / 4;
        this.initialSliceCapacity = initialSliceCapacity;

        if (splatGroupCount > 2)
        {
            Debug.LogWarning($"[ChunkMaterialManager] {layerCount} layers require {splatGroupCount} splatmap groups. " +
                "Max 2 groups (8 layers) supported. Extra layers will be ignored.");
            splatGroupCount = 2;
        }

        // --- Shared Material ---
        sharedMaterial = terrainShader != null
            ? new Material(terrainShader)
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        sharedMaterial.name = "Terrain Chunk Material (Shared)";

        sharedBatchedMaterial = new Material(sharedMaterial);
        sharedBatchedMaterial.name = "Terrain Chunk Material (Batched)";
        sharedBatchedMaterial.EnableKeyword("BATCHED_CHUNKS");

        // --- Per-tier structures ---
        splatmapArrays    = new Texture2DArray[tierCount][];
        tierWidths        = new int[tierCount];
        tierHeights       = new int[tierCount];
        tierSliceCapacity = new int[tierCount];
        freeSlices        = new Stack<int>[tierCount];
        sliceRecords      = new List<SliceRecord>[tierCount];
        stagingTextures   = new Texture2D[tierCount];
        reshapeBuffers    = new byte[tierCount][];

        for (int t = 0; t < tierCount; t++)
        {
            freeSlices[t]   = new Stack<int>();
            sliceRecords[t] = new List<SliceRecord>();
        }

        keyToSlice = new Dictionary<(Vector2SByte, byte, FaceId), int>();

        // --- 1×1 dummy array for uninitialized tier slots ---
        dummyArray = new Texture2DArray(1, 1, 1, TextureFormat.RGBA32, false);
        dummyArray.SetPixelData(new byte[] { 255, 255, 255, 255 }, 0, 0);
        dummyArray.Apply(false, true);
        dummyArray.name = "DummySplatmap";

        // Assign dummy to all tier + group slots on the material
        int maxTierSlots = Mathf.Min(tierCount, 4);
        for (int t = 0; t < maxTierSlots; t++)
            for (int g = 0; g < splatGroupCount; g++)
                {sharedMaterial.SetTexture(SplatPropIds[g][t], dummyArray);
                sharedBatchedMaterial.SetTexture(SplatPropIds[g][t], dummyArray);
                }

        // --- Layer diffuse array ---
        CreateLayerDiffuseArray(streamer);

        sharedMaterial.SetTexture(Prop_LayerDiffuseArray, layerDiffuseArray);
        sharedBatchedMaterial.SetTexture(Prop_LayerDiffuseArray, layerDiffuseArray);

        // --- Material uniforms ---
        sharedMaterial.SetFloat(Prop_LayerCount, layerCount);
        sharedBatchedMaterial.SetFloat(Prop_LayerCount, layerCount);
        sharedMaterial.SetFloat(Prop_SplatGroupCount, splatGroupCount);
        sharedBatchedMaterial.SetFloat(Prop_SplatGroupCount, splatGroupCount);

        // Default uniform dominant layer to -1 (multi-layer path).
        // Uniform cells set this to 0..3 via MaterialPropertyBlock in BindUniformCellToRenderer.
        sharedMaterial.SetFloat(Prop_UniformDominantLayer, -1f);
        sharedBatchedMaterial.SetFloat(Prop_UniformDominantLayer, -1f);

        sharedMaterial.SetVector(Prop_NormalUVOffsetScale, new Vector4(0f, 0f, 1f, 1f));
        sharedBatchedMaterial.SetVector(Prop_NormalUVOffsetScale, new Vector4(0f, 0f, 1f, 1f));

        if (streamer.LayerMetas != null && streamer.LayerMetas.Length > 0)
        {
            Vector4[] tilingData = new Vector4[layerCount];
            for (int i = 0; i < layerCount; i++)
            {
                TextureStreamer.TerrainLayerMeta meta = streamer.LayerMetas[i];
                tilingData[i] = new Vector4(
                    meta.tileSize.x, meta.tileSize.y,
                    meta.tileOffset.x, meta.tileOffset.y);
            }
            sharedMaterial.SetVectorArray(Prop_LayerTiling, tilingData);
            sharedBatchedMaterial.SetVectorArray(Prop_LayerTiling, tilingData);
        }

        // --- Heightmap normal map structures (Phase 2) ---
        InitNormalArrays(streamer);
    }

    // ========== HEIGHTMAP NORMAL MAP INIT ==========

    private void InitNormalArrays(TextureStreamer streamer)
    {
        normalsEnabled = streamer.HasHeightmapNormals;
        normalTierCount = streamer.NormalTierCount;
        if (normalTierCount > MAX_NORMAL_TIER_SLOTS)
        {
            Debug.LogWarning($"[ChunkMaterialManager] {normalTierCount} normal tiers requested but " +
                $"shader supports only {MAX_NORMAL_TIER_SLOTS}. Excess tiers will be ignored.");
            normalTierCount = MAX_NORMAL_TIER_SLOTS;
        }

        // 1×1 dummy normal array (encodes +Y world up: (0.5, 1.0, 0.5) RGB).
        dummyNormalArray = new Texture2DArray(1, 1, 1, TextureFormat.RGB24, false, true);
        dummyNormalArray.SetPixelData(new byte[] { 128, 255, 128 }, 0, 0);
        dummyNormalArray.Apply(false, true);
        dummyNormalArray.name = "DummyNormalmap";

        // Always bind the dummy to all 4 slots so the shader has valid samplers even
        // when normals are disabled or only some tiers are populated.
        for (int t = 0; t < MAX_NORMAL_TIER_SLOTS; t++)
        {
            sharedMaterial.SetTexture(NormalArrayPropIds[t], dummyNormalArray);
            sharedBatchedMaterial.SetTexture(NormalArrayPropIds[t], dummyNormalArray);
        }

        if (!normalsEnabled || normalTierCount == 0) return;

        normalArrays            = new Texture2DArray[normalTierCount];
        normalTierWidths        = new int[normalTierCount];
        normalTierHeights       = new int[normalTierCount];
        normalTierSliceCapacity = new int[normalTierCount];
        normalFreeSlices        = new Stack<int>[normalTierCount];
        normalSliceRecords      = new List<SliceRecord>[normalTierCount];
        normalStagingTextures   = new Texture2D[normalTierCount];
        normalKeyToSlice        = new Dictionary<(Vector2SByte, byte, FaceId), int>();

        for (int t = 0; t < normalTierCount; t++)
        {
            normalFreeSlices[t]   = new Stack<int>();
            normalSliceRecords[t] = new List<SliceRecord>();
        }
    }

    // ========== LAYER DIFFUSE ARRAY ==========

    private void CreateLayerDiffuseArray(TextureStreamer streamer)
    {
        if (layerCount <= 0) return;

        int res = streamer.LayerTextureResolution;
        if (res <= 0) res = 512;

        layerDiffuseArray = new Texture2DArray(res, res, layerCount,
            TextureFormat.RGBA32, mipChain: true, linear: false);
        layerDiffuseArray.filterMode = FilterMode.Trilinear;
        layerDiffuseArray.wrapMode = TextureWrapMode.Repeat; // layer textures tile
        layerDiffuseArray.name = "LayerDiffuseArray";

        // Upload each layer with mipmaps into the array.
        // Use a mip-enabled staging texture: load base level via SetPixelData,
        // Apply(true) generates mips from CPU data, then CopyTexture copies all mips into the array.
        Texture2D mipStaging = new Texture2D(res, res, TextureFormat.RGBA32, mipChain: true, linear: false);
        int expectedSize = res * res * 4;

        for (int i = 0; i < layerCount; i++)
        {
            byte[] rgba = streamer.GetLayerDiffuseData(i);
            if (rgba != null && rgba.Length == expectedSize)
            {
                mipStaging.SetPixelData(rgba, 0); // mip 0 only
                mipStaging.Apply(true, false);     // generate all mip levels from base
                Graphics.CopyTexture(mipStaging, 0, layerDiffuseArray, i); // copies all mip levels
            }
            else
            {
                Debug.LogWarning($"[ChunkMaterialManager] Layer {i} diffuse data " +
                    $"missing or wrong size (expected {expectedSize}, got {rgba?.Length ?? 0}).");
            }
        }

        UnityEngine.Object.Destroy(mipStaging);
        sharedMaterial.SetTexture(Prop_LayerDiffuseArray, layerDiffuseArray);

        // Free CPU copies now that data is on GPU
        streamer.ReleaseLayerCPUData();
    }

    // ========== SLICE ALLOCATION ==========

    /// <summary>
    /// Allocates a splatmap slice for the given (map, tier, isTop) key.
    /// If a slice already exists for this key, increments ref count (no re-upload).
    /// Returns the slice index within the tier's Texture2DArrays, or -1 on failure.
    /// </summary>
    public int AllocateSlice(Vector2SByte map, byte tier, FaceId face,
        TextureStreamer.SplatmapTile tile)
    {
        if (!tile.IsValid)
        {
            Debug.LogWarning($"[ChunkMaterialManager] Invalid tile for ({map.x},{map.y}) tier {tier}.");
            return -1;
        }

        if (tier >= tierCount)
        {
            Debug.LogError($"[ChunkMaterialManager] Tier {tier} >= tierCount {tierCount}.");
            return -1;
        }

        // Ref-counted reuse: if already uploaded for this key, just bump ref count
        var key = (map, tier, face);
        if (keyToSlice.TryGetValue(key, out int existingSlice))
        {
            SliceRecord rec = sliceRecords[tier][existingSlice];
            rec.refCount++;
            sliceRecords[tier][existingSlice] = rec;
            return existingSlice;
        }

        // Lazy-init this tier's arrays on first allocation
        if (splatmapArrays[tier] == null)
            CreateSplatmapArraysForTier(tier, tile.width, tile.height);

        // Validate resolution consistency (all tiles at a tier must match)
        if (tile.width != tierWidths[tier] || tile.height != tierHeights[tier])
        {
            Debug.LogError($"[ChunkMaterialManager] Tier {tier} resolution mismatch: " +
                $"array is {tierWidths[tier]}×{tierHeights[tier]}, " +
                $"tile is {tile.width}×{tile.height}.");
            return -1;
        }

        // Pop a free slice, growing arrays if necessary
        if (freeSlices[tier].Count == 0)
            GrowTierArrays(tier);

        int sliceIndex = freeSlices[tier].Pop();

        // Upload pixel data to GPU (all splatmap groups)
        UploadSliceData(tier, sliceIndex, tile);

        // Track
        SliceRecord record = new SliceRecord
        {
            map = map,
            tier = tier,
            face = face,
            refCount = 1
        };

        while (sliceRecords[tier].Count <= sliceIndex)
            sliceRecords[tier].Add(default);

        sliceRecords[tier][sliceIndex] = record;
        keyToSlice[key] = sliceIndex;

        return sliceIndex;
    }

    // ========== RENDERER BINDING ==========

    /// <summary>
    /// Binds splatmap data to a renderer via MaterialPropertyBlock.
    /// Sets the shared material and per-renderer slice/tier/UV properties.
    /// No textures are set on the MPB — all textures live on the shared Material
    /// for SRP batcher compatibility.
    /// </summary>
    public void BindToRenderer(Renderer renderer, int sliceIndex, byte tier, Vector4 uvOffsetScale)
    {
        if (renderer == null || sliceIndex < 0) return;

        renderer.sharedMaterial = sharedMaterial;

        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(mpb);

        mpb.SetFloat(Prop_SplatSliceIndex, sliceIndex);
        mpb.SetFloat(Prop_SplatTier, tier);
        mpb.SetVector(Prop_UVOffsetScale, uvOffsetScale);

        renderer.SetPropertyBlock(mpb);
    }

    /// <summary>
    /// Convenience: allocates a slice (or reuses existing) and binds to renderer in one call.
    /// </summary>
    public int AllocateAndBind(Renderer renderer, Vector2SByte map, byte tier, FaceId face,
        TextureStreamer.SplatmapTile tile, Vector4 uvOffsetScale)
    {
        int sliceIndex = AllocateSlice(map, tier, face, tile);
        if (sliceIndex >= 0)
            BindToRenderer(renderer, sliceIndex, tier, uvOffsetScale);
        return sliceIndex;
    }

    /// <summary>
    /// Sets the per-renderer normal slice + tier on an existing MaterialPropertyBlock.
    /// Must be called AFTER BindToRenderer (which sets the splat MPB) so we don't clobber
    /// splat properties. Reuses the renderer's current property block.
    /// </summary>
    public void BindNormalToRenderer(Renderer renderer, int normalSliceIndex, byte normalTier,
        Vector4 normalUvOffsetScale)
    {
        if (renderer == null || !normalsEnabled) return;

        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(mpb);
        mpb.SetFloat(Prop_NormalSliceIndex, normalSliceIndex < 0 ? 0 : normalSliceIndex);
        mpb.SetFloat(Prop_NormalTier, normalTier);
        mpb.SetVector(Prop_NormalUVOffsetScale, normalUvOffsetScale);
        renderer.SetPropertyBlock(mpb);
    }

    // ========== UNIFORM SPLATMAP CLASSIFICATION ==========

    /// <summary>
    /// Loads uniform classification files for all 6 faces from StreamingAssets.
    /// Call once at startup (after TextureStreamer.Init but before chunk creation).
    /// </summary>
    public void LoadUniformClassification()
    {
        string classFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "ClassData");
        if (!Directory.Exists(classFolder))
        {
            Debug.LogWarning("[ChunkMaterialManager] Classification folder not found. Uniform path disabled.");
            uniformClassificationEnabled = false;
            return;
        }

        uniformCellClassifications = new Dictionary<(sbyte, sbyte, sbyte), sbyte>();
        int totalCells = 0;
        int uniformCells = 0;

        for (int f = 0; f < FaceIdUtility.StorageFaceCount; f++)
        {
            string side = FaceIdUtility.GetFilePrefix((FaceId)f);
            string filePath = Path.Combine(classFolder, $"{UNIFORM_CLASS_SUFFIX}{side}.bytes");
            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"[ChunkMaterialManager] Classification file missing for face {side}. Uniform path may be incomplete.");
                continue;
            }

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    ulong magic = br.ReadUInt64();
                    ushort version = br.ReadUInt16();
                    int cellCount = br.ReadInt32();

                    if (magic != UNIFORM_CLASS_MAGIC || version != UNIFORM_CLASS_VERSION)
                    {
                        Debug.LogWarning($"[ChunkMaterialManager] Bad header in {filePath} (magic=0x{magic:X}, v={version}). Skipping.");
                        continue;
                    }

                    for (int i = 0; i < cellCount; i++)
                    {
                        sbyte mx = br.ReadSByte();
                        sbyte my = br.ReadSByte();
                        sbyte dl = br.ReadSByte();
                        var key = ((sbyte)f, mx, my);
                        if (!uniformCellClassifications.ContainsKey(key))
                            uniformCellClassifications[key] = dl;
                        if (dl >= 0) uniformCells++;
                    }
                    totalCells += cellCount;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChunkMaterialManager] Error loading {filePath}: {ex.Message}");
            }
        }

        uniformClassificationEnabled = totalCells > 0;
        if (uniformClassificationEnabled)
        {
            float pct = (float)uniformCells / totalCells * 100f;
            Debug.Log($"[ChunkMaterialManager] Uniform classification loaded: {uniformCells}/{totalCells} cells ({pct:F1}%) across {FaceIdUtility.StorageFaceCount} faces.");
        }
        else
        {
            Debug.LogWarning("[ChunkMaterialManager] No uniform classification data loaded. All cells will use multi-layer path.");
        }
    }

    /// <summary>
    /// Returns the dominant layer index for a cell, or -1 if the cell is not uniform.
    /// Layers 0..3 are valid uniform types. -1 means "use standard multi-layer path".
    /// </summary>
    public int GetUniformDominantLayer(sbyte mx, sbyte my, FaceId face)
    {
        if (!uniformClassificationEnabled || uniformCellClassifications == null)
            return -1;

        var key = ((sbyte)face, mx, my);
        if (uniformCellClassifications.TryGetValue(key, out sbyte dl))
            return dl;
        return -1;
    }

    /// <summary>
    /// Binds a renderer for a uniform cell (single-layer rendering).
    /// Sets sharedMaterial to the base material and configures _UniformDominantLayer
    /// on the MaterialPropertyBlock. Normal maps are still applied normally.
    /// No splatmap slice is allocated or bound.
    /// </summary>
    public void BindUniformCellToRenderer(Renderer renderer, sbyte uniformDominantLayer,
        Vector4 uvOffsetScale, Vector4 normalUvOffsetScale,
        int normalSliceIndex, byte normalTier)
    {
        if (renderer == null) return;

        renderer.sharedMaterial = sharedMaterial;

        MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(mpb);

        // Set the uniform dominant layer flag (>= 0 enables single-layer path in shader).
        mpb.SetFloat(Prop_UniformDominantLayer, uniformDominantLayer);

        // Still bind normal map data (independent of splatmap path).
        if (normalsEnabled && normalSliceIndex >= 0)
        {
            mpb.SetFloat(Prop_NormalSliceIndex, normalSliceIndex);
            mpb.SetFloat(Prop_NormalTier, normalTier);
            mpb.SetVector(Prop_NormalUVOffsetScale, normalUvOffsetScale);
        }

        // Set UV offset for the layer diffuse sampling (triplanar blend works in world space,
        // but the uniform cell still needs correct UVs for the batched chunk path).
        mpb.SetVector(Prop_UVOffsetScale, uvOffsetScale);

        renderer.SetPropertyBlock(mpb);
    }

    /// <summary>
    /// Convenience: allocates a normal slice (or reuses) and binds to renderer in one call.
    /// Returns -1 if normals are disabled (caller may ignore).
    /// </summary>
    public int AllocateAndBindNormal(Renderer renderer, Vector2SByte map, byte tier, FaceId face,
        TextureStreamer.NormalTile tile, Vector4 normalUvOffsetScale)
    {
        int sliceIndex = AllocateNormalSlice(map, tier, face, tile);
        if (sliceIndex >= 0)
            BindNormalToRenderer(renderer, sliceIndex, tier, normalUvOffsetScale);
        return sliceIndex;
    }

    // ========== SLICE RELEASE ==========

    /// <summary>
    /// Decrements ref count for a slice. When it reaches 0, the slice index is returned
    /// to the free-list (GPU data remains until overwritten by a future allocation).
    /// </summary>
    public void ReleaseSlice(int sliceIndex, byte tier)
    {
        if (tier >= tierCount || sliceIndex < 0 || sliceIndex >= sliceRecords[tier].Count)
            return;

        SliceRecord record = sliceRecords[tier][sliceIndex];
        if (record.refCount <= 0) return; // already freed

        record.refCount--;

        if (record.refCount <= 0)
        {
            freeSlices[tier].Push(sliceIndex);
            keyToSlice.Remove((record.map, record.tier, record.face));
            record.refCount = 0;
        }

        sliceRecords[tier][sliceIndex] = record;
    }

    // ========== HEIGHTMAP NORMAL SLICE ALLOCATION ==========

    /// <summary>
    /// Allocates a normal-map slice for the given (map, normalTier, face) key.
    /// Independent ref-counted allocator from the splatmap one — normal tiers do NOT
    /// share slice indices with splat tiers (different per-LOD tier mapping).
    /// Returns the slice index within the tier's Texture2DArray, or -1 on failure / disabled.
    /// </summary>
    public int AllocateNormalSlice(Vector2SByte map, byte tier, FaceId face,
        TextureStreamer.NormalTile tile)
    {
        if (!normalsEnabled) return -1;
        if (!tile.IsValid)
        {
            Debug.LogWarning($"[ChunkMaterialManager] Invalid normal tile for ({map.x},{map.y}) tier {tier}.");
            return -1;
        }
        if (tier >= normalTierCount)
        {
            Debug.LogError($"[ChunkMaterialManager] Normal tier {tier} >= normalTierCount {normalTierCount}.");
            return -1;
        }

        var key = (map, tier, face);
        if (normalKeyToSlice.TryGetValue(key, out int existingSlice))
        {
            SliceRecord rec = normalSliceRecords[tier][existingSlice];
            rec.refCount++;
            normalSliceRecords[tier][existingSlice] = rec;
            return existingSlice;
        }

        if (normalArrays[tier] == null)
            CreateNormalArrayForTier(tier, tile.width, tile.height);

        if (tile.width != normalTierWidths[tier] || tile.height != normalTierHeights[tier])
        {
            Debug.LogError($"[ChunkMaterialManager] Normal tier {tier} resolution mismatch: " +
                $"array is {normalTierWidths[tier]}×{normalTierHeights[tier]}, " +
                $"tile is {tile.width}×{tile.height}.");
            return -1;
        }

        if (normalFreeSlices[tier].Count == 0)
            GrowNormalTierArray(tier);

        int sliceIndex = normalFreeSlices[tier].Pop();

        UploadNormalSliceData(tier, sliceIndex, tile);

        SliceRecord record = new SliceRecord
        {
            map = map,
            tier = tier,
            face = face,
            refCount = 1
        };
        while (normalSliceRecords[tier].Count <= sliceIndex)
            normalSliceRecords[tier].Add(default);
        normalSliceRecords[tier][sliceIndex] = record;
        normalKeyToSlice[key] = sliceIndex;

        return sliceIndex;
    }

    /// <summary>
    /// Releases a normal-map slice (ref-counted). Mirrors ReleaseSlice for splatmaps.
    /// </summary>
    public void ReleaseNormalSlice(int sliceIndex, byte tier)
    {
        if (!normalsEnabled) return;
        if (tier >= normalTierCount || sliceIndex < 0 || sliceIndex >= normalSliceRecords[tier].Count)
            return;

        SliceRecord record = normalSliceRecords[tier][sliceIndex];
        if (record.refCount <= 0) return;

        record.refCount--;
        if (record.refCount <= 0)
        {
            normalFreeSlices[tier].Push(sliceIndex);
            normalKeyToSlice.Remove((record.map, record.tier, record.face));
            record.refCount = 0;
        }
        normalSliceRecords[tier][sliceIndex] = record;
    }

    private void CreateNormalArrayForTier(int tier, int width, int height)
    {
        normalTierWidths[tier]        = width;
        normalTierHeights[tier]       = height;
        normalTierSliceCapacity[tier] = initialSliceCapacity;

        Texture2DArray arr = new Texture2DArray(width, height, initialSliceCapacity,
            TextureFormat.RGB24, mipChain: false, linear: true);
        arr.filterMode = FilterMode.Bilinear;
        arr.wrapMode   = TextureWrapMode.Clamp;
        arr.name       = $"Normalmap_T{tier}";
        normalArrays[tier] = arr;

        for (int i = initialSliceCapacity - 1; i >= 0; i--)
            normalFreeSlices[tier].Push(i);

        normalStagingTextures[tier] = new Texture2D(width, height, TextureFormat.RGB24, mipChain: false, linear: true);
        normalStagingTextures[tier].name = $"NormalmapStaging_T{tier}";

        UpdateMaterialNormalBindings(tier);
    }

    private void GrowNormalTierArray(int tier)
    {
        int oldCapacity = normalTierSliceCapacity[tier];
        int newCapacity = oldCapacity * 2;
        int w = normalTierWidths[tier];
        int h = normalTierHeights[tier];

        Texture2DArray oldArr = normalArrays[tier];
        Texture2DArray newArr = new Texture2DArray(w, h, newCapacity,
            TextureFormat.RGB24, mipChain: false, linear: true);
        newArr.filterMode = FilterMode.Bilinear;
        newArr.wrapMode   = TextureWrapMode.Clamp;
        newArr.name       = $"Normalmap_T{tier}";

        for (int s = 0; s < oldCapacity; s++)
            Graphics.CopyTexture(oldArr, s, 0, newArr, s, 0);

        normalArrays[tier] = newArr;
        UnityEngine.Object.Destroy(oldArr);

        for (int i = newCapacity - 1; i >= oldCapacity; i--)
            normalFreeSlices[tier].Push(i);

        normalTierSliceCapacity[tier] = newCapacity;
        UpdateMaterialNormalBindings(tier);
    }

    private void UploadNormalSliceData(int tier, int sliceIndex, TextureStreamer.NormalTile tile)
    {
        Texture2D staging = normalStagingTextures[tier];
        staging.LoadRawTextureData(tile.pixelData);
        staging.Apply(false, false);
        Graphics.CopyTexture(staging, 0, 0, normalArrays[tier], sliceIndex, 0);
    }

    private void UpdateMaterialNormalBindings(int tier)
    {
        if (tier >= MAX_NORMAL_TIER_SLOTS) return;
        if (normalArrays[tier] == null) return;
        if (sharedMaterial != null)
            sharedMaterial.SetTexture(NormalArrayPropIds[tier], normalArrays[tier]);
        if (sharedBatchedMaterial != null)
            sharedBatchedMaterial.SetTexture(NormalArrayPropIds[tier], normalArrays[tier]);
    }

    // ========== STATIC HELPERS ==========

    /// <summary>
    /// Computes UV offset and scale for a single chunk within its heightmap cell's splatmap tile.
    /// The splatmap covers the entire cell plus a border; each chunk maps to a sub-region.
    ///
    /// Returns Vector4(offsetU, offsetV, scaleU, scaleV) in [0,1] texture coordinates.
    ///
    /// chunkX, chunkY:   chunk position within the cell [0, chunksPerAxis-1]
    /// chunksPerAxis:    numberOfChunks (chunks per heightmap cell per axis)
    /// borderPixels:     SplatmapTile.borderPixels (actual border at this tile's tier)
    /// tileWidth/Height: total splatmap dimensions including border
    /// </summary>
    public static Vector4 ComputeChunkUVOffsetScale(
        int chunkX, int chunkY, int chunksPerAxis,
        float borderPixels, int tileWidth, int tileHeight)
    {
        // Core region = total minus border on each side
        float coreW = tileWidth  - 2f * borderPixels;
        float coreH = tileHeight - 2f * borderPixels;

        // Each chunk covers 1/chunksPerAxis of the core
        float scaleU = (coreW / chunksPerAxis) / tileWidth;
        float scaleV = (coreH / chunksPerAxis) / tileHeight;

        // Offset into the tile: border + chunk position × chunk size in core, normalized
        float offsetU = (borderPixels + chunkX * (coreW / chunksPerAxis)) / tileWidth;
        float offsetV = (borderPixels + chunkY * (coreH / chunksPerAxis)) / tileHeight;

        return new Vector4(offsetU, offsetV, scaleU, scaleV);
    }

    // ========== CANOPY PALETTE ==========

    /// <summary>
    /// Uploads the canopy colour palette to the terrain shader constant buffer
    /// (<c>_CanopyPalette[5]</c>). Should be called once after <see cref="Init"/> and
    /// again whenever the palette colours are changed at runtime.
    /// <para>
    /// Slots are 1-based in <see cref="TreePrototypeEntry.canopyPaletteIndex"/> but
    /// 0-based in this array: slot 1 -> <paramref name="palette"/>[0], slot 5 -> [4].
    /// </para>
    /// <para>
    /// The non-batched material also receives the upload (harmless; it never samples
    /// <c>_CanopyPalette</c>). Colours are converted to linear space before upload so
    /// the shader sees correct values regardless of the project's colour-space setting.
    /// </para>
    /// </summary>
    public void UploadCanopyPalette(Color[] palette)
    {
        const int SIZE = 5;
        var vectors = new Vector4[SIZE];
        for (int i = 0; i < SIZE; i++)
        {
            Color lin = (palette != null && i < palette.Length)
                ? palette[i].linear
                : Color.black;
            vectors[i] = new Vector4(lin.r, lin.g, lin.b, 1f);
        }
        sharedBatchedMaterial?.SetVectorArray("_CanopyPalette", vectors);
        sharedMaterial?.SetVectorArray("_CanopyPalette", vectors);
    }

    // ========== CLEANUP ==========

    /// <summary>
    /// Destroys all GPU resources (Material, Texture2DArrays, staging textures).
    /// Call from ChunkManager.OnDestroy().
    /// </summary>
    public void Dispose()
    {
        if (sharedMaterial != null)
            UnityEngine.Object.Destroy(sharedMaterial);

            if (sharedBatchedMaterial != null)
                UnityEngine.Object.Destroy(sharedBatchedMaterial);

        if (layerDiffuseArray != null)
            UnityEngine.Object.Destroy(layerDiffuseArray);

        if (dummyArray != null)
            UnityEngine.Object.Destroy(dummyArray);

        if (splatmapArrays != null)
        {
            for (int t = 0; t < tierCount; t++)
            {
                if (splatmapArrays[t] == null) continue;
                for (int g = 0; g < splatmapArrays[t].Length; g++)
                {
                    if (splatmapArrays[t][g] != null)
                        UnityEngine.Object.Destroy(splatmapArrays[t][g]);
                }
            }
        }

        if (stagingTextures != null)
        {
            for (int t = 0; t < stagingTextures.Length; t++)
            {
                if (stagingTextures[t] != null)
                    UnityEngine.Object.Destroy(stagingTextures[t]);
            }
        }

        if (normalArrays != null)
        {
            for (int t = 0; t < normalArrays.Length; t++)
            {
                if (normalArrays[t] != null)
                    UnityEngine.Object.Destroy(normalArrays[t]);
            }
        }
        if (normalStagingTextures != null)
        {
            for (int t = 0; t < normalStagingTextures.Length; t++)
            {
                if (normalStagingTextures[t] != null)
                    UnityEngine.Object.Destroy(normalStagingTextures[t]);
            }
        }
        if (dummyNormalArray != null)
            UnityEngine.Object.Destroy(dummyNormalArray);

        keyToSlice?.Clear();
        normalKeyToSlice?.Clear();
        sharedMaterial    = null;
        layerDiffuseArray = null;
        dummyArray        = null;
        splatmapArrays    = null;
        stagingTextures   = null;
        reshapeBuffers    = null;
        normalArrays      = null;
        normalStagingTextures = null;
        dummyNormalArray  = null;
    }

    // ========== INTERNAL: TIER ARRAY CREATION ==========

    /// <summary>
    /// Creates Texture2DArrays for one tier, initializes its free-list and staging texture.
    /// Called lazily on the first AllocateSlice for this tier.
    /// </summary>
    private void CreateSplatmapArraysForTier(int tier, int width, int height)
    {
        tierWidths[tier]        = width;
        tierHeights[tier]       = height;
        tierSliceCapacity[tier] = initialSliceCapacity;

        splatmapArrays[tier] = new Texture2DArray[splatGroupCount];
        for (int g = 0; g < splatGroupCount; g++)
        {
            Texture2DArray arr = new Texture2DArray(width, height, initialSliceCapacity,
                TextureFormat.RGBA32, mipChain: false, linear: true);
            arr.filterMode = FilterMode.Bilinear;
            arr.wrapMode   = TextureWrapMode.Clamp; // splatmaps don't tile
            arr.name       = $"Splatmap_T{tier}_G{g}";
            splatmapArrays[tier][g] = arr;
        }

        // Fill free-list (all slices available, push in reverse so lowest pops first)
        for (int i = initialSliceCapacity - 1; i >= 0; i--)
            freeSlices[tier].Push(i);

        // Staging texture at this tier's resolution (reused for each slice upload)
        stagingTextures[tier] = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: true);
        stagingTextures[tier].name = $"SplatmapStaging_T{tier}";

        // Pre-allocate reshape buffer
        reshapeBuffers[tier] = new byte[width * height * 4];

        // Bind new arrays to the shared material (replaces dummy)
        UpdateMaterialSplatmapBindings(tier);
    }

    // ========== INTERNAL: ARRAY GROWTH ==========

    /// <summary>
    /// Doubles the capacity of a tier's Texture2DArrays, copies existing slices via
    /// Graphics.CopyTexture, and pushes new indices onto the free-list.
    /// </summary>
    private void GrowTierArrays(int tier)
    {
        int oldCapacity = tierSliceCapacity[tier];
        int newCapacity = oldCapacity * 2;

        int w = tierWidths[tier];
        int h = tierHeights[tier];

        for (int g = 0; g < splatGroupCount; g++)
        {
            Texture2DArray oldArr = splatmapArrays[tier][g];
            Texture2DArray newArr = new Texture2DArray(w, h, newCapacity,
                TextureFormat.RGBA32, mipChain: false, linear: true);
            newArr.filterMode = FilterMode.Bilinear;
            newArr.wrapMode   = TextureWrapMode.Clamp;
            newArr.name       = $"Splatmap_T{tier}_G{g}";

            // GPU-to-GPU copy of existing slices
            for (int s = 0; s < oldCapacity; s++)
                Graphics.CopyTexture(oldArr, s, 0, newArr, s, 0);

            splatmapArrays[tier][g] = newArr;
            UnityEngine.Object.Destroy(oldArr);
        }

        // Push new indices onto free-list
        for (int i = newCapacity - 1; i >= oldCapacity; i--)
            freeSlices[tier].Push(i);

        tierSliceCapacity[tier] = newCapacity;

        // Re-bind to material (new array objects)
        UpdateMaterialSplatmapBindings(tier);

    }

    // ========== INTERNAL: PIXEL DATA UPLOAD ==========

    /// <summary>
    /// Reshapes SplatmapTile pixel data from interleaved [y][x][layer] to per-group RGBA,
    /// uploads to the staging texture, then copies to the target slice via Graphics.CopyTexture.
    /// </summary>
    private void UploadSliceData(int tier, int sliceIndex, TextureStreamer.SplatmapTile tile)
    {
        int w = tile.width;
        int h = tile.height;
        int L = tile.layerCount;
        int pixelCount = w * h;

        Texture2D staging = stagingTextures[tier];
        byte[] rgba = reshapeBuffers[tier];

        for (int g = 0; g < splatGroupCount; g++)
        {
            int baseLayer = g * 4;

            // Reshape: interleaved [y * w * L + x * L + layer] → RGBA per group
            for (int i = 0; i < pixelCount; i++)
            {
                int srcBase = i * L;
                int dstBase = i * 4;

                // Unrolled for 4 channels
                int l0 = baseLayer;
                int l1 = baseLayer + 1;
                int l2 = baseLayer + 2;
                int l3 = baseLayer + 3;

                rgba[dstBase]     = l0 < L ? tile.pixelData[srcBase + l0] : (byte)0;
                rgba[dstBase + 1] = l1 < L ? tile.pixelData[srcBase + l1] : (byte)0;
                rgba[dstBase + 2] = l2 < L ? tile.pixelData[srcBase + l2] : (byte)0;
                rgba[dstBase + 3] = l3 < L ? tile.pixelData[srcBase + l3] : (byte)0;
            }

            staging.LoadRawTextureData(rgba);
            staging.Apply(false, false);
            Graphics.CopyTexture(staging, 0, 0, splatmapArrays[tier][g], sliceIndex, 0);
        }
    }

    // ========== INTERNAL: MATERIAL BINDINGS ==========

    /// <summary>
    /// Updates the shared Material's texture properties for one tier.
    /// Called after creating or growing a tier's arrays.
    /// </summary>
    private void UpdateMaterialSplatmapBindings(int tier)
    {
        if (tier >= 4) return;

        for (int g = 0; g < splatGroupCount && g < 2; g++)
        {
            if (splatmapArrays[tier] != null && splatmapArrays[tier][g] != null)
            {
                if (sharedMaterial != null)
                    sharedMaterial.SetTexture(SplatPropIds[g][tier], splatmapArrays[tier][g]);
                if (sharedBatchedMaterial != null)
                    sharedBatchedMaterial.SetTexture(SplatPropIds[g][tier], splatmapArrays[tier][g]);
            }
        }
    }

    // ========== DEFERRED RELEASE ==========

    private List<(int sliceIndex, byte tier)> deferredReleases = new List<(int, byte)>();
    private List<(int sliceIndex, byte tier)> deferredNormalReleases = new List<(int, byte)>();

    /// <summary>
    /// Queues a slice for release at the end of the generation cycle.
    /// The slice data remains valid on GPU until CommitDeferredReleases is called,
    /// preventing stale-index visual corruption in batches that haven't been rebuilt yet.
    /// </summary>
    public void DeferRelease(int sliceIndex, byte tier)
    {
        deferredReleases.Add((sliceIndex, tier));
    }

    /// <summary>
    /// Queues a normal-map slice for release at the end of the generation cycle.
    /// </summary>
    public void DeferReleaseNormal(int sliceIndex, byte tier)
    {
        if (!normalsEnabled || sliceIndex < 0) return;
        deferredNormalReleases.Add((sliceIndex, tier));
    }

    /// <summary>
    /// Actually frees all deferred slices. Call at the end of a generation cycle,
    /// after all batches have been rebuilt.
    /// </summary>
    public void CommitDeferredReleases()
    {
        foreach (var (sliceIndex, tier) in deferredReleases)
        {
            ReleaseSlice(sliceIndex, tier);
        }
        deferredReleases.Clear();

        foreach (var (sliceIndex, tier) in deferredNormalReleases)
        {
            ReleaseNormalSlice(sliceIndex, tier);
        }
        deferredNormalReleases.Clear();
    }


}
