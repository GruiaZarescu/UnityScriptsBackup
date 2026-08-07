#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using STPTME.MapObjects;

/// <summary>
/// Works with fence runs reconstructed from existing connector geometry (see
/// SplineChainReconstructor) — no additional stored data, so runs placed long before this tool
/// existed are just as editable as new ones.
///
/// Two modes:
///   Select — click any object in a run to highlight the whole run; delete it as a unit.
///   Extend — click ANYWHERE on an existing run (including mid-segment, not just at joints) to
///     seed a new spline that starts exactly there, then continue placing waypoints as normal.
///     The existing run is never modified; the new one simply begins at a solved point on it.
/// </summary>
public class SplineChainEditTool : IMapObjectAuthoringTool
{
    public string DisplayName => "Chain Edit";

    private enum EditMode { Off, Select, Extend }
    private EditMode _mode = EditMode.Off;

    private List<SplineChainReconstructor.Chain> _chains;
    private int _cachedVersion = -1;
    private float _cellSize = 0.25f;
    private bool _breakOnPrototypeChange = false;

    private SplineChainReconstructor.Chain _selectedChain;

    // ── Extend-mode state ────────────────────────────────────────────────────
    private readonly List<Vector3> _newWaypoints = new List<Vector3>();
    private bool _anchored;                 // has the first (snapped) waypoint been placed?
    private int _extendPrototypeIndex = -1;  // inherited from the chain we anchored to
    private List<ulong> _lastCommittedIds = new List<ulong>();

    private const int PREVIEW_STEPS_PER_SEGMENT = 12;
    private const float SNAP_MAX_DISTANCE = 15f;

    public void OnToolDeactivated()
    {
        _mode = EditMode.Off;
        _selectedChain = null;
        _newWaypoints.Clear();
        _anchored = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dashboard
    // ═══════════════════════════════════════════════════════════════════════

    public void OnDashboardGUI(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        EditorGUILayout.BeginHorizontal();
        DrawModeButton(EditMode.Off, "Off");
        DrawModeButton(EditMode.Select, "Select / Delete");
        DrawModeButton(EditMode.Extend, "Extend From Chain");
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        EditorGUI.BeginChangeCheck();
        _cellSize = EditorGUILayout.Slider("Join Tolerance", _cellSize, 0.02f, 2f);
        _breakOnPrototypeChange = EditorGUILayout.ToggleLeft(
            "Break chains where prototype changes", _breakOnPrototypeChange);
        if (EditorGUI.EndChangeCheck())
            _cachedVersion = -1; // force rebuild with the new settings

        EnsureChains(database, registry);
        EditorGUILayout.LabelField($"Reconstructed runs: {(_chains?.Count ?? 0)}");

        if (_mode == EditMode.Select && _selectedChain != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Selected run: {_selectedChain.objectIds.Count} object(s)", EditorStyles.boldLabel);
            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button($"Delete Entire Run ({_selectedChain.objectIds.Count})", GUILayout.Height(26)))
                DeleteSelectedChain(database);
            GUI.backgroundColor = Color.white;
        }

        if (_mode == EditMode.Extend)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_anchored
                ? $"Anchored. Waypoints: {_newWaypoints.Count}"
                : "Click an existing run to anchor the new spline's start.");

