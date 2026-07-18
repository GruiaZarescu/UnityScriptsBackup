using UnityEngine;
using System;

// ---------------------------------------------------------------------------
// Map Object Prototype Registry
//
// Merges the roles of the old TreePrototypeRegistry and LegacyMapObjectRegistry.
// Every object placed on the map — trees, grass, rocks, buildings, props —
// has a prototype entry here. The entry defines:
//   - LOD meshes for GPU instancing (LOD1+)
//   - GameObjects for LOD0 interactive objects
//   - Blotch parameters for the procedural foliage pipeline
//   - Canopy overlay settings for far-LOD terrain
//
// Index in the entries[] array = the prototypeIndex used in baked cell files.
// ---------------------------------------------------------------------------

/// <summary>
/// Per-entry LOD configuration for the canopy mask system.
/// Controls the per-chunk mask texture that softens tree blobs at far LODs.
/// </summary>
[Serializable]
public class CanopyMaskSettings
{
    [Tooltip("Enable per-chunk canopy mask texture for this LOD. Disabled = uses vertex-only alpha (no mask).")]
    public bool enabled = true;
    [Tooltip("Resolution of the canopy mask texture per chunk (e.g. 16 = 16x16 pixels). "
           + "Higher = smoother edges but more atlas memory. 8-16 is usually sufficient for far LODs.")]
    public int maskSize = 16;
    [Range(0.1f, 1f), Tooltip("Softness of tree footprint edges in the mask. "
           + "0.1 = hard edges, 0.5 = soft, 1.0 = very soft / wide.")]
    public float softRadius = 0.35f;
    [Range(0f, 1f), Tooltip("Opacity of this prototype's canopy overlay at this LOD. "
           + "1.0 = fully opaque, 0.0 = invisible. Scales how much each tree blob contributes.")]
    public float alphaMultiplier = 1f;
}

/// <summary>
/// Size variability mode for procedural instance scaling.
/// Uniform: single scale factor applied to both height and width.
/// HeightWidth: separate height/width factors, but width tracks height proportionally.
/// </summary>
public enum SizeVariabilityMode
{
    Uniform,      // Single scale factor for both dimensions
    HeightWidth   // Separate height/width, width proportional to height
}

/// <summary>
/// Per-prototype size variability settings.
/// Enables deterministic, repeatable size variation for instances.
/// </summary>
[Serializable]
public class SizeVariabilitySettings
{
    [Tooltip("Enable size variability for this prototype. Disabled = all instances at scale 1.0.")]
    public bool enabled = false;

    [Tooltip("Uniform mode: single scale factor. HeightWidth mode: separate height/width factors.")]
    public SizeVariabilityMode mode = SizeVariabilityMode.Uniform;

    // ── Uniform mode settings ─────────────────────────────────────────────
    [Header("Uniform Mode")]
    [Tooltip("Minimum uniform scale factor (e.g. 0.7 = 70% of base size).")]
    [Range(0.1f, 2f)] public float minUniformScale = 0.7f;
    [Tooltip("Maximum uniform scale factor (e.g. 1.3 = 130% of base size).")]
    [Range(0.1f, 3f)] public float maxUniformScale = 1.3f;

    // ── Height/Width mode settings ────────────────────────────────────────
    [Header("Height/Width Mode")]
    [Tooltip("Minimum height scale factor.")]
    [Range(0.1f, 2f)] public float minHeightScale = 0.8f;
    [Tooltip("Maximum height scale factor.")]
    [Range(0.1f, 3f)] public float maxHeightScale = 1.2f;
    [Tooltip("Minimum width scale factor (relative to base, not height).")]
    [Range(0.1f, 2f)] public float minWidthScale = 0.8f;
    [Tooltip("Maximum width scale factor (relative to base, not height).")]
    [Range(0.1f, 3f)] public float maxWidthScale = 1.2f;

