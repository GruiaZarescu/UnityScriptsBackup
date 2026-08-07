#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Single-object placement tool with a live hover-preview ghost: the selected prefab follows
/// the cursor, can be rotated before committing (Q/E = 15° steps, Shift+Q/E = 1° steps), and
/// only becomes a real database entry on click. Ctrl+Click an existing object to remove it.
/// </summary>
public class SimplePlacementTool : IMapObjectAuthoringTool
{
    public string DisplayName => "Simple Placement";

    private enum EditMode { Off, Place }
    private EditMode _mode = EditMode.Off;

    private int _selectedPrototypeIndex = 0;
    private string[] _prototypeNames = new string[0];

    // ── Hover-preview ghost ──────────────────────────────────────────────────
    private GameObject _ghost;
    private int _ghostPrototypeIndex = -1;
    private float _pendingYawDegrees = 0f;
    private bool _ghostValidHit = false;
    private Vector3 _ghostHitPoint;
    private Quaternion _ghostBaseRot; // terrain-aligned "up", before the user's yaw offset

    private static int PickMask
    {
        get
        {
            int layer = LayerMask.NameToLayer("MapObjectPicking");
            if (layer < 0)
            {
                Debug.LogError("[SimplePlacementTool] Layer 'MapObjectPicking' does not exist. " +
                    "Add it in Project Settings > Tags and Layers.");
                return 0;
            }
            return 1 << layer;
        }
    }

    public void OnDashboardGUI(MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (registry == null || registry.entries == null)
        {
            EditorGUILayout.HelpBox("Assign a MapObjectPrototypeRegistry to place objects.", MessageType.Warning);
            return;
        }

        if (_prototypeNames.Length != registry.entries.Length)
        {
            _prototypeNames = new string[registry.entries.Length];
            for (int i = 0; i < registry.entries.Length; i++)
                _prototypeNames[i] = $"[{i}] {(registry.entries[i]?.name ?? "null")}";
        }

        bool placing = _mode == EditMode.Place;
        GUI.backgroundColor = placing ? new Color(0.4f, 1f, 0.5f) : Color.white;
        if (GUILayout.Button(placing ? "PLACEMENT MODE: ON  (click to exit)" : "Placement Mode: OFF  (click to enter)",
                GUILayout.Height(30)))
        {
            _mode = placing ? EditMode.Off : EditMode.Place;
            if (_mode == EditMode.Off) DestroyGhost();
            SceneView.RepaintAll();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Prototype To Place", EditorStyles.boldLabel);
        int newIndex = EditorGUILayout.Popup(_selectedPrototypeIndex, _prototypeNames);
        newIndex = Mathf.Clamp(newIndex, 0, Mathf.Max(0, registry.entries.Length - 1));
        if (newIndex != _selectedPrototypeIndex)
        {
            _selectedPrototypeIndex = newIndex;
            DestroyGhost(); // rebuilt lazily next tick for the new prototype
        }

        if (GUILayout.Button("Reset Rotation"))
            _pendingYawDegrees = 0f;

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "PLACEMENT MODE ON:\n" +
            "  Move mouse over terrain → preview follows cursor\n" +
            "  Q / E → rotate 15°   Shift+Q / Shift+E → rotate 1°\n" +
            "  Click terrain → place selected prototype\n" +
            "  Ctrl+Click an object → remove it\n" +
            "  Escape → exit placement mode\n\n" +
            "PLACEMENT MODE OFF: normal Unity selection and editing.",
            MessageType.Info);
    }

    public void OnToolDeactivated()
    {
        _mode = EditMode.Off;
        DestroyGhost();
    }

    public void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (database == null || registry == null) return;

        if (_mode == EditMode.Place)
            DrawModeBanner(view);

        if (!Application.isPlaying || _mode != EditMode.Place)
        {
            DestroyGhost();
            return;
        }

