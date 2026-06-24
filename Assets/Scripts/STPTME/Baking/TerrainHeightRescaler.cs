#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Utility to find max terrain height and rescale terrain heightmaps to a new maxHeight
/// while preserving real-world heights.
/// </summary>
public class TerrainHeightRescaler : MonoBehaviour
{
    [Header("Terrain Containers")]
    [Tooltip("Parent object containing all top hemisphere terrains")]
    public GameObject topHalfContainer;
    
    [Tooltip("Parent object containing all bottom hemisphere terrains")]
    public GameObject bottomHalfContainer;

    [Header("Rescaling Settings")]
    [Tooltip("New maximum terrain height to set (must be >= max real height found)")]
    public float newMaxHeight = 2000f;

    [Header("Results (Read Only)")]
    [SerializeField] private float foundMaxRealHeight;
    [SerializeField] private string maxHeightTerrainName;
    [SerializeField] private float currentTerrainHeight;
    [SerializeField] private int totalTerrainsFound;

    /// <summary>
    /// Finds the maximum real-world height point across all terrains.
    /// Real height = heightmapValue * terrainData.size.y
    /// </summary>
    [ContextMenu("1. Find Maximum Real Height")]
    public void FindMaxRealHeight()
    {
        List<Terrain> allTerrains = CollectAllTerrains();
        
        if (allTerrains.Count == 0)
        {
            Debug.LogError("[TerrainHeightRescaler] No terrains found in containers!");
            return;
        }

        totalTerrainsFound = allTerrains.Count;
        foundMaxRealHeight = 0f;
        maxHeightTerrainName = "";
        currentTerrainHeight = 0f;

        foreach (var terrain in allTerrains)
        {
            TerrainData td = terrain.terrainData;
            float terrainHeight = td.size.y;
            currentTerrainHeight = terrainHeight; // They should all be the same
            
            int res = td.heightmapResolution;
            float[,] heights = td.GetHeights(0, 0, res, res);

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float realHeight = heights[y, x] * terrainHeight;
                    if (realHeight > foundMaxRealHeight)
                    {
                        foundMaxRealHeight = realHeight;
                        maxHeightTerrainName = terrain.name;
                    }
                }
            }
        }

        Debug.Log($"[TerrainHeightRescaler] Scanned {totalTerrainsFound} terrains.\n" +
            $"  Current terrain height setting: {currentTerrainHeight}\n" +
            $"  Maximum real-world height found: {foundMaxRealHeight:F2}m\n" +
            $"  Found in terrain: '{maxHeightTerrainName}'\n" +
            $"  Recommended newMaxHeight: {Mathf.Ceil(foundMaxRealHeight / 100f) * 100f}m (rounded up to nearest 100)");
    }

    /// <summary>
    /// Rescales all terrain heightmaps to preserve real-world heights when
    /// changing terrain size.y to newMaxHeight.
    /// 
    /// Formula: newHeightmapValue = (oldHeightmapValue * oldTerrainHeight) / newTerrainHeight
    /// </summary>
    [ContextMenu("2. Rescale Heights To New Max")]
    public void RescaleHeightsToNewMax()
    {
        List<Terrain> allTerrains = CollectAllTerrains();
        
        if (allTerrains.Count == 0)
        {
            Debug.LogError("[TerrainHeightRescaler] No terrains found in containers!");
            return;
        }

        // First find max height to validate
        float maxRealHeight = 0f;
        float oldTerrainHeight = 0f;
        
        foreach (var terrain in allTerrains)
        {
            TerrainData td = terrain.terrainData;
            oldTerrainHeight = td.size.y;
            
            int res = td.heightmapResolution;
            float[,] heights = td.GetHeights(0, 0, res, res);

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float realHeight = heights[y, x] * oldTerrainHeight;
                    if (realHeight > maxRealHeight)
                        maxRealHeight = realHeight;
                }
            }
        }

        // Validate
        if (newMaxHeight < maxRealHeight)
        {
            Debug.LogError($"[TerrainHeightRescaler] newMaxHeight ({newMaxHeight}) must be >= max real height ({maxRealHeight:F2})!\n" +
                $"The terrain would be clipped. Set newMaxHeight to at least {Mathf.Ceil(maxRealHeight)}.");
            return;
        }

        if (Mathf.Approximately(oldTerrainHeight, newMaxHeight))
        {
            Debug.LogWarning("[TerrainHeightRescaler] Old and new heights are the same. Nothing to do.");
            return;
        }

        // Confirm with user
        if (!EditorUtility.DisplayDialog("Confirm Height Rescale",
            $"This will rescale all {allTerrains.Count} terrain heightmaps.\n\n" +
            $"Old terrain height: {oldTerrainHeight}\n" +
            $"New terrain height: {newMaxHeight}\n" +
            $"Max real height: {maxRealHeight:F2}m (will be preserved)\n\n" +
            $"This operation modifies TerrainData assets and cannot be easily undone.\n" +
            $"Make sure you have a backup!",
            "Proceed", "Cancel"))
        {
            Debug.Log("[TerrainHeightRescaler] Operation cancelled by user.");
            return;
        }

        // Perform rescaling
        float scaleFactor = oldTerrainHeight / newMaxHeight;
        int terrainsProcessed = 0;

        foreach (var terrain in allTerrains)
        {
            TerrainData td = terrain.terrainData;
            int res = td.heightmapResolution;
            float[,] heights = td.GetHeights(0, 0, res, res);

            // Rescale all height values
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    heights[y, x] *= scaleFactor;
                }
            }

            // Apply rescaled heights
            td.SetHeights(0, 0, heights);

            // Update terrain size
            Vector3 size = td.size;
            size.y = newMaxHeight;
            td.size = size;

            // Mark dirty for saving
            EditorUtility.SetDirty(td);
            
            terrainsProcessed++;
            EditorUtility.DisplayProgressBar("Rescaling Terrains", 
                $"Processing {terrain.name}...", 
                (float)terrainsProcessed / allTerrains.Count);
        }

        EditorUtility.ClearProgressBar();
        AssetDatabase.SaveAssets();

        Debug.Log($"[TerrainHeightRescaler] Successfully rescaled {terrainsProcessed} terrains.\n" +
            $"  Old terrain height: {oldTerrainHeight}\n" +
            $"  New terrain height: {newMaxHeight}\n" +
            $"  Scale factor applied: {scaleFactor:F6}\n" +
            $"  Real-world heights preserved.\n\n" +
            $"IMPORTANT: Update your STPTMESettings.maxHeight to {newMaxHeight}!");
    }

    /// <summary>
    /// Collects all terrains from both containers.
    /// </summary>
    private List<Terrain> CollectAllTerrains()
    {
        List<Terrain> terrains = new List<Terrain>();

        if (topHalfContainer != null)
        {
            terrains.AddRange(topHalfContainer.GetComponentsInChildren<Terrain>());
        }

        if (bottomHalfContainer != null)
        {
            terrains.AddRange(bottomHalfContainer.GetComponentsInChildren<Terrain>());
        }

        return terrains;
    }

    /// <summary>
    /// Validates the setup.
    /// </summary>
    [ContextMenu("Validate Setup")]
    public void ValidateSetup()
    {
        List<Terrain> terrains = CollectAllTerrains();
        
        if (terrains.Count == 0)
        {
            Debug.LogError("[TerrainHeightRescaler] No terrains found! Assign topHalfContainer and/or bottomHalfContainer.");
            return;
        }

        // Check all terrains have same height
        float firstHeight = terrains[0].terrainData.size.y;
        bool allSame = true;
        
        foreach (var t in terrains)
        {
            if (!Mathf.Approximately(t.terrainData.size.y, firstHeight))
            {
                Debug.LogWarning($"[TerrainHeightRescaler] Terrain '{t.name}' has different height: {t.terrainData.size.y} vs {firstHeight}");
                allSame = false;
            }
        }

        if (allSame)
        {
            Debug.Log($"[TerrainHeightRescaler] Setup valid!\n" +
                $"  Found {terrains.Count} terrains\n" +
                $"  All terrains have height: {firstHeight}");
        }
    }
}
#endif
