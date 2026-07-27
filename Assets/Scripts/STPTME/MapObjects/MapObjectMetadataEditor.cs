#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(STPTME.MapObjects.MapObjectMetadata))]
[CanEditMultipleObjects]
public class MapObjectMetadataEditor : Editor
{
    private void OnSceneGUI()
    {
        var meta = (STPTME.MapObjects.MapObjectMetadata)target;
        if (meta.id == 0 || meta.sourceDatabase == null) return; // not database-backed — nothing to sync

        if (meta.transform.hasChanged)
        {
            meta.sourceDatabase.UpdateDatabase(meta.id, meta.transform.position, meta.transform.rotation, meta.transform.localScale);
            meta.transform.hasChanged = false;
        }
    }
}
#endif