    // ── Distribution curve ────────────────────────────────────────────────
    [Header("Distribution")]
    [Tooltip("Steepness of the distribution curve. "
           + "1.0 = uniform triangular (linear falloff from center). "
           + ">1.0 = tighter clustering around center (fewer extremes). "
           + "<1.0 = more extremes, less center clustering.")]
    [Range(0.2f, 5f)] public float distributionSteepness = 2.0f;

    /// <summary>
    /// Packs settings into a float4 for GPU upload.
    /// x = minScale (uniform) or minHeightScale
    /// y = maxScale (uniform) or maxHeightScale
    /// z = minWidthScale (0 if uniform mode)
    /// w = maxWidthScale (0 if uniform mode)
    /// Mode and steepness are packed into separate buffer.
    /// </summary>
    public Vector4 PackForGPU()
    {
        if (mode == SizeVariabilityMode.Uniform)
        {
            return new Vector4(minUniformScale, maxUniformScale, 0f, 0f);
        }
        else
        {
            return new Vector4(minHeightScale, maxHeightScale, minWidthScale, maxWidthScale);
        }
    }

    /// <summary>
    /// Packs mode and steepness for GPU.
    /// x = mode (0 = Uniform, 1 = HeightWidth)
    /// y = distributionSteepness
    /// </summary>
    public Vector2 PackModeAndSteepness()
    {
        return new Vector2((float)mode, distributionSteepness);
    }

    // =====================================================================
    // CPU-SIDE SIZE COMPUTATION (for prefab pipeline)
    // Must match GPU BellCurveDistribution + ComputeSizeScales exactly.
    // =====================================================================

    /// <summary>
    /// PCG hash for deterministic random generation (matches GPU version).
    /// </summary>
    private static uint PCGHash(uint input)
    {
        uint state = input * 747796405u + 2891336453u;
        uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
        return (word >> 22) ^ word;
    }

    /// <summary>
    /// Computes deterministic height and width scales for the given seed.
    /// Uses the same bell-curve distribution as the GPU version.
    /// </summary>
    /// <param name="seed">Deterministic seed (e.g., blotchSeed << 16 | instanceID)</param>
    /// <returns>Vector2(heightScale, widthScale)</returns>
    public Vector2 ComputeSizeScalesCPU(uint seed)
    {
        if (!enabled)
            return Vector2.one;

        // Two independent uniform values
        uint h1 = PCGHash(seed);
        uint h2 = PCGHash(h1);
        float u1 = h1 / 4294967296f;
        float u2 = h2 / 4294967296f;

        // Average gives triangular distribution (peaks at 0.5)
        float bell = (u1 + u2) * 0.5f;

        // Distance from center [0,1]
        float dist = Mathf.Abs(bell - 0.5f) * 2f;

        // Apply steepness
        float shaped = Mathf.Pow(dist, distributionSteepness);

        // Map back to [0,1], preserving which side of center
        float t = bell >= 0.5f ? 0.5f + shaped * 0.5f : 0.5f - shaped * 0.5f;

        if (mode == SizeVariabilityMode.Uniform)
        {
            float scale = Mathf.Lerp(minUniformScale, maxUniformScale, t);
            return new Vector2(scale, scale);
        }
        else // HeightWidth mode
        {
            float heightScale = Mathf.Lerp(minHeightScale, maxHeightScale, t);
            float widthScale = Mathf.Lerp(minWidthScale, maxWidthScale, t);
            return new Vector2(heightScale, widthScale);
        }
    }
}

/// <summary>
/// Runtime registry mapping prototypeIndex to all map object data.
/// Every placed object — trees, grass, rocks, buildings, foliage, props —
/// has an entry here. Index in entries[] = prototypeIndex from baked data.
/// </summary>
[CreateAssetMenu(fileName = "MapObjectPrototypeRegistry", menuName = "STPTME/Map Object Prototype Registry")]
public class MapObjectPrototypeRegistry : ScriptableObject
{
    [Serializable]
    public class MapObjectPrototypeEntry
    {
        [Tooltip("Display name for editor reference")]
        public string name;

