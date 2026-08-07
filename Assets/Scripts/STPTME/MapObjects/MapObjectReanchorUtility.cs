#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Re-anchors TerrainSurface entries in MapObjectDatabase onto the CURRENT terrain.
    ///
    /// Why this is needed even though the compact format re-samples height at load: that
    /// re-sampling happens in the BAKED path only. MapObjectDatabase itself still stores a
    /// plain world position, so after a terrain edit the live/editor view (and anything that
    /// reads the database directly — gizmos, the move tool, spline extension anchoring) keeps
    /// showing the old, now-floating-or-buried positions. This writes the corrected positions
    /// back into the database so every path agrees again.
    ///
    /// For connector-bearing objects (fences) it also fixes TILT, which the bake never
    /// recomputes: each object is re-derived from its own two connector points, both projected
    /// onto the current terrain. Because adjacent objects share a connector point EXACTLY (see
    /// SplinePlacementTool's chained placement), and this re-anchoring is a pure function of
    /// that point, neighbours map to the same new point — so joints stay exactly closed rather
    /// than drifting apart. Object ids and counts are preserved; nothing is created or deleted.
    ///
    /// Requires Play Mode: terrain heights are read through ChunkManager's baked cell files,
    /// which only exist once the runtime chunk system is initialized.
    /// </summary>
    public static class MapObjectReanchorUtility
    {
        [MenuItem("STPTME/Re-anchor Map Objects To Current Terrain")]
        public static void ReanchorAll()
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[Reanchor] Requires Play Mode — terrain heights are read via " +
                    "ChunkManager's baked cell data, which isn't available in edit mode.");
                return;
            }

            var database = FindDatabase();
            var registry = FindRegistry();
            if (database == null || registry == null)
            {
                Debug.LogError("[Reanchor] Could not locate a MapObjectDatabase and/or " +
                    "MapObjectPrototypeRegistry in the project.");
                return;
            }
            if (ChunkManager.Instance == null)
            {
                Debug.LogError("[Reanchor] ChunkManager.Instance is null.");
                return;
            }

            var settings = TerrainManagementSettings.Instance;
            Vector3 sphereCenter = settings.sphereCenter;

            Undo.RecordObject(database, "Re-anchor Map Objects");

            int movedConnector = 0, movedSimple = 0, skipped = 0;
            float maxDelta = 0f;

            // Snapshot first: UpdateDatabase mutates the backing list while we iterate.
            var snapshot = new List<MapObjectDatabase.MapObjectEntry>(database.All);

            foreach (var entry in snapshot)
            {
                if (entry.anchorMode != MapObjectDatabase.AnchorMode.TerrainSurface) continue;
                if (entry.prototypeIndex < 0 || entry.prototypeIndex >= registry.entries.Length) { skipped++; continue; }
                var proto = registry.entries[entry.prototypeIndex];
                if (proto == null) { skipped++; continue; }

                Vector3 newPos;
                Quaternion newRot = entry.worldRotation;

                if (proto.hasConnectors)
                {
                    // Re-derive from this object's own two connector points, each projected onto
                    // the current terrain at connector height. Fixes tilt as well as height.
                    Vector3 startPt = entry.worldPosition + entry.worldRotation * proto.connectorStartLocal;
                    Vector3 endPt   = entry.worldPosition + entry.worldRotation * proto.connectorEndLocal;
                    float connectorHeight = (proto.connectorStartLocal.y + proto.connectorEndLocal.y) * 0.5f;

                    if (!TryProjectToTerrain(startPt, sphereCenter, connectorHeight, out Vector3 newStart) ||
                        !TryProjectToTerrain(endPt, sphereCenter, connectorHeight, out Vector3 newEnd))
                    { skipped++; continue; }

                    Vector3 forward3D = newEnd - newStart;
                    if (forward3D.sqrMagnitude < 1e-8f) { skipped++; continue; }
                    forward3D.Normalize();

                    Vector3 radialUp = (newStart - sphereCenter).normalized;
                    Vector3 up = Vector3.ProjectOnPlane(radialUp, forward3D);
                    if (up.sqrMagnitude < 1e-8f)
                    {
                        Vector3 fallback = Mathf.Abs(forward3D.y) < 0.99f ? Vector3.up : Vector3.right;
                        up = Vector3.ProjectOnPlane(fallback, forward3D);
                    }
                    up.Normalize();

                    // Connector axis is local +X — same construction SplinePlacementTool uses.
                    newRot = Quaternion.LookRotation(Vector3.Cross(forward3D, up), up);
                    newPos = newStart - (newRot * proto.connectorStartLocal);
                    movedConnector++;
                }
                else
                {
                    // No connectors: nothing defines a slope for this object, so only its
                    // height is corrected and its authored rotation is left untouched.
                    if (!TryProjectToTerrain(entry.worldPosition, sphereCenter, 0f, out newPos))
                    { skipped++; continue; }
                    movedSimple++;
                }

                maxDelta = Mathf.Max(maxDelta, Vector3.Distance(newPos, entry.worldPosition));
                database.UpdateDatabase(entry.id, newPos, newRot, entry.localScale);
            }

            EditorUtility.SetDirty(database);

            Debug.Log($"[Reanchor] Re-anchored {movedConnector} connector object(s) (position + tilt) and " +
                $"{movedSimple} simple object(s) (position only). Skipped {skipped}. " +
                $"Largest position change: {maxDelta:F2}m. " +
                "Re-bake to update the shipped files.");
        }

        /// <summary>
        /// Projects a world point onto the current terrain surface along its own radial, then
        /// lifts it by <paramref name="heightAboveGround"/>. Uses the exact same chunk-address
        /// and height-sampling math the baked load path uses (ComputeExactInstanceDir /
        /// SampleTerrainHeight), so re-anchored positions agree with what a bake would produce
        /// rather than being a second, independently-derived answer.
        /// </summary>
        private static bool TryProjectToTerrain(Vector3 worldPoint, Vector3 sphereCenter,
            float heightAboveGround, out Vector3 result)
        {
            result = worldPoint;

            var settings = TerrainManagementSettings.Instance;
            float chunkSize = settings.terrainSize / settings.tilingFactor;

            if (!MapObjectChunkMath.TryResolve(worldPoint, sphereCenter, chunkSize,
                    settings.faceWorldSize, settings.numberOfChunks, settings.minX, settings.maxX,
                    out var addr))
                return false;

            Vector3 dir = ChunkManager.Instance.ComputeExactInstanceDir(
                addr.packed, addr.face, addr.localXMeters, addr.localZMeters);
            float height = ChunkManager.Instance.SampleTerrainHeight(
                addr.packed, addr.face, addr.localXMeters, addr.localZMeters);

            result = sphereCenter + dir * (settings.sphereRadius + height + heightAboveGround);
            return true;
        }

        private static MapObjectDatabase FindDatabase()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:MapObjectDatabase"))
            {
                var db = AssetDatabase.LoadAssetAtPath<MapObjectDatabase>(AssetDatabase.GUIDToAssetPath(guid));
                if (db != null) return db;
            }
            return null;
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
}
#endif