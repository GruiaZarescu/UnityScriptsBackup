#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(STPTME.MapObjects.MapObjectMetadata))]
[CanEditMultipleObjects]
public class MapObjectMetadataEditor : Editor
{
    private bool _dragDirty;

    private void OnSceneGUI()
    {
        var meta = (STPTME.MapObjects.MapObjectMetadata)target;
        if (meta.id == 0 || meta.sourceDatabase == null) return;

        Event e = Event.current;

        // Accumulate movement during the drag, but don't act on it yet — snapping mid-drag
        // yanks the object out from under the cursor and makes the handle feel broken.
        if (meta.transform.hasChanged)
        {
            _dragDirty = true;
            meta.transform.hasChanged = false;
        }

        // Commit once the user releases: snap, re-sync the pick collider, write to database.
        bool released = e.type == EventType.MouseUp && e.button == 0;
        if (_dragDirty && released)
        {
            _dragDirty = false;

            if (STPTME.MapObjects.MapObjectMetadata.SnapToGroundEnabled)
            {
                bool snapped = SnapToGround(meta.transform);
                Debug.Log($"[MapObjectMetadataEditor] Snap on release: {(snapped ? "hit terrain" : "NO HIT — object left where dropped")}");
            }

            meta.EnsurePickCollider();

            meta.sourceDatabase.UpdateDatabase(meta.id, meta.transform.position,
                meta.transform.rotation, meta.transform.localScale);

            SceneView.RepaintAll();
        }
    }

    /// <summary>
    /// Casts along the radial line the object already sits on — from above all possible
    /// terrain, inward toward the sphere center — and places the object at the first hit.
    /// Rotation is rebuilt from the surface normal, preserving the object's existing facing
    /// flattened onto the new slope.
    /// </summary>
    private static bool SnapToGround(Transform t)
    {
        var settings = TerrainManagementSettings.Instance;
        Vector3 sphereCenter = settings.sphereCenter;

        Vector3 dirFromCenter = t.position - sphereCenter;
        float distFromCenter = dirFromCenter.magnitude;
        if (distFromCenter < 0.01f) return false;
        dirFromCenter /= distFromCenter;

        float castStartRadius = settings.sphereRadius + 2000f;
        Vector3 castOrigin = sphereCenter + dirFromCenter * castStartRadius;
        Vector3 castDir = -dirFromCenter;

        int pickLayer = LayerMask.NameToLayer("MapObjectPicking");
        int mask = pickLayer >= 0 ? ~(1 << pickLayer) : ~0;

        if (!Physics.Raycast(castOrigin, castDir, out RaycastHit hit, castStartRadius + 2000f, mask))
            return false;

        Vector3 oldForward = t.forward;
        t.position = hit.point;

        Vector3 flatForward = Vector3.ProjectOnPlane(oldForward, hit.normal);
        if (flatForward.sqrMagnitude > 0.0001f)
            t.rotation = Quaternion.LookRotation(flatForward.normalized, hit.normal);

        return true;
    }
}
#endif