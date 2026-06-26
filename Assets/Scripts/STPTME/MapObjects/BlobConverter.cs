using UnityEngine;
using System.Collections.Generic;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Converts between BlotchData and object-based representation (cell objects).
    /// Used by ChunkObjectLoader to handle mixed data sources.
    /// 
    /// A blob can represent either:
    /// 1. A single tree instance (radius=0, density=1) → Convert to pseudo cell-object
    /// 2. A cluster (radius>0, density>1) → Keep as blob for GPU expansion
    /// </summary>
    public static class BlobConverter
    {
        /// <summary>
        /// Pseudo cell object created from a single-instance blob.
        /// Bridges blob data into the object loader pipeline.
        /// </summary>
        public struct PseudoCellObject
        {
            public int chunkPacked;
            public byte prototypeIndex;
            public Vector3 worldPosition;
            public float rotation;      // degrees
            public float scale;
            public uint seed;

            public static PseudoCellObject FromBlob(BlotchData blob, Vector3 worldPos)
            {
                return new PseudoCellObject
                {
                    chunkPacked = blob.chunkPacked,
                    prototypeIndex = blob.PrototypeIndex,
                    worldPosition = worldPos,
                    rotation = 0f,      // Blobs don't store per-instance rotation
                    scale = 1f,         // Blobs use density, not per-instance scale
                    seed = blob.Seed
                };
            }
        }

        /// <summary>
        /// Determines if a blob represents a single instance (not a cluster).
        /// Single instances should be converted to objects at LOD0 if the prototype
        /// requires GameObjects for that LOD.
        /// </summary>
        public static bool IsSingleInstance(BlotchData blob)
        {
            // Single instance: radius=0 (no cluster), density=1 (one instance)
            return blob.RadiusMeters < 0.01f && blob.DensityPerSqM < 1.5f;
        }

        /// <summary>
        /// Determines if a blob should be processed as a cluster (GPU expansion).
        /// Clusters are either:
        /// - Large radius (procedural distribution)
        /// - High density (grass, undergrowth)
        /// </summary>
        public static bool IsCluster(BlotchData blob)
        {
            return !IsSingleInstance(blob);
        }

        /// <summary>
        /// Calculates world position from blob's quantized local coordinates.
        /// Converts face-local plane coordinates to world space on the sphere surface.
        /// </summary>
        /*public static Vector3 CalculateBlotchWorldPosition(
        BlotchData blob,
        Vector3 sphereCenter,
        float sphereRadius,
        int numberOfChunks,
        sbyte minX,
        float faceWorldSize,       // NEW: Added to fix projection
        float chunkSize = 75f)
        {
            // Extract local plane coordinates from packed position
            blob.GetLocalPosition(chunkSize, out float localX, out float localZ);

            // Extract chunk grid coordinates from packed value
            STPTMEUtils.ReadFourSBytesFromInt(blob.chunkPacked,
                out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);

            // Calculate cell size (one cell contains numberOfChunks * numberOfChunks chunks)
            float cellSize = numberOfChunks * chunkSize;

            // Calculate absolute position on the face plane:
            // 1. (mapX - minX) * cellSize  -> Offset to the correct Cell on the face
            // 2. chunkX * chunkSize        -> Offset to the correct Chunk inside that Cell
            // 3. localX                    -> Exact offset inside that Chunk
            float worldPlaneX = (mapX - minX) * cellSize + chunkX * chunkSize + localX;
            float worldPlaneZ = (mapY - minX) * cellSize + chunkY * chunkSize + localZ;
            Debug.Log($"Obtained worldPlaneX from mapX {mapX}, minX {minX}, cellSize {cellSize}, chunkX {chunkX}, chunkSize {chunkSize}, localX {localX} => worldPlaneX {worldPlaneX}");
            Debug.Log($"Obtained worldPlaneZ from mapY {mapY}, minX {minX}, cellSize {cellSize}, chunkY {chunkY}, chunkSize {chunkSize}, localZ {localZ} => worldPlaneZ {worldPlaneZ}");

            // Project the 2D plane coordinate onto the 3D sphere
            var worldPos = FaceIdUtility.ProjectFacePlanePoint(
                blob.Face,
                worldPlaneX,
                worldPlaneZ,
                faceWorldSize,           // FIXED: Pass true face size, not cellSize
                sphereCenter,
                sphereRadius);
            Debug.Log($"Obtained worldPos from parameters blob face {blob.Face}, worldPlaneX {worldPlaneX}, worldPlaneZ {worldPlaneZ}, faceWorldSize {faceWorldSize}, sphereCenter {sphereCenter}, sphereRadius {sphereRadius} => worldPos {worldPos}");
            return worldPos;
        }*/
    }
}
