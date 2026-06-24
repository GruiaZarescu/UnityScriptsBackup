using System;
using CustomTypes;

/// <summary>
/// Per-face rotation that maps a side container's editor-XZ layout to the face's plane (axisA, axisB) coordinates.
///
/// Bake / runtime convention: cells, heightmaps and tree positions are stored in face-plane space
/// (planeX along axisA, planeY along axisB). Editor side containers are flat patches in the world
/// XZ plane laid out in a cross around Top, so the container's worldX/worldZ axes do NOT generally
/// correspond to the face's plane axes — each side needs a 90°/180°/270° rotation, depending on
/// where the user placed it relative to Top.
///
/// Hard-coded for the layout: Top center, Right=−Z, Left=+Z, Forward=+X, Back=−X.
/// If you re-arrange the containers in the editor you must update <see cref="Get"/>.
/// Up and Down are always Identity (Top is the canonical "no rotation" reference; Bottom is
/// handled by the existing Z-mirror in TerrainFaceSwapper, not by this enum).
///
/// Forward (W→P): given container coords (wx, wz) ∈ [0, S], produce plane coords (px, py) ∈ [0, S].
///   Identity:       px = wx,        py = wz
///   Rot180:         px = S − wx,    py = S − wz
///   RotCW:          px = wz,        py = S − wx
///   RotCCW:         px = S − wz,    py = wx
///   MirrorX:        px = S − wx,    py = wz             (flip across Y axis in plane space)
///   MirrorY:        px = wx,        py = S − wz         (flip across X axis in plane space)
///   MirrorDiag:     px = wz,        py = wx             (transpose; flip across px=py diagonal)
///   MirrorAntiDiag: px = S − wz,    py = S − wx         (flip across px+py=S diagonal)
///
/// The 8 values together form the dihedral group D4. Mirror variants are needed when the
/// container layout is mirrored relative to plane space (e.g. side faces that meet the bottom
/// face along a seam need an extra flip perpendicular to that seam).
/// </summary>
public enum FaceContainerOrientation : byte
{
    Identity = 0,
    Rot180 = 1,
    RotCW = 2,
    RotCCW = 3,
    MirrorX = 4,
    MirrorY = 5,
    MirrorDiag = 6,
    MirrorAntiDiag = 7,
}

/// <summary>Pure rotation component of a face container orientation, exposed in the inspector
/// so the user can pick rotation independently from mirror.</summary>
public enum FaceContainerRotation : byte
{
    Identity = 0,
    Rot180 = 1,
    RotCW = 2,
    RotCCW = 3,
}

/// <summary>Optional mirror applied AFTER rotation, in face plane space. Exposed in the
/// inspector as a separate dropdown so the user can flip a side face along its seam with
/// the bottom face without losing the rotation.</summary>
public enum FaceContainerMirror : byte
{
    None = 0,
    /// <summary>Flip plane X axis: (px, py) → (N-1-px, py).</summary>
    MirrorX = 1,
    /// <summary>Flip plane Y axis: (px, py) → (px, N-1-py).</summary>
    MirrorY = 2,
}

public static class FaceContainerOrientations
{
    /// <summary>Compose a rotation + mirror pair (rotation applied first, mirror applied
    /// after in plane space) into the equivalent single <see cref="FaceContainerOrientation"/>.
    /// Composition table is hand-derived; verified for the 8 elements of the dihedral group D4.</summary>
    public static FaceContainerOrientation Compose(FaceContainerRotation rotation, FaceContainerMirror mirror)
    {
        switch (mirror)
        {
            case FaceContainerMirror.None:
                return (FaceContainerOrientation)(byte)rotation;

            case FaceContainerMirror.MirrorX:
                return rotation switch
                {
                    FaceContainerRotation.Identity => FaceContainerOrientation.MirrorX,
                    FaceContainerRotation.Rot180   => FaceContainerOrientation.MirrorY,
                    FaceContainerRotation.RotCW    => FaceContainerOrientation.MirrorAntiDiag,
                    FaceContainerRotation.RotCCW   => FaceContainerOrientation.MirrorDiag,
                    _ => FaceContainerOrientation.MirrorX,
                };

            case FaceContainerMirror.MirrorY:
                return rotation switch
                {
                    FaceContainerRotation.Identity => FaceContainerOrientation.MirrorY,
                    FaceContainerRotation.Rot180   => FaceContainerOrientation.MirrorX,
                    FaceContainerRotation.RotCW    => FaceContainerOrientation.MirrorDiag,
                    FaceContainerRotation.RotCCW   => FaceContainerOrientation.MirrorAntiDiag,
                    _ => FaceContainerOrientation.MirrorY,
                };

            default:
                return (FaceContainerOrientation)(byte)rotation;
        }
    }

