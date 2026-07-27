using UnityEngine;
using CustomTypes;

/// <summary>
/// Single shared implementation of "world position → face/cell/chunk address" for
/// standalone map objects. Used identically by MapObjectBaker (bake-time export) and
/// LiveDatabaseObjectSource (editor-time live streaming) so the two paths can never
/// silently disagree about which chunk an object belongs to.
/// </summary>
public static class MapObjectChunkMath
{
    public struct ChunkAddress
    {
        public FaceId face;
        public sbyte heightmapX, heightmapY;
        public sbyte chunkX, chunkY;
        public int packed;
    }

    public static bool TryResolve(
        Vector3 worldPosition,
        Vector3 sphereCenter,
        float chunkSize,      // terrainSize / tilingFactor
        float faceWorldSize,
        int numberOfChunks,
        sbyte minX, sbyte maxX,
        out ChunkAddress address)
    {
        address = default;

        FaceId face = FaceIdUtility.GetClosestFace(worldPosition, sphereCenter);
        if (!FaceIdUtility.TryProjectWorldPointToFacePlane(worldPosition, face, faceWorldSize, sphereCenter, out Vector2 plane))
            return false;

        int faceSpanInChunks = (maxX - minX + 1) * numberOfChunks;
        int globalChunkX = Mathf.Clamp(Mathf.FloorToInt(plane.x / chunkSize), 0, faceSpanInChunks - 1);
        int globalChunkY = Mathf.Clamp(Mathf.FloorToInt(plane.y / chunkSize), 0, faceSpanInChunks - 1);

        sbyte heightmapX = (sbyte)(minX + (globalChunkX / numberOfChunks));
        sbyte heightmapY = (sbyte)(minX + (globalChunkY / numberOfChunks));
        sbyte chunkX = (sbyte)(globalChunkX % numberOfChunks);
        sbyte chunkY = (sbyte)(globalChunkY % numberOfChunks);

        address = new ChunkAddress
        {
            face = face,
            heightmapX = heightmapX, heightmapY = heightmapY,
            chunkX = chunkX, chunkY = chunkY,
            packed = STPTMEUtils.WriteFourSBytesInInt(heightmapX, heightmapY, chunkX, chunkY)
        };
        return true;
    }
}