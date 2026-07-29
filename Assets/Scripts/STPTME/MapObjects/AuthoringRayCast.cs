using UnityEngine;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Shared raycasting for the authoring tools. Single source of truth on purpose: both the
    /// simple placer and the spline tool need identical "where is the ground here" semantics,
    /// and having two copies of that logic is how they silently drift apart.
    /// </summary>
    public static class AuthoringRaycast
    {
        // Preallocated so the spline tool's bisection (hundreds to thousands of casts per
        // committed run) doesn't allocate a fresh array on every single cast.
        private static readonly RaycastHit[] Buffer = new RaycastHit[64];

        /// <summary>
        /// Nearest hit that is NOT a placed map object — i.e. actual terrain.
        ///
        /// A plain Physics.Raycast returns the nearest hit of ANY kind, so a tree canopy or an
        /// already-placed fence standing between the cursor and the ground would win, and the
        /// new object would be planted on top of it. Filtering on MapObjectMetadata catches
        /// every spawned prefab (blotch-derived trees included, since MapPrefabStreamer always
        /// attaches it) while never matching a terrain chunk, which has no such component.
        /// </summary>
        public static bool TryRaycastTerrain(Ray ray, float maxDistance, out RaycastHit terrainHit)
        {
            terrainHit = default;

            int pickLayer = LayerMask.NameToLayer("MapObjectPicking");
            int mask = pickLayer >= 0 ? ~(1 << pickLayer) : ~0;

            int count = Physics.RaycastNonAlloc(ray, Buffer, maxDistance, mask, QueryTriggerInteraction.Ignore);
            if (count == 0) return false;

            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                if (Buffer[i].collider == null) continue;
                if (Buffer[i].collider.GetComponentInParent<MapObjectMetadata>() != null) continue; // a placed object, not ground

                if (Buffer[i].distance < bestDist)
                {
                    bestDist = Buffer[i].distance;
                    terrainHit = Buffer[i];
                    found = true;
                }
            }

            // Note: if RaycastNonAlloc filled the buffer exactly, there may be further hits we
            // never saw. 64 is far beyond any plausible stack of colliders along one ray here,
            // but it's a silent truncation rather than an error, so it's worth knowing about.
            return found;
        }
    }
}