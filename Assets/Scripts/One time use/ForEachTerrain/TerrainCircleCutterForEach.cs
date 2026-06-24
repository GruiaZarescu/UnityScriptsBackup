#if UNITY_EDITOR
using UnityEngine;
using System.Collections;

public class TerrainCircleCutterForEach : MonoBehaviour
{
    [SerializeField] private float radius;
    [SerializeField] private Vector3 centerCoords;

    [ContextMenu("Cut")]
    private void Cut()
    {
        StartCoroutine(RunCutRoutine());
        Debug.Log("Started cutting...");
    }
    [ContextMenu("Remove all holes")]
    private void RemoveHoles()
    {
        StartCoroutine(RemoveAllHoles());
        Debug.Log("Removing holes, please stay still..");
    }

    private IEnumerator RunCutRoutine()
    {
        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);

        foreach (Terrain terrain in terrains)
        {
            if (terrain == null) continue;

            TerrainData td = terrain.terrainData;
            Vector3 terrainPosition = terrain.GetPosition();

            int holesRes = td.holesResolution;
            float pixelDistance = td.size.x / (holesRes - 1);
            bool[,] holes = new bool[holesRes, holesRes];

            for (int i = 0; i < holesRes; i++)
            {
                for (int j = 0; j < holesRes; j++)
                {
                    float worldX = terrainPosition.x + j * pixelDistance;
                    float worldZ = terrainPosition.z + i * pixelDistance;

                    float dx = worldX - centerCoords.x;
                    float dz = worldZ - centerCoords.z;
                    float distanceSquared = dx * dx + dz * dz;

                    // Keep inside radius, cut outside
                    holes[i, j] = distanceSquared <= radius * radius;
                }
            }

            td.SetHoles(0, 0, holes);
            Debug.Log($"Processed terrain at {terrainPosition}");

            yield return null;
        }

        Debug.Log("Finished cutting holes in all terrains.");
    }

    private IEnumerator RemoveAllHoles()
    {
        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);

        foreach (Terrain terrain in terrains)
        {
            if (terrain == null) continue;

            TerrainData td = terrain.terrainData;

            int holesRes = td.holesResolution;
            bool[,] holes = new bool[holesRes, holesRes];

            for (int i = 0; i < holesRes; i++)
            {
                for (int j = 0; j < holesRes; j++)
                {
                    holes[i, j] = true;
                }
            }

            td.SetHoles(0, 0, holes);
            Vector3 terrainPosition = terrain.GetPosition();
            Debug.Log($"Processed terrain at {terrainPosition}");
            yield return null;
        }

        Debug.Log("Finished cutting holes in all terrains.");
    }
}
#endif