#if false
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(MapObjectRecorder))]
public class MapObjectRecorderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();
        serializedObject.ApplyModifiedProperties();

        MapObjectRecorder recorder = (MapObjectRecorder)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Placement Tools", EditorStyles.boldLabel);

        // Snapshot info
        MapObjectSnapshot snapshot   = recorder.Snapshot;
        int               entryCount = snapshot?.entries != null ? snapshot.entries.Length : 0;

        if (snapshot != null)
            EditorGUILayout.HelpBox($"Snapshot: {entryCount} serialized object(s).", MessageType.Info);
        else
            EditorGUILayout.HelpBox(
                "No snapshot assigned. Save To Snapshot will auto-create one at the configured path.",
                MessageType.Warning);

        // Alignment row
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Align All To Sphere"))
                recorder.AlignAllToSphereSurface();

            if (GUILayout.Button("Align Selected"))
                AlignSelectedObjects(recorder);
        }

        EditorGUILayout.HelpBox(
            "Align: rotates objects so local-up points radially outward from the sphere center. " +
            "'Align Selected' acts only on the currently selected GameObjects.",
            MessageType.None);

        // Save row
        EditorGUILayout.Space();
        if (GUILayout.Button("Save To Snapshot"))
        {
            recorder.SaveToSnapshot();
            MarkSceneDirty(recorder);
        }
    }

    // Helpers

    private static void AlignSelectedObjects(MapObjectRecorder recorder)
    {
        GameObject[] selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
        {
            Debug.LogWarning("[MapObjectRecorderEditor] No GameObjects selected.");
            return;
        }

        foreach (GameObject go in selected)
            recorder.AlignTransformToSphere(go.transform);
    }

    private static void MarkSceneDirty(MapObjectRecorder recorder)
    {
        EditorUtility.SetDirty(recorder);
        if (!Application.isPlaying && recorder.gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(recorder.gameObject.scene);
    }
}
#endif
