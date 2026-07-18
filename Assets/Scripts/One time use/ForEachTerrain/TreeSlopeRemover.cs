#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;

public class TreeSlopeRemover : MonoBehaviour
{
    [System.Serializable]
    public struct PrototypeRule
    {
        public int   prototypeIndex;
        [Range(0f, 90f)]
        public float maxSlopeAngle; // degrees
    }

    [Tooltip("Max slope angle per prototype index. "
           + "Unlisted prototypes are left untouched.")]
    [SerializeField] private List<PrototypeRule> rules = new List<PrototypeRule>();

    [ContextMenu("Remove Trees on Steep Slopes")]
    private void RemoveSteepTrees()
    {
        if (rules == null || rules.Count == 0)
        {
            Debug.LogWarning("[SlopeRemover] No rules defined. Add at least one entry.");
            return;
        }

        Terrain[] terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);
        int totalRemoved   = 0;

        foreach (Terrain terrain in terrains)
        {
            TerrainData    td           = terrain.terrainData;
            TreeInstance[] originalTrees = td.treeInstances;
            var            kept         = new List<TreeInstance>(originalTrees.Length);
            int            removedCount = 0;

            foreach (var tree in originalTrees)
            {
                float maxSlope = GetMaxSlopeAngle(tree.prototypeIndex);

                if (maxSlope < 0f)
                {
                    // Prototype not in rules — keep unconditionally
                    kept.Add(tree);
                    continue;
                }

                // tree.position is already normalised (0–1) in terrain space
                Vector3 normal     = td.GetInterpolatedNormal(tree.position.x, tree.position.z);
                float   slopeAngle = Vector3.Angle(normal, Vector3.up);

                if (slopeAngle <= maxSlope)
                    kept.Add(tree);
                else
                    removedCount++;
            }

            td.treeInstances = kept.ToArray();
            terrain.Flush();

            totalRemoved += removedCount;
            Debug.Log($"[SlopeRemover] {terrain.name}: removed {removedCount}, "
                    + $"kept {kept.Count}.");
        }

        Debug.Log($"[SlopeRemover] Done. Total removed: {totalRemoved}.");
    }

    // Returns the configured max slope for a prototype, or -1 if unlisted.
    private float GetMaxSlopeAngle(int prototypeIndex)
    {
        foreach (var rule in rules)
            if (rule.prototypeIndex == prototypeIndex)
                return rule.maxSlopeAngle;
        return -1f;
    }
}
#endif
