using UnityEditor;
using UnityEngine;

/// <summary>
/// Minimal single-object placement tool — the smallest thing that exercises the whole
/// Add/Remove/stream pipeline end to end. Click terrain to place the selected prototype,
/// Alt+Click an existing placed object to remove it.
/// </summary>
public class SimplePlacementTool : IMapObjectAuthoringTool
{
    public string DisplayName => "Simple Placement";

    private int _selectedPrototypeIndex = 0;
    private string[] _prototypeNames = new string[0];

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

        EditorGUILayout.LabelField("Prototype To Place", EditorStyles.boldLabel);
        _selectedPrototypeIndex = EditorGUILayout.Popup(_selectedPrototypeIndex, _prototypeNames);
        _selectedPrototypeIndex = Mathf.Clamp(_selectedPrototypeIndex, 0, Mathf.Max(0, registry.entries.Length - 1));

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Click on terrain (Scene view, Play Mode) to place the selected prototype.\n" +
            "Alt+Click an existing placed object to remove it.",
            MessageType.Info);
    }

    public void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry)
    {
        if (database == null || registry == null) return;
        if (!Application.isPlaying)
        {
            Handles.BeginGUI();
            GUILayout.Label("Simple Placement Tool: requires Play Mode (raycasts against live terrain colliders).",
                EditorStyles.helpBox);
            Handles.EndGUI();
            return;
        }

        Event e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 0) return;

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

        if (e.alt)
        {
            // Alt+Click: try to remove an existing placed object under the cursor.
            if (Physics.Raycast(ray, out RaycastHit objHit, 2000f))
            {
                var meta = objHit.collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>();
                if (meta != null && meta.id != 0)
                {
                    Vector3 removedPos = objHit.point;
                    database.Remove(meta.id);
                    Debug.Log($"[SimplePlacementTool] Removed entry id={meta.id}");

                    var loader = UnityEngine.Object.FindAnyObjectByType<ChunkObjectLoader>();
                    loader?.ForceReprocessChunkObjectsAt(removedPos);

                    e.Use();
                    view.Repaint();
                }
            }
            return;
        }

        // Plain click: place on terrain.
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f))
        {
            // Only place on terrain chunk colliders, not on already-placed objects.
            if (hit.collider.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>() != null)
                return;

            Vector3 sphereCenter = TerrainManagementSettings.Instance.sphereCenter;
            Vector3 radialUp = (hit.point - sphereCenter).normalized;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, radialUp);

            ulong id = database.Add(_selectedPrototypeIndex, hit.point, rot, Vector3.one);
            Debug.Log($"[SimplePlacementTool] Added entry id={id} prototype={_selectedPrototypeIndex} at {hit.point}");

            var loader = UnityEngine.Object.FindAnyObjectByType<ChunkObjectLoader>();
            loader?.ForceReprocessChunkObjectsAt(hit.point);

            e.Use();
            view.Repaint();
        }
    }
}