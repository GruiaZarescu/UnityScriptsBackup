#if false
using UnityEngine;
using System;

// ---------------------------------------------------------------------------
// Tree collider shape types
// ---------------------------------------------------------------------------

/// <summary>Shape geometry used for a tree collider.</summary>
public enum TreeColliderType
{
    Capsule,
    Mesh
}

/// <summary>
/// Purpose of a tree collider shape.
/// Physical shapes block movement and participate in raycasts.
/// Trigger shapes fire events (e.g. OnPlayerInsideLeaves) without blocking.
/// Keeping these separate from the start allows different MonoBehaviours,
/// layer masks, and physics materials to be attached per purpose later.
/// </summary>
public enum TreeColliderPurpose
{
    Physical,
    Trigger
}

/// <summary>Axis alignment for a capsule collider (mirrors CapsuleCollider.direction).</summary>
public enum CapsuleColliderAxis
{
    X = 0,
    Y = 1,
    Z = 2
}

// Bake-time canopy overlay is now fully runtime-based via per-vertex alpha blending.
// TreeOverlayShape enum removed (no longer used).

/// <summary>
/// Describes one collider shape attached to a tree prototype.
/// A single prototype may carry any number of these — for example a trunk
/// capsule (Physical) and a canopy sphere-approximated capsule (Trigger).
/// </summary>
[Serializable]
public class TreeColliderShape
{
    [Tooltip("Human-readable label shown in the inspector (e.g. 'Trunk', 'Canopy').")]
    public string label = "Collider";

    [Tooltip("Capsule: specify parameters manually. Mesh: drag a convex mesh asset.")]
    public TreeColliderType type = TreeColliderType.Capsule;

    [Tooltip("Physical shapes block movement. Trigger shapes raise overlap events without blocking.")]
    public TreeColliderPurpose purpose = TreeColliderPurpose.Physical;

    // --- Capsule parameters (used when type == Capsule) ---

    [Tooltip("Radius of the capsule in local space (scaled by tree widthScale at runtime).")]
    public float radius = 0.3f;

    [Tooltip("Total height of the capsule in local space (scaled by tree heightScale at runtime).")]
    public float height = 5f;

    [Tooltip("Position of this collider's center relative to the tree base in local tree space. " +
             "This offset is applied before the collider's local rotation, so tilted branch colliders rotate around their own shifted center.")]
    public Vector3 center = Vector3.zero;

    [Tooltip("Local Euler rotation of this collider shape in tree space. Use this to tilt branch colliders away from the trunk.")]
    public Vector3 localEulerAngles = Vector3.zero;

    [Tooltip("Long axis of the capsule.")]
    public CapsuleColliderAxis axis = CapsuleColliderAxis.Y;

    // --- Mesh parameters (used when type == Mesh) ---

    [Tooltip("Mesh to use as a MeshCollider. Should be kept low-poly. Ignored when type is Capsule.")]
    public Mesh colliderMesh;

    [Tooltip("Whether the MeshCollider should be convex (required for trigger mode and Rigidbody interaction).")]
    public bool convex = true;

    // ---------------------------------------------------------------------------
    // Future extensibility hook: trigger event configuration.
    // Not yet implemented — adding the field later won't require restructuring
    // the pool or the shape array because purpose is already authoritative.
    // public TriggerEventData triggerEvent;
    // ---------------------------------------------------------------------------

    /// <summary>Returns a display name combining label and purpose.</summary>
    public override string ToString() => $"{label} ({type}, {purpose})";
}

/// <summary>
/// Runtime registry mapping prototypeIndex → renderable tree data.
/// Assign this via inspector with meshes extracted from your tree prefabs.
/// Chunk LOD maps to tree mesh LOD: LOD0 chunk uses lodMeshes[0], etc.
///
/// NOTE: This class is being superseded by MapObjectPrototypeRegistry
/// which merges TreePrototypeRegistry + MapObjectRegistry into a single
/// registry for all map objects (trees, grass, buildings, props).
/// All new code should use MapObjectPrototypeRegistry instead.
/// This class is retained for backward compatibility with TreeRenderer
/// during the migration period.
/// </summary>
[CreateAssetMenu(fileName = "TreePrototypeRegistry", menuName = "STPTME/Tree Prototype Registry")]
public class TreePrototypeRegistry : ScriptableObject
{
    [Serializable]
    public class TreePrototypeEntry
    {
        [Tooltip("Display name for editor reference")]
        public string name;

