/*#if UNITY_EDITOR
using System.Collections;
using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;

public class MeshBaker : MonoBehaviour
{
    
    /// <summary>
    /// Generates tiled meshes asynchronously, calling onMeshGenerated for each mesh.
    /// </summary>

    public static IEnumerator GenerateTiledMeshesAsync(
        Terrain terrain,
        int factor,
        bool isUpperHemisphere,
        float sphereRadius,
        Vector3 SphereCenter,
        Action<Mesh[]> onMeshesGenerated) // Still callback-based
    {
        if (terrain == null)
        {
            Debug.LogWarning("MeshBaker: Terrain is null!");
            yield break;
        }

        TerrainData td = terrain.terrainData;
        if (!Mathf.IsPowerOfTwo(factor))
        {
            Debug.LogWarning($"MeshBaker: Factor {factor} is not a power of two!");
            yield break;
        }

        int heightRes = td.heightmapResolution;

        int heightResAux = heightRes - 1;
        int numberOfLodLevels = 0;
        while (heightResAux / factor >= 2)
        {
            numberOfLodLevels++;
            heightResAux /= 2;
        }
        int step = (heightRes - 1) / factor;

        float[,] terrainHeights = td.GetHeights(0, 0, heightRes, heightRes);
        

        Dictionary<int, float[,]> lodHeightCache = new Dictionary<int, float[,]>();
        for (int currentLOD = 0; currentLOD <= numberOfLodLevels; currentLOD++)
        {
            float[,] heights = GetHeightsLod(terrainHeights, currentLOD);
            lodHeightCache[currentLOD] = heights;
        }

        Vector3 terrainOrigin = terrain.GetPosition();
        Dictionary<int, Vector3[,]> lodWorldPosCache = new Dictionary<int, Vector3[,]>();
        bool[,,] excludedVerticesGlobal = new bool[td.heightmapResolution, td.heightmapResolution, lodHeightCache.Count];
        foreach (var kvp in lodHeightCache)
        {
            int lod = kvp.Key;
            float[,] heights = kvp.Value;
            //bool[,] verticesToExclude = new bool[heights.GetLength(0), heights.GetLength(0)];
            Vector3[,] worldPos = new Vector3[heights.GetLength(0), heights.GetLength(0)];
            float pixelDistance = td.size.x / (heights.GetLength(0) - 1f);


            for (int i = 0; i < heights.GetLength(0); i++)
            {
                for (int j = 0; j < heights.GetLength(0); j++)
                {
                    worldPos[i, j] = new Vector3(
                        terrainOrigin.x + pixelDistance * j,
                        heights[i, j] * td.size.y,
                        terrainOrigin.z + pixelDistance * i
                    );

                }
            }

            float[,] pixelHeightsAux = new float[heights.GetLength(0), heights.GetLength(0)];
            for (int i = 0; i < heights.GetLength(0); i++)
            {
                for (int j = 0; j < heights.GetLength(0); j++)
                {
                    pixelHeightsAux[i, j] = worldPos[i, j].y;
                    worldPos[i, j].y = 0;
                    float dx = worldPos[i, j].x - SphereCenter.x;
                    float dy = worldPos[i, j].z - SphereCenter.z;
                    float distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared <= sphereRadius * sphereRadius)
                    {
                        float dz = Mathf.Sqrt(sphereRadius * sphereRadius - distanceSquared);
                        if (isUpperHemisphere)
                        {
                            worldPos[i, j].y = dz;
                            Vector3 pos = worldPos[i, j];
                            Vector3 dir = (pos - SphereCenter).normalized;
                            worldPos[i, j] += dir * pixelHeightsAux[i, j];
                        }
                        else
                        {
                            worldPos[i, j].y = -dz;
                            Vector3 pos = worldPos[i, j];
                            Vector3 dir = (pos - SphereCenter).normalized;
                            worldPos[i, j] += dir * pixelHeightsAux[i, j];
                        }
                    }
                    excludedVerticesGlobal[i, j, lod] = false;
                }
            }
            for (int i = 0; i < heights.GetLength(0); i++)
            {
                for (int j = 0; j < heights.GetLength(0); j++)
                {
                    if (worldPos[i, j].y != 0) excludedVerticesGlobal[i, j,lod] = false;
                }
            }

            lodWorldPosCache[lod] = worldPos;
        }


        // === CHANGED: Loop tiles first, then generate all LODs per tile ===
        for (int tileY = 0; tileY < factor; tileY++)
        {
            for (int tileX = 0; tileX < factor; tileX++)
            {
                List<Mesh> tileLODs = new List<Mesh>(); // CHANGED: Collect meshes for this tile only

                for (int currentLOD = 0; currentLOD <= numberOfLodLevels; currentLOD++)
                {
                    bool currentMeshHasTriangles = false;
                    float[,] heights = lodHeightCache[currentLOD];

                    step = (heights.GetLength(0) - 1) / factor;
                    float pixelDistance = td.size.x / (heights.GetLength(0) - 1f);


                    // Vertices
                    int startX = tileX * step;
                    int startY = tileY * step;
                    Vector3[] vertices = new Vector3[(step + 1) * (step + 1)];
                    List<Vector3> verticesList = new List<Vector3>();

                    Vector3[,] pixelWorldPosition = lodWorldPosCache[currentLOD];

                    //Verts
                    for (int y = 0; y <= step; y++)
                    {
                        for (int x = 0; x <= step; x++)
                        {
                            int globalX = startX + x;
                            int globalY = startY + y;
                            if (!excludedVerticesGlobal[globalY, globalX, currentLOD])
                            {
                                //vertices[y * (step + 1) + x] = pixelWorldPosition[globalY, globalX];
                                verticesList.Add(pixelWorldPosition[globalY, globalX]);
                            }

                        }
                    }
                    vertices = verticesList.ToArray();
                    // Triangles
                    List<int> trianglesList = new List<int>();
                    for (int y = 0; y < step; y++)
                    {
                        for (int x = 0; x < step; x++)
                        {
                            int globalX = startX + x;
                            int globalY = startY + y;

                            if (!excludedVerticesGlobal[globalY, globalX, currentLOD])
                            {
                                int bottomLeft = y * (step + 1) + x;
                                int bottomRight = bottomLeft + 1;
                                int topLeft = bottomLeft + (step + 1);
                                int topRight = topLeft + 1;

                                trianglesList.Add(bottomLeft);
                                trianglesList.Add(topLeft);
                                trianglesList.Add(topRight);

                                trianglesList.Add(bottomLeft);
                                trianglesList.Add(topRight);
                                trianglesList.Add(bottomRight);
                                currentMeshHasTriangles = true;
                            }
                        }
                    }

                    //Flip face for lower part
                    if (!isUpperHemisphere)
                    {
                        for (int i = 0; i < trianglesList.Count; i += 3)
                        {
                            int tmp = trianglesList[i];
                            trianglesList[i] = trianglesList[i + 1];
                            trianglesList[i + 1] = tmp;
                        }
                    }

                    int[] triangles = trianglesList.ToArray();

                    // UVs
                    Vector2[] uvs = new Vector2[(step + 1) * (step + 1)];
                    for (int y = 0; y <= step; y++)
                    {
                        for (int x = 0; x <= step; x++)
                        {
                            int globalX = startX + x;
                            int globalY = startY + y;
                            if (!excludedVerticesGlobal[globalY, globalX, currentLOD])
                            {
                                uvs[y * (step + 1) + x] = new Vector2((float)x / step, (float)y / step);
                            }
                        }
                    }

                    Mesh mesh = new Mesh
                    {
                        vertices = vertices,
                        triangles = triangles,
                        uv = uvs,
                        name = $"Tile_{tileY}_{tileX}_LOD{currentLOD}"
                    };
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();

                    if (currentMeshHasTriangles) tileLODs.Add(mesh);
                    else tileLODs.Add(null);
                }

                // === CHANGED: Callback per tile ===
                onMeshesGenerated?.Invoke(tileLODs.ToArray());

                yield return null; // keep editor responsive
            }
        }

        Debug.Log("MeshBaker: Finished generating all tile LOD arrays.");
    }


    private static float[,] GetHeightsLod(float[,] heights, int desiredLOD)
    {
        // halve resolution until resolution = (32*factor)+1

        int heightmapResolution = heights.GetLength(0);
        int divisionFactor = (int)Mathf.Pow(2f, (float)desiredLOD);
        int newRes = divisionFactor != 1 ? (heightmapResolution / divisionFactor) + 1 : heightmapResolution;

        float[,] newHeights = new float[newRes, newRes];
        for (int y = 0; y < newRes; y++)
        {
            for (int x = 0; x < newRes; x++)
            {
                float u = (float)x / (newRes - 1);
                float v = (float)y / (newRes - 1);

                float oldX = u * (heightmapResolution - 1);
                float oldY = v * (heightmapResolution - 1);

                int x0 = Mathf.FloorToInt(oldX);
                int y0 = Mathf.FloorToInt(oldY);
                int x1 = Mathf.Min(x0 + 1, heightmapResolution - 1);
                int y1 = Mathf.Min(y0 + 1, heightmapResolution - 1);

                float tx = oldX - x0;
                float ty = oldY - y0;

                float a = Mathf.Lerp(heights[y0, x0], heights[y0, x1], tx);
                float b = Mathf.Lerp(heights[y1, x0], heights[y1, x1], tx);
                newHeights[y, x] = Mathf.Lerp(a, b, ty);
            }
        }

        return newHeights;
    }

}
#endif
*/

