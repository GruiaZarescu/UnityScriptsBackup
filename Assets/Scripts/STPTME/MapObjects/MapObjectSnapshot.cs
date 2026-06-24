#if false
using System;
using UnityEngine;

[Serializable]
public struct PlacedObjectEntry
{
    /// <summary>32-character AssetDatabase GUID of the prefab asset.</summary>
    public string prefabGuid;
    /// <summary>Display-only name stored for readability in the inspector.</summary>
    public string prefabName;
    public Vector3 worldPosition;
    public Quaternion worldRotation;
    public Vector3 localScale;
    /// <summary>Index into MapObjectRecorder._lodContainers. 0 = highest detail LOD.</summary>
    public int lodLevel;
}

/// <summary>
/// Serialised record of all prefab instances placed in the LOD containers during a play-mode
/// authoring session. Acts as the source of truth for MapObjectBaker and the editor preview.
/// </summary>
[CreateAssetMenu(fileName = "MapObjectSnapshot", menuName = "STPTME/Map Object Snapshot")]
public class MapObjectSnapshot : ScriptableObject
{
    public PlacedObjectEntry[] entries = Array.Empty<PlacedObjectEntry>();
}
#endif