        Event e = Event.current;

        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(controlID);

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            _mode = EditMode.Off;
            DestroyGhost();
            e.Use();
            view.Repaint();
            return;
        }

        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Q || e.keyCode == KeyCode.E))
        {
            float step = e.shift ? 1f : 15f;
            _pendingYawDegrees += (e.keyCode == KeyCode.E ? step : -step);
            _pendingYawDegrees = Mathf.Repeat(_pendingYawDegrees, 360f);
            e.Use();
            view.Repaint();
        }

        // Hide the placement ghost while remove-intent (Ctrl/Cmd) is held — showing a "place
        // here" preview while the user is clearly trying to remove something is confusing.
        if (e.control || e.command)
        {
            if (_ghost != null) _ghost.SetActive(false);
            _ghostValidHit = false;
        }
        else
        {
            UpdateGhostPreview(registry, e);
        }

        if (e.type != EventType.MouseDown || e.button != 0) return;
        if (HandleUtility.nearestControl != controlID) return;

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

        if (e.control || e.command)
        {
            if (TryPickMapObject(ray, 2000f, out var meta, out _))
            {
                Vector3 removedPos = meta.transform.position;
                database.Remove(meta.id);
                Debug.Log($"[SimplePlacementTool] Removed entry id={meta.id} ('{meta.gameObject.name}')");

                var loader = UnityEngine.Object.FindAnyObjectByType<ChunkObjectLoader>();
                loader?.ForceReprocessChunkObjectsAt(removedPos);

                GUIUtility.hotControl = 0;
                e.Use();
                view.Repaint();
            }
            return;
        }

        if (!_ghostValidHit) return;

        ulong id = database.Add(_selectedPrototypeIndex, _ghostHitPoint, _ghost.transform.rotation, Vector3.one);
        Debug.Log($"[SimplePlacementTool] Added entry id={id} prototype={_selectedPrototypeIndex} at {_ghostHitPoint} yaw={_pendingYawDegrees:F1}");

        var reprocessLoader = UnityEngine.Object.FindAnyObjectByType<ChunkObjectLoader>();
        reprocessLoader?.ForceReprocessChunkObjectsAt(_ghostHitPoint);

        GUIUtility.hotControl = 0;
        e.Use();
        view.Repaint();
    }

    private void UpdateGhostPreview(MapObjectPrototypeRegistry registry, Event e)
    {
        EnsureGhost(registry);
        if (_ghost == null) { _ghostValidHit = false; return; }

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

        // Terrain-only: previously the ghost simply disappeared whenever a tree or an existing
        // fence was the nearest thing under the cursor, instead of previewing on the ground
        // behind it.
        if (STPTME.MapObjects.AuthoringRaycast.TryRaycastTerrain(ray, 2000f, out RaycastHit hit))
        {
            Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
            Vector3 radialUp = (hit.point - sphereCenter).normalized;
            _ghostBaseRot = Quaternion.FromToRotation(Vector3.up, radialUp);

            _ghostHitPoint = hit.point;
            _ghostValidHit = true;

            _ghost.SetActive(true);
            _ghost.transform.position = _ghostHitPoint;
            _ghost.transform.rotation = _ghostBaseRot * Quaternion.Euler(0f, _pendingYawDegrees, 0f);
        }
        else
        {
            _ghostValidHit = false;
            _ghost.SetActive(false);
        }
    }

    private void EnsureGhost(MapObjectPrototypeRegistry registry)
    {
        if (_ghost != null && _ghostPrototypeIndex == _selectedPrototypeIndex) return;

        DestroyGhost();

        if (_selectedPrototypeIndex < 0 || _selectedPrototypeIndex >= registry.entries.Length) return;
        var entry = registry.entries[_selectedPrototypeIndex];
        if (entry?.sourcePrefab == null) return;

        _ghost = Object.Instantiate(entry.sourcePrefab);
        _ghost.name = "__PlacementGhost";
        _ghost.hideFlags = HideFlags.DontSave;

        foreach (var col in _ghost.GetComponentsInChildren<Collider>())
            col.enabled = false;

        foreach (var meta in _ghost.GetComponentsInChildren<STPTME.MapObjects.MapObjectMetadata>())
            Object.DestroyImmediate(meta);

        _ghostPrototypeIndex = _selectedPrototypeIndex;
    }

    private void DestroyGhost()
    {
        if (_ghost != null)
            Object.Destroy(_ghost);
        _ghost = null;
        _ghostPrototypeIndex = -1;
        _ghostValidHit = false;
    }

    /// <summary>
    /// Resolves which placed map object is under the ray. Mesh geometry first (accurate, and
    /// unambiguous when bounding volumes overlap), falling back to pick spheres only when no
    /// real geometry was hit — e.g. clicking through a gap in a fence. In the fallback the
    /// SMALLEST sphere wins, so a small object enclosed by a larger object's sphere stays
    /// selectable instead of always losing to the enclosing volume.
    /// </summary>
    public static bool TryPickMapObject(Ray ray, float maxDist, out STPTME.MapObjects.MapObjectMetadata meta, out Vector3 hitPoint)
    {
        meta = null;
        hitPoint = Vector3.zero;

        int pickLayer = LayerMask.NameToLayer("MapObjectPicking");
        int pickMask = pickLayer >= 0 ? 1 << pickLayer : 0;
        int meshMask = ~pickMask;

        RaycastHit[] hits = Physics.RaycastAll(ray, maxDist, meshMask, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            var m = hits[i].collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>();
            if (m == null || m.id == 0) continue;
            if (hits[i].distance < bestDist)
            {
                bestDist = hits[i].distance;
                meta = m;
                hitPoint = hits[i].point;
            }
        }
        if (meta != null) return true;

        if (!STPTME.MapObjects.MapObjectMetadata.PickSpheresEnabled) return false; // mesh-only mode
        if (pickMask == 0) return false;
        RaycastHit[] sphereHits = Physics.RaycastAll(ray, maxDist, pickMask, QueryTriggerInteraction.Collide);
        float smallestRadius = float.MaxValue;
        for (int i = 0; i < sphereHits.Length; i++)
        {
            var m = sphereHits[i].collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>();
            if (m == null || m.id == 0) continue;

            var sc = sphereHits[i].collider as SphereCollider;
            float worldRadius = sc != null
                ? sc.radius * Mathf.Max(Mathf.Abs(sc.transform.lossyScale.x),
                    Mathf.Max(Mathf.Abs(sc.transform.lossyScale.y), Mathf.Abs(sc.transform.lossyScale.z)))
                : float.MaxValue;

            if (worldRadius < smallestRadius)
            {
                smallestRadius = worldRadius;
                meta = m;
                hitPoint = sphereHits[i].point;
            }
        }
        return meta != null;
    }

    private void DrawModeBanner(SceneView view)
    {
        Handles.BeginGUI();
        var rect = new Rect(10, 10, 300, 64);
        EditorGUI.DrawRect(rect, new Color(0.1f, 0.5f, 0.2f, 0.85f));
        GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
            "● PLACEMENT MODE ACTIVE", EditorStyles.whiteBoldLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, 18),
            "Click = place · Ctrl+Click = remove · Esc = exit", EditorStyles.whiteMiniLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 42, rect.width - 16, 18),
            $"Q/E rotate (Shift = fine) · yaw={_pendingYawDegrees:F0}°", EditorStyles.whiteMiniLabel);
        Handles.EndGUI();
    }
}
#endif