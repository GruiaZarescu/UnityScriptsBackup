#if UNITY_EDITOR
using UnityEngine;

public class TerrainTextureReplacer : MonoBehaviour
{
    [Tooltip("New terrain layers to assign to all terrains in the scene.")]
    [SerializeField] private TerrainLayer[] newTerrainLayers;

    [ContextMenu("Replace Terrain Layers")]
    private void ReplaceTerrainTextures()
    {
        if (newTerrainLayers == null || newTerrainLayers.Length == 0)
        {
            Debug.LogWarning("No TerrainLayers assigned.");
            return;
        }

        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);

        foreach (Terrain terrain in terrains)
        {
            terrain.terrainData.terrainLayers = newTerrainLayers;
            Debug.Log($"Replaced terrain layers for terrain at {terrain.transform.position}");
        }

        Debug.Log("All terrain layers replaced successfully.");
    }
}
#endif