        // ── LOD meshes ──────────────────────────────────────────────────────
        [Tooltip("Meshes per LOD level. Index 0 = highest detail (LOD0), etc. "
               + "LOD0 meshes are used by GameObjects. "
               + "LOD0+ meshes are used by GPU instancing for LOD1+ chunks.")]
        public Mesh[] lodMeshes;

        [Tooltip("Optional per-LOD GameObjects for spawning at any LOD. "
               + "LOD0 always spawns from sourcePrefab (or lodGameObjects[0] if set). "
               + "When shouldInstance=false, LOD1+ also uses these GameObjects instead of instancing.")]
        public GameObject[] lodGameObjects;

        [Tooltip("Shared material for all LODs (should support GPU instancing when shouldInstance=true).")]
        public Material material;

        // ── Dimensions ──────────────────────────────────────────────────────
        [Tooltip("Base width in world units (scale=1.0). Instance's widthScale multiplies this.")]
        public float baseWidth = 1f;

        [Tooltip("Base height in world units (scale=1.0). Instance's heightScale multiplies this.")]
        public float baseHeight = 1f;

        [Tooltip("A global offset applied to each instance's height.")]
        public float heightOffset = 0f;

        [Tooltip("An additional height offset applied ONLY to LOD1+ instances to correct for global heightmap (RHalf) precision loss.")]
        public float lod1PlusHeightOffset = 0f;

        // ── Instance / pool behaviour ──────────────────────────────────────
        [Tooltip("If true, LOD1+ uses GPU instancing (blotch-based procedural pipeline). "
               + "If false, LOD1+ spawns GameObjects like LOD0. "
               + "LOD0 always spawns GameObjects regardless of this flag.")]
        public bool shouldInstance = true;

        [Tooltip("If true, this prototype is GPU-instanced at ALL LODs including LOD0. "
               + "Use for dense foliage (grass, algae) that has no colliders. "
               + "LOD0 GameObjects are NOT spawned for these prototypes. "
               + "Ignored when shouldInstance=false.")]
        public bool instanceAlways = false;

        // ── Blotch parameters ──────────────────────────────────────────────
        // ALL prototypes have blotch parameters, even non-instanced ones.
        // For single trees: radius=0, density=1. For grass clumps: radius>0, density>1.
        // These are baked into BlotchData by MeshSaver for the procedural pipeline.

        [Header("Blotch Parameters (all prototypes)")]
        [Tooltip("Blotch radius in meters. 0 = single-instance (exact position). "
               + ">0 = procedural cluster around center.")]
        public float blotchRadius = 0f;

        [Tooltip("Blotch density in instances per square meter. "
               + "For single trees: 1. For grass: 10-50. "
               + "Multiplied by per-LOD density multiplier at runtime for pruning.")]
        public float blotchDensity = 1f;

        [Tooltip("Conflict category for the grid competition system. "
               + "1 = Grass, 2 = Canopy, 4 = Trunk. "
               + "Grass allows canopies on top, trunks block everything.")]
        public byte conflictCategory = 4; // Default to Trunk (blocks all)

        [Tooltip("Override LOD at which this prototype is culled (hidden). "
               + "255 = use the global cull LOD from BlotchExpansionDefines. "
               + "e.g. cullLOD=2 means this prototype is hidden from LOD3 onward.")]
        public byte cullLOD = 255;

        // ── Size variability ────────────────────────────────────────────────
        [Header("Size Variability")]
        [Tooltip("Deterministic size variation per instance. Same seed = same size in both prefab and instanced pipelines.")]
        public SizeVariabilitySettings sizeVariability = new SizeVariabilitySettings();

        // ── Material color override ─────────────────────────────────────────
        [Header("Material Color Override")]
        [Tooltip("Override the impostor material color. Leave white (1,1,1,1) to use the material's _Color property.")]
        public Color impostorColorOverride = Color.white;

        // ── Prefab reference ───────────────────────────────────────────────
        [Tooltip("Original prefab reference (for editor/debug LOD0 spawning, not used at runtime)")]
        public GameObject sourcePrefab;

