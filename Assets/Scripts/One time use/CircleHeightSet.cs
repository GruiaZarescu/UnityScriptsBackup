#if UNITY_EDITOR
using UnityEngine;


[ExecuteAlways]
public class CircleHeightSet : MonoBehaviour
{
    public Terrain terrain;
    [SerializeField] private bool customCircleRadius = false;
    
    [SerializeField] private bool inverted = false;
    [SerializeField] private float customRadius;
    [Tooltip("If you want the circle to be raised from the center, set inner radius to 0")]
    [SerializeField] private float innerRadius;
    [SerializeField] private float height;

    [ContextMenu("Raise circular area to height")]
    public void CutTerrainIntoCircle()
    {
        if (terrain == null)
        {
            Debug.LogError("Terrain is not assigned.");
            return;
        }

        TerrainData td = terrain.terrainData;
        int res = td.heightmapResolution;
        float adjustedFinalRadius = ((customRadius/(td.size.x))*res)/2f;
        float adjustedInnerRadius=((innerRadius/(td.size.x))*res)/2f;

        float[,] heights = td.GetHeights(0, 0, res, res);

        Vector2 center = new Vector2(res / 2f, res / 2f);
        Debug.Log(res / 2f);
        float maxDist = customCircleRadius? adjustedFinalRadius : (res/2f) ;  // radius = half the terrain size
        float minDist = customCircleRadius ? adjustedInnerRadius : 0;

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dx = x - center.x;
                float dy = y - center.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);


                if (inverted)
                {
                    if (dist >= maxDist) heights[y, x] += height / td.size.y;
                }
                else if (dist <= maxDist && dist >= minDist) heights[y, x] += height / td.size.y;
            }
        }
        td.SetHeights(0, 0, heights);
        Debug.Log("Circle height altered.");
    }
}
#endif