using UnityEngine;

public class TreeOrientator : MonoBehaviour
{
    void Awake()
    {
        // Suppress Unity's built-in terrain billboard tree rendering.
        // Trees are now rendered exclusively by TreeRenderer (GPU instancing).
        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);
        foreach (Terrain _terrain in terrains)
        {
            _terrain.treeDistance = 0;
        }
    }
}