        // ===== Far-LOD canopy vertex overlay =====
        // Written into UV0.x of batched (far-LOD) chunk meshes during ChunkBatcher.Add().
        // In the batched shader path UV0 is free (splatmap UV lives in UV1.xy instead),
        // so UV0.x is repurposed to carry an integer canopy palette index per vertex:
        //   0..4  = palette slot -> MapObjectPrototypeRegistry.canopyPalette[index]
        //
        // All 3 verts of any triangle whose footprint contains >= 1 tree of a canopy-enabled
        // prototype are stamped with canopyPaletteIndex at mesh-build time (ChunkBatcher.Add).
        // nointerpolation in the shader's Varyings keeps the value crisp at triangle edges;
        // since every vert in a canopy triangle shares the same index this is lossless.
        //
        // Non-batched (LOD0) chunks use UV0 for terrain splat UVs and are NEVER affected.
        // Re-entering play mode is required when canopyOverlayEnabled/canopyPaletteIndex changes
        // (UV0.x is written at runtime mesh-build time, not serialized).
        // Colour changes to MapObjectPrototypeRegistry.canopyPalette do NOT require re-baking.

        [Header("Far-LOD Canopy Vertex Overlay")]
        [Tooltip("Mark batched far-LOD chunk triangles containing instances of this prototype with a "
               + "solid canopy colour overlay. No effect on non-batched (close LOD0) chunks.")]
        public bool canopyOverlayEnabled = false;

        [Tooltip("Zero-based palette slot from MapObjectPrototypeRegistry.canopyPalette. "
             + "Valid range is 0..4 for the default 5-color palette.")]
        public int canopyPaletteIndex = 0;

        [Header("Per-LOD Canopy Mask Settings")]
        [Tooltip("Per-LOD canopy mask configuration for this prototype. "
               + "Controls mask resolution, blob soft-radius and opacity per LOD. "
               + "LOD 0 is disabled by default (close chunks show real objects, not blobs).")]
        public CanopyMaskSettings[] canopyMaskByLOD = new CanopyMaskSettings[]
        {
            new CanopyMaskSettings { enabled = false, maskSize = 8  },  // LOD 0 — real objects visible
            new CanopyMaskSettings { enabled = true,  maskSize = 32 },  // LOD 1
            new CanopyMaskSettings { enabled = true,  maskSize = 32 },  // LOD 2
            new CanopyMaskSettings { enabled = true,  maskSize = 32 },  // LOD 3
            new CanopyMaskSettings { enabled = true,  maskSize = 16 },  // LOD 4
            new CanopyMaskSettings { enabled = true,  maskSize = 16 },  // LOD 5
            new CanopyMaskSettings { enabled = true,  maskSize = 16 },  // LOD 6
            new CanopyMaskSettings { enabled = true,  maskSize = 8  },  // LOD 7
        };

        [Header("Density Gradient Across Blotch Radius")]
        [Tooltip("Enable density gradient across the blotch radius.")]
        public bool densityFadeEnabled = false;
        [Range(0f, 1f), Tooltip("Density fade start radius (0 = center of blotch).")]
        public float densityFadeStart = 0f;
        
        [Header("Use screen space LOD instead of per-chunk LOD")]
        public bool useDistanceLOD = false;

        [Tooltip("LEGACY. Kept for migration; the variable array below is what the GPU reads.")]
        public Vector4 lodDistances = new Vector4(20f, 40f, 80f, 120f);

        [Tooltip("Variable-length LOD distance thresholds (replaces the Vector4 limit). "
            + "Element i = max screen-space distance for LOD i. The last element is the cull distance. "
            + "Length is unbounded — add as many LODs as the mesh array supports.")]
        public float[] lodDistancesVariable = new float[] { 20f, 40f, 80f, 120f };

        [Tooltip("Per-LOD density keep fraction for distance-mode pruning. 1 = keep all. "
            + "Length should match lodDistancesVariable. e.g. last entry 0.5 = 50% at the furthest LOD. "
            + "Pruning is probabilistic and stable per-instance (no shimmer).")]
        [Range(0f, 1f)] public float[] lodKeepFractions = new float[] { 1f, 1f, 1f, 1f };

