#if UNITY_EDITOR
using UnityEngine;
using System.Collections;

public class TerrainCircleRaiserForEach : MonoBehaviour
{
    [SerializeField] private float innerRadius=0;
    [SerializeField] private float radius;
    [SerializeField] private Vector3 centerCoords;
    [SerializeField] private float height;

    [ContextMenu("Raise")]
    private void Raise()
    {
        StartCoroutine(RunCutRoutine());
        Debug.Log("Started cutting...");
    }

    private IEnumerator RunCutRoutine()
    {
        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);

        foreach (Terrain terrain in terrains)
        {
            if (terrain == null) continue;

            TerrainData td = terrain.terrainData;
            Vector3 terrainPosition = terrain.GetPosition();

            int heightRes = td.heightmapResolution;
            float pixelDistance = td.size.x / (heightRes - 1);
            float[,] heights = td.GetHeights(0, 0, heightRes, heightRes);


            for (int i = 0; i < heightRes; i++)
            {
                for (int j = 0; j < heightRes; j++)
                {
                    float worldX = terrainPosition.x + j * pixelDistance;
                    float worldZ = terrainPosition.z + i * pixelDistance;

                    float dx = worldX - centerCoords.x;
                    float dz = worldZ - centerCoords.z;
                    float distanceSquared = dx * dx + dz * dz;

                    // Keep inside radius, cut outside
                    if(distanceSquared<= radius * radius && distanceSquared >=innerRadius*innerRadius)
                        heights[i, j] += height/td.size.y;
                }
            }

            td.SetHeights(0, 0, heights);
            Debug.Log($"Processed terrain at {terrainPosition}");

            yield return null;
        }

        Debug.Log("Finished cutting holes in all terrains.");
    }

}
#endif