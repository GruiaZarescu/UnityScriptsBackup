#if false
using UnityEngine;
using System;

/// <summary>
/// Runtime decoder for compact 6-byte tree instances.
/// Mirrors CellFileBaking.QuantizeTree encoding in reverse.
/// NOTE: This file is #if false — kept for reference.
/// </summary>
public static class TreeDecoder
{
    // ===== CONSTANTS (must match CellFileBaking) =====
    public const float DEFAULT_SCALE_MIN = 0.5f;
    public const float DEFAULT_SCALE_MAX = 2.0f;

    // ===== DATA STRUCTURES =====

    /// <summary>
    /// Decoded tree instance with world-space values.
    /// </summary>
    public struct DecodedTreeInstance
    {
        public Vector3 worldPosition;   // Position on sphere surface
        public float widthScale;        // Scale factor for width
        public float heightScale;       // Scale factor for height  
        public float rotationRadians;   // Y-axis rotation in radians
        public byte prototypeIndex;     // Index into tree prototype registry

        public Vector3 Scale => new Vector3(widthScale, heightScale, widthScale);
    }

    /// <summary>
    /// Pre-computed chunk geometry for tree decoding.
    /// </summary>
    public struct ChunkGeometry
    {
        public Vector3 chunkCenter;     // Center point on sphere surface
        public Vector3 tangentNorth;    // North direction on tangent plane
        public Vector3 tangentEast;     // East direction on tangent plane
        public float maxPolarDistance;  // Maximum distance from center to chunk corner (on tangent plane)
        public Vector3 sphereNormal;    // Outward normal at chunk center

        public bool IsValid => maxPolarDistance > 0f;
    }

    // ===== GEOMETRY COMPUTATION =====

    /// <summary>
    /// Computes chunk center on sphere and tangent vectors.
    /// Must match CellFileBaking.ComputeChunkCenterAndTangents exactly.
    /// </summary>
    public static void ComputeChunkCenterAndTangents(
        Vector3 corner00, Vector3 corner10, Vector3 corner01, Vector3 corner11,
        Vector3 sphereCenter, float sphereRadius,
        out Vector3 chunkCenter, out Vector3 tangentNorth, out Vector3 tangentEast)
    {
        // Bilinear interpolation at center (u=0.5, v=0.5)
        Vector3 centerFlat = 0.25f * (corner00 + corner10 + corner01 + corner11);
        
        // Project onto sphere
        Vector3 dir = (centerFlat - sphereCenter).normalized;
        chunkCenter = sphereCenter + dir * sphereRadius;

        // Compute tangent plane basis
        // North: roughly towards +Y in world, but orthogonal to sphere normal
        Vector3 up = Vector3.up;
        tangentEast = Vector3.Cross(up, dir).normalized;
        if (tangentEast.sqrMagnitude < 0.001f)
        {
            // Handle pole case
            tangentEast = Vector3.Cross(Vector3.forward, dir).normalized;
        }
        tangentNorth = Vector3.Cross(dir, tangentEast).normalized;
    }

    /// <summary>
    /// Computes the maximum polar distance for a chunk (half diagonal on tangent plane).
    /// Must match CellFileBaking.ComputeMaxPolarDistance exactly.
    /// </summary>
    public static float ComputeMaxPolarDistance(
        Vector3 corner00, Vector3 corner10, Vector3 corner01, Vector3 corner11,
        Vector3 chunkCenter, Vector3 tangentNorth, Vector3 tangentEast)
    {
        float maxDist = 0f;

        // Check all 4 corners
        Vector3 offset = corner00 - chunkCenter;
        float dist = Mathf.Sqrt(
            Vector3.Dot(offset, tangentEast) * Vector3.Dot(offset, tangentEast) +
            Vector3.Dot(offset, tangentNorth) * Vector3.Dot(offset, tangentNorth));
        if (dist > maxDist) maxDist = dist;

        offset = corner10 - chunkCenter;
        dist = Mathf.Sqrt(
            Vector3.Dot(offset, tangentEast) * Vector3.Dot(offset, tangentEast) +
            Vector3.Dot(offset, tangentNorth) * Vector3.Dot(offset, tangentNorth));
        if (dist > maxDist) maxDist = dist;

        offset = corner01 - chunkCenter;
        dist = Mathf.Sqrt(
            Vector3.Dot(offset, tangentEast) * Vector3.Dot(offset, tangentEast) +
            Vector3.Dot(offset, tangentNorth) * Vector3.Dot(offset, tangentNorth));
        if (dist > maxDist) maxDist = dist;

        offset = corner11 - chunkCenter;
        dist = Mathf.Sqrt(
            Vector3.Dot(offset, tangentEast) * Vector3.Dot(offset, tangentEast) +
            Vector3.Dot(offset, tangentNorth) * Vector3.Dot(offset, tangentNorth));
        if (dist > maxDist) maxDist = dist;

        return maxDist;
    }