        [Tooltip("Meshes per LOD level. Index 0 = highest detail (LOD0), etc.")]
        public Mesh[] lodMeshes;

        [Tooltip("Optional per-prototype distance-by-LOD table. Index = tree LOD, Value = max chunk distance for that LOD. -1 = cull beyond previous LOD. Leave empty to fall back to the registry-wide default.")]
        public float[] treeDistanceByLOD;

        [Tooltip("Shared material for all LODs (should support GPU instancing)")]
        public Material material;

        [Tooltip("Base width in world units (scale=1.0). Tree's widthScale multiplies this.")]
        public float baseWidth = 1f;

        [Tooltip("Base height in world units (scale=1.0). Tree's heightScale multiplies this.")]
        public float baseHeight = 1f;

        [Tooltip("A global offset applied to each tree's height.")]
        public float heightOffset = 0f;

        [Tooltip("Collider shapes for this tree prototype. Evaluated in order. "
            + "Physical shapes block movement; Trigger shapes are isTrigger and intended for overlap events. "
            + "Leave empty for no collision on this prototype.")]
        public TreeColliderShape[] colliderShapes;

        [Tooltip("Original prefab reference (for editor/debug LOD0 spawning, not used at runtime)")]
        public GameObject sourcePrefab;

        // ── Blotch parameters (all entries, even non-instanced) ────────────
        // ALL trees are treated as blotches by MeshSaver during bake.
        // A single tree: radius=0, density=1. Grass clump: radius>0, density>1.

        [Header("Blotch Parameters")]
        [Tooltip("Blotch radius in meters. 0 = single-instance (exact position). "
               + ">0 = procedural cluster around center. Serialized into BlotchData.")]
        public float blotchRadius = 0f;

        [Tooltip("Blotch density in instances per square meter. "
               + "For single trees: 1. For grass: 10-50. "
               + "Multiplied by per-LOD density multiplier at runtime for pruning.")]
        public float blotchDensity = 1f;

        [Tooltip("Conflict category for the grid competition system. "
               + "1 = Grass, 2 = Canopy, 4 = Trunk (blocks all). "
               + "Grass allows canopies on top; trunks block everything.")]
        public byte conflictCategory = 4; // Default to Trunk (blocks all)

        [Tooltip("Override LOD at which this prototype is culled (hidden). "
               + "255 = use the global cull LOD from BlotchExpansionDefines. "
               + "e.g. cullLOD=2 means hidden from LOD3 onward.")]
        public byte cullLOD = 255;

        // ===== Runtime far-LOD canopy vertex overlay =====
        // Written into UV0.x of batched (far-LOD) chunk meshes during ChunkBatcher.Add().
        // In the batched shader path UV0 is free (splatmap UV lives in UV1.xy instead),
        // so UV0.x is repurposed to carry an integer canopy palette index per vertex:
        //   0..4  = palette slot -> TreePrototypeRegistry.canopyPalette[index]
        //
        // All 3 verts of any triangle whose footprint contains >= 1 tree of a canopy-enabled
        // prototype are stamped with canopyPaletteIndex at mesh-build time (ChunkBatcher.Add).
        // nointerpolation in the shader's Varyings keeps the value crisp at triangle edges;
        // since every vert in a canopy triangle shares the same index this is lossless.
        //
        // Non-batched (LOD0) chunks use UV0 for terrain splat UVs and are NEVER affected.
        // Re-entering play mode is required when canopyOverlayEnabled/canopyPaletteIndex changes
        // (UV0.x is written at runtime mesh-build time, not serialized).
        // Colour changes to TreePrototypeRegistry.canopyPalette do NOT require re-baking.

