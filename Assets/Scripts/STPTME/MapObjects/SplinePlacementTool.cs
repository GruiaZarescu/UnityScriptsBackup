using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Places a line of connector-based prefabs (fences etc.) along a waypoint path. Waypoints
/// are plain clicks on the terrain; the path between them is a Catmull-Rom spline (a straight
/// line for exactly 2 waypoints). Nothing is written to the database until "Finish" — the
/// whole in-progress run is preview-only (drawn with Handles, no real GameObjects) until then.
///
/// Only prototypes with hasConnectors=true are offered — see MapObjectPrototypeRegistry.
/// </summary>
public class SplinePlacementTool : IMapObjectAuthoringTool
{
    public string DisplayName => "Spline (Fence Line)";

    private enum EditMode { Off, Drawing }
    private EditMode _mode = EditMode.Off;

    private readonly List<Vector3> _waypoints = new List<Vector3>();
    private int _fenceDropdownIndex = 0;
    private List<int> _fenceProtoRegistryIndices = new List<int>();
    private string[] _fenceProtoNames = new string[0];

    private bool _autoSnapLastWaypoint = true;
    private const int PREVIEW_STEPS_PER_SEGMENT = 12;
    private const int SNAP_BISECTION_ITERATIONS = 14;
    private const int GROUND_SOLVE_ITERATIONS = 12;

    private List<ulong> _lastCommittedIds = new List<ulong>();

