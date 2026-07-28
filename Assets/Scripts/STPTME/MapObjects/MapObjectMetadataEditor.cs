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
        if (meta.id == 0 || meta.sourceDatabase == null) return;

        if (meta.transform.hasChanged)
        {
            if (STPTME.MapObjects.MapObjectMetadata.SnapToGroundEnabled)
                SnapToGround(meta.transform);

            meta.sourceDatabase.UpdateDatabase(meta.id, meta.transform.position, meta.transform.rotation, meta.transform.localScale);
            meta.transform.hasChanged = false;
        }
    }

    private static void SnapToGround(Transform t)
    {
        var settings = TerrainManagementSettings.Instance;
        Vector3 sphereCenter = settings.sphereCenter;
        Vector3 oldUp = t.up;

        Vector3 dirFromCenter = (t.position - sphereCenter);
        float distFromCenter = dirFromCenter.magnitude;
        if (distFromCenter < 0.01f) return; // degenerate, object sitting exactly at sphere center
        dirFromCenter /= distFromCenter;

        // Cast from well outside any possible terrain height, straight down toward the surface
        // along the SAME radial direction the object is already on — not from the object's
        // current (possibly floating or displaced) position.
        float castStartRadius = settings.sphereRadius + 2000f; // comfortably above max terrain height
        Vector3 castOrigin = sphereCenter + dirFromCenter * castStartRadius;
        Vector3 castDir = -dirFromCenter; // toward the center

        int pickLayer = LayerMask.NameToLayer("MapObjectPicking");
        int mask = pickLayer >= 0 ? ~(1 << pickLayer) : ~0;

        if (Physics.Raycast(castOrigin, castDir, out RaycastHit hit, castStartRadius + 2000f, mask))
        {
            // Preserve current yaw around the OLD up axis, then rebuild orientation from scratch
            // against the new normal — avoids compounding drift from repeated delta-rotations.
            Vector3 oldForward = t.forward;
            t.position = hit.point;
            t.rotation = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(oldForward, hit.normal).normalized,
                hit.normal);
        }
        
    }

}
#endif