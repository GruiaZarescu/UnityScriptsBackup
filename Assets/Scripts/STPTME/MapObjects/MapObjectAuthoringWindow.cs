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

    // [SerializeField] on an EditorWindow only survives a domain reload while the SAME window
    // instance stays alive. Fully closing the window (the X button) destroys that instance —
    // the next Open() call constructs a brand new one with no serialized state to inherit,
    // which is why database/registry reset every time. EditorPrefs (keyed by asset GUID,
    // resolved back through AssetDatabase) is what actually survives a real close+reopen —
    // and, as a bonus, a full Unity restart too.
    private const string DatabasePrefKey = "STPTME.MapObjectAuthoringWindow.DatabaseGUID";
    private const string RegistryPrefKey = "STPTME.MapObjectAuthoringWindow.RegistryGUID";

    private IMapObjectAuthoringTool[] _tools;
    private int _activeToolIndex = 0;

    [MenuItem("STPTME/Map Object Authoring")]
    public static void Open()
    {
        var win = GetWindow<MapObjectAuthoringWindow>("Map Object Authoring");
        win.Show();
    }

    private static T LoadPref<T>(string key) where T : UnityEngine.Object
    {
        string guid = EditorPrefs.GetString(key, "");
        if (string.IsNullOrEmpty(guid)) return null;
        string path = AssetDatabase.GUIDToAssetPath(guid);
        return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
    }

    private static void SavePref(string key, UnityEngine.Object obj)
    {
        if (obj == null) { EditorPrefs.DeleteKey(key); return; }
        string path = AssetDatabase.GetAssetPath(obj);
        if (string.IsNullOrEmpty(path)) return;
        EditorPrefs.SetString(key, AssetDatabase.AssetPathToGUID(path));
    }

    private void OnEnable()
    {
        STPTME.MapObjects.MapObjectMetadata.ShowAuthoringGizmos = true;

        if (database == null) database = LoadPref<MapObjectDatabase>(DatabasePrefKey);
        if (registry == null) registry = LoadPref<MapObjectPrototypeRegistry>(RegistryPrefKey);

        _tools = new IMapObjectAuthoringTool[]
        {
            new SimplePlacementTool(),
            new SplinePlacementTool(),
            new SplineRemovalTool(),
            new SplineChainEditTool(),
        };
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        STPTME.MapObjects.MapObjectMetadata.ShowAuthoringGizmos = false;
        SceneView.duringSceneGui -= OnSceneGUI;

        if (_tools != null && _activeToolIndex >= 0 && _activeToolIndex < _tools.Length)
            _tools[_activeToolIndex].OnToolDeactivated();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Shared References", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        var newDatabase = (MapObjectDatabase)EditorGUILayout.ObjectField("Database", database, typeof(MapObjectDatabase), false);
        var newRegistry = (MapObjectPrototypeRegistry)EditorGUILayout.ObjectField("Prototype Registry", registry, typeof(MapObjectPrototypeRegistry), false);
        if (EditorGUI.EndChangeCheck())
        {
            database = newDatabase;
            registry = newRegistry;
            SavePref(DatabasePrefKey, database);
            SavePref(RegistryPrefKey, registry);
        }

        EditorGUILayout.Space();
        STPTME.MapObjects.MapObjectMetadata.SnapToGroundEnabled = EditorGUILayout.ToggleLeft(
            "Snap To Ground When Moved", STPTME.MapObjects.MapObjectMetadata.SnapToGroundEnabled);

        STPTME.MapObjects.MapObjectMetadata.PickSpheresEnabled = EditorGUILayout.ToggleLeft(
            "Show Pick Spheres (uncheck for mesh-only picking)",
            STPTME.MapObjects.MapObjectMetadata.PickSpheresEnabled);
        using (new EditorGUI.DisabledScope(!STPTME.MapObjects.MapObjectMetadata.PickSpheresEnabled))
        {
            STPTME.MapObjects.MapObjectMetadata.PickSphereScale = EditorGUILayout.Slider(
                "Pick Sphere Scale", STPTME.MapObjects.MapObjectMetadata.PickSphereScale, 0.1f, 1f);
        }

        if (database == null)
        {
            EditorGUILayout.HelpBox("Assign a MapObjectDatabase asset to begin authoring.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField($"Entries in database: {database.Count}");
        EditorGUILayout.Space();

        string[] toolNames = new string[_tools.Length];
        for (int i = 0; i < _tools.Length; i++) toolNames[i] = _tools[i].DisplayName;
        int newToolIndex = GUILayout.Toolbar(_activeToolIndex, toolNames);
        if (newToolIndex != _activeToolIndex)
        {
            // Only the ACTIVE tool's OnSceneGUI runs — without this, switching away leaves
            // the old tool's mode "on" and its ghost preview frozen in the scene forever.
            _tools[_activeToolIndex].OnToolDeactivated();
            _activeToolIndex = newToolIndex;
            SceneView.RepaintAll();
        }

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