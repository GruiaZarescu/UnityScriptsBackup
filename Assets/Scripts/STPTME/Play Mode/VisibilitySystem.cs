using System;
using UnityEngine;

/// <summary>
/// Global per-frame visibility decision system for the cube-sphere planet.
///
/// Two tests, evaluated cheaply per chunk:
///   1. Analytic horizon culling — uses pre-baked per-chunk max height (cosThetaC) and
///      per-frame player altitude (cosThetaP). A chunk is potentially visible iff
///      dot(nP, nC) > cosThetaP*cosThetaC - sinThetaP*sinThetaC - horizonMargin.
///   2. Frustum culling — bounding-sphere test against the camera's 6 frustum planes.
///
/// Phase A: per-chunk queries on demand (no bitset, no jobs). Designed for the
/// TreeRenderer which iterates a few thousand registered chunks per frame.
///
/// Phase B will add a parallel batch registry (RegisterBatch/IsBatchVisible), letting
/// ChunkRegistry decide per-batch MeshRenderer.enabled. The batch API hooks are
/// already stubbed below so wiring is minimal.
///
/// Future-compat: visibility result is a single bool/bit per query that can later be
/// AND-ed with an occlusion bit. Buildings/grass can register their own bounds via the
/// same batch API.
///
/// Owned and initialized by ChunkManager. Singleton via static <see cref="Instance"/>.
/// Not a MonoBehaviour — no Update loop, all per-frame work driven by
/// <see cref="PrepareFrame"/> called by consumers.
/// </summary>
public sealed class VisibilitySystem
{
    public static VisibilitySystem Instance { get; private set; }

    // ===== TUNABLES =====

    /// <summary>
    /// Extra height (meters) added to per-chunk max terrain height when computing
    /// cosThetaC. Covers tree canopies / props that sit above the heightmap.
    /// </summary>
    public const float TREE_MARGIN = 50f;

    /// <summary>
    /// Default slack on the horizon test (cosine). Positive values make the test more
    /// permissive (fewer false negatives, more false positives). 0 = exact.
    /// Runtime value is supplied by TerrainManagementSettings via Initialize.
    /// </summary>
    public const float DEFAULT_HORIZON_MARGIN = 0f;

    /// <summary>
    /// Additive padding (meters) on every chunk bounding-sphere radius. Absorbs the
    /// floating-point tie that occurs when the camera stands exactly at a chunk corner
    /// (camera-to-bound-center distance ≈ bound radius), which would otherwise cause
    /// the chunk to flicker out of the frustum at certain camera angles. A few meters
    /// is invisible to actual cull effectiveness — chunks this close to the camera are
    /// never legitimately frustum-cullable anyway.
    /// </summary>
    public const float BOUND_RADIUS_PAD = 10f;

    // ===== DEBUG TOGGLES =====
    // Set at runtime (e.g. from VisibilityDebugger) to bisect which test is producing
    // false positives. When SkipHorizon is true, horizon test is treated as PASS.
    // When SkipFrustum is true, frustum test is treated as PASS.
    public static bool SkipHorizon = false;
    public static bool SkipFrustum = false;

    // ===== PER-CHUNK BAKED DATA (indexed by storage slot) =====

    private Vector3[] chunkCenterDir;       // unit vector from sphere center
    private float[] chunkCosThetaC;         // = R / (R + maxH + TREE_MARGIN)
    private float[] chunkSinThetaC;         // = sqrt(1 - cos²)
    // Bound sphere vertical placement. centerAlt = altitude (meters above sphere surface)
    // of the bound's center; halfH = half the vertical extent. Tightly tracks the chunk's
    // [minH, maxH+TREE_MARGIN] interval so plateaus/mountains don't produce 1km bounds.
    private float[] chunkBoundCenterAlt;
    private float[] chunkBoundHalfH;

