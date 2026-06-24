#if UNITY_EDITOR
using UnityEngine;

public class TreePrefabReplacer : MonoBehaviour
{
    [Tooltip("Assign one prefab per tree type index")]
    [SerializeField] private GameObject[] newTreePrefabs;

    [ContextMenu("Replace Tree Prefabs")]
    private void ReplaceTreePrefabs()
    {
        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);

        foreach (Terrain terrain in terrains)
        {
            TerrainData td = terrain.terrainData;

            if (newTreePrefabs.Length == 0)
            {
                Debug.LogWarning($"No prefabs assigned for terrain at {terrain.transform.position}");
                continue;
            }

            TreePrototype[] newPrototypes = new TreePrototype[newTreePrefabs.Length];

            for (int i = 0; i < newTreePrefabs.Length; i++)
            {
                if (newTreePrefabs[i] == null)
                {
                    Debug.LogWarning($"Prefab at index {i} is null");
                    continue;
                }

                TreePrototype prototype = new TreePrototype();
                prototype.prefab = newTreePrefabs[i];
                newPrototypes[i] = prototype;
            }

            td.treePrototypes = newPrototypes;
            Debug.Log($"Replaced tree prototypes for terrain at {terrain.transform.position}");
        }

        Debug.Log("Finished replacing tree prefabs.");
    }
}
#endif