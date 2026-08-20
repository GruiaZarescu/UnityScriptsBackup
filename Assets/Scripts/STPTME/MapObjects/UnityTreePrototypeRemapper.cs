#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fills in MapObjectPrototypeEntry.unityTerrainPrototypeIndex by matching each entry's
/// name against a terrain's tree prototypes — so an existing registry with N tree entries
/// in the same relative order as Unity's tree prototype list can be brought up to date in
/// one click, rather than typing the mapping in by hand.
///
/// Matching is by prefab reference first (exact), then by name substring as a fallback
/// (since registry entry names and Unity prototype prefab names don't always match exactly
/// — e.g. "-01 savana" vs a prefab literally named "01_savana"). Anything not confidently
/// matched is left alone and reported, rather than guessed.
/// </summary>
public static class UnityTreePrototypeMapper
{
    [MenuItem("STPTME/Map Registry Entries To Unity Tree Prototypes")]
    public static void MapFromSelectedTerrain()
    {
        var terrain = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<Terrain>()
            : null;
        if (terrain == null)
        {
            Debug.LogError("[UnityTreePrototypeMapper] Select a GameObject with a Terrain component first.");
            return;
        }

        MapObjectPrototypeRegistry registry = FindRegistry();
        if (registry == null || registry.entries == null)
        {
            Debug.LogError("[UnityTreePrototypeMapper] Could not locate a MapObjectPrototypeRegistry asset.");
            return;
        }

        TreePrototype[] treeProtos = terrain.terrainData.treePrototypes;
        if (treeProtos == null || treeProtos.Length == 0)
        {
            Debug.LogError("[UnityTreePrototypeMapper] Selected terrain has no tree prototypes.");
            return;
        }

        Undo.RecordObject(registry, "Map Registry To Unity Tree Prototypes");

        int matched = 0, alreadySet = 0;
        var unmatchedEntries = new System.Collections.Generic.List<string>();
        var usedUnityIndices = new System.Collections.Generic.HashSet<int>();

        for (int i = 0; i < registry.entries.Length; i++)
        {
            var entry = registry.entries[i];
            if (entry == null) continue;

            if (entry.unityTerrainPrototypeIndex >= 0)
            {
                // Already mapped — don't override an existing (possibly manually corrected)
                // value, but still record it as "used" so duplicate-detection below is accurate.
                usedUnityIndices.Add(entry.unityTerrainPrototypeIndex);
                alreadySet++;
                continue;
            }

            int foundIdx = FindBestMatch(entry, treeProtos, usedUnityIndices);
            if (foundIdx >= 0)
            {
                entry.unityTerrainPrototypeIndex = foundIdx;
                usedUnityIndices.Add(foundIdx);
                matched++;
            }
            else
            {
                unmatchedEntries.Add($"[{i}] \"{entry.name}\"");
            }
        }

        EditorUtility.SetDirty(registry);

        string report = $"[UnityTreePrototypeMapper] Matched {matched} new entr{(matched == 1 ? "y" : "ies")}, " +
            $"{alreadySet} already set, {unmatchedEntries.Count} unmatched.";
        if (unmatchedEntries.Count > 0)
            report += "\nUnmatched (left as -1, meaning \"not a tree\" — verify this is correct for each): " +
                string.Join(", ", unmatchedEntries);
        Debug.Log(report);
    }

    private static int FindBestMatch(
        MapObjectPrototypeRegistry.MapObjectPrototypeEntry entry,
        TreePrototype[] treeProtos,
        System.Collections.Generic.HashSet<int> usedUnityIndices)
    {
        // Pass 1: exact prefab reference match, if the entry's source is itself the tree prefab.
        // lodGameObjects was removed (the prefab itself handles LOD now) — match on sourcePrefab.
        GameObject entryPrefab = entry.sourcePrefab;
        if (entryPrefab != null)
        {
            for (int t = 0; t < treeProtos.Length; t++)
            {
                if (usedUnityIndices.Contains(t)) continue;
                if (treeProtos[t].prefab == entryPrefab) return t;
            }
        }

        // Pass 2: name containment, case-insensitive, either direction. Registry names in
        // this project use a "-" prefix / free-form style ("-01 savana") that won't equal a
        // Unity prefab name exactly, so substring matching is deliberately the primary path.
        string cleanEntryName = CleanName(entry.name);
        if (string.IsNullOrEmpty(cleanEntryName)) return -1;

        for (int t = 0; t < treeProtos.Length; t++)
        {
            if (usedUnityIndices.Contains(t)) continue;
            if (treeProtos[t].prefab == null) continue;

            string cleanProtoName = CleanName(treeProtos[t].prefab.name);
            if (string.IsNullOrEmpty(cleanProtoName)) continue;

            if (cleanEntryName.Contains(cleanProtoName) || cleanProtoName.Contains(cleanEntryName))
                return t;
        }

        return -1;
    }

    private static string CleanName(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
    }

    private static MapObjectPrototypeRegistry FindRegistry()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:MapObjectPrototypeRegistry"))
        {
            var reg = AssetDatabase.LoadAssetAtPath<MapObjectPrototypeRegistry>(AssetDatabase.GUIDToAssetPath(guid));
            if (reg != null) return reg;
        }
        return null;
    }
}
#endif