    private int slotCount;
    private float sphereRadius;
    private Vector3 sphereCenter;
    private float halfChunkLinearSize;
    /// <summary>Pre-squared tangent-plane corner extent (= 2·halfChunkLinearSize²).
    /// Hoisted out of <see cref="GetChunkBoundingSphere"/> hot path.</summary>
    private float tangentExtentSq;
    private float horizonMargin;
    private STPTMEUtils.GlobalIndexCalculator globalIndexCalculator;

    // ===== HORIZON RECOMPUTE THROTTLING =====
    // Two gates, AND-ed: cadence (run at most every N frames) and movement
    // significance (run only if the player moved/changed altitude enough that
    // a chunk's horizon classification could plausibly flip). Frustum still
    // runs every frame on every batch — it's the cheap part.
    private int horizonRecomputeFrameInterval = 1;
    private float horizonRecomputePosThresholdSq = 0f; // squared, 0 disables
    private float horizonRecomputeAltThreshold = 0f;   // 0 disables
    private int lastHorizonFrame = -1;
    private Vector3 lastHorizonPlayerPos;
    private float lastHorizonPlayerAlt;
    private bool horizonStateInitialized;
    // Tracks the SkipHorizon value used for the most recent horizon recompute.
    // When the static debug toggle flips between recompute frames, the cached
    // batchHorizonPass[] entries are stale (computed under the previous toggle
    // state); we force a recompute on the very next PrepareFrame so the toggle
    // takes effect instantly instead of waiting for the movement threshold.
    private bool lastSkipHorizon;

    // ===== PER-FRAME STATE =====

    private int preparedFrame = -1;
    private float cosThetaP;
    private float sinThetaP;
    private Vector3 playerNorm;
    private readonly Plane[] frustumPlanes = new Plane[6];
    private bool frustumValid;

    // Cached camera (resolved lazily; falls back to Camera.main).
    private Camera cachedCamera;
    private bool warnedMissingCamera;

    // ===== BATCH REGISTRY (Phase B placeholder — functional, no consumers yet) =====

    public struct BatchBounds
    {
        public Vector3 worldCenter;
        public float worldRadius;
        public Vector3 centerDir;       // dominant radial direction
        public float cosThetaBatch;     // for analytic horizon at the batch level
        public float sinThetaBatch;
    }

    private BatchBounds[] batchBounds = Array.Empty<BatchBounds>();
    private bool[] batchInUse = Array.Empty<bool>();
    private bool[] batchVisible = Array.Empty<bool>();
    /// <summary>Cached result of the horizon test for each batch slot.
    /// Recomputed only on horizon-update frames (see throttling fields above);
    /// reused on intermediate frames so per-frame work collapses to just the
    /// frustum test.</summary>
    private bool[] batchHorizonPass = Array.Empty<bool>();
    // Per-batch member storage indices for tight horizon culling. The conservative
    // batch-level cone (cosThetaBatch) is still computed and used as a fallback when
    // no member array was supplied (e.g. RegisterBatch overload that doesn't take
    // member ids — useful for non-terrain consumers like buildings). When members
    // ARE supplied, horizon test ORs per-member chunk visibility, which avoids the
    // false-positive halo where one near member kept the whole sector batch enabled
    // while every other member sat well past the horizon.
    private int[][] batchMemberIdxs = Array.Empty<int[]>();
    private int[] batchMemberCount = Array.Empty<int>();

    // ===== O(1) BATCH REGISTRY BOOKKEEPING =====
    // Freelist: singly-linked through batchNextFree[]. Head index or -1.
    // O(1) register + unregister, no linear scan over batchInUse.
    private int[] batchNextFree = Array.Empty<int>();
    private int batchFreeListHead = -1;

    // Compact list of currently-registered batch ids. Iterated by PrepareFrame so
    // we touch only live slots, not the full (high-water-mark) batchBounds array.
    // Maintained via swap-back removal; batchActiveIndex[id] gives O(1) lookup of
    // a batch's position in activeBatchIds for unregistration.
    private int[] activeBatchIds = Array.Empty<int>();
    private int[] batchActiveIndex = Array.Empty<int>(); // -1 when not active
    private int batchCount = 0;

