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

        // ── Ctrl+Click: remove ──
        if (e.control || e.command)
        {
            int mask = PickMask;
            if (mask == 0) return;

            if (Physics.Raycast(ray, out RaycastHit objHit, 2000f, mask, QueryTriggerInteraction.Collide))
            {
                var meta = objHit.collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>();
                if (meta != null && meta.id != 0)
                {
                    Vector3 removedPos = objHit.point;
                    database.Remove(meta.id);
                    Debug.Log($"[SimplePlacementTool] Removed entry id={meta.id}");

                    var loader = UnityEngine.Object.FindAnyObjectByType<ChunkObjectLoader>();
                    loader?.ForceReprocessChunkObjectsAt(removedPos);

                    GUIUtility.hotControl = 0;
                    e.Use();
                    view.Repaint();
                }
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