        [Header("Far-LOD Canopy Vertex Overlay")]
        [Tooltip("Mark batched far-LOD chunk triangles containing trees of this prototype with a "
               + "solid canopy colour overlay. No effect on non-batched (close LOD0) chunks.")]
        public bool canopyOverlayEnabled = false;

         [Tooltip("Zero-based palette slot from TreePrototypeRegistry.canopyPalette. "
             + "Valid range is 0..4 for the default 5-color palette.")]
         public int canopyPaletteIndex = 0;

        [Header("Per-LOD Canopy Mask Settings")]
        [Tooltip("Per-LOD canopy mask configuration for this prototype. "
               + "Controls mask resolution, blob soft-radius and opacity per LOD. "
               + "LOD 0 is disabled by default (close chunks show real trees, not blobs).")]
        public CanopyMaskSettings[] canopyMaskByLOD = new CanopyMaskSettings[]
        {
            new CanopyMaskSettings { enabled = false, maskSize = 8  },  // LOD 0 — real trees visible
            new CanopyMaskSettings { enabled = true,  maskSize = 32 },  // LOD 1
            new CanopyMaskSettings { enabled = true,  maskSize = 32 },  // LOD 2
            new CanopyMaskSettings { enabled = true,  maskSize = 32 },  // LOD 3
            new CanopyMaskSettings { enabled = true,  maskSize = 16 },  // LOD 4
            new CanopyMaskSettings { enabled = true,  maskSize = 16 },  // LOD 5
            new CanopyMaskSettings { enabled = true,  maskSize = 16 },  // LOD 6
            new CanopyMaskSettings { enabled = true,  maskSize = 8  },  // LOD 7
        };

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
            // Clamp to the highest actual tree LOD so extra entries are silently ignored.
            int maxTreeLOD = (lodMeshes != null && lodMeshes.Length > 0) ? lodMeshes.Length - 1 : canopyMaskByLOD.Length - 1;
            int clampedLod = Mathf.Min(lod, maxTreeLOD);
            // Also clamp to the array length in case canopyMaskByLOD is shorter than lodMeshes.
            clampedLod = Mathf.Min(clampedLod, canopyMaskByLOD.Length - 1);
            return canopyMaskByLOD[clampedLod];
        }

        // ===== Bake-time canopy overlay (DEPRECATED) =====
        // Old texture-based splatmap overlay system. Replaced by runtime per-vertex alpha blending.
        // Fields kept for backward compatibility; no longer consumed by the bake.

        // ---- Cached mesh geometry (populated by CacheMeshData) ----
        [NonSerialized] public bool meshDataCached;
        [NonSerialized] public bool cachedIsZOriented;
        [NonSerialized] public float cachedInvMeshHeight;
        [NonSerialized] public float cachedInvMeshWidth;
        [NonSerialized] public Vector3 cachedMeshBaseAnchor;
        // Billboard-specific cached bounds (last lodMesh = implicit billboard tier).
        [NonSerialized] public float cachedBillboardInvWidth;
        [NonSerialized] public float cachedBillboardInvHeight;
        [NonSerialized] public Vector3 cachedBillboardBaseAnchor;

        /// <summary>
        /// Pre-compute per-prototype mesh geometry constants so ComputeTreeMatrix
        /// never has to call GetMeshForLOD / access Mesh.bounds at runtime.
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