    private static int PickMask
    {
        get
        {
            int layer = LayerMask.NameToLayer("MapObjectPicking");
            return layer >= 0 ? 1 << layer : 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dashboard
    // ═══════════════════════════════════════════════════════════════════════

    public void OnDashboardGUI(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        RefreshFenceProtoList(registry);

        if (_fenceProtoNames.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "No prototypes have hasConnectors=true. Enable it on a fence-like prototype " +
                "in the registry (with connectorStartLocal/connectorEndLocal set) to use this tool.",
                MessageType.Warning);
            return;
        }

        bool drawing = _mode == EditMode.Drawing;
        GUI.backgroundColor = drawing ? new Color(0.4f, 0.7f, 1f) : Color.white;
        if (GUILayout.Button(drawing ? "SPLINE MODE: ON  (click to exit)" : "Spline Mode: OFF  (click to enter)",
                GUILayout.Height(30)))
        {
            if (drawing) CancelRun();
            _mode = drawing ? EditMode.Off : EditMode.Drawing;
            SceneView.RepaintAll();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Fence Prototype", EditorStyles.boldLabel);
        _fenceDropdownIndex = EditorGUILayout.Popup(_fenceDropdownIndex, _fenceProtoNames);
        _fenceDropdownIndex = Mathf.Clamp(_fenceDropdownIndex, 0, _fenceProtoNames.Length - 1);

        _autoSnapLastWaypoint = EditorGUILayout.ToggleLeft(
            "Auto-snap last waypoint (fit whole segments, eliminate trailing gap)", _autoSnapLastWaypoint);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Waypoints: {_waypoints.Count}");

        using (new EditorGUI.DisabledScope(_waypoints.Count < 2))
        {
            if (GUILayout.Button("Finish Line (commit)", GUILayout.Height(26)))
                FinishRun(database, registry);
        }
        if (GUILayout.Button("Cancel Line"))
            CancelRun();

        using (new EditorGUI.DisabledScope(_lastCommittedIds.Count == 0))
        {
            if (GUILayout.Button($"Undo Last Committed Line ({_lastCommittedIds.Count} objects)"))
                UndoLastCommit(database);
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "SPLINE MODE ON:\n" +
            "  Click terrain → add waypoint\n" +
            "  Backspace / Right-click → remove last waypoint\n" +
            "  Enter → finish (commit) this line\n" +
            "  Escape → cancel this line (nothing placed)\n" +
            "2 waypoints = straight line. 3+ = smooth curve through all of them.",
            MessageType.Info);
    }

    private void RefreshFenceProtoList(MapObjectPrototypeRegistry registry)
    {
        _fenceProtoRegistryIndices.Clear();
        var names = new List<string>();
        if (registry?.entries != null)
        {
            for (int i = 0; i < registry.entries.Length; i++)
            {
                var e = registry.entries[i];
                if (e != null && e.hasConnectors)
                {
                    _fenceProtoRegistryIndices.Add(i);
                    names.Add($"[{i}] {e.name}");
                }
            }
        }
        _fenceProtoNames = names.ToArray();
    }

    private int CurrentPrototypeIndex =>
        (_fenceDropdownIndex >= 0 && _fenceDropdownIndex < _fenceProtoRegistryIndices.Count)
            ? _fenceProtoRegistryIndices[_fenceDropdownIndex] : -1;

    // ═══════════════════════════════════════════════════════════════════════
    // Scene interaction
    // ═══════════════════════════════════════════════════════════════════════

    public void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (_mode != EditMode.Drawing) return;
        DrawModeBanner(view);

        if (!Application.isPlaying) return;
        int protoIndex = CurrentPrototypeIndex;
        if (protoIndex < 0) return;
        var entry = registry.entries[protoIndex];

        Event e = Event.current;

        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(controlID);

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { CancelRun(); e.Use(); view.Repaint(); return; }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            { FinishRun(database, registry); e.Use(); view.Repaint(); return; }
            if (e.keyCode == KeyCode.Backspace && _waypoints.Count > 0)
            { _waypoints.RemoveAt(_waypoints.Count - 1); e.Use(); view.Repaint(); return; }
        }

        DrawLivePreview(entry);

        bool leftClick = e.type == EventType.MouseDown && e.button == 0;
        bool rightClick = e.type == EventType.MouseDown && e.button == 1;
        if (!leftClick && !rightClick) return;
        if (HandleUtility.nearestControl != controlID) return;

        if (rightClick)
        {
            if (_waypoints.Count > 0) _waypoints.RemoveAt(_waypoints.Count - 1);
            e.Use();
            view.Repaint();
            return;
        }

        // Left click — add a waypoint.
        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        int placementMask = ~PickMask;
        if (!Physics.Raycast(ray, out RaycastHit hit, 2000f, placementMask)) return;
        if (hit.collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>() != null) return;

        Vector3 newPoint = hit.point;

        if (_autoSnapLastWaypoint && _waypoints.Count >= 1 && entry.ConnectorSpacing > 0.01f)
            newPoint = SnapNewWaypoint(_waypoints, newPoint, entry.ConnectorSpacing);

        _waypoints.Add(newPoint);
        e.Use();
        view.Repaint();
    }

    private void CancelRun()
    {
        _waypoints.Clear();
    }