    /// <summary>
    /// Raised after <see cref="PrepareFrame"/> when at least one registered batch
    /// changed its visibility state since the previous frame. Subscribers (e.g.,
    /// ChunkRegistry) toggle MeshRenderer.enabled in response.
    /// </summary>
    public event Action OnBatchVisibilityChanged;

    // ===== INIT =====

    /// <summary>
    /// Called once by ChunkManager after AdjacentData has been read for every face.
    /// Takes ownership of the per-chunk arrays (not copied — assumed stable for the
    /// lifetime of the play session).
    /// </summary>
    public static void Initialize(
        Vector3 sphereCenter,
        float sphereRadius,
        float halfChunkLinearSize,
        Vector3[] chunkCenterDir,
        float[] chunkCosThetaC,
        float[] chunkSinThetaC,
        float[] chunkBoundCenterAlt,
        float[] chunkBoundHalfH,
        STPTMEUtils.GlobalIndexCalculator globalIndexCalculator,
        float horizonMargin = DEFAULT_HORIZON_MARGIN,
        int horizonRecomputeFrameInterval = 1,
        float horizonRecomputePosThreshold = 0f,
        float horizonRecomputeAltThreshold = 0f)
    {
        float clampedHorizonMargin = Mathf.Clamp(horizonMargin, -1f, 1f);
        int clampedInterval = horizonRecomputeFrameInterval > 1 ? horizonRecomputeFrameInterval : 1;
        float posThresh = horizonRecomputePosThreshold > 0f ? horizonRecomputePosThreshold : 0f;
        float altThresh = horizonRecomputeAltThreshold > 0f ? horizonRecomputeAltThreshold : 0f;
        var sys = new VisibilitySystem
        {
            sphereCenter = sphereCenter,
            sphereRadius = sphereRadius,
            halfChunkLinearSize = halfChunkLinearSize,
            tangentExtentSq = 2f * halfChunkLinearSize * halfChunkLinearSize,
            horizonMargin = clampedHorizonMargin,
            chunkCenterDir = chunkCenterDir,
            chunkCosThetaC = chunkCosThetaC,
            chunkSinThetaC = chunkSinThetaC,
            chunkBoundCenterAlt = chunkBoundCenterAlt,
            chunkBoundHalfH = chunkBoundHalfH,
            slotCount = chunkCenterDir.Length,
            globalIndexCalculator = globalIndexCalculator,
            horizonRecomputeFrameInterval = clampedInterval,
            horizonRecomputePosThresholdSq = posThresh * posThresh,
            horizonRecomputeAltThreshold = altThresh,
        };
        Instance = sys;
    }

    public static bool IsReady => Instance != null && Instance.chunkCenterDir != null;

    /// <summary>
    /// Set the camera used for frustum culling. If null, <see cref="PrepareFrame"/>
    /// re-resolves to Camera.main.
    /// </summary>
    public void SetCamera(Camera cam) => cachedCamera = cam;

    /// <summary>
    /// Camera currently used for frustum culling and (optionally) draw-call targeting.
    /// May be null if neither <see cref="SetCamera"/> nor Camera.main has resolved one.
    /// </summary>
    public Camera ActiveCamera => cachedCamera;

    /// <summary>
    /// True when PrepareFrame resolved a live Game camera and frustum planes are valid.
    /// If false, only horizon culling is active and frustum culling is skipped.
    /// </summary>
    public bool HasValidFrustum => frustumValid;

    // ===== PER-FRAME PREP =====

