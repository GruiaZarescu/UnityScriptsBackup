#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;

public class LayerMerger : EditorWindow
{
    private GameObject topParent;
    private GameObject bottomParent;
    private Terrain templateTerrain;

    private int targetLayerIndex = -1;
    private bool[] sourceLayerToggles;
    private Terrain lastTemplate;
    private int lastLayerCount;
    private Vector2 scrollPos;

    [MenuItem("Tools/Terrain/Layer Merger")]
    public static void ShowWindow()
    {
        GetWindow<LayerMerger>("Layer Merger");
    }

    private void OnGUI()
    {
        GUILayout.Label("Terrain Layer Merger", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Merge multiple terrain layers into a single target layer.\n" +
            "Alphamap weights from source layers are added to the target, then source layers are removed.\n\n" +
            "WARNING: This operation is NOT undoable. Save your scene/project first!",
            MessageType.Info);

        templateTerrain = (Terrain)EditorGUILayout.ObjectField(
            "Template Terrain", templateTerrain, typeof(Terrain), true);

        topParent = (GameObject)EditorGUILayout.ObjectField(
            "Top Parent", topParent, typeof(GameObject), true);

        bottomParent = (GameObject)EditorGUILayout.ObjectField(
            "Bottom Parent", bottomParent, typeof(GameObject), true);

        GUILayout.Space(10);

        if (templateTerrain == null || templateTerrain.terrainData == null)
        {
            EditorGUILayout.HelpBox("Assign a Template Terrain to see its layers.", MessageType.Warning);
            return;
        }

        TerrainLayer[] layers = templateTerrain.terrainData.terrainLayers;

        if (lastTemplate != templateTerrain || lastLayerCount != layers.Length
            || sourceLayerToggles == null || sourceLayerToggles.Length != layers.Length)
        {
            lastTemplate = templateTerrain;
            lastLayerCount = layers.Length;
            sourceLayerToggles = new bool[layers.Length];
            targetLayerIndex = -1;
        }

        if (layers.Length == 0)
        {
            EditorGUILayout.HelpBox("Template terrain has no layers.", MessageType.Warning);
            return;
        }

        string[] layerNames = new string[layers.Length];
        for (int i = 0; i < layers.Length; i++)
            layerNames[i] = layers[i] != null ? $"{i}: {layers[i].name}" : $"{i}: (null)";

        GUILayout.Label("Target Layer (keep this one):", EditorStyles.boldLabel);
        targetLayerIndex = EditorGUILayout.Popup("Target", targetLayerIndex, layerNames);

        GUILayout.Space(5);
        GUILayout.Label("Source Layers (merge into target, then remove):", EditorStyles.boldLabel);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.MaxHeight(200));
        for (int i = 0; i < layers.Length; i++)
        {
            if (i == targetLayerIndex)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ToggleLeft(layerNames[i] + "  [TARGET]", false);
                EditorGUI.EndDisabledGroup();
                sourceLayerToggles[i] = false;
            }
            else
            {
                sourceLayerToggles[i] = EditorGUILayout.ToggleLeft(layerNames[i], sourceLayerToggles[i]);
            }
        }
        EditorGUILayout.EndScrollView();

        GUILayout.Space(10);

        int sourceCount = 0;
        for (int i = 0; i < sourceLayerToggles.Length; i++)
            if (sourceLayerToggles[i]) sourceCount++;

        EditorGUILayout.LabelField($"Layers after merge: {layers.Length} → {layers.Length - sourceCount}");

        GUILayout.Space(5);

        EditorGUI.BeginDisabledGroup(targetLayerIndex < 0 || sourceCount == 0);
        if (GUILayout.Button("Merge Layers", GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog(
                "Merge Terrain Layers",
                $"This will permanently modify {(topParent != null ? topParent.GetComponentsInChildren<Terrain>(true).Length : 0) + (bottomParent != null ? bottomParent.GetComponentsInChildren<Terrain>(true).Length : 0)} terrains.\n\n" +
                "This operation CANNOT be undone.\nMake sure you have saved your project.\n\nContinue?",
                "Merge", "Cancel"))
            {
                MergeLayers(layers);
            }
        }
        EditorGUI.EndDisabledGroup();
    }

    private void MergeLayers(TerrainLayer[] layers)
    {
        EditorCoroutineRunner.StartEditorCoroutine(MergeLayersCoroutine(layers));
    }

    private IEnumerator MergeLayersCoroutine(TerrainLayer[] layers)
    {
        List<int> sourceIndices = new List<int>();
        for (int i = 0; i < sourceLayerToggles.Length; i++)
        {
            if (sourceLayerToggles[i] && i != targetLayerIndex)
                sourceIndices.Add(i);
        }

        if (sourceIndices.Count == 0)
        {
            Debug.LogError("[LayerMerger] No source layers selected.");
            yield break;
        }

        List<Terrain> allTerrains = new List<Terrain>();
        if (topParent != null)
            allTerrains.AddRange(topParent.GetComponentsInChildren<Terrain>(true));
        if (bottomParent != null)
            allTerrains.AddRange(bottomParent.GetComponentsInChildren<Terrain>(true));

        if (allTerrains.Count == 0)
        {
            Debug.LogError("[LayerMerger] No terrains found under the specified parents.");
            yield break;
        }

        HashSet<int> sourceSet = new HashSet<int>(sourceIndices);

        // Build new layer array and old-to-new index map
        List<TerrainLayer> newLayers = new List<TerrainLayer>();
        int[] indexMap = new int[layers.Length];

        for (int i = 0; i < layers.Length; i++)
        {
            if (sourceSet.Contains(i))
            {
                indexMap[i] = -1;
            }
            else
            {
                indexMap[i] = newLayers.Count;
                newLayers.Add(layers[i]);
            }
        }

        int newTargetIndex = indexMap[targetLayerIndex];
        TerrainLayer[] newLayerArray = newLayers.ToArray();

        int processedCount = 0;
        int skippedCount = 0;
        int totalTerrains = allTerrains.Count;

        // Track already-processed TerrainData to avoid corrupting shared assets
        HashSet<int> processedDataIds = new HashSet<int>();

        const int SAVE_INTERVAL = 5;

        try
        {
            for (int t = 0; t < totalTerrains; t++)
            {
                Terrain terrain = allTerrains[t];
                TerrainData data = terrain.terrainData;

                if (data == null)
                {
                    skippedCount++;
                    continue;
                }

                // Skip if this TerrainData was already processed (shared asset)
                int dataId = data.GetInstanceID();
                if (processedDataIds.Contains(dataId))
                {
                    Debug.Log($"[LayerMerger] '{terrain.name}' shares TerrainData with an already-processed terrain. Skipping.");
                    continue;
                }

                bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "Merging Terrain Layers",
                    $"Processing '{terrain.name}' ({t + 1}/{totalTerrains})",
                    (float)t / totalTerrains);

                if (cancelled)
                {
                    Debug.LogWarning($"[LayerMerger] Cancelled by user after {processedCount} terrains.");
                    break;
                }

                int w = data.alphamapWidth;
                int h = data.alphamapHeight;
                float[,,] alphamaps = data.GetAlphamaps(0, 0, w, h);
                int oldLayerCount = alphamaps.GetLength(2);

                if (oldLayerCount != layers.Length)
                {
                    Debug.LogWarning($"[LayerMerger] '{terrain.name}' has {oldLayerCount} layers but template has {layers.Length}. Skipping.");
                    skippedCount++;
                    continue;
                }

                int newLayerCount = newLayers.Count;
                float[,,] newAlphamaps = new float[w, h, newLayerCount];

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        for (int l = 0; l < oldLayerCount; l++)
                        {
                            float weight = alphamaps[x, y, l];
                            if (weight == 0f) continue;

                            if (sourceSet.Contains(l))
                                newAlphamaps[x, y, newTargetIndex] += weight;
                            else
                                newAlphamaps[x, y, indexMap[l]] += weight;
                        }
                    }
                }

                // Set layers first, then alphamaps
                data.terrainLayers = newLayerArray;
                data.SetAlphamaps(0, 0, newAlphamaps);

                EditorUtility.SetDirty(data);
                processedDataIds.Add(dataId);
                processedCount++;

                // Release references and force GC to reclaim ~320 MB per terrain
                alphamaps = null;
                newAlphamaps = null;
                System.GC.Collect();

                // Save periodically to avoid a massive serialization spike at the end
                if (processedCount % SAVE_INTERVAL == 0)
                {
                    EditorUtility.DisplayCancelableProgressBar(
                        "Merging Terrain Layers",
                        $"Saving assets ({processedCount}/{totalTerrains} done)...",
                        (float)t / totalTerrains);
                    AssetDatabase.SaveAssets();
                    System.GC.Collect();
                }

                // Yield to let the editor flush native resources between terrains
                yield return null;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();

        Debug.Log($"[LayerMerger] Done. Processed {processedCount} terrains, skipped {skippedCount}. Layers: {layers.Length} → {newLayers.Count}.");

        // Reset so the UI refreshes with the new layer list
        lastTemplate = null;
        sourceLayerToggles = null;
    }
}
#endif