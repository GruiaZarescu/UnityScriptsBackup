#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class DuplicatedTreeRemover : MonoBehaviour
{
    [SerializeField] private Terrain _terrain;
    [SerializeField] private float cellSize = 10f;
    [SerializeField] private float minDistance0 = 2.5f;
    [SerializeField] private float minDistance1 = 3f;
    [SerializeField] private float minDistance2 = 3f;
    [SerializeField] private float minDistance3 = 3f;
    [SerializeField] private float minDistance4 = 3f;
    [SerializeField] private float minDistance5 = 3f;

    private TreeInstance[] treeInstances;
    private int treesRemoved;

    [ContextMenu("Remove Duplicated Trees")]
    private void RemoveDuplicatedTrees()
    {
#if UNITY_EDITOR
        if (_terrain == null)
        {
            Debug.LogError("Terrain not assigned.");
            return;
        }

        treeInstances = _terrain.terrainData.treeInstances;
        treesRemoved = -1;

        int passCount = 0;

        while (treesRemoved != 0)
        {
            Debug.Log("Removed " + treesRemoved + "trees.");
            treesRemoved = 0;
            RunPass(Vector2.zero);
            RunPass(new Vector2(cellSize / 2f, cellSize / 2f));
            passCount++;
        }

        _terrain.terrainData.treeInstances = treeInstances;

        Debug.Log($"Tree cleanup complete. Final tree count: {treeInstances.Length}. Passes: {passCount}");
#endif
    }

    private void RunPass(Vector2 offset)
    {
        Dictionary<Vector2Int, List<int>> cellTreeIndices = new Dictionary<Vector2Int, List<int>>();

        for (int i = 0; i < treeInstances.Length; i++)
        {
            Vector3 worldPos = GetTreeWorldPosition(treeInstances[i]);
            Vector2Int cell = new Vector2Int(
                Mathf.FloorToInt((worldPos.x + offset.x) / cellSize),
                Mathf.FloorToInt((worldPos.z + offset.y) / cellSize)
            );

            if (!cellTreeIndices.ContainsKey(cell))
                cellTreeIndices[cell] = new List<int>();

            cellTreeIndices[cell].Add(i);
        }

        HashSet<int> treesToRemove = new HashSet<int>();

        foreach (var kvp in cellTreeIndices)
        {
            List<int> indices = kvp.Value;
            for (int i = 0; i < indices.Count; i++)
            {
                for (int j = i + 1; j < indices.Count; j++)
                {
                    int idx1 = indices[i];
                    int idx2 = indices[j];

                    Vector3 pos1 = GetTreeWorldPosition(treeInstances[idx1]);
                    Vector3 pos2 = GetTreeWorldPosition(treeInstances[idx2]);

                    int idx1PrototypeIndex = treeInstances[idx1].prototypeIndex;
                    int idx2PrototypeIndex = treeInstances[idx2].prototypeIndex;

                    float minDistanceidx1 = GetMinDistance(idx1PrototypeIndex);
                    float minDistanceidx2 = GetMinDistance(idx2PrototypeIndex);

                    float minDistSqr = (minDistanceidx1 + minDistanceidx2) * (minDistanceidx1 + minDistanceidx2);
                    if ((pos1 - pos2).sqrMagnitude < minDistSqr)
                    {
                        treesToRemove.Add(idx2);
                        treesRemoved++;
                    }
                }
            }
        }

        treeInstances = treeInstances
            .Where((_, i) => !treesToRemove.Contains(i))
            .ToArray();
    }

    private Vector3 GetTreeWorldPosition(TreeInstance tree)
    {
        Vector3 size = _terrain.terrainData.size;
        return Vector3.Scale(tree.position, size) + _terrain.transform.position;
    }
    private float GetMinDistance(int treeIndex)
    {
        switch (treeIndex)
        {
            case 0:
                return minDistance0;
            case 1:
                return minDistance1;
            case 2:
                return minDistance2;
            case 3:
                return minDistance3;
            case 4:
                return minDistance4;
            case 5:
                return minDistance5;
            default:
                Debug.LogWarning($"Unknown tree type: {treeIndex}");
                return 0f;
         }
     }
}
#endif