    /// <summary>
    /// Updates per-frame state (player horizon angle, frustum planes, batch results).
    /// Idempotent within a single frame: subsequent calls in the same Time.frameCount
    /// are no-ops. Consumers (TreeRenderer, ChunkRegistry) should call this once
    /// before querying.
    /// </summary>
    public void PrepareFrame(Vector3 playerPosition, float playerAltitude)
    {
        int frame = Time.frameCount;
        if (frame == preparedFrame) return;
        preparedFrame = frame;

        // Player horizon angle. Clamp altitude to >= 0 so descending below the
        // reference radius doesn't produce a NaN; in practice altitude should always
        // be positive for the player above the surface.
        float h = playerAltitude > 0f ? playerAltitude : 0f;
        cosThetaP = sphereRadius / (sphereRadius + h);
        // sin² = 1 - cos²; numerically safe because cosThetaP ∈ (0, 1].
        float s2 = 1f - cosThetaP * cosThetaP;
        sinThetaP = s2 > 0f ? Mathf.Sqrt(s2) : 0f;

        Vector3 d = playerPosition - sphereCenter;
        float dl = d.magnitude;
        playerNorm = dl > 1e-5f ? d / dl : Vector3.up;

        // Camera / frustum.
        cachedCamera = ResolveActiveCamera(cachedCamera);
        if (cachedCamera != null)
        {
            GeometryUtility.CalculateFrustumPlanes(cachedCamera, frustumPlanes);
            frustumValid = true;
            warnedMissingCamera = false;
        }
        else
        {
            frustumValid = false;
            if (!warnedMissingCamera)
            {
                Debug.LogWarning("[VisibilitySystem] No enabled Game camera resolved. Frustum culling is disabled; only horizon culling will run.");
                warnedMissingCamera = true;
            }
        }

        // Recompute batch visibility and fire change event if anything flipped.
        if (batchCount > 0)
        {
            // Gate horizon recompute on (cadence) AND (movement significance). When
            // either gate fails we reuse cached batchHorizonPass[] from the last
            // recompute frame; only the cheap frustum test runs.
            // SkipHorizon toggle changed since last recompute? Cached results were
            // baked under the old toggle and would otherwise stay stale until the
            // movement threshold trips. Force a recompute so the bisect toggle is
            // usable as a real-time debugging tool.
            bool skipToggleChanged = horizonStateInitialized && SkipHorizon != lastSkipHorizon;
            bool intervalReady = !horizonStateInitialized
                || skipToggleChanged
                || (frame - lastHorizonFrame) >= horizonRecomputeFrameInterval;
            bool movedEnough;
            if (!horizonStateInitialized || skipToggleChanged)
            {
                movedEnough = true;
            }
            else
            {
                Vector3 dPos = playerPosition - lastHorizonPlayerPos;
                float distSq = dPos.x * dPos.x + dPos.y * dPos.y + dPos.z * dPos.z;
                float dAlt = playerAltitude - lastHorizonPlayerAlt;
                if (dAlt < 0f) dAlt = -dAlt;
                bool posTrip = horizonRecomputePosThresholdSq <= 0f
                    || distSq >= horizonRecomputePosThresholdSq;
                bool altTrip = horizonRecomputeAltThreshold <= 0f
                    || dAlt >= horizonRecomputeAltThreshold;
                // OR: any single threshold crossing is enough to invalidate the cache.
                // (If both thresholds are disabled, posTrip && altTrip are both true → always recompute.)
                movedEnough = posTrip || altTrip;
            }
            bool recomputeHorizon = intervalReady && movedEnough;

            if (recomputeHorizon)
            {
                lastHorizonFrame = frame;
                lastHorizonPlayerPos = playerPosition;
                lastHorizonPlayerAlt = playerAltitude;
                lastSkipHorizon = SkipHorizon;
                horizonStateInitialized = true;
            }

            bool anyChanged = false;
            int n = batchCount;
            for (int k = 0; k < n; k++)
            {
                int i = activeBatchIds[k];
                if (recomputeHorizon)
                    batchHorizonPass[i] = TestBatchHorizon(in batchBounds[i], batchMemberIdxs[i], batchMemberCount[i]);
                bool now = batchHorizonPass[i] && TestBatchFrustum(in batchBounds[i]);
                if (now != batchVisible[i])
                {
                    batchVisible[i] = now;
                    anyChanged = true;
                }
            }
            if (anyChanged) OnBatchVisibilityChanged?.Invoke();
        }
    }

    // ===== PER-CHUNK QUERIES =====

