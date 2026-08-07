#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using STPTME.MapObjects;

/// <summary>
/// Draws a spline the same way SplinePlacementTool does (click = waypoint, Backspace/
/// backspace= undo, Enter = commit, Escape = cancel), but instead of placing objects
/// along it, removes every existing placed object within a configurable radius ("corridor
/// width") of the curve. Meant for undoing a long fence line without deleting fences one
/// at a time.
///
/// Nothing is removed until Finish — the corridor and everything currently inside it are
/// preview-only (Handles + a live count) until then, same as placement's preview-first
/// convention.
/// </summary>
public class SplineRemovalTool : IMapObjectAuthoringTool
{
    public string DisplayName => "Spline (Removal)";

    private enum EditMode { Off, Drawing }
    private EditMode _mode = EditMode.Off;

    private readonly List<Vector3> _waypoints = new List<Vector3>();
    private float _corridorRadius = 2f;

    private int _prototypeFilterIndex = 0; // 0 = "All Prototypes"
    private string[] _prototypeFilterNames = new string[0];

    private const int PREVIEW_STEPS_PER_SEGMENT = 12;

    private List<MapObjectDatabase.MapObjectEntry> _lastRemovedSnapshot = new List<MapObjectDatabase.MapObjectEntry>();

    public void OnToolDeactivated()
    {
        _mode = EditMode.Off;
        _waypoints.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Dashboard
    // ═══════════════════════════════════════════════════════════════════

    public void OnDashboardGUI(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        RefreshPrototypeFilterList(registry);

        bool drawing = _mode == EditMode.Drawing;
        GUI.backgroundColor = drawing ? new Color(1f, 0.5f, 0.4f) : Color.white;
        if (GUILayout.Button(drawing ? "REMOVAL MODE: ON  (click to exit)" : "Removal Mode: OFF  (click to enter)",
                GUILayout.Height(30)))
        {
            if (drawing) CancelRun();
            _mode = drawing ? EditMode.Off : EditMode.Drawing;
            SceneView.RepaintAll();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Prototype Filter", EditorStyles.boldLabel);
        _prototypeFilterIndex = EditorGUILayout.Popup(_prototypeFilterIndex, _prototypeFilterNames);
        _prototypeFilterIndex = Mathf.Clamp(_prototypeFilterIndex, 0, _prototypeFilterNames.Length - 1);

        _corridorRadius = EditorGUILayout.Slider("Corridor Radius", _corridorRadius, 0.1f, 10f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Waypoints: {_waypoints.Count}");

        int matchCount = _waypoints.Count >= 2 ? CountMatches(database, registry) : 0;
        EditorGUILayout.LabelField($"Objects that will be removed: {matchCount}",
            matchCount > 0 ? EditorStyles.boldLabel : EditorStyles.label);

        using (new EditorGUI.DisabledScope(_waypoints.Count < 2))
        {
            GUI.backgroundColor = matchCount > 0 ? new Color(1f, 0.4f, 0.4f) : Color.white;
            if (GUILayout.Button($"Finish Line (remove {matchCount})", GUILayout.Height(26)))
                FinishRun(database, registry);
            GUI.backgroundColor = Color.white;
        }
        if (GUILayout.Button("Cancel Line"))
            CancelRun();

        using (new EditorGUI.DisabledScope(_lastRemovedSnapshot.Count == 0))
        {
            if (GUILayout.Button($"Undo Last Removal ({_lastRemovedSnapshot.Count} objects)"))
                UndoLastRemoval(database);
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "REMOVAL MODE ON:\n" +
            "  Click terrain → add waypoint\n" +
            "  Backspace → undo last waypoint\n" +
            "  Enter → finish (remove everything in the corridor)\n" +
            "  Escape → cancel (nothing removed)\n" +
            "Objects currently inside the corridor are highlighted red as you draw.",
            MessageType.Info);
    }

    private void RefreshPrototypeFilterList(MapObjectPrototypeRegistry registry)
    {
        var names = new List<string> { "[All Prototypes]" };
        if (registry?.entries != null)
            for (int i = 0; i < registry.entries.Length; i++)
                names.Add($"[{i}] {(registry.entries[i]?.name ?? "null")}");
        _prototypeFilterNames = names.ToArray();
    }

    /// <summary>-1 means "match every prototype"; otherwise a real registry index.</summary>
    private int FilterPrototypeIndex => _prototypeFilterIndex - 1;

    // ═══════════════════════════════════════════════════════════════════
    // Scene interaction
    // ═══════════════════════════════════════════════════════════════════

    public void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (_mode != EditMode.Drawing) return;
        DrawModeBanner(view);

        if (!Application.isPlaying) return;

        Event e = Event.current;

        int controlID = GUIUtility.GetControlID(FocusType.Passive);

        // Always register unconditionally — this only decides "who wins when nothing else
        // claims the event," it does NOT itself block camera navigation. Gating this behind
        // a guessed e.button/e.alt check was the actual bug: Event.current.button during a
        // Layout event doesn't reliably reflect what's about to be clicked (Layout fires every
        // repaint regardless of button state), so that guess was unreliable in both directions.
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(controlID);

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape) { CancelRun(); e.Use(); view.Repaint(); return; }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            { FinishRun(database, registry); e.Use(); view.Repaint(); return; }
            if (e.keyCode == KeyCode.Backspace && _waypoints.Count > 0)
            { _waypoints.RemoveAt(_waypoints.Count - 1); InvalidateMatchCache(); e.Use(); view.Repaint(); return; }
        }

        DrawLivePreview(database, registry);

        // Only a clean, unmodified left-click is ours. Right-click, middle-click, Alt+drag,
        // Ctrl/Cmd+anything all fall through here untouched — nothing is consumed, nothing is
        // claimed, so Unity's native scene navigation (orbit, pan, zoom) works exactly as if
        // no custom tool were active at all.
        bool placeClick = e.type == EventType.MouseDown && e.button == 0 && !e.alt && !e.control && !e.command;
        if (!placeClick) return;
        if (HandleUtility.nearestControl != controlID) return;

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (!AuthoringRaycast.TryRaycastTerrain(ray, 2000f, out RaycastHit hit)) return;

        _waypoints.Add(hit.point);
        InvalidateMatchCache();
        e.Use();
        view.Repaint();
    }