    private void FinishRun(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        int protoIndex = CurrentPrototypeIndex;
        if (protoIndex < 0 || _waypoints.Count < 2) { CancelRun(); return; }
        var entry = registry.entries[protoIndex];
        float spacing = entry.ConnectorSpacing;
        if (spacing < 0.01f)
        {
            Debug.LogWarning("[SplinePlacementTool] Prototype has a degenerate connector spacing (~0). Aborting.");
            CancelRun();
            return;
        }

        var arcTable = BuildArcLengthTable(_waypoints, PREVIEW_STEPS_PER_SEGMENT * 2);
        float totalLength = arcTable[arcTable.Count - 1].cumulativeLength;
        int fenceCount = Mathf.FloorToInt(totalLength / spacing);
        if (fenceCount < 1)
        {
            Debug.LogWarning("[SplinePlacementTool] Path too short for even one segment. Aborting.");
            CancelRun();
            return;
        }

        if (Mathf.Abs(entry.connectorStartLocal.y) > 0.05f || Mathf.Abs(entry.connectorEndLocal.y) > 0.05f)
        {
            Debug.LogWarning($"[SplinePlacementTool] Prototype '{entry.name}' has a non-zero connector height " +
                $"(start.y={entry.connectorStartLocal.y:F3}, end.y={entry.connectorEndLocal.y:F3}). This is " +
                "IGNORED by placement — the connector is only used for horizontal (X/Z) spacing, since vertical " +
                "position always comes from the terrain sample, not the connector. If your fence looks buried or " +
                "floating, re-measure the connector at ground level (Y=0), not at rail/visual-join height.");
        }

        Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
        var placedIds = new List<ulong>(fenceCount);
        var touchedChunks = new HashSet<(int packed, FaceId face)>();
        var settings = TerrainManagementSettings.Instance;
        float chunkSize = settings.terrainSize / settings.tilingFactor;
        int subdivPow2 = 1 << settings.heightmapSubdivisions;
        float faceWorldSize = (settings.maxX - settings.minX + 1) * (settings.terrainSize / subdivPow2);

        // Segment length along the connector axis (horizontal in the prefab's local space).
        float segmentLength = Vector3.Distance(
            new Vector3(entry.connectorStartLocal.x, 0f, entry.connectorStartLocal.z),
            new Vector3(entry.connectorEndLocal.x, 0f, entry.connectorEndLocal.z));

        // Start the chain on the terrain at the path's origin.
        Vector3 chainPos = SampleAtArcLength(_waypoints, arcTable, 0f, out _);

        // For each fence: its START is held EXACTLY at the previous fence's END (so joints are
        // exact by construction and never corrected afterward), and its END is SOLVED for —
        // we search along the curve's heading for the point that is simultaneously (a) on the
        // terrain surface and (b) exactly segmentLength away from the start. Because that
        // point already satisfies both conditions, there is no post-hoc snap to displace the
        // joint, and both of the fence's legs land on the ground. Earlier versions picked an
        // end point and then snapped it to the terrain, which is what silently broke joint
        // continuity on slopes (the visible vertical step between adjacent fences).
        for (int i = 0; i < fenceCount; i++)
        {
            float targetLen = (i + 0.5f) * spacing;
            SampleAtArcLength(_waypoints, arcTable, targetLen, out Vector3 tangent);

            Vector3 radialUp = (chainPos - sphereCenter).normalized;

            // Heading = the curve's local direction, flattened into the tangent plane at the
            // chain's current position. Pitch is NOT taken from here — it falls out of the
            // solved end point below, which is what makes the fence follow real slope.
            Vector3 heading = Vector3.ProjectOnPlane(tangent, radialUp);
            if (heading.sqrMagnitude < 1e-8f)
            {
                Vector3 fallback = Mathf.Abs(radialUp.y) < 0.99f ? Vector3.up : Vector3.right;
                heading = Vector3.ProjectOnPlane(fallback, radialUp);
            }
            heading.Normalize();

            Vector3 endPoint = SolveGroundPointAtDistance(chainPos, heading, segmentLength, radialUp);

            Vector3 forward3D = endPoint - chainPos;
            if (forward3D.sqrMagnitude < 1e-8f) continue; // degenerate solve — skip this one
            forward3D.Normalize();

            Vector3 up = Vector3.ProjectOnPlane(radialUp, forward3D);
            if (up.sqrMagnitude < 1e-8f)
            {
                Vector3 fallback = Mathf.Abs(forward3D.y) < 0.99f ? Vector3.up : Vector3.right;
                up = Vector3.ProjectOnPlane(fallback, forward3D);
            }
            up.Normalize();

            Vector3 lookForward = Vector3.Cross(forward3D, up).normalized;
            Quaternion rot = Quaternion.LookRotation(lookForward, up);

            Vector3 connectorStartFlat = new Vector3(entry.connectorStartLocal.x, 0f, entry.connectorStartLocal.z);
            Vector3 worldPos = chainPos - (rot * connectorStartFlat);

            ulong id = database.Add(protoIndex, worldPos, rot, Vector3.one);
            placedIds.Add(id);

            if (MapObjectChunkMath.TryResolve(worldPos, sphereCenter, chunkSize, faceWorldSize,
                    settings.numberOfChunks, settings.minX, settings.maxX, out var addr))
                touchedChunks.Add((addr.packed, addr.face));

            // Advance the chain to the SOLVED end point exactly. No re-snap here — that is
            // precisely what broke joint continuity before. endPoint is already on the terrain
            // (the solve guaranteed it) AND already exactly segmentLength from the start, so
            // there is nothing left to correct.
            chainPos = endPoint;
        }

        var loader = Object.FindAnyObjectByType<ChunkObjectLoader>();
        if (loader != null)
            foreach (var (packed, face) in touchedChunks)
                loader.ForceReprocessChunkObjects(packed, face, 0);

        Debug.Log($"[SplinePlacementTool] Committed {placedIds.Count} instances of prototype {protoIndex} " +
            $"across {touchedChunks.Count} chunk(s).");

        _lastCommittedIds = placedIds;
        _waypoints.Clear();
    }

