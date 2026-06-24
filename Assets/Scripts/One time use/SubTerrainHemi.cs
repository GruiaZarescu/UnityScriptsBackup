#if UNITY_EDITOR
using UnityEngine;


public class SubTerrainHemi : MonoBehaviour
{
    public Terrain terrain;
    private TerrainData td;
    private float pixelDistance;
    [SerializeField]private float radius;
    private Vector2[,] PixelCornerCoordinates;
    private float[,] heights;
    [SerializeField]private Vector3 centerCoords;

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
        Debug.Log(terrain.GetPosition());

        Vector2 centerIndex = new Vector2((td.heightmapResolution - 1) / 2f, (td.heightmapResolution - 1) / 2f);

        for (int i = 0; i < td.heightmapResolution; i++)
        {
            for (int j = 0; j < td.heightmapResolution; j++)
            {
                PixelCornerCoordinates[i, j].x = PixelCornerCoordinates[0, 0].x + pixelDistance * j;
                PixelCornerCoordinates[i, j].y = PixelCornerCoordinates[0, 0].y + pixelDistance * i;
                //Debug.Log("Pixel[" + i + "," + j + "]=" + PixelCornerCoordinates[i, j]);
            }
        }

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