    private void CancelRun()
    {
        _waypoints.Clear();
        InvalidateMatchCache();
    }

    private void FinishRun(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (_waypoints.Count < 2) { CancelRun(); return; }

        var table = SplineMath.BuildArcLengthTable(_waypoints, PREVIEW_STEPS_PER_SEGMENT * 2);
        int filterProto = FilterPrototypeIndex;

        var toRemove = new List<ulong>();
        var snapshot = new List<MapObjectDatabase.MapObjectEntry>();
        var touchedChunks = new HashSet<(int packed, FaceId face)>();

        var settings = TerrainManagementSettings.Instance;
        Vector3 sphereCenter = settings.sphereCenter;
        float chunkSize = settings.terrainSize / settings.tilingFactor;
        float faceWorldSize = settings.faceWorldSize; // see ChunkManager.GetFaceWorldSize for why this must not be reinvented locally

        foreach (var entry in database.All)
        {
            if (filterProto >= 0 && entry.prototypeIndex != filterProto) continue;
            if (SplineMath.DistanceToPolyline(entry.worldPosition, table) > _corridorRadius) continue;

            toRemove.Add(entry.id);
            snapshot.Add(entry);

            if (MapObjectChunkMath.TryResolve(entry.worldPosition, sphereCenter, chunkSize, faceWorldSize,
                    settings.numberOfChunks, settings.minX, settings.maxX, out var addr))
                touchedChunks.Add((addr.packed, addr.face));
        }

        foreach (var id in toRemove)
            database.Remove(id);

        var loader = Object.FindAnyObjectByType<ChunkObjectLoader>();
        if (loader != null)
            foreach (var (packed, face) in touchedChunks)
                loader.ForceReprocessChunkObjects(packed, face, 0);

        Debug.Log($"[SplineRemovalTool] Removed {toRemove.Count} object(s) across {touchedChunks.Count} chunk(s).");

        _lastRemovedSnapshot = snapshot;
        _waypoints.Clear();
    }

    private void UndoLastRemoval(MapObjectDatabase database)
    {
        foreach (var entry in _lastRemovedSnapshot)
            database.Add(entry.prototypeIndex, entry.worldPosition, entry.worldRotation, entry.localScale);

        Debug.Log($"[SplineRemovalTool] Restored {_lastRemovedSnapshot.Count} object(s). " +
            "Note: restored objects get NEW ids — anything that referenced the old ids " +
            "(e.g. another tool's undo bookkeeping) won't recognize them as the same entries.");
        _lastRemovedSnapshot.Clear();
        // Not force-reprocessing here — walk away and back, or touch a nearby chunk, to
        // see the restored objects appear.
    }

    // ── Cached match state ───────────────────────────────────────────────
    // Both the dashboard's count and the scene view's highlight need the same answer, and
    // both used to recompute it independently EVERY repaint: rebuilding the arc-length table
    // (a terrain raycast per sample) and then testing all ~20k database objects against the
    // full polyline. Cost scaled as objects x polyline-segments, twice per frame, which is
    // why lag grew sharply with spline length. Now computed once per actual change and reused.
    private List<SplineMath.ArcSample> _cachedTable;
    private readonly List<Vector3> _cachedMatchPositions = new List<Vector3>();
    private int _cachedMatchCount;
    private int _cachedWaypointCount = -1;
    private float _cachedRadius = -1f;
    private int _cachedFilter = int.MinValue;
    private int _cachedDbVersion = -1;

