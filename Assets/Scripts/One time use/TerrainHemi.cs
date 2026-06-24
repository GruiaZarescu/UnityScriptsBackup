#if UNITY_EDITOR
using UnityEngine;


public class TerrainHemi : MonoBehaviour
{
    public Terrain terrain;
    private TerrainData td;
    private float pixelDistance;
    private float radius;
    private Vector2[,] PixelCornerCoordinates;
    private float[,] heights;
    private Vector3 centerCoords;

    [ContextMenu("Round")]
    private void Round()
    {
        Run(1);
        Debug.Log("Rounded!");
     }
    [ContextMenu("Flatten")]
    private void Flatten()
    {
        Run(-1);
        Debug.Log("Flat!");
    }
    private void Run(int multiplier)
    {
        if (terrain == null) return;

        td = terrain.terrainData;
        Vector3 terrainPosition = terrain.GetPosition();

        PixelCornerCoordinates = new Vector2[td.heightmapResolution, td.heightmapResolution];
        heights = new float[td.heightmapResolution, td.heightmapResolution];
        heights = td.GetHeights(0,0,td.heightmapResolution,td.heightmapResolution);

        pixelDistance = td.size.x / (td.heightmapResolution - 1);
        PixelCornerCoordinates[0, 0].x = terrainPosition.x;
        PixelCornerCoordinates[0, 0].y = terrainPosition.z;

        Vector2 centerIndex = new Vector2((td.heightmapResolution - 1) / 2f, (td.heightmapResolution - 1) / 2f);

        for (int i = 0; i < td.heightmapResolution; i++)
        {
            for (int j = 0; j < td.heightmapResolution; j++)
            {
                PixelCornerCoordinates[i, j].x = PixelCornerCoordinates[0, 0].x + pixelDistance * i;
                PixelCornerCoordinates[i, j].y = PixelCornerCoordinates[0, 0].y + pixelDistance * j;
                //Debug.Log("Pixel[" + i + "," + j + "]=" + PixelCornerCoordinates[i, j]);
            }
        }
        centerCoords.x = terrainPosition.x + centerIndex.x * pixelDistance;
        centerCoords.y = terrainPosition.y;
        centerCoords.z = terrainPosition.z + centerIndex.y * pixelDistance;

        Debug.Log("Sphere center coordinates = " + centerCoords);

        //Sphere is made to leave one pixel lowered on each corner
        radius = (td.size.x - 1) / 2f;
        Debug.Log("Sphere radius = " + radius);

        for (int i = 0; i < td.heightmapResolution; i++)
        {
            for (int j = 0; j < td.heightmapResolution; j++)
            {
                float dx = PixelCornerCoordinates[i, j].x - centerCoords.x;
                float dy = PixelCornerCoordinates[i, j].y - centerCoords.z;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared <= radius * radius)
                {
                    float dz = Mathf.Sqrt(radius * radius - distanceSquared);
                    float normalizedHeight = (dz / radius) * (radius / td.size.y);
                    heights[i, j] += normalizedHeight * multiplier;
                }
            }
        }
        td.SetHeights(0, 0, heights);
    }
}
#endif