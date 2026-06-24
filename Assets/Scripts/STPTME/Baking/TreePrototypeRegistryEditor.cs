#if false
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Editor utilities for TreePrototypeRegistry setup.
/// Scaffold creates entries in terrain prototype order and auto-fills stable prefab-derived data.
/// Users still assign LOD meshes manually per prototype.
/// </summary>
public static class TreePrototypeRegistryEditor
{
#if UNITY_EDITOR
    /// <summary>
    /// Creates an entry from a terrain tree prefab.
    /// Material and base dimensions are extracted automatically; LOD meshes remain manual.
    /// </summary>
    public static TreePrototypeRegistry.TreePrototypeEntry CreateScaffoldEntryFromPrefab(GameObject prefab)
    {
        if (prefab == null)
            return new TreePrototypeRegistry.TreePrototypeEntry { name = "Empty" };

        var entry = new TreePrototypeRegistry.TreePrototypeEntry
        {
            name = prefab.name,
            sourcePrefab = prefab,
        };

        AssignMaterial(prefab, entry);
        AssignBaseDimensions(prefab, entry);

        return entry;
    }

    private static void AssignMaterial(GameObject prefab, TreePrototypeRegistry.TreePrototypeEntry entry)
    {
        var lodGroup = prefab.GetComponent<LODGroup>();
        if (lodGroup != null)
        {
            var lods = lodGroup.GetLODs();
            if (lods.Length > 0 && lods[0].renderers != null && lods[0].renderers.Length > 0)
            {
                var lodRenderer = lods[0].renderers[0];
                if (lodRenderer is MeshRenderer lodMeshRenderer && lodMeshRenderer.sharedMaterial != null)
                {
                    entry.material = lodMeshRenderer.sharedMaterial;
                    return;
                }
            }
        }

        var fallbackRenderer = prefab.GetComponentInChildren<MeshRenderer>();
        if (fallbackRenderer != null && fallbackRenderer.sharedMaterial != null)
        {
            entry.material = fallbackRenderer.sharedMaterial;
        }
    }

    private static void AssignBaseDimensions(GameObject prefab, TreePrototypeRegistry.TreePrototypeEntry entry)
    {
        var meshFilter = GetReferenceMeshFilter(prefab);
        if (meshFilter == null || meshFilter.sharedMesh == null)
            return;

        var bounds = meshFilter.sharedMesh.bounds;
        Vector3 rendererScale = meshFilter.transform.lossyScale;
        Vector3 scaledSize = new Vector3(
            bounds.size.x * Mathf.Abs(rendererScale.x),
            bounds.size.y * Mathf.Abs(rendererScale.y),
            bounds.size.z * Mathf.Abs(rendererScale.z)
        );

        bool isZOriented = scaledSize.z > scaledSize.y * 1.5f;
        if (isZOriented)
        {
            entry.baseWidth = Mathf.Max(scaledSize.x, scaledSize.y);
            entry.baseHeight = scaledSize.z;
        }
        else
        {
            entry.baseWidth = Mathf.Max(scaledSize.x, scaledSize.z);
            entry.baseHeight = scaledSize.y;
        }

        Debug.Log($"[TreePrototypeRegistryEditor] '{prefab.name}': meshBounds={bounds.size}, " +
            $"rendererScale={rendererScale}, scaledSize={scaledSize}, " +
            $"isZOriented={isZOriented}, baseWidth={entry.baseWidth:F2}, baseHeight={entry.baseHeight:F2}");
    }

    private static MeshFilter GetReferenceMeshFilter(GameObject prefab)
    {
        var lodGroup = prefab.GetComponent<LODGroup>();
        if (lodGroup != null)
        {
            var lods = lodGroup.GetLODs();
            if (lods.Length > 0 && lods[0].renderers != null && lods[0].renderers.Length > 0)
            {
                var lodRenderer = lods[0].renderers[0];
                if (lodRenderer != null)
                {
                    var lodMeshFilter = lodRenderer.GetComponent<MeshFilter>();
                    if (lodMeshFilter != null && lodMeshFilter.sharedMesh != null)
                        return lodMeshFilter;
                }
            }
        }

        var fallbackMeshFilter = prefab.GetComponentInChildren<MeshFilter>();
        if (fallbackMeshFilter != null && fallbackMeshFilter.sharedMesh != null)
            return fallbackMeshFilter;

        return null;
    }

