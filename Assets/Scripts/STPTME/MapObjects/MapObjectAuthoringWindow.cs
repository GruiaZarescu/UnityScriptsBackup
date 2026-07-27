using UnityEditor;
using UnityEngine;

/// <summary>
/// Central authoring dashboard: holds the shared database/registry references, lets you
/// switch between placement tools, and exposes global actions (Save). Individual tools
/// (SimplePlacementTool, and later a spline tool) plug in via IMapObjectAuthoringTool
/// without this window needing to know their internals.
/// </summary>
public class MapObjectAuthoringWindow : EditorWindow
{
    [SerializeField] private MapObjectDatabase database;
    [SerializeField] private MapObjectPrototypeRegistry registry;

    private IMapObjectAuthoringTool[] _tools;
    private int _activeToolIndex = 0;

    [MenuItem("STPTME/Map Object Authoring")]
    public static void Open()
    {
        var win = GetWindow<MapObjectAuthoringWindow>("Map Object Authoring");
        win.Show();
    }

    private void OnEnable()
    {
        _tools = new IMapObjectAuthoringTool[]
        {
            new SimplePlacementTool(),
            // Future: new SplinePlacementTool(),
        };
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Shared References", EditorStyles.boldLabel);
        database = (MapObjectDatabase)EditorGUILayout.ObjectField("Database", database, typeof(MapObjectDatabase), false);
        registry = (MapObjectPrototypeRegistry)EditorGUILayout.ObjectField("Prototype Registry", registry, typeof(MapObjectPrototypeRegistry), false);

        EditorGUILayout.Space();

        if (database == null)
        {
            EditorGUILayout.HelpBox("Assign a MapObjectDatabase asset to begin authoring.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField($"Entries in database: {database.Count}");
        EditorGUILayout.Space();

        string[] toolNames = new string[_tools.Length];
        for (int i = 0; i < _tools.Length; i++) toolNames[i] = _tools[i].DisplayName;
        _activeToolIndex = GUILayout.Toolbar(_activeToolIndex, toolNames);

        EditorGUILayout.Space();
        _tools[_activeToolIndex].OnDashboardGUI(database, registry);

        EditorGUILayout.Space();
        if (GUILayout.Button("Save Database Now"))
        {
            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssets();
            Debug.Log("[MapObjectAuthoringWindow] Database saved.");
        }
    }

    private void OnSceneGUI(SceneView view)
    {
        if (database == null || registry == null || _tools == null || _tools.Length == 0) return;
        _tools[_activeToolIndex].OnSceneGUI(view, database, registry);
    }
}