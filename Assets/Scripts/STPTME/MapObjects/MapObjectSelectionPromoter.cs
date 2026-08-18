#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class MapObjectSelectionPromoter
{
    static MapObjectSelectionPromoter()
    {
        //Debug.Log("[MapObjectSelectionPromoter] Registered.");
        Selection.selectionChanged += Promote;
    }

    private static void Promote()
    {
        var go = Selection.activeGameObject;
        if (go == null) return;

        var meta = go.GetComponentInParent<STPTME.MapObjects.MapObjectMetadata>();
        if (meta != null && meta.gameObject != go)
        {
            Debug.Log($"[MapObjectSelectionPromoter] Promoted selection '{go.name}' → '{meta.gameObject.name}'");
            Selection.activeGameObject = meta.gameObject;
        }
    }
}
#endif