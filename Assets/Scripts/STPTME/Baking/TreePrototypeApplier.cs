#if false
#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

public class TreePrototypeApplier : EditorWindow
{
    private Terrain templateTerrain;
    private GameObject[] faceParents = new GameObject[6];

    private static readonly string[] faceNames = { "Top", "Bottom", "Front", "Back", "Left", "Right" };

    [MenuItem("Tools/Terrain/Apply Template Tree Prototypes To Children")]
    public static void ShowWindow()
    {
        GetWindow<TreePrototypeApplier>("Tree Prototype Applier");
    }

    private void OnGUI()
    {
        GUILayout.Label("Template Terrain Tree Prototype Sync", EditorStyles.boldLabel);

        templateTerrain = (Terrain)EditorGUILayout.ObjectField(
            "Template Terrain",
            templateTerrain,
            typeof(Terrain),
            true);

        for (int i = 0; i < 6; i++)
        {
            faceParents[i] = (GameObject)EditorGUILayout.ObjectField(
                $"{faceNames[i]} Parent (Optional)",
                faceParents[i],
                typeof(GameObject),
                true);
        }

        GUILayout.Space(10);

        if (GUILayout.Button("Apply Tree Prototypes"))
        {
            ApplyPrototypes();
        }
    }

    private void ApplyPrototypes()
    {
        if (templateTerrain == null)
        {
            Debug.LogError("Template Terrain is required.");
            return;
        }

        bool anySet = false;
        for (int i = 0; i < 6; i++)
        {
            if (faceParents[i] != null) { anySet = true; break; }
        }
        if (!anySet)
        {
            Debug.LogError("All face parents are null. Nothing to process.");
            return;
        }

        TreePrototype[] sharedPrototypes = templateTerrain.terrainData.treePrototypes;

        if (sharedPrototypes == null || sharedPrototypes.Length == 0)
        {
            Debug.LogWarning("Template terrain has no TreePrototypes assigned.");
            return;
        }

        int appliedCount = 0;

        for (int i = 0; i < 6; i++)
        {
            if (faceParents[i] != null)
                appliedCount += ApplyToChildren(faceParents[i], sharedPrototypes);
            else
                Debug.LogWarning($"{faceNames[i]} Parent is null. Skipping.");
        }

        Debug.Log($"Finished applying tree prototypes. Updated {appliedCount} terrains.");
    }

    private int ApplyToChildren(GameObject parent, TreePrototype[] prototypes)
    {
        int count = 0;

        Terrain[] terrains = parent.GetComponentsInChildren<Terrain>(true);

        foreach (Terrain t in terrains)
        {
            if (t == templateTerrain)
                continue;

            Undo.RecordObject(t.terrainData, "Apply Tree Prototypes");

            t.terrainData.treePrototypes = prototypes;
            EditorUtility.SetDirty(t.terrainData);

            count++;
        }

        return count;
    }
}
#endif
#endif
