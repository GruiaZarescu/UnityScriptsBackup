using UnityEngine;
using UnityEngine.Rendering;

public class TerrainFace
{
    Mesh mesh;
    int resolution;
    Vector3 localUp;
    float radius;
    float terrainHeight;
    int terrainResolution;
    FaceHeightmapGridData faceHeightmapGrid;

    Vector3 axisA;
    Vector3 axisB;

    public TerrainFace(Mesh mesh, int resolution, Vector3 localUp, float radius, float terrainHeight, int terrainResolution, FaceHeightmapGridData faceHeightmapGrid)
    {
        this.mesh = mesh;
        this.resolution = resolution;
        this.localUp = localUp;
        this.radius = radius;
        this.terrainHeight = terrainHeight;
        this.terrainResolution = terrainResolution;
        this.faceHeightmapGrid = faceHeightmapGrid;

        axisA = new Vector3(localUp.y, localUp.z, localUp.x);
        axisB = Vector3.Cross(localUp, axisA);
    }

    public void ConstructMesh()
    {
        if (faceHeightmapGrid == null || faceHeightmapGrid.heightmaps == null || faceHeightmapGrid.heightmaps.Length == 0 || faceHeightmapGrid.gridSize <= 0)
        {
            mesh.Clear();
            return;
        }

        Vector3[] vertices = new Vector3[resolution * resolution];
        int[] triangles = new int[(resolution - 1) * (resolution - 1) * 6];
        int triIndex = 0;

        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int i = x + y * resolution;
                Vector2 percent = new Vector2(x, y) / (resolution - 1);
                Vector3 pointOnUnitCube = localUp + (percent.x - 0.5f) * 2 * axisA + (percent.y - 0.5f) * 2 * axisB; 
                Vector3 pointOnUnitSphere = pointOnUnitCube.normalized;
                float sampledHeight = SampleHeight(x, y);
                vertices[i] = pointOnUnitSphere * (radius + sampledHeight);

                if (x != resolution - 1 && y != resolution - 1)
                {
                    triangles[triIndex] = i;
                    triangles[triIndex + 1] = i + resolution + 1;
                    triangles[triIndex + 2] = i + resolution;

                    triangles[triIndex + 3] = i;
                    triangles[triIndex + 4] = i + 1;
                    triangles[triIndex + 5] = i + resolution + 1;
                    triIndex += 6;
                }
            }
        }

        mesh.Clear();
        mesh.indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
    }

    private float SampleHeight(int x, int y)
    {
        int samplesPerTerrainEdge = terrainResolution - 1;
        int tileX = Mathf.Min(x / samplesPerTerrainEdge, faceHeightmapGrid.gridSize - 1);
        int tileY = Mathf.Min(y / samplesPerTerrainEdge, faceHeightmapGrid.gridSize - 1);

        int localX = x - tileX * samplesPerTerrainEdge;
        int localY = y - tileY * samplesPerTerrainEdge;
        int tileIndex = tileY * faceHeightmapGrid.gridSize + tileX;
        PlaneTerrainData tileData = faceHeightmapGrid.heightmaps[tileIndex];

        if (tileData.heightmap == null || tileData.heightmap.Length == 0)
        {
            return 0f;
        }

        int sampleIndex = localY * terrainResolution + localX;
        if (sampleIndex < 0 || sampleIndex >= tileData.heightmap.Length)
        {
            return 0f;
        }

        return tileData.heightmap[sampleIndex] / (float)ushort.MaxValue * terrainHeight;
    }
}