            using (new EditorGUI.DisabledScope(_newWaypoints.Count < 2))
            {
                if (GUILayout.Button("Finish Line (commit)", GUILayout.Height(26)))
                    FinishExtend(database, registry);
            }
            if (GUILayout.Button("Cancel Line"))
            {
                _newWaypoints.Clear();
                _anchored = false;
            }
        }

        using (new EditorGUI.DisabledScope(_lastCommittedIds.Count == 0))
        {
            if (GUILayout.Button($"Undo Last Commit ({_lastCommittedIds.Count})"))
            {
                foreach (ulong id in _lastCommittedIds) database.Remove(id);
                _lastCommittedIds.Clear();
                _cachedVersion = -1;
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "SELECT: click any fence → highlights its whole run → delete as a unit.\n\n" +
            "EXTEND: click anywhere on an existing run (mid-segment is fine — the exact point " +
            "is solved for) to anchor, then click to add waypoints and Enter to commit. The " +
            "existing run is never modified.\n\n" +
            "Backspace = undo waypoint · Enter = commit · Esc = cancel",
            MessageType.Info);
    }

    private void DrawModeButton(EditMode mode, string label)
    {
        GUI.backgroundColor = _mode == mode ? new Color(0.4f, 1f, 0.5f) : Color.white;
        if (GUILayout.Button(label, GUILayout.Height(26)))
        {
            _mode = mode;
            _selectedChain = null;
            _newWaypoints.Clear();
            _anchored = false;
            SceneView.RepaintAll();
        }
        GUI.backgroundColor = Color.white;
    }

    private void EnsureChains(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (database == null || registry == null) return;
        if (_chains != null && _cachedVersion == database.Version) return;

        _chains = SplineChainReconstructor.BuildChains(database, registry, _cellSize, _breakOnPrototypeChange);
        _cachedVersion = database.Version;
        _selectedChain = null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scene interaction
    // ═══════════════════════════════════════════════════════════════════════

    public void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (_mode == EditMode.Off || database == null || registry == null) return;
        if (!Application.isPlaying) return;

        EnsureChains(database, registry);
        DrawModeBanner(view);
        DrawChainPreview();

        Event e = Event.current;
        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(controlID);

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape)
            {
                _newWaypoints.Clear(); _anchored = false; _selectedChain = null;
                e.Use(); view.Repaint(); return;
            }
            if (_mode == EditMode.Extend)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                { FinishExtend(database, registry); e.Use(); view.Repaint(); return; }
                if (e.keyCode == KeyCode.Backspace && _newWaypoints.Count > 1)
                {
                    // Never remove the anchor itself — that would silently detach the run.
                    _newWaypoints.RemoveAt(_newWaypoints.Count - 1);
                    e.Use(); view.Repaint(); return;
                }
            }
        }

        bool placeClick = e.type == EventType.MouseDown && e.button == 0
            && !e.alt && !e.control && !e.command;
        if (!placeClick) return;
        if (HandleUtility.nearestControl != controlID) return;

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

        if (_mode == EditMode.Select)
        {
            HandleSelectClick(ray);
            e.Use(); view.Repaint();
            return;
        }

        // ── Extend mode ──
        if (!_anchored)
        {
            if (TryAnchorToChain(ray))
            {
                e.Use(); view.Repaint();
            }
            return;
        }

        if (AuthoringRaycast.TryRaycastTerrain(ray, 2000f, out RaycastHit hit))
        {
            _newWaypoints.Add(hit.point);
            e.Use(); view.Repaint();
        }
    }

    private void HandleSelectClick(Ray ray)
    {
        if (!SimplePlacementTool.TryPickMapObject(ray, 2000f, out var meta, out _)) { _selectedChain = null; return; }
        if (_chains == null) return;

        foreach (var chain in _chains)
        {
            if (chain.objectIds.Contains(meta.id))
            {
                _selectedChain = chain;
                return;
            }
        }
        _selectedChain = null;
    }

    /// <summary>
    /// Anchors the new spline to the closest point on any existing run — including partway
    /// along a segment, not just at joints. This is what makes "run a perpendicular fence up to
    /// an existing line and meet it wherever" work without having to hit a joint exactly.
    /// </summary>
    private bool TryAnchorToChain(Ray ray)
    {
        if (!AuthoringRaycast.TryRaycastTerrain(ray, 2000f, out RaycastHit hit)) return false;
        if (_chains == null) return false;

        if (!SplineChainReconstructor.TryFindNearestPointOnChains(
                _chains, hit.point, SNAP_MAX_DISTANCE, out var chain, out Vector3 point, out _))
            return false;

        // Inherit the prototype from the run being extended, so the new fence matches the one
        // it connects to rather than whatever an unrelated dropdown happened to be set to.
        _extendPrototypeIndex = chain.prototypeIndex;
        if (_extendPrototypeIndex < 0) return false;

        _newWaypoints.Clear();
        _newWaypoints.Add(point);
        _anchored = true;
        return true;
    }

    private void DeleteSelectedChain(MapObjectDatabase database)
    {
        if (_selectedChain == null) return;

        var settings = TerrainManagementSettings.Instance;
        float chunkSize = settings.terrainSize / settings.tilingFactor;
        float faceWorldSize = settings.faceWorldSize;
        var touchedChunks = new HashSet<(int packed, FaceId face)>();

        foreach (ulong id in _selectedChain.objectIds)
        {
            if (database.TryGet(id, out var entry) &&
                MapObjectChunkMath.TryResolve(entry.worldPosition, settings.sphereCenter, chunkSize,
                    faceWorldSize, settings.numberOfChunks, settings.minX, settings.maxX, out var addr))
                touchedChunks.Add((addr.packed, addr.face));

            database.Remove(id);
        }

        var loader = Object.FindAnyObjectByType<ChunkObjectLoader>();
        if (loader != null)
            foreach (var (packed, face) in touchedChunks)
                loader.ForceReprocessChunkObjects(packed, face, 0);

        Debug.Log($"[SplineChainEditTool] Deleted run of {_selectedChain.objectIds.Count} object(s).");
        _selectedChain = null;
        _cachedVersion = -1;
    }

    /// <summary>
    /// Commits the new run. Deliberately reuses SplinePlacementTool's public placement helper
    /// rather than duplicating the chained-placement / ground-solve math, which took a long
    /// time to get exactly right and must not exist in two divergent copies.
    /// </summary>
    private void FinishExtend(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (_newWaypoints.Count < 2 || _extendPrototypeIndex < 0)
        {
            _newWaypoints.Clear(); _anchored = false;
            return;
        }

        var placed = SplinePlacementTool.PlaceRunAlongWaypoints(
            database, registry, _extendPrototypeIndex, _newWaypoints);

        Debug.Log($"[SplineChainEditTool] Extended run: placed {placed.Count} object(s).");
        _lastCommittedIds = placed;
        _newWaypoints.Clear();
        _anchored = false;
        _cachedVersion = -1;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Preview drawing
    // ═══════════════════════════════════════════════════════════════════════

    private void DrawChainPreview()
    {
        if (_chains == null) return;
        Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;

        // All reconstructed runs, faint — so it's visible what the tool considers connected.
        Handles.color = new Color(0.4f, 0.8f, 1f, 0.35f);
        foreach (var chain in _chains)
        {
            if (chain.connectorPoints.Count < 2) continue;
            Handles.DrawAAPolyLine(2f, chain.connectorPoints.ToArray());
        }

        if (_selectedChain != null && _selectedChain.connectorPoints.Count >= 2)
        {
            Handles.color = Color.red;
            Handles.DrawAAPolyLine(5f, _selectedChain.connectorPoints.ToArray());
            foreach (var p in _selectedChain.connectorPoints)
                Handles.DrawWireDisc(p, (p - sphereCenter).normalized, 0.3f);
        }

        if (_mode == EditMode.Extend && _newWaypoints.Count > 0)
        {
            Handles.color = Color.yellow;
            foreach (var p in _newWaypoints)
                Handles.DrawWireDisc(p, (p - sphereCenter).normalized, 0.4f);

            if (_newWaypoints.Count >= 2)
            {
                Handles.color = new Color(0.3f, 1f, 0.4f);
                Handles.DrawAAPolyLine(3f, _newWaypoints.ToArray());
            }

            // Mark the anchor distinctly — it's the one point that isn't freely movable.
            Handles.color = Color.magenta;
            Handles.DrawWireDisc(_newWaypoints[0], (_newWaypoints[0] - sphereCenter).normalized, 0.7f);
        }
    }

    private void DrawModeBanner(SceneView view)
    {
        Handles.BeginGUI();
        var rect = new Rect(10, 10, 360, 46);
        EditorGUI.DrawRect(rect, new Color(0.2f, 0.45f, 0.55f, 0.85f));
        GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
            _mode == EditMode.Select ? "● CHAIN SELECT" : "● CHAIN EXTEND", EditorStyles.whiteBoldLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, 18),
            _mode == EditMode.Select
                ? "Click a fence to select its whole run · Esc = clear"
                : "Click a run to anchor · click to add waypoints · Enter = commit",
            EditorStyles.whiteMiniLabel);
        Handles.EndGUI();
    }
}
#endif