    public static FaceContainerOrientation Get(FaceId face) => face switch
    {
        FaceId.Up      => FaceContainerOrientation.Identity,
        FaceId.Down    => FaceContainerOrientation.Identity,
        FaceId.Right   => FaceContainerOrientation.Identity,
        FaceId.Left    => FaceContainerOrientation.Rot180,
        FaceId.Forward => FaceContainerOrientation.RotCW,
        FaceId.Back    => FaceContainerOrientation.RotCCW,
        _ => FaceContainerOrientation.Identity,
    };

    /// <summary>
    /// Map an integer grid coordinate (gx, gy) in container/world axes to the corresponding (px, py) in plane axes.
    /// N is the grid size along one axis (so coords are in [0, N-1]).
    /// </summary>
    public static void GridWorldToPlane(FaceContainerOrientation o, int gx, int gy, int N, out int px, out int py)
    {
        switch (o)
        {
            case FaceContainerOrientation.Identity:       px = gx;         py = gy;         break;
            case FaceContainerOrientation.Rot180:         px = N - 1 - gx; py = N - 1 - gy; break;
            case FaceContainerOrientation.RotCW:          px = gy;         py = N - 1 - gx; break;
            case FaceContainerOrientation.RotCCW:         px = N - 1 - gy; py = gx;         break;
            case FaceContainerOrientation.MirrorX:        px = N - 1 - gx; py = gy;         break;
            case FaceContainerOrientation.MirrorY:        px = gx;         py = N - 1 - gy; break;
            case FaceContainerOrientation.MirrorDiag:     px = gy;         py = gx;         break;
            case FaceContainerOrientation.MirrorAntiDiag: px = N - 1 - gy; py = N - 1 - gx; break;
            default: px = gx; py = gy; break;
        }
    }

    /// <summary>Inverse of <see cref="GridWorldToPlane"/>.</summary>
    public static void GridPlaneToWorld(FaceContainerOrientation o, int px, int py, int N, out int gx, out int gy)
    {
        switch (o)
        {
            case FaceContainerOrientation.Identity:       gx = px;         gy = py;         break;
            case FaceContainerOrientation.Rot180:         gx = N - 1 - px; gy = N - 1 - py; break;
            case FaceContainerOrientation.RotCW:          gx = N - 1 - py; gy = px;         break;
            case FaceContainerOrientation.RotCCW:         gx = py;         gy = N - 1 - px; break;
            // Mirrors are self-inverse.
            case FaceContainerOrientation.MirrorX:        gx = N - 1 - px; gy = py;         break;
            case FaceContainerOrientation.MirrorY:        gx = px;         gy = N - 1 - py; break;
            case FaceContainerOrientation.MirrorDiag:     gx = py;         gy = px;         break;
            case FaceContainerOrientation.MirrorAntiDiag: gx = N - 1 - py; gy = N - 1 - px; break;
            default: gx = px; gy = py; break;
        }
    }

