using System;
using CustomTypes;
using UnityEngine;

public enum FaceId : byte
{
    Up = 0,
    Down = 1,
    Left = 2,
    Right = 3,
    Forward = 4,
    Back = 5,
}

public readonly struct ChunkKey : IEquatable<ChunkKey>
{
    public readonly int packed;
    public readonly FaceId face;

    public ChunkKey(int packed, FaceId face)
    {
        this.packed = packed;
        this.face = face;
    }

    public bool Equals(ChunkKey other)
    {
        return packed == other.packed && face == other.face;
    }

    public override bool Equals(object obj)
    {
        return obj is ChunkKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return (packed << 3) | (int)face;
    }

    public static bool operator ==(ChunkKey left, ChunkKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ChunkKey left, ChunkKey right)
    {
        return !left.Equals(right);
    }
}

public readonly struct MapFaceKey : IEquatable<MapFaceKey>
{
    public readonly Vector2SByte map;
    public readonly FaceId face;

    public MapFaceKey(Vector2SByte map, FaceId face)
    {
        this.map = map;
        this.face = face;
    }

    public bool Equals(MapFaceKey other)
    {
        return map.Equals(other.map) && face == other.face;
    }

    public override bool Equals(object obj)
    {
        return obj is MapFaceKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(map, (byte)face);
    }

    public static bool operator ==(MapFaceKey left, MapFaceKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(MapFaceKey left, MapFaceKey right)
    {
        return !left.Equals(right);
    }
}

public static class FaceIdUtility
{
    public const int StorageFaceCount = 6;

    public static bool TryToLegacyIsTop(FaceId face, out bool isTop)
    {
        if (face == FaceId.Up)
        {
            isTop = true;
            return true;
        }

        if (face == FaceId.Down)
        {
            isTop = false;
            return true;
        }

        isTop = false;
        return false;
    }

    public static FaceId FromLegacyIsTop(bool isTop)
    {
        return isTop ? FaceId.Up : FaceId.Down;
    }

    public static int GetStorageFaceColumn(FaceId face)
    {
        return (int)face;
    }

    public static int GetStorageIndex(int globalIndex, FaceId face)
    {
        return (globalIndex * StorageFaceCount) + GetStorageFaceColumn(face);
    }

    public static string GetFilePrefix(FaceId face)
    {
        return face switch
        {
            FaceId.Up => "top",
            FaceId.Down => "bottom",
            FaceId.Left => "left",
            FaceId.Right => "right",
            FaceId.Forward => "forward",
            FaceId.Back => "back",
            _ => throw new ArgumentOutOfRangeException(nameof(face), face, null),
        };
    }

    public static Vector3 GetLocalUp(FaceId face)
    {
        return face switch
        {
            FaceId.Up => Vector3.up,
            FaceId.Down => Vector3.down,
            FaceId.Left => Vector3.left,
            FaceId.Right => Vector3.right,
            FaceId.Forward => Vector3.forward,
            FaceId.Back => Vector3.back,
            _ => throw new ArgumentOutOfRangeException(nameof(face), face, null),
        };
    }

    public static void GetFaceAxes(FaceId face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB)
    {
        localUp = GetLocalUp(face);
        switch (face)
        {
            case FaceId.Up:
                axisA = Vector3.right;
                axisB = Vector3.forward;
                break;
            case FaceId.Down:
                axisA = Vector3.right;
                axisB = Vector3.back;
                break;
            case FaceId.Left:
                axisA = Vector3.forward;
                axisB = Vector3.up;
                break;
            case FaceId.Right:
                axisA = Vector3.back;
                axisB = Vector3.up;
                break;
            case FaceId.Forward:
                axisA = Vector3.right;
                axisB = Vector3.up;
                break;
            case FaceId.Back:
                axisA = Vector3.left;
                axisB = Vector3.up;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(face), face, null);
        }
    }

    public static FaceId GetClosestFace(Vector3 worldPosition, Vector3 sphereCenter)
    {
        Vector3 direction = worldPosition - sphereCenter;
        if (direction.sqrMagnitude <= 1e-12f)
            return FaceId.Up;

        direction.Normalize();

        FaceId bestFace = FaceId.Up;
        float bestDot = float.NegativeInfinity;
        for (int i = 0; i < StorageFaceCount; i++)
        {
            FaceId face = (FaceId)i;
            float dot = Vector3.Dot(direction, GetLocalUp(face));
            if (dot > bestDot)
            {
                bestDot = dot;
                bestFace = face;
            }
        }

        return bestFace;
    }

    public static bool TryProjectWorldPointToFacePlane(Vector3 worldPosition, FaceId face, float faceSize, Vector3 sphereCenter, out Vector2 planePosition)
    {
        planePosition = default;

        Vector3 direction = worldPosition - sphereCenter;
        if (direction.sqrMagnitude <= 1e-12f || faceSize <= 0f)
            return false;

        direction.Normalize();
        GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);

        float upDot = Vector3.Dot(direction, localUp);
        if (upDot <= 1e-5f)
            return false;

        Vector3 pointOnUnitCube = direction / upDot;
        Vector3 faceOffset = pointOnUnitCube - localUp;

        float percentX = 0.5f + (Vector3.Dot(faceOffset, axisA) * 0.5f);
        float percentY = 0.5f + (Vector3.Dot(faceOffset, axisB) * 0.5f);

        const float tolerance = 1e-4f;
        if (percentX < -tolerance || percentX > 1f + tolerance || percentY < -tolerance || percentY > 1f + tolerance)
            return false;

        planePosition = new Vector2(
            Mathf.Clamp01(percentX) * faceSize,
            Mathf.Clamp01(percentY) * faceSize);
        return true;
    }

    public static Vector3 ProjectFacePlanePoint(FaceId face, float planeX, float planeY, float faceSize, Vector3 sphereCenter, float sphereRadius)
    {
        GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);

        float percentX = faceSize > 0f ? planeX / faceSize : 0f;
        float percentY = faceSize > 0f ? planeY / faceSize : 0f;

        Vector3 pointOnUnitCube = localUp
            + (percentX - 0.5f) * 2f * axisA
            + (percentY - 0.5f) * 2f * axisB;

        return sphereCenter + pointOnUnitCube.normalized * sphereRadius;
    }
}