        /// <summary>
        /// Returns canopy mask settings for this prototype at the given chunk LOD.
        /// The index is clamped to the last valid tree LOD (lodMeshes.Length - 1) so that
        /// extra canopyMaskByLOD entries beyond the prototype's actual LOD count are ignored,
        /// and the last entry is reused for all further-out chunks.
        /// Returns null if the array is empty or lod is negative.
        /// </summary>
        public CanopyMaskSettings GetCanopyMaskSettingsForLOD(int lod)
        {
            if (canopyMaskByLOD == null || canopyMaskByLOD.Length == 0 || lod < 0)
                return null;
            int maxLOD = (lodMeshes != null && lodMeshes.Length > 0) ? lodMeshes.Length - 1 : canopyMaskByLOD.Length - 1;
            int clampedLod = Mathf.Min(lod, maxLOD);
            clampedLod = Mathf.Min(clampedLod, canopyMaskByLOD.Length - 1);
            return canopyMaskByLOD[clampedLod];
        }


        // ---- Cached mesh geometry (populated by CacheMeshData) ----
        [NonSerialized] public bool meshDataCached;
        [NonSerialized] public bool cachedIsZOriented;
        [NonSerialized] public float cachedInvMeshHeight;
        [NonSerialized] public float cachedInvMeshWidth;
        [NonSerialized] public Vector3 cachedMeshBaseAnchor;
        [NonSerialized] public float cachedBillboardInvWidth;
        [NonSerialized] public float cachedBillboardInvHeight;
        [NonSerialized] public Vector3 cachedBillboardBaseAnchor;

        /// <summary>
        /// Pre-compute per-prototype mesh geometry constants.
        /// </summary>
        public void CacheMeshData()
        {
            Mesh mesh = GetMeshForLOD(0);
            Vector3 boundsSize = mesh != null ? mesh.bounds.size : Vector3.one;

            cachedIsZOriented = boundsSize.z > boundsSize.y * 1.5f;

            float meshHeight = cachedIsZOriented ? boundsSize.z : boundsSize.y;
            float meshWidth = Mathf.Max(boundsSize.x, cachedIsZOriented ? boundsSize.y : boundsSize.z);
            meshHeight = Mathf.Max(meshHeight, 0.01f);
            meshWidth = Mathf.Max(meshWidth, 0.01f);
            cachedInvMeshHeight = 1f / meshHeight;
            cachedInvMeshWidth = 1f / meshWidth;

            if (mesh != null)
            {
                Bounds bounds = mesh.bounds;
                Vector3 center = bounds.center;
                if (cachedIsZOriented)
                {
                    float baseZ = center.z <= 0f ? bounds.max.z : bounds.min.z;
                    cachedMeshBaseAnchor = new Vector3(center.x, center.y, baseZ);
                }
                else
                {
                    cachedMeshBaseAnchor = new Vector3(center.x, bounds.min.y, center.z);
                }
            }
            else
            {
                cachedMeshBaseAnchor = Vector3.zero;
            }

            // Cache billboard-specific bounds (last lodMesh = implicit billboard when >= 2 LODs).
            if (lodMeshes.Length >= 2)
            {
                Mesh bbMesh = lodMeshes[lodMeshes.Length - 1];
                if (bbMesh != null)
                {
                    Bounds b = bbMesh.bounds;
                    float bbW = Mathf.Max(Mathf.Max(b.size.x, b.size.z), 0.01f);
                    float bbH = Mathf.Max(b.size.y, 0.01f);
                    cachedBillboardInvWidth  = 1f / bbW;
                    cachedBillboardInvHeight = 1f / bbH;
                    cachedBillboardBaseAnchor = new Vector3(b.center.x, b.min.y, b.center.z);
                }
                else
                {
                    cachedBillboardInvWidth  = cachedInvMeshWidth;
                    cachedBillboardInvHeight = cachedInvMeshHeight;
                    cachedBillboardBaseAnchor = cachedMeshBaseAnchor;
                }
            }
            else
            {
                cachedBillboardInvWidth  = cachedInvMeshWidth;
                cachedBillboardInvHeight = cachedInvMeshHeight;
                cachedBillboardBaseAnchor = cachedMeshBaseAnchor;
            }

            meshDataCached = true;
        }