    /// <summary>
    /// Computes all geometry needed for tree decoding from chunk corners.
    /// </summary>
    public static ChunkGeometry ComputeChunkGeometry(
        Vector3 corner00, Vector3 corner10, Vector3 corner01, Vector3 corner11,
        Vector3 sphereCenter, float sphereRadius)
    {
        ChunkGeometry geo;

        ComputeChunkCenterAndTangents(
            corner00, corner10, corner01, corner11,
            sphereCenter, sphereRadius,
            out geo.chunkCenter, out geo.tangentNorth, out geo.tangentEast);

        geo.maxPolarDistance = ComputeMaxPolarDistance(
            corner00, corner10, corner01, corner11,
            geo.chunkCenter, geo.tangentNorth, geo.tangentEast);

        geo.sphereNormal = (geo.chunkCenter - sphereCenter).normalized;

        return geo;
    }

    // ===== TREE DECODING =====

    /// <summary>
    /// Decodes a compact 6-byte tree instance to world-space values.
    /// Reverses CellFileBaking.QuantizeTree encoding.
    /// </summary>
    /// <param name="tree">Compact tree instance</param>
    /// <param name="geometry">Pre-computed chunk geometry</param>
    /// <param name="sphereCenter">Center of the planet sphere</param>
    /// <param name="sphereRadius">Radius of the planet sphere</param>
    /// <param name="maxHeight">Maximum terrain height for decoding heightOffset</param>
    /// <param name="scaleMin">Minimum scale value (default 0.5)</param>
    /// <param name="scaleMax">Maximum scale value (default 2.0)</param>
    public static DecodedTreeInstance DecodeTree(
        in CellReader.STPTMETreeInstance tree,
        in ChunkGeometry geometry,
        Vector3 sphereCenter,
        float sphereRadius,
        float maxHeight,
        float scaleMin = DEFAULT_SCALE_MIN,
        float scaleMax = DEFAULT_SCALE_MAX)
    {
        // Decode polar coordinates
        float angle = tree.spin / 255f * Mathf.PI * 2f;         // 0-255 → 0-2π
        float radius = tree.distance / 255f * geometry.maxPolarDistance;  // 0-255 → 0-maxDist

        // Convert polar to cartesian on tangent plane (angle is from north, clockwise)
        // Atan2(east, north) was used to encode, so:
        // east = sin(angle) * radius, north = cos(angle) * radius
        float eastComponent = Mathf.Sin(angle) * radius;
        float northComponent = Mathf.Cos(angle) * radius;

        // Compute position on tangent plane
        Vector3 tangentPos = geometry.chunkCenter 
            + geometry.tangentEast * eastComponent 
            + geometry.tangentNorth * northComponent;

        // Decode terrain height from heightOffset ushort: 0-65535 → 0-maxHeight
        float terrainHeight = tree.heightOffset / 65535f * maxHeight;

        // Project back onto sphere surface at (sphereRadius + terrainHeight)
        // Trees should be placed ON TOP of the terrain, not at base sphere radius
        Vector3 dir = (tangentPos - sphereCenter).normalized;
        Vector3 worldPos = sphereCenter + dir * (sphereRadius + terrainHeight);

        // Decode scales: 0-255 → [scaleMin, scaleMax]
        float widthScale = Mathf.Lerp(scaleMin, scaleMax, tree.widthScale / 255f);
        float heightScale = Mathf.Lerp(scaleMin, scaleMax, tree.heightScale / 255f);

        // Decode rotation: 0-255 → 0-2π
        float rotationRad = tree.rotation / 255f * Mathf.PI * 2f;

        return new DecodedTreeInstance
        {
            worldPosition = worldPos,
            widthScale = widthScale,
            heightScale = heightScale,
            rotationRadians = rotationRad,
            prototypeIndex = tree.prototypeIndex
        };
    }

    /// <summary>
    /// Batch decode all trees for a chunk into a pre-allocated array.
    /// Returns the number of trees decoded.
    /// </summary>
    public static int DecodeTreeBatch(
        ArraySegment<CellReader.STPTMETreeInstance> trees,
        DecodedTreeInstance[] output,
        in ChunkGeometry geometry,
        Vector3 sphereCenter,
        float sphereRadius,
        float maxHeight,
        float scaleMin = DEFAULT_SCALE_MIN,
        float scaleMax = DEFAULT_SCALE_MAX)
    {
        if (output == null || output.Length < trees.Count)
            return 0;

        int count = trees.Count;
        for (int i = 0; i < count; i++)
        {
            output[i] = DecodeTree(trees.Array[trees.Offset + i], geometry, sphereCenter, sphereRadius, maxHeight, scaleMin, scaleMax);
        }
        return count;
    }
}
#endif