    private void UndoLastCommit(MapObjectDatabase database)
    {
        foreach (var id in _lastCommittedIds)
            database.Remove(id);
        Debug.Log($"[SplinePlacementTool] Undid {_lastCommittedIds.Count} instances.");
        _lastCommittedIds.Clear();
        // Note: does not force-reprocess chunks here — walk away and back, or place/remove
        // something nearby, to see the removal reflected immediately.
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Live preview drawing (Handles only — no real GameObjects while unconfirmed)
    // ═══════════════════════════════════════════════════════════════════════

    private void DrawLivePreview(MapObjectPrototypeRegistry.MapObjectPrototypeEntry entry)
    {
        if (_waypoints.Count == 0) return;

        for (int i = 0; i < _waypoints.Count; i++)
        {
            Handles.color = Color.yellow;
            Handles.DrawWireDisc(_waypoints[i], (_waypoints[i] - TerrainManagementSettings.Instance.sphereCenter).normalized, 0.4f);
        }

        if (_waypoints.Count < 2) return;

        var arcTable = BuildArcLengthTable(_waypoints, PREVIEW_STEPS_PER_SEGMENT);
        var linePoints = new Vector3[arcTable.Count];
        for (int i = 0; i < arcTable.Count; i++) linePoints[i] = arcTable[i].point;
        Handles.color = Color.cyan;
        Handles.DrawAAPolyLine(3f, linePoints);

        float spacing = entry.ConnectorSpacing;
        if (spacing < 0.01f) return;
        float totalLength = arcTable[arcTable.Count - 1].cumulativeLength;
        int fenceCount = Mathf.FloorToInt(totalLength / spacing);

        Handles.color = new Color(0.3f, 1f, 0.5f, 0.8f);
        for (int i = 0; i < fenceCount; i++)
        {
            float targetLen = (i + 0.5f) * spacing;
            Vector3 p = SampleAtArcLength(_waypoints, arcTable, targetLen, out _);
            Vector3 up = (p - TerrainManagementSettings.Instance.sphereCenter).normalized;
            Handles.DrawWireDisc(p, up, spacing * 0.3f);
        }
    }

    private void DrawModeBanner(SceneView view)
    {
        Handles.BeginGUI();
        var rect = new Rect(10, 10, 320, 46);
        EditorGUI.DrawRect(rect, new Color(0.15f, 0.3f, 0.6f, 0.85f));
        GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
            "● SPLINE MODE ACTIVE", EditorStyles.whiteBoldLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, 18),
            "Click = waypoint · Backspace/RClick = undo · Enter = finish · Esc = cancel", EditorStyles.whiteMiniLabel);
        Handles.EndGUI();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spline math — Catmull-Rom through waypoints, terrain-snapped per sample.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Finds a point that is simultaneously (a) on the terrain surface, and (b) exactly
    /// <paramref name="targetDistance"/> away in 3-D from <paramref name="start"/>, searching
    /// along <paramref name="heading"/> (a unit vector in the tangent plane at start).
    ///
    /// This is what lets a fence's start stay pinned to the previous fence's end (exact joint)
    /// while both of its legs still land on the ground: rather than picking an end point and
    /// then snapping it down — which displaces the joint — we solve directly for the end that
    /// already satisfies both conditions.
    ///
    /// On sloped ground the 3-D distance to the snapped surface point grows monotonically with
    /// horizontal travel, so plain bisection on horizontal distance converges reliably.
    /// </summary>
    private static Vector3 SolveGroundPointAtDistance(Vector3 start, Vector3 heading, float targetDistance, Vector3 radialUp)
    {
        // Horizontal travel needed is at most targetDistance (flat ground) and less on slopes,
        // so bracket [0, targetDistance] — with a little headroom for numerical slack.
        float lo = 0f;
        float hi = targetDistance * 1.05f;
        Vector3 best = SnapToSurface(start + heading * targetDistance);

        for (int iter = 0; iter < GROUND_SOLVE_ITERATIONS; iter++)
        {
            float mid = (lo + hi) * 0.5f;
            Vector3 candidate = SnapToSurface(start + heading * mid);
            float dist = Vector3.Distance(start, candidate);

            best = candidate;
            if (dist < targetDistance) lo = mid; else hi = mid;
        }

        // Fallback: if the solve produced something degenerate (e.g. no terrain under the
        // whole search span), fall back to a flat step so the run still completes rather
        // than collapsing into a zero-length segment.
        if (Vector3.Distance(start, best) < targetDistance * 0.25f)
            best = start + heading * targetDistance;

        return best;
    }

