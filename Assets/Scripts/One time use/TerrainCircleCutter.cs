#if UNITY_EDITOR
using UnityEngine;

[ExecuteAlways]
public class TerrainCircleCutter : MonoBehaviour
{
    public Terrain terrain;
    [SerializeField] private bool customCircleRadius = false;
    [SerializeField] private bool inverted=false;
    [SerializeField] private float customRadius;

    [ContextMenu("Cut Terrain Into Circle")]
    public void CutTerrainIntoCircle()
    {
        if (terrain == null)
        {
            Debug.LogError("Terrain is not assigned.");
            return;
        }

        TerrainData td = terrain.terrainData;
        // Get the hole map resolution dynamically
        int res = td.holesResolution;

        bool[,] holes = new bool[res, res];

        Vector2 center = new Vector2(res / 2f, res / 2f);
        Debug.Log(res / 2f);
        float adjustedFinalRadius = ((customRadius/(td.size.x))*res)/2f;
        float maxDist = customCircleRadius? adjustedFinalRadius : (res/2f) ;  // radius = half the terrain size

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float dx = x - center.x;
                float dy = y - center.y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);

                //Must be <= for normal operation
                holes[y, x] = inverted? dist >= maxDist : dist <= maxDist+1;
            }
        }

        td.SetHoles(0, 0, holes);
        Debug.Log("Terrain corners cut: terrain is now circular.");
    }
}
#endif