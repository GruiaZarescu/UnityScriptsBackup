using UnityEngine;

/// <summary>
/// Drop this on any GameObject in the scene to inspect what
/// <see cref="VisibilitySystem"/> is deciding each frame.
///
/// Usage:
///   1. Toggle <see cref="skipHorizon"/> / <see cref="skipFrustum"/> in the
///      inspector to bisect which test is producing false positives.
///      - With both enabled  : every chunk is "visible" (sanity check).
///      - Only horizon active: trees behind player should still render
///        (horizon doesn't cull just-behind chunks).
///      - Only frustum active: trees outside the camera frustum should be
///        culled cleanly. If they aren't, the frustum bound is wrong.
///   2. Enable <see cref="logCountsEverySecond"/> to see how many chunks
///      were classified Visible / FailedHorizon / FailedFrustum / InvalidSlot.
///   3. Enable <see cref="drawGizmos"/> to draw per-chunk bounding spheres
///      colored by classification. Limit with <see cref="maxGizmosToDraw"/>.
///       Visible       = green
///       FailedFrustum = red
///       FailedHorizon = yellow
/// </summary>
[ExecuteAlways]
public class VisibilityDebugger : MonoBehaviour
{
    [Header("Bypass toggles (live)")]
    public bool skipHorizon = false;
    public bool skipFrustum = false;

    [Header("Logging")]
    public bool logCountsEverySecond = false;
    [Tooltip("Also log classification + bound-radius stats restricted to TreeRenderer's registered chunks (the only ones that issue draw calls).")]
    public bool logTreeChunksEverySecond = false;
    [Tooltip("Press this key to dump a one-shot histogram of bound radii for registered tree chunks.")]
    public KeyCode dumpTreeBoundsKey = KeyCode.T;
    [Tooltip("Press this key to dump VisibilitySystem batch diagnostics (active count, visible count, horizon/frustum fail counts, etc).")]
    public KeyCode dumpVisibilityKey = KeyCode.Y;

    [Header("Gizmos")]
    public bool drawGizmos = false;
    [Tooltip("Cap to keep editor responsive on a 200k-chunk world.")]
    public int maxGizmosToDraw = 2000;
    [Tooltip("Skip drawing chunks that classify as Visible (focus on culled).")]
    public bool hideVisibleGizmos = false;
    [Tooltip("Skip drawing FailedHorizon (often the entire far hemisphere).")]
    public bool hideHorizonFailGizmos = true;
    [Tooltip("Also draw the active camera's frustum corners.")]
    public bool drawFrustum = true;
    [Tooltip("Restrict gizmos to chunks currently registered in TreeRenderer (the population that actually draws).")]
    public bool onlyTreeChunks = false;

    private float lastLogTime;

    private void Update()
    {
        VisibilitySystem.SkipHorizon = skipHorizon;
        VisibilitySystem.SkipFrustum = skipFrustum;

        if (Application.isPlaying && Input.GetKeyDown(dumpTreeBoundsKey))
            DumpTreeBoundStats();

        if (Application.isPlaying && Input.GetKeyDown(dumpVisibilityKey)
            && VisibilitySystem.IsReady)
            VisibilitySystem.Instance.DumpDiagnostics();

        if (!logCountsEverySecond && !logTreeChunksEverySecond) return;
        if (!Application.isPlaying) return;
        if (Time.unscaledTime - lastLogTime < 1f) return;
        lastLogTime = Time.unscaledTime;

        if (!VisibilitySystem.IsReady) return;
        var sys = VisibilitySystem.Instance;

        if (logCountsEverySecond)
        {
            int visible = 0, failHorizon = 0, failFrustum = 0, invalid = 0;
            int n = sys.SlotCount;
            for (int i = 0; i < n; i++)
            {
                switch (sys.ClassifyChunk(i))
                {
                    case VisibilitySystem.ChunkVisibility.Visible: visible++; break;
                    case VisibilitySystem.ChunkVisibility.FailedHorizon: failHorizon++; break;
                    case VisibilitySystem.ChunkVisibility.FailedFrustum: failFrustum++; break;
                    default: invalid++; break;
                }
            }
            int validTotal = visible + failHorizon + failFrustum;
            Camera cam = sys.ActiveCamera;
            Debug.Log($"[VisibilityDebugger] valid={validTotal} visible={visible} failHorizon={failHorizon} failFrustum={failFrustum} (invalid={invalid}) cam={(cam != null ? cam.name : "<none>")} frustumActive={sys.HasValidFrustum}");
        }

        if (logTreeChunksEverySecond && TreeRenderer.Instance != null)
        {
            var tr = TreeRenderer.Instance;
            var idxs = tr.PopulatedStorageIndices;
            int visible = 0, failHorizon = 0, failFrustum = 0, invalid = 0;
            for (int k = 0; k < idxs.Count; k++)
            {
                switch (sys.ClassifyChunk(idxs[k]))
                {
                    case VisibilitySystem.ChunkVisibility.Visible: visible++; break;
                    case VisibilitySystem.ChunkVisibility.FailedHorizon: failHorizon++; break;
                    case VisibilitySystem.ChunkVisibility.FailedFrustum: failFrustum++; break;
                    default: invalid++; break;
                }
            }
            Debug.Log($"[VisibilityDebugger TREE] registered={idxs.Count} visible={visible} failHorizon={failHorizon} failFrustum={failFrustum} invalid={invalid}");
        }
    }

