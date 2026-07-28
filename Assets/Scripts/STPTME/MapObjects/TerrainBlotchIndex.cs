using System.Collections.Generic;
using UnityEngine;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>
    /// Per-chunk lookup for terrain-authored blotches, used by ChunkObjectLoader.ProcessBlobs
    /// to decide prefab spawning at chunk-stream time. Fed once by MapContentOrchestrator's
    /// output at scene load — replaces the old CellBlotchQuery, which performed a SECOND,
    /// entirely independent full reload of the same cell files.
    ///
    /// Keyed by (chunkPacked, face), NOT chunkPacked alone: chunkPacked encodes
    /// (mapX, mapY, chunkX, chunkY) but not which cube face those coordinates are on, so two
    /// different faces can share identical chunkPacked values. The old CellBlotchQuery keyed
    /// only by chunkPacked and could silently merge two faces' blotches on a collision — this
    /// fixes that.
    /// </summary>
    public static class TerrainBlotchIndex
    {
        private static Dictionary<(int packed, FaceId face), List<BlotchData>> _byChunk;
        private static readonly List<BlotchData> Empty = new List<BlotchData>();

        public static void SetIndex(Dictionary<(int, FaceId), List<BlotchData>> byChunk)
        {
            _byChunk = byChunk;
        }

        public static List<BlotchData> GetBlobsForChunk(int packed, FaceId face)
        {
            if (_byChunk == null)
            {
                Debug.LogError("[TerrainBlotchIndex] Not initialized — MapContentOrchestrator.Build was never called or ChunkManager didn't call SetIndex.");
                return Empty;
            }
            return _byChunk.TryGetValue((packed, face), out var list) ? list : Empty;
        }

        public static bool IsInitialized => _byChunk != null;
    }
}