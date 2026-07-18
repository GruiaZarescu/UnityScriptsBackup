#if UNITY_EDITOR
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class DuplicatedTreeRemoverForEach : MonoBehaviour
{
    [System.Serializable]
    public struct PrototypeRule
    {
        public int   prototypeIndex;
        public float minDistance;
    }

    [Tooltip("Minimum distance between trees for each prototype index. "
           + "Unlisted prototypes are skipped entirely (minDistance treated as 0).")]
    [SerializeField] private List<PrototypeRule> rules = new List<PrototypeRule>();

    // Computed at runtime — not exposed, no manual tuning needed.
    private float     cellSize;
    private Terrain[] terrains;
    private int       currentTerrainIndex;

    [ContextMenu("Remove Duplicated Trees")]
    private void RemoveDuplicatedTrees()
    {
        if (rules == null || rules.Count == 0)
        {
            Debug.LogWarning("[TreeRemover] No rules defined. Add at least one entry.");
            return;
        }

        // Cell size must be >= the largest minDistance so that any two trees within
        // threshold always land in the same cell or an immediately adjacent one.
        // The 3x3 neighborhood check below then guarantees no pair is ever missed.
        cellSize = rules.Max(r => r.minDistance);
        if (cellSize <= 0f)
        {
            Debug.LogWarning("[TreeRemover] All minDistances are zero or negative. Nothing to do.");
            return;
        }

        terrains = FindObjectsByType<Terrain>(FindObjectsSortMode.None);
        Debug.Log($"[TreeRemover] Found {terrains.Length} terrain(s). Cell size: {cellSize}");

        int totalRemoved = 0;
        int pass         = 0;
        int removedThisPass;

        do
        {
            removedThisPass = 0;
            RunPass(ref removedThisPass);
            totalRemoved += removedThisPass;
            pass++;
            Debug.Log($"[TreeRemover] Pass {pass}: removed {removedThisPass} trees.");
        }
        while (removedThisPass > 0);

        Debug.Log($"[TreeRemover] Done. Total removed: {totalRemoved} across {pass} pass(es).");
    }

    private void RunPass(ref int removedCount)
    {
        for (int ti = 0; ti < terrains.Length; ti++)
        {
            currentTerrainIndex  = ti;
            Terrain      terrain = terrains[ti];
            TreeInstance[] trees = terrain.terrainData.treeInstances;

            // ── Build spatial grid ─────────────────────────────────────────────
            var grid = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < trees.Length; i++)
            {
                Vector2Int cell = GetCell(WorldPos(trees[i]));
                if (!grid.TryGetValue(cell, out var bucket))
                    grid[cell] = bucket = new List<int>();
                bucket.Add(i);
            }

            // ── Compare each tree against its 3×3 cell neighbourhood ──────────
            // Checking all 9 cells (self + 8 neighbours) guarantees we never miss
            // a pair regardless of where trees fall relative to cell boundaries.
            // The j > i guard ensures each pair is evaluated exactly once.
            var toRemove = new HashSet<int>();

            for (int i = 0; i < trees.Length; i++)
            {
                if (toRemove.Contains(i)) continue; // already marked, skip

                float d1 = GetMinDistance(trees[i].prototypeIndex);
                if (d1 <= 0f) continue; // prototype not in rules, ignore

                Vector3    pos1   = WorldPos(trees[i]);
                Vector2Int origin = GetCell(pos1);

                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    var neighbour = new Vector2Int(origin.x + dx, origin.y + dz);
                    if (!grid.TryGetValue(neighbour, out var bucket)) continue;

                    foreach (int j in bucket)
                    {
                        if (j <= i)                  continue; // only check each pair once
                        if (toRemove.Contains(j))    continue; // already going

                        float d2 = GetMinDistance(trees[j].prototypeIndex);
                        if (d2 <= 0f) continue;

                        float threshold = d1 + d2;
                        if ((pos1 - WorldPos(trees[j])).sqrMagnitude < threshold * threshold)
                            toRemove.Add(j);
                    }
                }
            }

            removedCount += toRemove.Count;
            terrain.terrainData.treeInstances = trees
                .Where((_, i) => !toRemove.Contains(i))
                .ToArray();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Vector2Int GetCell(Vector3 worldPos) => new Vector2Int(
        Mathf.FloorToInt(worldPos.x / cellSize),
        Mathf.FloorToInt(worldPos.z / cellSize));

    private Vector3 WorldPos(TreeInstance tree)
    {
        Terrain t = terrains[currentTerrainIndex];
        return Vector3.Scale(tree.position, t.terrainData.size) + t.transform.position;
    }

    private float GetMinDistance(int prototypeIndex)
    {
        foreach (var rule in rules)
            if (rule.prototypeIndex == prototypeIndex)
                return rule.minDistance;
        return 0f;
    }
}
#endif