    private void DumpTreeBoundStats()
    {
        if (!VisibilitySystem.IsReady || TreeRenderer.Instance == null) { Debug.LogWarning("[VisibilityDebugger] DumpTreeBoundStats: VisibilitySystem or TreeRenderer not ready."); return; }
        var sys = VisibilitySystem.Instance;
        var idxs = TreeRenderer.Instance.PopulatedStorageIndices;
        if (idxs.Count == 0) { Debug.Log("[VisibilityDebugger] No registered tree chunks."); return; }

        // Histogram of bound radii — the prime suspect for "frustum is too permissive".
        // Buckets in meters: 0–50, 50–100, 100–200, 200–500, 500–1000, 1000+.
        int[] buckets = new int[6];
        float minR = float.PositiveInfinity, maxR = 0f, sumR = 0f;
        int counted = 0;
        Camera cam = sys.ActiveCamera;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 camFwd = cam != null ? cam.transform.forward : Vector3.forward;
        // Find a worst offender: a tree chunk classified Visible but actually behind camera (dot < -0.2).
        int worstIdx = -1; float worstDot = 0f; float worstR = 0f; Vector3 worstC = Vector3.zero;
        for (int k = 0; k < idxs.Count; k++)
        {
            int idx = idxs[k];
            if (!sys.IsSlotValid(idx)) continue;
            sys.GetChunkBoundingSphere(idx, out Vector3 c, out float r);
            if (r < minR) minR = r;
            if (r > maxR) maxR = r;
            sumR += r;
            counted++;
            int b = r < 50f ? 0 : r < 100f ? 1 : r < 200f ? 2 : r < 500f ? 3 : r < 1000f ? 4 : 5;
            buckets[b]++;

            if (cam != null && sys.ClassifyChunk(idx) == VisibilitySystem.ChunkVisibility.Visible)
            {
                Vector3 toCenter = (c - camPos).normalized;
                float d = Vector3.Dot(toCenter, camFwd);
                if (d < worstDot)
                {
                    worstDot = d;
                    worstIdx = idx;
                    worstR = r;
                    worstC = c;
                }
            }
        }

        float avg = counted > 0 ? sumR / counted : 0f;
        Debug.Log($"[VisibilityDebugger TREE BOUNDS] count={counted} radius min={minR:F1} max={maxR:F1} avg={avg:F1} | <50:{buckets[0]} 50-100:{buckets[1]} 100-200:{buckets[2]} 200-500:{buckets[3]} 500-1000:{buckets[4]} 1000+:{buckets[5]}");
        if (worstIdx >= 0)
        {
            float distToCenter = Vector3.Distance(camPos, worstC);
            Debug.Log($"[VisibilityDebugger TREE WORST] idx={worstIdx} classified=Visible but dot(camFwd, toCenter)={worstDot:F2} (negative=behind) distToCenter={distToCenter:F1}m radius={worstR:F1}m centerWorld={worstC}");
        }
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;
        if (!Application.isPlaying) return;
        if (!VisibilitySystem.IsReady) return;
        var sys = VisibilitySystem.Instance;

        if (drawFrustum && sys.ActiveCamera != null)
        {
            Gizmos.color = Color.cyan;
            Matrix4x4 prev = Gizmos.matrix;
            Gizmos.matrix = sys.ActiveCamera.transform.localToWorldMatrix;
            Gizmos.DrawFrustum(Vector3.zero, sys.ActiveCamera.fieldOfView, sys.ActiveCamera.farClipPlane, sys.ActiveCamera.nearClipPlane, sys.ActiveCamera.aspect);
            Gizmos.matrix = prev;
        }

        int n = sys.SlotCount;
        int drawn = 0;

        // Optionally restrict to chunks the TreeRenderer actually draws.
        if (onlyTreeChunks && TreeRenderer.Instance != null)
        {
            var idxs = TreeRenderer.Instance.PopulatedStorageIndices;
            for (int k = 0; k < idxs.Count && drawn < maxGizmosToDraw; k++)
            {
                int i = idxs[k];
                if (!TryDrawChunkGizmo(sys, i)) continue;
                drawn++;
            }
            return;
        }

        for (int i = 0; i < n && drawn < maxGizmosToDraw; i++)
        {
            if (!TryDrawChunkGizmo(sys, i)) continue;
            drawn++;
        }
    }

    private bool TryDrawChunkGizmo(VisibilitySystem sys, int i)
    {
        if (!sys.IsSlotValid(i)) return false;
        var cls = sys.ClassifyChunk(i);
        if (cls == VisibilitySystem.ChunkVisibility.Visible && hideVisibleGizmos) return false;
        if (cls == VisibilitySystem.ChunkVisibility.FailedHorizon && hideHorizonFailGizmos) return false;
        if (cls == VisibilitySystem.ChunkVisibility.InvalidSlot) return false;

        switch (cls)
        {
            case VisibilitySystem.ChunkVisibility.Visible: Gizmos.color = new Color(0f, 1f, 0f, 0.6f); break;
            case VisibilitySystem.ChunkVisibility.FailedFrustum: Gizmos.color = new Color(1f, 0f, 0f, 0.6f); break;
            case VisibilitySystem.ChunkVisibility.FailedHorizon: Gizmos.color = new Color(1f, 1f, 0f, 0.4f); break;
        }
        sys.GetChunkBoundingSphere(i, out Vector3 c, out float r);
        Gizmos.DrawWireSphere(c, r);
        return true;
    }
}
