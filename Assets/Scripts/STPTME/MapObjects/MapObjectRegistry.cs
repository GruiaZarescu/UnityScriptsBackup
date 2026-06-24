using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps prefab GUID (stored as two ulongs in cell files) to a spawnable prefab reference.
/// Add one entry per unique prefab that MapObjectBaker serialises into CellObjectGroup files.
/// </summary>
[CreateAssetMenu(fileName = "LegacyMapObjectRegistry", menuName = "STPTME/Legacy Map Object Registry")]
public class LegacyMapObjectRegistry : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("32-character GUID from the AssetDatabase (shown in .meta files). " +
                 "Copy from the prefab's .meta file if the prefab field is empty.")]
        public string guid;

        [Tooltip("Prefab to instantiate at runtime when this GUID is encountered.")]
        public GameObject prefab;
    }

    public Entry[] entries = Array.Empty<Entry>();

    // Runtime lookup built on first use. Keyed by (guidHigh, guidLow) matching the
    // two-ulong encoding written by MapObjectBaker.
    private Dictionary<(ulong, ulong), GameObject> _lookup;

    /// <summary>
    /// Returns the prefab for the given two-ulong GUID encoding, or null if not registered.
    /// Thread-safe after the first call (dictionary is read-only thereafter).
    /// </summary>
    public GameObject GetPrefab(ulong guidHigh, ulong guidLow)
    {
        if (_lookup == null)
            BuildLookup();

        _lookup.TryGetValue((guidHigh, guidLow), out GameObject prefab);
        return prefab;
    }

    // ── Editor helper: called by MapObjectBaker to populate entries automatically.
    // At runtime only GetPrefab is used. ──────────────────────────────────────────

    private void BuildLookup()
    {
        _lookup = new Dictionary<(ulong, ulong), GameObject>(
            entries != null ? entries.Length : 0);

        if (entries == null) return;

        foreach (Entry e in entries)
        {
            if (string.IsNullOrEmpty(e.guid) || e.guid.Length != 32) continue;
            if (!TryParseGuid(e.guid, out ulong high, out ulong low)) continue;
            _lookup[(high, low)] = e.prefab;
        }
    }

    // Parses the first 16 hex chars of a GUID string as a ulong (big-endian hex value)
    // and the last 16 as another ulong.
    internal static bool TryParseGuid(string guid, out ulong high, out ulong low)
    {
        high = 0; low = 0;
        if (guid == null || guid.Length != 32) return false;

        try
        {
            high = Convert.ToUInt64(guid.Substring(0, 16), 16);
            low  = Convert.ToUInt64(guid.Substring(16, 16), 16);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