    /// <summary>Recomputes the corridor match set only when something that affects it actually
    /// changed (waypoints, radius, prototype filter, or the database itself).</summary>
    private void EnsureMatches(MapObjectDatabase database)
    {
        if (database == null || _waypoints.Count < 2)
        {
            _cachedTable = null;
            _cachedMatchPositions.Clear();
            _cachedMatchCount = 0;
            return;
        }

        int filterProto = FilterPrototypeIndex;
        if (_cachedTable != null
            && _cachedWaypointCount == _waypoints.Count
            && Mathf.Approximately(_cachedRadius, _corridorRadius)
            && _cachedFilter == filterProto
            && _cachedDbVersion == database.Version)
            return;

        _cachedTable = SplineMath.BuildArcLengthTable(_waypoints, PREVIEW_STEPS_PER_SEGMENT);
        _cachedWaypointCount = _waypoints.Count;
        _cachedRadius = _corridorRadius;
        _cachedFilter = filterProto;
        _cachedDbVersion = database.Version;

        // Bounding box of the whole corridor. Rejecting on this first turns the expensive
        // per-object polyline walk (O(segments)) into a couple of float compares for the vast
        // majority of objects, which are nowhere near the drawn line.
        Vector3 min = _cachedTable[0].point, max = _cachedTable[0].point;
        for (int i = 1; i < _cachedTable.Count; i++)
        {
            min = Vector3.Min(min, _cachedTable[i].point);
            max = Vector3.Max(max, _cachedTable[i].point);
        }
        min -= Vector3.one * _corridorRadius;
        max += Vector3.one * _corridorRadius;

        _cachedMatchPositions.Clear();
        foreach (var entry in database.All)
        {
            if (filterProto >= 0 && entry.prototypeIndex != filterProto) continue;

            Vector3 p = entry.worldPosition;
            if (p.x < min.x || p.x > max.x || p.y < min.y || p.y > max.y || p.z < min.z || p.z > max.z)
                continue;

            if (SplineMath.DistanceToPolyline(p, _cachedTable) <= _corridorRadius)
                _cachedMatchPositions.Add(p);
        }
        _cachedMatchCount = _cachedMatchPositions.Count;
    }

    private int CountMatches(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        EnsureMatches(database);
        return _cachedMatchCount;
    }

    /// <summary>Forces the next EnsureMatches to recompute. The count/radius/filter/version
    /// checks catch almost everything, but a Clear() followed by rebuilding to the same
    /// waypoint count within one frame would otherwise slip through unnoticed.</summary>
    private void InvalidateMatchCache() => _cachedTable = null;

    // ═══════════════════════════════════════════════════════════════════
    // Live preview
    // ═══════════════════════════════════════════════════════════════════

    private void DrawLivePreview(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (_waypoints.Count == 0) return;

        Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;

        for (int i = 0; i < _waypoints.Count; i++)
        {
            Handles.color = Color.yellow;
            Handles.DrawWireDisc(_waypoints[i], (_waypoints[i] - sphereCenter).normalized, 0.4f);
        }

        if (_waypoints.Count < 2) return;

        EnsureMatches(database);
        if (_cachedTable == null) return;

        var table = _cachedTable;
        var linePoints = new Vector3[table.Count];
        for (int i = 0; i < table.Count; i++) linePoints[i] = table[i].point;
        Handles.color = new Color(1f, 0.5f, 0.3f);
        Handles.DrawAAPolyLine(3f, linePoints);

        // Corridor width, drawn as a translucent ribbon so the "eraser thickness" is
        // visible before committing. Stride scales with table length so a very long spline
        // doesn't issue thousands of DrawSolidDisc calls per frame.
        Handles.color = new Color(1f, 0.5f, 0.3f, 0.15f);
        int stride = Mathf.Max(2, table.Count / 120);
        for (int i = 0; i < table.Count; i += stride)
        {
            Vector3 up = (table[i].point - sphereCenter).normalized;
            Handles.DrawSolidDisc(table[i].point, up, _corridorRadius);
        }

        // Highlights come straight from the cached match set — no re-scan of the database.
        Handles.color = Color.red;
        foreach (Vector3 p in _cachedMatchPositions)
        {
            Vector3 up = (p - sphereCenter).normalized;
            Handles.DrawWireDisc(p, up, 0.6f);
            Handles.DrawWireCube(p, Vector3.one * 0.5f);
        }
    }

    private void DrawModeBanner(SceneView view)
    {
        Handles.BeginGUI();
        var rect = new Rect(10, 10, 340, 46);
        EditorGUI.DrawRect(rect, new Color(0.6f, 0.2f, 0.15f, 0.85f));
        GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
            "● REMOVAL MODE ACTIVE", EditorStyles.whiteBoldLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, 18),
            "Click = waypoint · Backspace = undo · Enter = remove · Esc = cancel", EditorStyles.whiteMiniLabel);
        Handles.EndGUI();
    }
}
#endif