        public bool ShouldSpawnAsPrefabAtLOD(int chunkLOD)
        {
            // Case 1: Never instance. Always spawn prefab at all LODs.
            if (!shouldInstance) return true;
            
            // Case 3: Always instance. Never spawn prefab.
            if (instanceAlways) return false;
            
            // Case 2: Instance at LOD1+, but spawn prefab at LOD0.
            return chunkLOD == 0;
        }

        /// <summary>
        /// Returns mesh for given LOD, clamped to available LODs.
        /// </summary>
        public Mesh GetMeshForLOD(int lod)
        {
            if (lodMeshes == null || lodMeshes.Length == 0) return null;
            int clampedLOD = Mathf.Clamp(lod, 0, lodMeshes.Length - 1);
            return lodMeshes[clampedLOD];
        }

        /// <summary>Returns true if this entry has valid render data.</summary>
        public bool IsValid => lodMeshes != null && lodMeshes.Length > 0 && lodMeshes[0] != null && material != null;

        /// <summary>True when the prototype has >= 2 LODs so the last entry is used as a billboard.</summary>
        public bool HasBillboardLOD => lodMeshes != null && lodMeshes.Length >= 2;

        /// <summary>Returns true if this entry should use GPU instancing for LOD1+.</summary>
        /// LOD0 always uses GameObjects for non-always-instanced prototypes.
        public bool IsInstancedLOD1Plus => shouldInstance && IsValid && !instanceAlways;

        /// <summary>Returns true if this entry should be GPU-instanced at the given chunk LOD.
        /// For LOD0: only if <see cref="instanceAlways"/> is true.
        /// For LOD1+: if <see cref="shouldInstance"/> is true.
        /// </summary>
        public bool IsInstancedAtLOD(int chunkLOD)
        {
            if (!IsValid) return false;
            if (instanceAlways) return true;
            if (chunkLOD == 0) return false;
            return shouldInstance;
        }
    }

    [Tooltip("One entry per prototype index. Index in this array = prototypeIndex from baked data.")]
    public MapObjectPrototypeEntry[] entries;

    // ── LOD pruning density ──────────────────────────────────────────────
    [Tooltip("Percentage of instances kept for each mesh LOD. Index 0 = keep percentage for LOD1, "
           + "index 1 = LOD2, etc. LOD0 is always kept at 100%. "
           + "The solve loop simply multiplies blotch density by this fraction. "
           + "If a LOD exceeds this array, the last available element is reused.")]
    public int[] densityPerLOD = new int[] { 100, 90, 80, 75, 70 };

    [Header("LOD Width Multipliers (Density Compensation)")]
    [Tooltip("Per-LOD horizontal width multiplier applied at draw time to visually compensate "
           + "for reduced instance density at far LODs. Index = LOD (0 = LOD0, 1 = LOD1, ...). "
           + "LOD0 should stay 1.0. Height is never modified. "
           + "Pairs with densityPerLOD: e.g. 90% density + 1.1x width ~ same perceived coverage.")]
    public float[] lodWidthMultipliers = new float[] { 1f, 1.1f, 1.2f, 1.4f, 1.5f };

    // ── Far-LOD canopy colour palette ────────────────────────────────────
    [Header("Far-LOD Canopy Palette")]
    [ColorUsage(showAlpha: false),
     Tooltip("Up to 5 canopy colours for the far-LOD batched terrain shader. "
           + "canopyPaletteIndex 0 -> slot [0], index 4 -> slot [4].")]
    public Color[] canopyPalette = new Color[5]
    {
        new Color(0.18f, 0.32f, 0.12f), // slot 0 - dark conifer
        new Color(0.30f, 0.42f, 0.18f), // slot 1 - medium deciduous
        new Color(0.38f, 0.50f, 0.22f), // slot 2 - light birch / poplar
        new Color(0.22f, 0.30f, 0.10f), // slot 3 - dense spruce
        new Color(0.40f, 0.48f, 0.28f), // slot 4 - mixed light forest
    };

