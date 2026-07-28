using UnityEditor;
using UnityEngine;

public class SimplePlacementTool : IMapObjectAuthoringTool
{
    public string DisplayName => "Simple Placement";

    private enum EditMode { Off, Place }
    private EditMode _mode = EditMode.Off;

    private int _selectedPrototypeIndex = 0;
    private string[] _prototypeNames = new string[0];

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

        // ── Mode toggle ──
        bool placing = _mode == EditMode.Place;
        GUI.backgroundColor = placing ? new Color(0.4f, 1f, 0.5f) : Color.white;
        if (GUILayout.Button(placing ? "PLACEMENT MODE: ON  (click to exit)" : "Placement Mode: OFF  (click to enter)",
                GUILayout.Height(30)))
        {
            _mode = placing ? EditMode.Off : EditMode.Place;
            SceneView.RepaintAll();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Prototype To Place", EditorStyles.boldLabel);
        _selectedPrototypeIndex = EditorGUILayout.Popup(_selectedPrototypeIndex, _prototypeNames);
        _selectedPrototypeIndex = Mathf.Clamp(_selectedPrototypeIndex, 0, Mathf.Max(0, registry.entries.Length - 1));

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "PLACEMENT MODE ON:\n" +
            "  Click terrain → place selected prototype\n" +
            "  Ctrl+Click an object → remove it\n" +
            "  Escape → exit placement mode\n" +
            "Transform gizmos still work on the selected object while placing.\n\n" +
            "PLACEMENT MODE OFF: normal Unity selection and editing.",
            MessageType.Info);
    }

    public void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (database == null || registry == null) return;

        if (_mode == EditMode.Place)
            DrawModeBanner(view);

        if (!Application.isPlaying || _mode != EditMode.Place) return;

        Event e = Event.current;

        // Register as the FALLBACK control. Unity's transform handles register their own
        // control IDs with real screen distances, so whenever the cursor is over a move/rotate
        // arrow, that handle becomes nearestControl and receives the click instead of us.
        // This is what makes "placement mode" and "dragging a selected object" coexist.
        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(controlID);

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            _mode = EditMode.Off;
            e.Use();
            view.Repaint();
            return;
        }

        if (e.type != EventType.MouseDown || e.button != 0) return;

        // Only act if nothing else (a handle, a collider gizmo) claimed this click.
        if (HandleUtility.nearestControl != controlID) return;

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

        if (e.control || e.command)
        {
            Ray pickRay = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (TryPickMapObject(pickRay, 2000f, out var meta, out Vector3 hitPoint))
            {
                Vector3 removedPos = meta.transform.position;   // use the object's own position, not the surface hit
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

        // ── Plain click: place ──
        int placementMask = ~PickMask;
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f, placementMask))
        {
            if (hit.collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>() != null)
                return;

            Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
            Vector3 radialUp = (hit.point - sphereCenter).normalized;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, radialUp);

            ulong id = database.Add(_selectedPrototypeIndex, hit.point, rot, Vector3.one);
            Debug.Log($"[SimplePlacementTool] Added entry id={id} prototype={_selectedPrototypeIndex} at {hit.point}");

            var loader = UnityEngine.Object.FindAnyObjectByType<ChunkObjectLoader>();
            loader?.ForceReprocessChunkObjectsAt(hit.point);

            GUIUtility.hotControl = 0;
            e.Use();
            view.Repaint();
        }
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

        // ── Pass 1: real geometry ──
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

        // ── Pass 2: pick spheres, smallest wins ──
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
        var rect = new Rect(10, 10, 280, 46);
        EditorGUI.DrawRect(rect, new Color(0.1f, 0.5f, 0.2f, 0.85f));
        GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
            "● PLACEMENT MODE ACTIVE", EditorStyles.whiteBoldLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, 18),
            "Click = place · Ctrl+Click = remove · Esc = exit", EditorStyles.whiteMiniLabel);
        Handles.EndGUI();
    }
}