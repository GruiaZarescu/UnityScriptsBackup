using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using STPTME.MapObjects;

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

    public void OnToolDeactivated()
    {
        _mode = EditMode.Off;
        CancelRun(); // clears any in-progress waypoints so switching away and back doesn't
                     // silently resume a forgotten, half-finished line
    }

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

        // Left click — add a waypoint. Terrain-only, so clicking "through" a tree lands the
        // waypoint on the ground behind it rather than doing nothing (the previous behaviour
        // bailed out entirely whenever the nearest hit happened to be a placed object).
        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (!AuthoringRaycast.TryRaycastTerrain(ray, 2000f, out RaycastHit hit)) return;

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

        if (Mathf.Abs(entry.connectorStartLocal.y - entry.connectorEndLocal.y) > 0.05f)
        {
            Debug.LogWarning($"[SplinePlacementTool] Prototype '{entry.name}' has connectors at DIFFERENT heights " +
                $"(start.y={entry.connectorStartLocal.y:F3}, end.y={entry.connectorEndLocal.y:F3}). The chain runs at " +
                "their average height, so a large mismatch will make consecutive rails meet imprecisely. Both " +
                "connectors should sit at the same height on the prefab (the height where fences visually join).");
        }

        Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
        var placedIds = new List<ulong>(fenceCount);
        var touchedChunks = new HashSet<(int packed, FaceId face)>();
        var settings = TerrainManagementSettings.Instance;
        float chunkSize = settings.terrainSize / settings.tilingFactor;
        int subdivPow2 = 1 << settings.heightmapSubdivisions;
        float faceWorldSize = (settings.maxX - settings.minX + 1) * (settings.terrainSize / subdivPow2);

        // Connector axis in the prefab's local space, and the height the connectors sit at.
        // The chain is run at THIS height (not ground level) so that each fence's end
        // connector and the next fence's start connector are the same point by construction —
        // anchoring at ground level instead leaves a gap of h·(up_i − up_i+1) between the
        // elevated rails whenever two neighbours differ in pitch.
        Vector3 connectorAxis = entry.connectorEndLocal - entry.connectorStartLocal;
        float segmentLength = connectorAxis.magnitude;
        float connectorHeight = (entry.connectorStartLocal.y + entry.connectorEndLocal.y) * 0.5f;

        // Start the chain at connector height above the terrain at the path's origin.
        Vector3 pathStart = SampleAtArcLength(_waypoints, arcTable, 0f, out _);
        Vector3 railChain = SnapToHeightAboveSurface(pathStart, connectorHeight);

        for (int i = 0; i < fenceCount; i++)
        {
            float targetLen = (i + 0.5f) * spacing;
            SampleAtArcLength(_waypoints, arcTable, targetLen, out Vector3 tangent);

            Vector3 radialUp = (railChain - sphereCenter).normalized;

            Vector3 heading = Vector3.ProjectOnPlane(tangent, radialUp);
            if (heading.sqrMagnitude < 1e-8f)
            {
                Vector3 fallback = Mathf.Abs(radialUp.y) < 0.99f ? Vector3.up : Vector3.right;
                heading = Vector3.ProjectOnPlane(fallback, radialUp);
            }
            heading.Normalize();

            Vector3 endRail = SolveSurfacePointAtDistance(railChain, heading, segmentLength, connectorHeight);

            Vector3 forward3D = endRail - railChain;
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

            // Full connector (Y included) — this is the whole point of the change. The pivot
            // lands on the ground because railChain sits exactly connectorHeight above it.
            Vector3 worldPos = railChain - (rot * entry.connectorStartLocal);

            ulong id = database.Add(protoIndex, worldPos, rot, Vector3.one);
            placedIds.Add(id);

            if (MapObjectChunkMath.TryResolve(worldPos, sphereCenter, chunkSize, faceWorldSize,
                    settings.numberOfChunks, settings.minX, settings.maxX, out var addr))
                touchedChunks.Add((addr.packed, addr.face));

            // Advance to the solved end connector exactly. This IS the next fence's start
            // connector — worldPos + rot·connectorEndLocal == railChain + L·forward == endRail
            // — so adjacent rails coincide by construction at any pitch, with nothing to
            // correct afterward.
            railChain = endRail;
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
    /// Snaps to the terrain surface, then lifts by <paramref name="height"/> along the local
    /// radial. Used to run the fence chain at CONNECTOR height rather than ground height —
    /// which is what makes adjacent rails meet exactly regardless of pitch difference.
    /// </summary>
    private static Vector3 SnapToHeightAboveSurface(Vector3 rawPoint, float height)
    {
        Vector3 ground = SnapToSurface(rawPoint);
        Vector3 up = (ground - TerrainManagementSettings.Instance.sphereCenter).normalized;
        return ground + up * height;
    }

    /// <summary>
    /// Finds a point that is simultaneously (a) at <paramref name="height"/> above the terrain
    /// surface, and (b) exactly <paramref name="targetDistance"/> away in 3-D from
    /// <paramref name="start"/>, searching along <paramref name="heading"/>.
    ///
    /// Running this at connector height (rather than ground height) is what lets each fence's
    /// end connector and the next fence's start connector be the SAME point by construction,
    /// at any pitch — while the pivot still lands on the ground, since it sits exactly
    /// `height` below the chain.
    /// </summary>
    private static Vector3 SolveSurfacePointAtDistance(Vector3 start, Vector3 heading, float targetDistance, float height)
    {
        float lo = 0f;
        float hi = targetDistance * 1.05f;
        Vector3 best = SnapToHeightAboveSurface(start + heading * targetDistance, height);

        for (int iter = 0; iter < GROUND_SOLVE_ITERATIONS; iter++)
        {
            float mid = (lo + hi) * 0.5f;
            Vector3 candidate = SnapToHeightAboveSurface(start + heading * mid, height);
            float dist = Vector3.Distance(start, candidate);

            best = candidate;
            if (dist < targetDistance) lo = mid; else hi = mid;
        }

        // Degenerate fallback (e.g. no terrain anywhere under the search span): step flat so
        // the run still completes rather than collapsing into a zero-length segment.
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

        // Terrain-only: a plain raycast here would happily land on a tree canopy or an
        // already-placed fence and plant the new segment on top of it.
        var ray = new Ray(castOrigin, -dirFromCenter);
        if (AuthoringRaycast.TryRaycastTerrain(ray, castStartRadius + 2000f, out RaycastHit hit))
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