    private struct ArcSample { public Vector3 point; public float cumulativeLength; }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    /// <summary>Evaluates the path at a global parameter t in [0, waypoints.Count-1], using
    /// Catmull-Rom with clamped end neighbors (straight line if only 2 waypoints).</summary>
    private static Vector3 EvaluatePath(List<Vector3> wp, float globalT)
    {
        int n = wp.Count;
        if (n < 2) return n == 1 ? wp[0] : Vector3.zero;

        globalT = Mathf.Clamp(globalT, 0f, n - 1);
        int seg = Mathf.Clamp(Mathf.FloorToInt(globalT), 0, n - 2);
        float t = globalT - seg;

        if (n == 2) return Vector3.Lerp(wp[0], wp[1], t);

        Vector3 p0 = wp[Mathf.Max(seg - 1, 0)];
        Vector3 p1 = wp[seg];
        Vector3 p2 = wp[seg + 1];
        Vector3 p3 = wp[Mathf.Min(seg + 2, n - 1)];
        return CatmullRom(p0, p1, p2, p3, t);
    }

    /// <summary>Snaps a raw evaluated path point onto the real terrain surface along its own
    /// radial direction — same principle as SnapToGround elsewhere in this tool set.</summary>
    private static Vector3 SnapToSurface(Vector3 rawPoint)
    {
        Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
        Vector3 dirFromCenter = rawPoint - sphereCenter;
        float dist = dirFromCenter.magnitude;
        if (dist < 0.01f) return rawPoint;
        dirFromCenter /= dist;

        float castStartRadius = TerrainManagementSettings.Instance.sphereRadius + 2000f;
        Vector3 castOrigin = sphereCenter + dirFromCenter * castStartRadius;
        int mask = PickMask != 0 ? ~PickMask : ~0;

        if (Physics.Raycast(castOrigin, -dirFromCenter, out RaycastHit hit, castStartRadius + 2000f, mask))
            return hit.point;
        return rawPoint; // no terrain under this sample — leave it at the raw curve position
    }

