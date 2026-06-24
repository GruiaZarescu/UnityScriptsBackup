#if UNITY_EDITOR
using UnityEngine;

[ExecuteInEditMode]
public class EditTerrainResolution : MonoBehaviour
{
    public Terrain terrain;

    [ContextMenu("Double Terrain Resolution")]
    private void DoubleRes()
    {
        ChangeRes(terrain, false);
    }

    [ContextMenu("Halve Terrain Resolution")]
    private void HalfRes()
    {
        ChangeRes(terrain, true);
    }

    public void ChangeRes(Terrain terrain, bool invert)
    {
        if (terrain == null)
        {
            Debug.LogWarning("Terrain not assigned!");
            return;
        }

        TerrainData oldData = terrain.terrainData;
        int oldRes = oldData.heightmapResolution;
        int newRes = invert ? oldRes / 2 + 1 : oldRes * 2 - 1;
        newRes = Mathf.Clamp(newRes, 33, 4097);

        // Copy and resample heightmap
        float[,] oldHeights = oldData.GetHeights(0, 0, oldRes, oldRes);
        float[,] newHeights = new float[newRes, newRes];

        for (int y = 0; y < newRes; y++)
        {
            for (int x = 0; x < newRes; x++)
            {
                float u = (float)x / (newRes - 1);
                float v = (float)y / (newRes - 1);

                float oldX = u * (oldRes - 1);
                float oldY = v * (oldRes - 1);

                int x0 = Mathf.FloorToInt(oldX);
                int y0 = Mathf.FloorToInt(oldY);
                int x1 = Mathf.Min(x0 + 1, oldRes - 1);
                int y1 = Mathf.Min(y0 + 1, oldRes - 1);

                float tx = oldX - x0;
                float ty = oldY - y0;

                float a = Mathf.Lerp(oldHeights[y0, x0], oldHeights[y0, x1], tx);
                float b = Mathf.Lerp(oldHeights[y1, x0], oldHeights[y1, x1], tx);
                newHeights[y, x] = Mathf.Lerp(a, b, ty);
            }
        }

        // Create new TerrainData
        TerrainData newData = new TerrainData
        {
            heightmapResolution = newRes,
            size = oldData.size,
            baseMapResolution = oldData.baseMapResolution,
            alphamapResolution = oldData.alphamapResolution
        };

        newData.SetDetailResolution(oldData.detailResolution, oldData.detailResolutionPerPatch);

        newData.SetHeights(0, 0, newHeights);

        // Copy alphamaps
        int alphaRes = oldData.alphamapResolution;
        int numLayers = oldData.alphamapLayers;
        float[,,] alphamaps = oldData.GetAlphamaps(0, 0, alphaRes, alphaRes);
        newData.SetAlphamaps(0, 0, alphamaps);

        // Copy terrain layers
        newData.terrainLayers = oldData.terrainLayers;

        // Copy tree data
        newData.treePrototypes = oldData.treePrototypes;
        newData.treeInstances = oldData.treeInstances;
        
        // Copy detail layers
        int detailLayers = oldData.detailPrototypes.Length;
        newData.detailPrototypes = oldData.detailPrototypes;
        for (int i = 0; i < detailLayers; i++)
        {
            int[,] layer = oldData.GetDetailLayer(0, 0, oldData.detailWidth, oldData.detailHeight, i);
            newData.SetDetailLayer(0, 0, i, layer);
        }

        // Copy waving grass settings
        newData.wavingGrassStrength = oldData.wavingGrassStrength;
        newData.wavingGrassAmount = oldData.wavingGrassAmount;
        newData.wavingGrassSpeed = oldData.wavingGrassSpeed;
        newData.wavingGrassTint = oldData.wavingGrassTint;

        // Apply new TerrainData
        terrain.terrainData = newData;

        TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
        if (collider != null)
        {
            collider.terrainData = newData;
        }

        Debug.Log($"Resolution {(invert ? "halved" : "doubled")} safely to {newRes}.");
    }
}
#endif