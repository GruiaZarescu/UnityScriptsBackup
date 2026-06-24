#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;


public class TreeSlopeRemover : MonoBehaviour
{
    [SerializeField] private float maxSlopeAngle = 30f; // Maximum allowed slope in degrees

    [ContextMenu("Remove Trees on Steep Slopes")]
    private void RemoveSteepTrees()
    {
        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);

        foreach (Terrain terrain in terrains)
        {
            TerrainData td = terrain.terrainData;
            Vector3 terrainPos = terrain.transform.position;

            TreeInstance[] originalTrees = td.treeInstances;
            List<TreeInstance> keptTrees = new List<TreeInstance>();

            int removedCount = 0;

            foreach (var tree in originalTrees)
            {
                
                // Convert to normalized terrain coordinates (0–1)
                float normX = tree.position.x;
                float normZ = tree.position.z;

                // Get terrain normal at tree location
                Vector3 normal = td.GetInterpolatedNormal(normX, normZ);
                float slopeAngle = Vector3.Angle(normal, Vector3.up);

                if (slopeAngle <= maxSlopeAngle)
                {
                    keptTrees.Add(tree);
                }
                else
                {
                    removedCount++;
                }
            }

            td.treeInstances = keptTrees.ToArray();
            terrain.Flush();
            Debug.Log($"Terrain at {terrainPos}: Removed {removedCount} steep-slope trees. Remaining: {keptTrees.Count}");
        }

        Debug.Log("Finished removing trees on steep slopes.");
    }
}
#endif
