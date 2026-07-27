#if UNITY_EDITOR
using UnityEditor;

/// <summary>
/// Unity discards in-memory changes to assets (including ScriptableObjects) made during
/// play mode when you exit play mode, unless they're explicitly saved first. Without this,
/// every object you Add()/Remove() while authoring in play mode would silently vanish the
/// moment you press Stop — no error, nothing in the console, just gone.
///
/// This flushes every MapObjectDatabase asset to disk right before Unity reverts state.
/// </summary>
[InitializeOnLoad]
public static class MapObjectDatabasePlayModePersistence
{
    static MapObjectDatabasePlayModePersistence()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingPlayMode) return;

        foreach (var guid in AssetDatabase.FindAssets("t:MapObjectDatabase"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var db = AssetDatabase.LoadAssetAtPath<MapObjectDatabase>(path);
            if (db != null) EditorUtility.SetDirty(db);
        }
        AssetDatabase.SaveAssets();
    }
}
#endif