    /// <summary>Builds a fine-grained (point, cumulativeArcLength) table over the whole path,
    /// with each sample re-snapped to the real terrain surface.</summary>
    private static List<ArcSample> BuildArcLengthTable(List<Vector3> wp, int stepsPerSegment)
    {
        int n = wp.Count;
        int totalSteps = (n - 1) * stepsPerSegment;
        var table = new List<ArcSample>(totalSteps + 1);

        Vector3 prev = SnapToSurface(EvaluatePath(wp, 0f));
        table.Add(new ArcSample { point = prev, cumulativeLength = 0f });

        float cumulative = 0f;
        for (int i = 1; i <= totalSteps; i++)
        {
            float t = (float)i / stepsPerSegment;
            Vector3 p = SnapToSurface(EvaluatePath(wp, t));
            cumulative += Vector3.Distance(prev, p);
            table.Add(new ArcSample { point = p, cumulativeLength = cumulative });
            prev = p;
        }
        return table;
    }

    /// <summary>Finds the world point (and forward tangent) at a given arc-length distance
    /// along the path, via linear interpolation between the nearest table entries.</summary>
    private static Vector3 SampleAtArcLength(List<Vector3> wp, List<ArcSample> table, float targetLength, out Vector3 tangent)
    {
        targetLength = Mathf.Clamp(targetLength, 0f, table[table.Count - 1].cumulativeLength);

        int lo = 0, hi = table.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (table[mid].cumulativeLength < targetLength) lo = mid + 1; else hi = mid;
        }
        int idx = Mathf.Max(lo, 1);
        var a = table[idx - 1];
        var b = table[idx];
        float span = b.cumulativeLength - a.cumulativeLength;
        float frac = span > 1e-6f ? (targetLength - a.cumulativeLength) / span : 0f;

        tangent = (b.point - a.point).sqrMagnitude > 1e-8f ? (b.point - a.point).normalized : Vector3.forward;
        return Vector3.Lerp(a.point, b.point, frac);
    }

    /// <summary>
    /// Adjusts a freshly-clicked waypoint so the path's TOTAL arc length becomes an exact
    /// multiple of spacing — eliminates the trailing gap in real time as each waypoint is
    /// placed. Only moves the new point along the ray from the previous waypoint through the
    /// raw click (preserving the user's intended direction); never deviates sideways.
    /// </summary>
    private static Vector3 SnapNewWaypoint(List<Vector3> existingWaypoints, Vector3 rawNewPoint, float spacing)
    {
        Vector3 prevWaypoint = existingWaypoints[existingWaypoints.Count - 1];
        Vector3 direction = rawNewPoint - prevWaypoint;
        float rawDistance = direction.magnitude;
        if (rawDistance < 0.001f) return rawNewPoint;
        direction /= rawDistance;

        var trial = new List<Vector3>(existingWaypoints) { rawNewPoint };
        var rawTable = BuildArcLengthTable(trial, PREVIEW_STEPS_PER_SEGMENT);
        float rawTotalLength = rawTable[rawTable.Count - 1].cumulativeLength;

        int count = Mathf.FloorToInt(rawTotalLength / spacing);
        if (count < 1) return rawNewPoint; // not even one full segment yet — leave as-is

        float idealLength = count * spacing;

        // Bisect the distance along `direction` from prevWaypoint so total arc length hits
        // idealLength. Arc length grows monotonically with distance for a trailing point,
        // so plain bisection is safe here.
        float low = 0f, high = rawDistance;
        Vector3 best = rawNewPoint;
        for (int iter = 0; iter < SNAP_BISECTION_ITERATIONS; iter++)
        {
            float mid = (low + high) * 0.5f;
            Vector3 candidateRaw = prevWaypoint + direction * mid;
            Vector3 candidate = SnapToSurface(candidateRaw);

            trial[trial.Count - 1] = candidate;
            var table = BuildArcLengthTable(trial, PREVIEW_STEPS_PER_SEGMENT);
            float len = table[table.Count - 1].cumulativeLength;

            best = candidate;
            if (len < idealLength) low = mid; else high = mid;
        }
        return best;
    }
}