    /// <summary>
    /// Map a normalized [0,1] container-space position (nwx, nwz) to a normalized [0,1] plane-space position.
    /// Used for tree positions (TerrainData.treeInstances stores normalized coordinates).
    /// </summary>
    public static void NormalizedWorldToPlane(FaceContainerOrientation o, float nwx, float nwz, out float npx, out float npz)
    {
        switch (o)
        {
            case FaceContainerOrientation.Identity:       npx = nwx;        npz = nwz;        break;
            case FaceContainerOrientation.Rot180:         npx = 1f - nwx;   npz = 1f - nwz;   break;
            case FaceContainerOrientation.RotCW:          npx = nwz;        npz = 1f - nwx;   break;
            case FaceContainerOrientation.RotCCW:         npx = 1f - nwz;   npz = nwx;        break;
            case FaceContainerOrientation.MirrorX:        npx = 1f - nwx;   npz = nwz;        break;
            case FaceContainerOrientation.MirrorY:        npx = nwx;        npz = 1f - nwz;   break;
            case FaceContainerOrientation.MirrorDiag:     npx = nwz;        npz = nwx;        break;
            case FaceContainerOrientation.MirrorAntiDiag: npx = 1f - nwz;   npz = 1f - nwx;   break;
            default: npx = nwx; npz = nwz; break;
        }
    }

    /// <summary>
    /// Returns a re-oriented copy of <paramref name="src"/> so that downstream code can treat
    /// indices [pz, px] as plane-space (planeY, planeX) instead of container-space (worldZ, worldX).
    /// Identity returns the original array (no copy).
    /// </summary>
    public static float[,] OrientHeights(float[,] src, FaceContainerOrientation o, int res)
    {
        if (o == FaceContainerOrientation.Identity)
            return src;

        float[,] dst = new float[res, res];
        int last = res - 1;
        for (int pz = 0; pz < res; pz++)
        {
            for (int px = 0; px < res; px++)
            {
                int wx, wz;
                switch (o)
                {
                    case FaceContainerOrientation.Rot180:         wx = last - px; wz = last - pz; break;
                    case FaceContainerOrientation.RotCW:          wx = last - pz; wz = px;        break;
                    case FaceContainerOrientation.RotCCW:         wx = pz;        wz = last - px; break;
                    case FaceContainerOrientation.MirrorX:        wx = last - px; wz = pz;        break;
                    case FaceContainerOrientation.MirrorY:        wx = px;        wz = last - pz; break;
                    case FaceContainerOrientation.MirrorDiag:     wx = pz;        wz = px;        break;
                    case FaceContainerOrientation.MirrorAntiDiag: wx = last - pz; wz = last - px; break;
                    default: wx = px; wz = pz; break;
                }
                dst[pz, px] = src[wz, wx];
            }
        }
        return dst;
    }

    /// <summary>
    /// Returns a re-oriented copy of an alphamap so downstream code can treat indices
    /// [planeY, planeX, layer] as face-plane space instead of container/world XZ space.
    /// Identity returns the original array (no copy).
    /// </summary>
    public static float[,,] OrientAlphamaps(float[,,] src, FaceContainerOrientation o, int res, int layers)
    {
        if (o == FaceContainerOrientation.Identity)
            return src;

        float[,,] dst = new float[res, res, layers];
        int last = res - 1;
        for (int py = 0; py < res; py++)
        {
            for (int px = 0; px < res; px++)
            {
                int wx, wz;
                switch (o)
                {
                    case FaceContainerOrientation.Rot180:         wx = last - px; wz = last - py; break;
                    case FaceContainerOrientation.RotCW:          wx = last - py; wz = px;        break;
                    case FaceContainerOrientation.RotCCW:         wx = py;        wz = last - px; break;
                    case FaceContainerOrientation.MirrorX:        wx = last - px; wz = py;        break;
                    case FaceContainerOrientation.MirrorY:        wx = px;        wz = last - py; break;
                    case FaceContainerOrientation.MirrorDiag:     wx = py;        wz = px;        break;
                    case FaceContainerOrientation.MirrorAntiDiag: wx = last - py; wz = last - px; break;
                    default: wx = px; wz = py; break;
                }

                for (int layer = 0; layer < layers; layer++)
                    dst[py, px, layer] = src[wz, wx, layer];
            }
        }

        return dst;
    }
}