    /// <summary>
    /// True if the chunk identified by (packed, face) is potentially visible this
    /// frame. Caller must have invoked <see cref="PrepareFrame"/> earlier in the
    /// same frame. Safe to call for invalid storage slots (returns false).
    /// </summary>
    public bool IsChunkVisible(int packed, FaceId face)
    {
        int storageIdx = globalIndexCalculator.GetIndex(packed) * FaceIdUtility.StorageFaceCount + (int)face;
        return IsChunkVisibleByStorageIdx(storageIdx);
    }

    public bool IsChunkVisibleByStorageIdx(int storageIdx)
    {
        return ClassifyChunk(storageIdx) == ChunkVisibility.Visible;
    }

    public enum ChunkVisibility : byte
    {
        InvalidSlot = 0,
        FailedHorizon = 1,
        FailedFrustum = 2,
        Visible = 3,
    }

    /// <summary>
    /// Returns the precise reason a chunk passed/failed visibility this frame.
    /// Honors <see cref="SkipHorizon"/> / <see cref="SkipFrustum"/> debug toggles.
    /// </summary>
    public ChunkVisibility ClassifyChunk(int storageIdx)
    {
        if ((uint)storageIdx >= (uint)slotCount) return ChunkVisibility.InvalidSlot;

        Vector3 nC = chunkCenterDir[storageIdx];
        float cosC = chunkCosThetaC[storageIdx];
        float sinC = chunkSinThetaC[storageIdx];

        if (!SkipHorizon)
        {
            float dot = nC.x * playerNorm.x + nC.y * playerNorm.y + nC.z * playerNorm.z;
            float threshold = cosThetaP * cosC - sinThetaP * sinC - horizonMargin;
            if (dot < threshold) return ChunkVisibility.FailedHorizon;
        }

        if (frustumValid && !SkipFrustum)
        {
            GetChunkBoundingSphere(storageIdx, out Vector3 center, out float r);
            for (int p = 0; p < 6; p++)
            {
                if (frustumPlanes[p].GetDistanceToPoint(center) < -r) return ChunkVisibility.FailedFrustum;
            }
        }

        return ChunkVisibility.Visible;
    }

    /// <summary>
    /// Returns the bounding sphere used by the frustum test for a chunk slot.
    /// Exposed for debug visualization.
    /// </summary>
    public void GetChunkBoundingSphere(int storageIdx, out Vector3 center, out float radius)
    {
        Vector3 nC = chunkCenterDir[storageIdx];
        float centerAlt = chunkBoundCenterAlt[storageIdx];
        float halfH = chunkBoundHalfH[storageIdx];
        float surfaceR = sphereRadius + centerAlt;
        center.x = sphereCenter.x + nC.x * surfaceR;
        center.y = sphereCenter.y + nC.y * surfaceR;
        center.z = sphereCenter.z + nC.z * surfaceR;
        // Tangent extent: chunk spans \u00b1halfChunkLinearSize in BOTH tangent axes,
        // so the tangent-plane corner sits at sqrt(2)*halfChunkLinearSize from center.
        // Earlier this used halfChunkLinearSize (segment, not square) and undersized
        // the bound by \u221a2 \u2014 chunks adjacent to the camera could fail the frustum
        // sphere test even when their corners were clearly on screen.
        // tangentExtentSq is precomputed in Initialize.
        // BOUND_RADIUS_PAD prevents corner-standing camera ties (see const docs).
        radius = Mathf.Sqrt(tangentExtentSq + halfH * halfH) + BOUND_RADIUS_PAD;
    }

    public int SlotCount => slotCount;
    public bool IsSlotValid(int storageIdx) => (uint)storageIdx < (uint)slotCount;