    [Header("Canopy Mask Atlas")]
    [Tooltip("Global atlas size for canopy mask tiles. Must be a power of two.")]
    public int canopyMaskAtlasSize = 512;

    // ── Lookup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Gets prototype entry by index, or null if invalid.
    /// </summary>
    public MapObjectPrototypeEntry GetEntry(int prototypeIndex)
    {
        if (entries == null || prototypeIndex < 0 || prototypeIndex >= entries.Length)
            return null;
        return entries[prototypeIndex];
    }

    // ── Density / width helpers ──────────────────────────────────────────

    /// <summary>
    /// Returns the percentage of instances to keep for a given mesh LOD.
    /// LOD0 always returns 100% (never reduced).
    /// LOD1+ indices into densityPerLOD array with offset: array[LOD-1].
    /// </summary>
    public int GetDensityPercentForLOD(int lod)
    {
        if (lod == 0) return 100;
        if (densityPerLOD == null || densityPerLOD.Length == 0) return 100;
        int index = Mathf.Clamp(lod - 1, 0, densityPerLOD.Length - 1);
        return Mathf.Clamp(densityPerLOD[index], 0, 100);
    }

    /// <summary>
    /// Returns the draw-time horizontal width multiplier for a given LOD.
    /// LOD0 defaults to 1.0 (interactive objects should not be resized).
    /// </summary>
    public float GetWidthMultiplierForLOD(int lod)
    {
        if (lodWidthMultipliers == null || lodWidthMultipliers.Length == 0) return 1f;
        return lodWidthMultipliers[Mathf.Clamp(lod, 0, lodWidthMultipliers.Length - 1)];
    }

    // ── Validation ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates all entries and logs warnings for invalid ones.
    /// </summary>
    public void ValidateAll()
    {
        if (entries == null)
        {
            Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: No entries assigned");
            return;
        }

        for (int i = 0; i < entries.Length; i++)
        {
            var p = entries[i];
            if (p == null)
            {
                Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] is null");
                continue;
            }

            if (p.shouldInstance)
            {
                if (p.lodMeshes == null || p.lodMeshes.Length == 0)
                    Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] '{p.name}' has no LOD meshes (required for LOD1+ instancing)");
                else if (p.lodMeshes[0] == null)
                    Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] '{p.name}' LOD0 mesh is null");

                if (p.material == null)
                    Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] '{p.name}' has no material (required for LOD1+ instancing)");
                else if (!p.material.enableInstancing)
                    Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] '{p.name}' material doesn't have GPU Instancing enabled");
            }
            else
            {
                if (p.sourcePrefab == null)
                    Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] '{p.name}' has no sourcePrefab (required when shouldInstance=false)");
            }

            if (p.canopyMaskByLOD != null && p.lodMeshes != null
                && p.canopyMaskByLOD.Length > p.lodMeshes.Length)
                Debug.LogWarning($"[MapObjectPrototypeRegistry] {name}: Entry[{i}] '{p.name}' "
                    + $"canopyMaskByLOD has {p.canopyMaskByLOD.Length} entries but only "
                    + $"{p.lodMeshes.Length} LOD mesh(es). "
                    + $"Entries [{p.lodMeshes.Length}..{p.canopyMaskByLOD.Length - 1}] will be ignored; "
                    + $"entry [{p.lodMeshes.Length - 1}] is used for all further LODs.");

            p.CacheMeshData();
        }
    }

    private void OnValidate()
    {
        // Auto-populate names from source prefabs in editor
        if (entries != null)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var p = entries[i];
                if (p == null) continue;
                if (string.IsNullOrEmpty(p.name) && p.sourcePrefab != null)
                    p.name = p.sourcePrefab.name;
            }
        }
    }
}