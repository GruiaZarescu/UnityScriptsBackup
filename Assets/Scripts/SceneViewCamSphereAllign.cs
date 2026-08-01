#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity's Scene View has no native "roll" control — its default orbit navigation always
/// treats world +Y as up, which becomes actively uncomfortable once you're authoring near the
/// poles or far side of a sphere world, where the terrain's real "down" doesn't match the
/// camera's assumed down. There's no exposed gesture for this (confirmed — it's a known,
/// long-standing gap; the only built-in way to roll the camera at all is "Align View to
/// Selected," which borrows a selected object's rotation wholesale, roll included, but
/// requires having a conveniently-rotated object to select).
///
/// This does directly what that trick does by accident: re-orients the Scene View camera's
/// "up" to match the sphere's radial direction at the current view pivot, while keeping the
/// same look direction and distance — a one-key fix instead of hunting for a rotated object.
/// </summary>
public static class SceneCameraSphereAlign
{
    // &#r = Alt+Shift+R (all platforms — Alt-based combos rarely collide with Unity's own
    // shortcuts, which lean almost entirely on Ctrl/Cmd). If this ever conflicts with
    // something else in your setup, rebind it via Edit > Shortcuts (search "Sphere Up") rather
    // than editing this attribute — that's the supported way and survives Unity updates.
    [MenuItem("STPTME/Align Scene Camera To Sphere Up &#r")]
    public static void AlignToSphereUp()
    {
        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null)
        {
            Debug.LogWarning("[SceneCameraSphereAlign] No active Scene View.");
            return;
        }

        var settings = TerrainManagementSettings.Instance;
        if (settings == null)
        {
            Debug.LogWarning("[SceneCameraSphereAlign] TerrainManagementSettings not found.");
            return;
        }

        // The pivot is the point the camera currently orbits/looks toward — exactly "wherever
        // you're working right now," and it's already tracked by the Scene View itself, so no
        // raycast is needed to figure out what you're looking at.
        Vector3 radialUp = sceneView.pivot - settings.sphereCenter;
        if (radialUp.sqrMagnitude < 0.0001f)
        {
            Debug.LogWarning("[SceneCameraSphereAlign] View pivot is at the sphere center — nothing to align to.");
            return;
        }
        radialUp.Normalize();

        Vector3 currentForward = sceneView.rotation * Vector3.forward;

        // Looking almost straight along the radial (near-vertical view of the surface) makes
        // forward/up nearly parallel, which LookRotation can't resolve — fall back to a
        // horizontal reference rather than producing a degenerate/undefined roll.
        if (Mathf.Abs(Vector3.Dot(currentForward.normalized, radialUp)) > 0.999f)
        {
            Debug.LogWarning("[SceneCameraSphereAlign] View is looking nearly straight along the radial — " +
                "orbit to a shallower angle first, then align.");
            return;
        }

        // Re-orthogonalizes forward against the new up, so the roll is corrected while the
        // view keeps looking at essentially the same spot.
        sceneView.rotation = Quaternion.LookRotation(currentForward, radialUp);
        sceneView.Repaint();
    }
}
#endif