            // Cache base anchor
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
                // Only one LOD — no dedicated billboard; re-use LOD0 values as safe fallback
                cachedBillboardInvWidth  = cachedInvMeshWidth;
                cachedBillboardInvHeight = cachedInvMeshHeight;
                cachedBillboardBaseAnchor = cachedMeshBaseAnchor;
            }

            meshDataCached = true;
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

        /// <summary>True when the prototype has >= 2 LODs so the last entry is used as a
        /// camera-facing billboard. The billboard matrix is rebuilt every frame in TreeRenderer;
        /// it is never cached as a static TRS matrix.</summary>
        public bool HasBillboardLOD => lodMeshes != null && lodMeshes.Length >= 2;

        /// <summary>Returns true if this entry has at least one collider shape defined.</summary>
        public bool HasColliders => colliderShapes != null && colliderShapes.Length > 0;
    }

    [Tooltip("One entry per prototype index. Index in this array = prototypeIndex from baked data.")]
    public TreePrototypeEntry[] prototypes;

    [Tooltip("Index = tree LOD, Value = max chunk distance for that LOD. Trees beyond the last entry's distance are culled.\n" +
        "Example: [1, 3, 5] means LOD0 up to distance 1, LOD1 up to distance 3, LOD2 up to distance 5, culled beyond 5.\n" +
        "If a prototype has fewer LODs than requested, it clamps to its highest available LOD.")]
    public float[] treeDistanceByLOD = new float[] { 1, 3, 5 };

    [Tooltip("Percentage of trees kept for each tree mesh LOD. Index 0 = keep percentage for tree LOD1, index 1 = tree LOD2, etc. " +
        "LOD0 is always kept at 100% (no reduction) since collision trees should be consistent. " +
        "The bake already shuffles tree order randomly in space, so pruning simply keeps the first N percent of each chunk's list. " +
        "If a tree LOD exceeds this array, the last available element is reused.")]
    public int[] treeDensityPerLOD = new int[] { 100, 90, 80, 75, 70 };

    [Tooltip("Maximum chunk LOD at which tree colliders are active (usually 0 or 1)")]
    public byte maxCollisionLOD = 0;

    [Header("LOD Width Multipliers (Density Compensation)")]
    [Tooltip("Per-tree-LOD horizontal width multiplier applied at draw time to visually compensate "
           + "for reduced tree density at far LODs. Index = tree LOD (0 = LOD0, 1 = LOD1, …). "
           + "LOD0 should stay 1.0 (collision trees must not grow). "
           + "Height is never modified. Pairs with treeDensityPerLOD: e.g. 90 % density + 1.1× "
           + "width ≈ same perceived canopy coverage. Billboard (last) LOD obeys this too.")]
    public float[] lodWidthMultipliers = new float[] { 1f, 1.1f, 1.2f, 1.4f, 1.5f };

    // ===== Far-LOD canopy colour palette =====
    // Up to 5 colours uploaded to the batched terrain shader constant buffer (_CanopyPalette[5])
    // by ChunkMaterialManager.UploadCanopyPalette() during ChunkManager.Init().
    // Indexed directly as canopyPalette[canopyPaletteIndex]  (slot 0 -> [0], slot 4 -> [4]).
    // Changing colours does NOT require re-entering play mode; only a material constant re-upload.

    [Header("Far-LOD Canopy Palette")]
    [ColorUsage(showAlpha: false),
     Tooltip("Up to 5 canopy colours for the far-LOD batched terrain shader. "
             + "canopyPaletteIndex 0 -> slot [0], index 4 -> slot [4].")]
    public Color[] canopyPalette = new Color[5]
    {
        new Color(0.18f, 0.32f, 0.12f), // slot 1 - dark conifer
        new Color(0.30f, 0.42f, 0.18f), // slot 2 - medium deciduous
        new Color(0.38f, 0.50f, 0.22f), // slot 3 - light birch / poplar
        new Color(0.22f, 0.30f, 0.10f), // slot 4 - dense spruce
        new Color(0.40f, 0.48f, 0.28f), // slot 5 - mixed light forest
    };

    // Bake-time canopy overlay deprecated. Runtime system handles all canopy rendering now.

    [Serializable]
    public class CanopyMaskSettings
    {
        [Tooltip("Enable per-chunk canopy mask texture for this LOD. Disabled = uses vertex-only alpha (no mask).")]
        public bool enabled = true;
        [Tooltip("Resolution of the canopy mask texture per chunk (e.g. 16 = 16×16 pixels). "
               + "Higher = smoother edges but more atlas memory. 8-16 is usually sufficient for far LODs.")]
        public int maskSize = 16;
        [Range(0.1f, 1f), Tooltip("Softness of tree footprint edges in the mask. "
               + "0.1 = hard edges, 0.5 = soft, 1.0 = very soft / wide.")]
        public float softRadius = 0.35f;
        [Range(0f, 1f), Tooltip("Opacity of this prototype's canopy overlay at this LOD. "
               + "1.0 = fully opaque, 0.0 = invisible. Scales how much each tree blob contributes.")]
        public float alphaMultiplier = 1f;
    }

    [Header("Canopy Mask Atlas")]
    [Tooltip("Global atlas size for canopy mask tiles. Must be a power of two. "
           + "Tiles are packed left-to-right, top-to-bottom. "
           + "Padded tile size is larger than maskSize because soft-edge border texels are added for seam-free chunk blending.")]
    public int canopyMaskAtlasSize = 512;

    /// <summary>
    /// Gets prototype entry by index, or null if invalid.
    /// </summary>
    public TreePrototypeEntry GetPrototype(int prototypeIndex)
    {
        if (prototypes == null || prototypeIndex < 0 || prototypeIndex >= prototypes.Length)
            return null;
        return prototypes[prototypeIndex];
    }

    /// <summary>
    /// Returns the tree mesh LOD for a given prototype at a given chunk distance from center,
    /// or -1 if that prototype should be culled.
    /// </summary>
    public int GetTreeLODForDistance(int prototypeIndex, int distance)
    {
        var prototype = GetPrototype(prototypeIndex);
        float[] distByLOD = prototype != null && prototype.treeDistanceByLOD != null && prototype.treeDistanceByLOD.Length > 0
            ? prototype.treeDistanceByLOD
            : treeDistanceByLOD;

        if (distByLOD == null || distByLOD.Length == 0)
            return -1;

        for (int lod = 0; lod < distByLOD.Length; lod++)
        {
            if (distance <= distByLOD[lod])
                return lod;
        }
        return -1; // Beyond last entry = cull
    }

    /// <summary>
    /// Returns true if any prototype should be rendered at this chunk distance from center.
    /// Uses cached max render distance for O(1) instead of looping prototypes.
    /// </summary>
    public bool ShouldRenderAtDistance(int distance)
    {
        return distance >= 0 && distance <= cachedMaxRenderDistance;
    }

    /// <summary>
    /// Bake-time helper: returns the smallest chunk distance at which trees of the given
    /// prototype are culled (no longer rendered as meshes). The bake uses this to decide
    /// from which splatmap tier on to stamp the canopy overlay.
    /// Returns int.MaxValue if the prototype has no LOD table (never culled by LOD).
    /// </summary>
    public int GetTreeCullChunkDistance(int prototypeIndex)
    {
        var p = GetPrototype(prototypeIndex);
        float[] dist = (p != null && p.treeDistanceByLOD != null && p.treeDistanceByLOD.Length > 0)
            ? p.treeDistanceByLOD
            : treeDistanceByLOD;

        if (dist == null || dist.Length == 0)
            return int.MaxValue;

        // GetTreeLODForDistance returns -1 (=cull) when distance > dist[last].
        // So the first culled distance is floor(dist[last]) + 1.
        return Mathf.FloorToInt(dist[dist.Length - 1]) + 1;
    }

    /// <summary>
    /// Pre-compute the maximum distance at which any prototype is still rendered.
    /// Call once after init / whenever LOD tables change.
    /// </summary>
    public void CacheMaxRenderDistance()
    {
        cachedMaxRenderDistance = -1;

        // Global table: max render distance is the value of the last entry
        if (treeDistanceByLOD != null && treeDistanceByLOD.Length > 0)
        {
            cachedMaxRenderDistance = (int)treeDistanceByLOD[treeDistanceByLOD.Length - 1];
        }

        // Per-prototype tables can extend further
        if (prototypes != null)
        {
            for (int p = 0; p < prototypes.Length; p++)
            {
                var proto = prototypes[p];
                float[] dist = proto != null ? proto.treeDistanceByLOD : null;
                if (dist == null || dist.Length == 0) continue;
                int maxDist = (int)dist[dist.Length - 1];
                if (maxDist > cachedMaxRenderDistance)
                    cachedMaxRenderDistance = maxDist;
            }
        }
    }

    private int cachedMaxRenderDistance = -1;

    /// <summary>
    /// Returns the percentage of trees to keep for a given tree mesh LOD.
    /// LOD0 always returns 100% (never reduced).
    /// LOD1+ indices into treeDensityPerLOD array with offset: array[treeLOD-1].
    /// If the array is shorter than the requested LOD, the last available element is reused.
    /// Empty or null array means keep all trees.
    /// </summary>
    public int GetTreeDensityPercentForLOD(int treeLOD)
    {
        // LOD0 always 100% — no reduction for collision trees
        if (treeLOD == 0)
            return 100;

        if (treeDensityPerLOD == null || treeDensityPerLOD.Length == 0)
            return 100;

        // Array index 0 corresponds to LOD1, so offset by 1
        int index = Mathf.Clamp(treeLOD - 1, 0, treeDensityPerLOD.Length - 1);
        return Mathf.Clamp(treeDensityPerLOD[index], 0, 100);
    }

    /// <summary>
    /// Returns the draw-time horizontal width multiplier for a given tree LOD.
    /// Applied to both horizontal axes so the tree appears wider from all angles,
    /// compensating for reduced density without needing to know player direction.
    /// LOD0 defaults to 1.0 (collision trees should not be resized).
    /// </summary>
    public float GetWidthMultiplierForLOD(int treeLOD)
    {
        if (lodWidthMultipliers == null || lodWidthMultipliers.Length == 0) return 1f;
        return lodWidthMultipliers[Mathf.Clamp(treeLOD, 0, lodWidthMultipliers.Length - 1)];
    }

    /// <summary>
    /// Validates all entries and logs warnings for invalid ones.
    /// </summary>
    public void ValidateAll()
    {
        if (prototypes == null)
        {
            Debug.LogWarning($"[TreePrototypeRegistry] {name}: No prototypes assigned");
            return;
        }

        for (int i = 0; i < prototypes.Length; i++)
        {
            var p = prototypes[i];
            if (p == null)
            {
                Debug.LogWarning($"[TreePrototypeRegistry] {name}: Prototype[{i}] is null");
                continue;
            }

            if (p.lodMeshes == null || p.lodMeshes.Length == 0)
                Debug.LogWarning($"[TreePrototypeRegistry] {name}: Prototype[{i}] '{p.name}' has no LOD meshes");
            else if (p.lodMeshes[0] == null)
                Debug.LogWarning($"[TreePrototypeRegistry] {name}: Prototype[{i}] '{p.name}' LOD0 mesh is null");

            if (p.material == null)
                Debug.LogWarning($"[TreePrototypeRegistry] {name}: Prototype[{i}] '{p.name}' has no material");
            else if (!p.material.enableInstancing)
                Debug.LogWarning($"[TreePrototypeRegistry] {name}: Prototype[{i}] '{p.name}' material doesn't have GPU Instancing enabled");

            if (p.canopyMaskByLOD != null && p.lodMeshes != null
                && p.canopyMaskByLOD.Length > p.lodMeshes.Length)
                Debug.LogWarning($"[TreePrototypeRegistry] {name}: Prototype[{i}] '{p.name}' "
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
        if (prototypes != null)
        {
            for (int i = 0; i < prototypes.Length; i++)
            {
                if (prototypes[i] != null && prototypes[i].sourcePrefab != null && string.IsNullOrEmpty(prototypes[i].name))
                {
                    prototypes[i].name = prototypes[i].sourcePrefab.name;
                }
            }
        }
    }
}
#endif