    /// <summary>
    /// Builds a conservative <see cref="BatchBounds"/> covering the supplied chunk slots.
    /// Used by ChunkRegistry to register a terrain batch (single-chunk or multi-chunk)
    /// once its renderer GameObject exists. The bound is intentionally conservative:
    /// world sphere = average of member bound centers expanded to enclose the farthest
    /// member sphere; horizon angle = angular spread of member centerDirs plus the
    /// largest member chunk angle.
    /// Invalid slot indices in the input are skipped silently.
    /// </summary>
    public BatchBounds BuildBatchBoundsFromStorageIdxs(int[] storageIdxs, int count)
    {
        BatchBounds b = default;
        if (storageIdxs == null || count <= 0) return b;

        // Pass 1: average bound centers and centerDirs over valid members.
        Vector3 sumCenter = Vector3.zero;
        Vector3 sumDir = Vector3.zero;
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            int idx = storageIdxs[i];
            if ((uint)idx >= (uint)slotCount) continue;
            GetChunkBoundingSphere(idx, out Vector3 c, out _);
            sumCenter += c;
            sumDir += chunkCenterDir[idx];
            valid++;
        }
        if (valid == 0) return b;

        Vector3 worldCenter = sumCenter / valid;
        Vector3 centerDir = sumDir.sqrMagnitude > 1e-8f ? sumDir.normalized : Vector3.up;

        // Pass 2: enclose all member spheres + find min dot to centerDir and min member cos.
        float worldRadius = 0f;
        float minCosToCenter = 1f;
        float minMemberCos = 1f;
        for (int i = 0; i < count; i++)
        {
            int idx = storageIdxs[i];
            if ((uint)idx >= (uint)slotCount) continue;
            GetChunkBoundingSphere(idx, out Vector3 c, out float r);
            float d = Vector3.Distance(worldCenter, c) + r;
            if (d > worldRadius) worldRadius = d;

            Vector3 nC = chunkCenterDir[idx];
            float dotToCenter = nC.x * centerDir.x + nC.y * centerDir.y + nC.z * centerDir.z;
            if (dotToCenter < minCosToCenter) minCosToCenter = dotToCenter;

            float cosC = chunkCosThetaC[idx];
            if (cosC < minMemberCos) minMemberCos = cosC;
        }

        float spreadAngle = Mathf.Acos(Mathf.Clamp(minCosToCenter, -1f, 1f));
        float chunkAngle = Mathf.Acos(Mathf.Clamp(minMemberCos, -1f, 1f));
        float batchAngle = spreadAngle + chunkAngle;
        if (batchAngle > Mathf.PI) batchAngle = Mathf.PI;

