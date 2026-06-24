#if UNITY_EDITOR
//!!!THIS CODE IS ONLY MEANT TO BE USED IN EDITOR!!!
using UnityEngine;
using UnityEditor;

[InitializeOnLoad]
public static class TerrainBrushPreviewFix
{
    private static bool ScriptActive = false;
    static TerrainBrushPreviewFix()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private static void OnSceneGUI(SceneView sceneView)
    {
        // Check if current selection is a Terrain
        if (Selection.activeGameObject != null &&
            Selection.activeGameObject.GetComponent<Terrain>() != null &&
            Event.current.type == EventType.Repaint &&ScriptActive)
        {
            Debug.Log("Terrain paint fix running, remember to turn off!");
            SceneView.RepaintAll();
        }
    }



    // Menu to toggle script on/off
    [MenuItem("Tools/Terrain Brush Fix/Enable")]
    private static void EnableScript() => ScriptActive = true;

    [MenuItem("Tools/Terrain Brush Fix/Disable")]
    private static void DisableScript() => ScriptActive = false;
}
#endif
