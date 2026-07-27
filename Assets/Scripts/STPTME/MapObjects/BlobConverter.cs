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
        
    }
}