        b.worldCenter = worldCenter;
        b.worldRadius = worldRadius;
        b.centerDir = centerDir;
        b.cosThetaBatch = Mathf.Cos(batchAngle);
        b.sinThetaBatch = Mathf.Sin(batchAngle);
        return b;
    }

    // ===== BATCH API (Phase B wiring point) =====

    /// <summary>
    /// Register a batch (group of chunks, building, etc.) for per-frame visibility.
    /// Returns an opaque batch id used by IsBatchVisible / UnregisterBatch.
    /// Use the (BatchBounds, int[], int) overload for terrain batches to enable
    /// tight per-member horizon culling.
    /// </summary>
    public int RegisterBatch(in BatchBounds bounds) => RegisterBatch(in bounds, null, 0);

    /// <summary>
    /// Same as <see cref="RegisterBatch(in BatchBounds)"/> but supplies the storage
    /// indices of the chunks that make up the batch. The horizon test then ORs each
    /// member's per-chunk horizon test instead of using a single conservative cone,
    /// preventing the case where one visible member keeps a whole sector batch on
    /// while most members sit past the horizon. The supplied array is COPIED.
    /// </summary>
    public int RegisterBatch(in BatchBounds bounds, int[] memberStorageIdxs, int memberCount)
    {
        // O(1) free-slot acquisition via singly-linked freelist; grow on empty.
        int idx;
        if (batchFreeListHead >= 0)
        {
            idx = batchFreeListHead;
            batchFreeListHead = batchNextFree[idx];
        }
        else
        {
            int oldLen = batchInUse.Length;
            int newCap = Mathf.Max(16, oldLen * 2);
            Array.Resize(ref batchBounds, newCap);
            Array.Resize(ref batchInUse, newCap);
            Array.Resize(ref batchVisible, newCap);
            Array.Resize(ref batchHorizonPass, newCap);
            Array.Resize(ref batchMemberIdxs, newCap);
            Array.Resize(ref batchMemberCount, newCap);
            Array.Resize(ref batchNextFree, newCap);
            Array.Resize(ref batchActiveIndex, newCap);
            // Link newly-grown slots [oldLen+1 .. newCap-1] into the freelist; idx
            // takes oldLen, so push (oldLen+1) … (newCap-1) onto the freelist head.
            for (int i = newCap - 1; i > oldLen; i--)
            {
                batchNextFree[i] = batchFreeListHead;
                batchFreeListHead = i;
            }
            for (int i = oldLen; i < newCap; i++) batchActiveIndex[i] = -1;
            idx = oldLen;
        }

        batchBounds[idx] = bounds;
        batchInUse[idx] = true;
        if (memberStorageIdxs != null && memberCount > 0)
        {
            // Reuse the existing array slot if it's already big enough — saves a GC alloc
            // when batches recycle. Copy only the live prefix.
            int[] dst = batchMemberIdxs[idx];
            if (dst == null || dst.Length < memberCount) dst = new int[memberCount];
            Array.Copy(memberStorageIdxs, dst, memberCount);
            batchMemberIdxs[idx] = dst;
            batchMemberCount[idx] = memberCount;
        }
        else
        {
            batchMemberCount[idx] = 0;
        }
        batchVisible[idx] = TestBatchInternalFresh(in bounds, batchMemberIdxs[idx], batchMemberCount[idx], idx);

        // Append to active list, recording position for O(1) swap-back removal.
        if (activeBatchIds.Length <= batchCount)
        {
            int newCap = Mathf.Max(16, activeBatchIds.Length * 2);
            Array.Resize(ref activeBatchIds, newCap);
        }
        activeBatchIds[batchCount] = idx;
        batchActiveIndex[idx] = batchCount;
        batchCount++;
        return idx;
    }

    public void UnregisterBatch(int batchId)
    {
        if ((uint)batchId >= (uint)batchInUse.Length) return;
        if (!batchInUse[batchId]) return;
        batchInUse[batchId] = false;
        batchVisible[batchId] = false;
        batchMemberCount[batchId] = 0;
        // Keep batchMemberIdxs[batchId] alive for reuse on the next registration.

        // Swap-back removal from activeBatchIds.
        int activeIdx = batchActiveIndex[batchId];
        int last = batchCount - 1;
        if (activeIdx != last)
        {
            int movedId = activeBatchIds[last];
            activeBatchIds[activeIdx] = movedId;
            batchActiveIndex[movedId] = activeIdx;
        }
        batchActiveIndex[batchId] = -1;
        batchCount--;

        // Push slot onto freelist for O(1) reuse.
        batchNextFree[batchId] = batchFreeListHead;
        batchFreeListHead = batchId;
    }

    public bool IsBatchVisible(int batchId)
    {
        if ((uint)batchId >= (uint)batchVisible.Length) return false;
        return batchInUse[batchId] && batchVisible[batchId];
    }

    /// <summary>
    /// Diagnostic snapshot of the batch registry. Counts active batches, how many are
    /// classified visible, how many fail horizon vs. frustum, and how many are stalled
    /// at unusable parameters (e.g. zero radius). Logs to Unity console.
    /// </summary>
    public void DumpDiagnostics(string prefix = "[VisibilitySystem]")
    {
        int active = batchCount;
        int vis = 0, hFail = 0, fFail = 0, both = 0, zeroRadius = 0, noMembers = 0;
        float minR = float.PositiveInfinity, maxR = 0f;
        for (int k = 0; k < active; k++)
        {
            int i = activeBatchIds[k];
            if (batchVisible[i]) vis++;
            bool h = batchHorizonPass[i];
            bool f = TestBatchFrustum(in batchBounds[i]);
            if (!h && !f) both++;
            else if (!h) hFail++;
            else if (!f) fFail++;
            float r = batchBounds[i].worldRadius;
            if (r <= 0f) zeroRadius++;
            else { if (r < minR) minR = r; if (r > maxR) maxR = r; }
            if (batchMemberCount[i] == 0) noMembers++;
        }
        Debug.Log(
            $"{prefix} batches active={active} visible={vis} hFail={hFail} fFail={fFail} both={both} " +
            $"zeroRadius={zeroRadius} noMembers={noMembers} radius=[{(minR == float.PositiveInfinity ? 0f : minR):F0}..{maxR:F0}]m " +
            $"skipH={SkipHorizon} skipF={SkipFrustum} frustumValid={frustumValid} cam={(cachedCamera != null ? cachedCamera.name : "<none>")} " +
            $"preparedFrame={preparedFrame} curFrame={Time.frameCount}");
    }

    /// <summary>
    /// Used by RegisterBatch to compute an initial visibility decision regardless
    /// of throttling state. Also caches the horizon result on the batch slot so
    /// subsequent throttled frames can reuse it.
    /// </summary>
    private bool TestBatchInternalFresh(in BatchBounds b, int[] memberIdxs, int memberCount, int slotIdx)
    {
        bool h = TestBatchHorizon(in b, memberIdxs, memberCount);
        batchHorizonPass[slotIdx] = h;
        return h && TestBatchFrustum(in b);
    }

    private bool TestBatchHorizon(in BatchBounds b, int[] memberIdxs, int memberCount)
    {
        if (SkipHorizon) return true;

        // Hoist per-frame invariants and instance fields into locals so the per-member
        // loop body keeps them in registers instead of reissuing field loads.
        float pNx = playerNorm.x, pNy = playerNorm.y, pNz = playerNorm.z;
        float cP = cosThetaP, sP = sinThetaP, hMargin = horizonMargin;

        // Prefer per-member OR (tight) when caller supplied member indices;
        // otherwise fall back to the conservative single-cone test on bounds.
        if (memberCount > 0 && memberIdxs != null)
        {
            int slotCnt = slotCount;
            Vector3[] dirs = chunkCenterDir;
            float[] cosArr = chunkCosThetaC;
            float[] sinArr = chunkSinThetaC;
            for (int m = 0; m < memberCount; m++)
            {
                int sIdx = memberIdxs[m];
                if ((uint)sIdx >= (uint)slotCnt) continue;
                Vector3 nC = dirs[sIdx];
                float cosC = cosArr[sIdx];
                float sinC = sinArr[sIdx];
                float mDot = nC.x * pNx + nC.y * pNy + nC.z * pNz;
                float mThresh = cP * cosC - sP * sinC - hMargin;
                if (mDot >= mThresh) return true;
            }
            return false;
        }

        float dot = b.centerDir.x * pNx + b.centerDir.y * pNy + b.centerDir.z * pNz;
        float threshold = cP * b.cosThetaBatch - sP * b.sinThetaBatch - hMargin;
        return dot >= threshold;
    }

    private bool TestBatchFrustum(in BatchBounds b)
    {
        if (!frustumValid || SkipFrustum) return true;
        Vector3 wc = b.worldCenter;
        float wr = b.worldRadius;
        for (int p = 0; p < 6; p++)
        {
            if (frustumPlanes[p].GetDistanceToPoint(wc) < -wr) return false;
        }
        return true;
    }

    private static Camera ResolveActiveCamera(Camera preferred)
    {
        if (IsUsableGameCamera(preferred))
            return preferred;

        Camera main = Camera.main;
        if (IsUsableGameCamera(main))
            return main;

        int count = Camera.allCamerasCount;
        if (count <= 0)
            return null;

        Camera[] cameras = Camera.allCameras;
        for (int i = 0; i < cameras.Length; i++)
        {
            Camera cam = cameras[i];
            if (IsUsableGameCamera(cam))
                return cam;
        }

        return null;
    }

    private static bool IsUsableGameCamera(Camera cam)
    {
        return cam != null
            && cam.isActiveAndEnabled
            && cam.cameraType == CameraType.Game;
    }
}
