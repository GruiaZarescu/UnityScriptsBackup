using UnityEngine;

public class TerrainPositionTester : MonoBehaviour
{
    [SerializeField] private Terrain terrain;
    [ContextMenu("Test")]
    private void test()
    {
        Debug.Log($"position from transform: {terrain.transform.position}");
        Debug.Log($"Local position: {terrain.transform.localPosition}");
        Vector3 pos = terrain.GetPosition();
        Debug.Log($"position from get position: {pos.x},{pos.y},{pos.z}");
        TerrainData td = terrain.terrainData;
        int heightRes = td.heightmapResolution;
        float[,] heights = td.GetHeights(0, 0, heightRes, heightRes);
        //heights[heightRes-1, 0] = 1;
        //td.SetHeights(0, 0, heights);
    }
}