    /// <summary>
    /// Creates entries from terrain prototypes in the correct order.
    /// This ensures baked prototypeIndex values map to the correct entries.
    /// User only needs to assign lodMeshes manually.
    /// </summary>
    public static void ScaffoldFromTerrain(TreePrototypeRegistry registry, Terrain terrain)
    {
        if (registry == null || terrain == null || terrain.terrainData == null) return;

        var terrainPrototypes = terrain.terrainData.treePrototypes;
        registry.prototypes = new TreePrototypeRegistry.TreePrototypeEntry[terrainPrototypes.Length];

        for (int i = 0; i < terrainPrototypes.Length; i++)
        {
            registry.prototypes[i] = CreateScaffoldEntryFromPrefab(terrainPrototypes[i].prefab);
            Debug.Log($"[TreePrototypeRegistryEditor] Scaffold [{i}]: '{registry.prototypes[i].name}' " +
                $"— assign lodMeshes manually");
        }

        EditorUtility.SetDirty(registry);
        Debug.Log($"[TreePrototypeRegistryEditor] Scaffolded {terrainPrototypes.Length} prototypes into '{registry.name}'. " +
            "Fill in lodMeshes for each entry.");
    }
#endif
}

#if UNITY_EDITOR
/// <summary>
/// Custom editor for TreePrototypeRegistry with utility buttons.
/// </summary>
[CustomEditor(typeof(TreePrototypeRegistry))]
public class TreePrototypeRegistryInspector : Editor
{
    private Terrain sourceTerrain;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var registry = (TreePrototypeRegistry)target;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Setup Utilities", EditorStyles.boldLabel);

        sourceTerrain = (Terrain)EditorGUILayout.ObjectField("Source Terrain", sourceTerrain, typeof(Terrain), true);

        if (sourceTerrain != null)
        {
            if (GUILayout.Button("Scaffold from Terrain"))
            {
                if (EditorUtility.DisplayDialog("Scaffold Prototypes",
                    "This creates prototype entries in the correct terrain order.\n" +
                    "Material, baseWidth, and baseHeight are extracted automatically.\n" +
                    "You must still assign lodMeshes manually for each entry.\n\n" +
                    "This will overwrite all existing prototypes. Continue?", "Yes", "Cancel"))
                {
                    TreePrototypeRegistryEditor.ScaffoldFromTerrain(registry, sourceTerrain);
                }
            }
        }

        EditorGUILayout.Space(5);

        if (GUILayout.Button("Validate All Prototypes"))
        {
            registry.ValidateAll();
        }

        if (registry.prototypes != null && registry.prototypes.Length > 0)
        {
            EditorGUILayout.Space(5);
            
            // Count incomplete prototypes
            int incomplete = 0;
            int maxLODs = 0;
            foreach (var p in registry.prototypes)
            {
                if (p == null || !p.IsValid) incomplete++;
                if (p?.lodMeshes != null) maxLODs = Mathf.Max(maxLODs, p.lodMeshes.Length);
            }
            
            // Build distance mapping string
            string lodMap = "";
            if (registry.treeDistanceByLOD != null)
            {
                for (int i = 0; i < registry.treeDistanceByLOD.Length; i++)
                {
                    lodMap += $"  Tree LOD {i}  →  up to distance {registry.treeDistanceByLOD[i]}\n";
                }
                lodMap += "  Beyond last distance  →  CULL\n";
            }
            
            var msgType = incomplete > 0 ? MessageType.Warning : MessageType.Info;
            EditorGUILayout.HelpBox(
                $"Prototypes: {registry.prototypes.Length} ({incomplete} incomplete)\n" +
                $"Max LOD meshes across prototypes: {maxLODs}\n" +
                $"Max collision LOD: {registry.maxCollisionLOD}\n" +
                $"\nDistance → Tree LOD mapping:\n{lodMap}" +
                $"(Prototypes with fewer LODs clamp to their highest available LOD)",
                msgType);
        }
    }
}
#endif
#endif
