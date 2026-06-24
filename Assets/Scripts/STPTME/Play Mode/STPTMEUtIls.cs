using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CustomTypes;

public static class MeshUtils
{
    /// <summary>
    /// Applies MeshData to a Mesh using WritableMeshData for zero-copy transfer.
    /// Disposes the MeshData after applying.
    /// </summary>
    public static void ApplyMeshData(Mesh mesh, ref ChunkManager.MeshData data)
    {
        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        var writableData = meshDataArray[0];

        writableData.SetVertexBufferParams(data.vertCount,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2)
        );

        writableData.SetIndexBufferParams(data.triCount, IndexFormat.UInt32);

        var vertexData = writableData.GetVertexData<VertexLayout>(0);
        for(int i=0; i<data.vertCount; i++)
        {
            vertexData[i] = new VertexLayout
            {
                position = data.verts[i],
                normal = data.normals[i],
                uv = data.uvs[i]
            };
        }

        var indexData = writableData.GetIndexData<int>();
        NativeArray<int>.Copy(data.tris, 0,indexData, 0, data.triCount);

        writableData.subMeshCount = 1;
        writableData.SetSubMesh(0, new SubMeshDescriptor(0, data.triCount,MeshTopology.Triangles), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

        Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);

        
        mesh.RecalculateBounds();
        data.Dispose();
    }

    /// <summary>
    /// Applies pre-built batch arrays to a Mesh using WritableMeshData.
    /// </summary>
    public static void ApplyBatchToMesh(Mesh mesh,
        NativeArray<Vector3> verts, NativeArray<int> tris, NativeArray<Vector2> uvs,
        NativeArray<Vector3> normals, int vertCount, int triCount,
        NativeArray<Vector4> uv1 = default,
        NativeArray<Vector2> uv2 = default,
        NativeArray<Vector2> uv3 = default,
        NativeArray<Vector2> uv4 = default)
        {
            mesh.Clear();

            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var meshData = meshDataArray[0];

            bool hasUV1 = uv1.IsCreated && uv1.Length >= vertCount;
            bool hasUV2 = uv2.IsCreated && uv2.Length >= vertCount;
            bool hasUV3 = uv3.IsCreated && uv3.Length >= vertCount;
            bool hasUV4 = uv4.IsCreated && uv4.Length >= vertCount;
            bool hasNormals = normals.IsCreated && normals.Length >= vertCount;
            int attributeCount = 1 + (hasNormals ? 1 : 0) + 1 + (hasUV1 ? 1 : 0) + (hasUV2 ? 1 : 0) + (hasUV3 ? 1 : 0) + (hasUV4 ? 1 : 0);

            var attributes = new NativeArray<VertexAttributeDescriptor>(attributeCount, Allocator.Temp);
            int attrIdx = 0;
            int streamIdx = 0;

            // Stream 0: Position
            attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: streamIdx);

            // Stream 1: Normal (optional)
            int normalStreamIdx = -1;
            if (hasNormals)
            {
                streamIdx++;
                normalStreamIdx = streamIdx;
                attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: streamIdx);
            }

            // Stream 2: TexCoord0 + TexCoord2 + TexCoord3 + TexCoord4 interleaved (max 4 streams total)
            streamIdx++;
            int uv0StreamIdx = streamIdx;
            attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, stream: streamIdx);
            if (hasUV2)
            {
                // Same stream as UV0 — interleaved
                attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2, stream: streamIdx);
            }
            if (hasUV3)
            {
                attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 2, stream: streamIdx);
            }
            if (hasUV4)
            {
                // Same stream as UV0/UV2/UV3 — interleaved to stay within 4-stream limit
                attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 2, stream: streamIdx);
            }

            // Stream 3: TexCoord1 (optional)
            int uv1StreamIdx = -1;
            if(hasUV1)
            {
                streamIdx++;
                uv1StreamIdx = streamIdx;
                attributes[attrIdx++] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, stream: streamIdx);
            }

            meshData.SetVertexBufferParams(vertCount, attributes);
            attributes.Dispose();

            meshData.SetIndexBufferParams(triCount, IndexFormat.UInt32);

            var posStream = meshData.GetVertexData<Vector3>(0);
            NativeArray<Vector3>.Copy(verts, 0, posStream, 0, vertCount);

            if (hasNormals)
            {
                var normalStream = meshData.GetVertexData<Vector3>(normalStreamIdx);
                NativeArray<Vector3>.Copy(normals, 0, normalStream, 0, vertCount);
            }

            if (hasUV2 || hasUV3 || hasUV4)
            {
                // UV0, UV2, UV3, and UV4 are interleaved in the same stream.
                var uv0234Stream = meshData.GetVertexData<UV0234Layout>(uv0StreamIdx);
                for (int i = 0; i < vertCount; i++)
                {
                    uv0234Stream[i] = new UV0234Layout
                    {
                        uv0 = uvs[i],
                        uv2 = hasUV2 ? uv2[i] : default,
                        uv3 = hasUV3 ? uv3[i] : default,
                        uv4 = hasUV4 ? uv4[i] : default
                    };
                }
            }
            else
            {
                var uv0Stream = meshData.GetVertexData<Vector2>(uv0StreamIdx);
                NativeArray<Vector2>.Copy(uvs, 0, uv0Stream, 0, vertCount);
            }

            if(hasUV1)
            {
                var uv1Stream = meshData.GetVertexData<Vector4>(uv1StreamIdx);
                NativeArray<Vector4>.Copy(uv1, 0, uv1Stream, 0, vertCount);
            }

            var indexData = meshData.GetIndexData<int>();
            NativeArray<int>.Copy(tris, 0, indexData, 0, triCount);

            meshData.subMeshCount = 1;
            meshData.SetSubMesh(0, new SubMeshDescriptor(0, triCount, MeshTopology.Triangles),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh);

            mesh.RecalculateBounds();
        }

    // =====================================================================
    // TERRAIN CHUNK VERTEX LAYOUT — full channel reference
    //
    // NON-BATCHED chunks  (ApplyMeshData, single interleaved stream, used at LOD0 / close rings)
    // -----------------------------------------------------------------------
    //   Descriptor: VertexLayout  [Position float3 | Normal float3 | UV0 float2]  = 32 B/vert
    //
    //   UV0.xy  Terrain splatmap UV. Computed in STPTMEUtils.ApplyMeshData, written per-vert.
    //           CONSUMED in TerrainCustomShader.shader non-batched (#else) branch:
    //             uv * _UVOffsetScale.zw + _UVOffsetScale.xy  -> splatUV
    //             uv * _NormalUVOffsetScale.zw + ...           -> normalUV
    //           Do NOT repurpose — used every frame in fragment shader.
    //
    // BATCHED chunks  (ApplyBatchToMesh, 4-stream layout, used at LOD1..maxLOD / far rings)
    // -----------------------------------------------------------------------
    //   Stream 0: Position float3                              = 12 B/vert  (VertexAttribute.Position)
    //   Stream 1: Normal   float3                              = 12 B/vert  (VertexAttribute.Normal)
    //   Stream 2: UV0 float2 | UV2 float2 | UV3 float2         = 24 B/vert  (UV023Layout, interleaved)
    //   Stream 3: UV1 float4                                   = 16 B/vert  (VertexAttribute.TexCoord1)
    //                                                 Total:    64 B/vert
    //
    //   UV0.x   Canopy palette index (integer 0-4 stored as float).
    //           Written by ChunkBatcher.Add() per-triangle from decoded tree positions.
    //           Canopy visibility is controlled by UV0.y; UV0.x selects the colour slot.
    //           0-4 = slot in TreePrototypeRegistry.canopyPalette.
    //           CONSUMED in TerrainCustomShader.shader via nointerpolation varying canopyIndex.
    //           Only active on LOD >= TerrainManagementSettings.canopyStartLOD (default LOD1+).
    //
    //   UV0.y   Canopy alpha fade (0-1). Smoothed across vertex neighbours to eliminate pixelation.
    //           Written by ChunkBatcher canopy smoothing pass (Chebyshev neighbor averaging).
    //           CONSUMED in shader via lerp(blended, canopyColor, alpha).
    //           Smooth transitions: full opacity where trees are, soft fade at edges.
    //
    //   UV1.xy  Pre-computed splatmap UV with offset+scale baked per chunk, edges clamped to [1e-5, 1-1e-5].
    //           CONSUMED in TerrainCustomShader vert() batched branch: OUT.splatUV = IN.uv1.xy
    //
    //   UV1.z   Splatmap atlas slice index (row in the Texture2DArray tier chosen by UV1.w).
    //           CONSUMED in shader: OUT.sliceIndex = IN.uv1.z
    //
    //   UV1.w   Splatmap tier (0-3 -> _SplatmapArray_T0 .. _SplatmapArray_T3).
    //           CONSUMED in shader: OUT.tier = IN.uv1.w
    //
    //   UV2.x   Heightmap-normal atlas slice index.
    //           CONSUMED in shader: OUT.normalSliceIndex = IN.uv2.x
    //
    //   UV2.y   Heightmap-normal tier (0-3 -> _NormalmapArray_T0 .. _NormalmapArray_T3).
    //           CONSUMED in shader: OUT.normalTier = IN.uv2.y
    //
    //   UV3.xy  Pre-computed normal UV (offset+scale baked per chunk, clamped).
    //           CONSUMED in shader: OUT.normalUV = IN.uv3
    //
    // SPLATMAP GROUPS:
    //   Group 0 (_SplatmapArray*_T0..T3): terrain layers 0-3 packed as RGBA into one array.
    //   Group 1 (_SplatmapArray1*_T0..T3): terrain layers 4-7 packed as RGBA into a second array.
    //   Layer colours blended by triplanar world-space projection; tiling from _LayerTiling[].
    // =====================================================================

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct VertexLayout
    {
        public Vector3 position;  // World-space XYZ. Written by ApplyMeshData (non-batched stream 0).
        public Vector3 normal;    // World-space surface normal. Written by ApplyMeshData.
        public Vector2 uv;        // UV0: splat UV (non-batched) | x = canopy index, y = canopy alpha (batched)
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct UV023Layout
    {
        public Vector2 uv0;  // x = canopy palette index (0-5), y = canopy alpha fade (0-1) after smoothing
        public Vector2 uv2;  // x = normal atlas slice index, y = normal tier (0-3)
        public Vector2 uv3;  // xy = pre-computed normal UV (offset+scale baked, clamped)
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct UV0234Layout
    {
        public Vector2 uv0;  // x = canopy palette index (0-5), y = canopy enable (0 or 1)
        public Vector2 uv2;  // x = normal atlas slice index, y = normal tier (0-3)
        public Vector2 uv3;  // xy = pre-computed normal UV (offset+scale baked, clamped)
        public Vector2 uv4;  // xy = canopy mask atlas UV for bilinear-sampled mask
    }
}

public static class STPTMEUtils
{

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadFourSBytesFromInt(int packed, out sbyte one, out sbyte two, out sbyte three, out sbyte four)
    {
        one = (sbyte)((packed >> 24) & 0xFF);
        two = (sbyte)((packed >> 16) & 0xFF);
        three = (sbyte)((packed >> 8) & 0xFF);
        four = (sbyte)(packed & 0xFF);
    }

    public static Vector2SByte ReadHeightmapFromPackedInt(int packed)
    {
        return new Vector2SByte((sbyte)((packed >> 24) & 0xFF), (sbyte)((packed >> 16) & 0xFF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WriteFourSBytesInInt(sbyte one, sbyte two, sbyte three, sbyte four)
    {
        return ((one & 0xFF) << 24) | ((two & 0xFF) << 16) | ((three & 0xFF) << 8) | (four & 0xFF);
    }

    public readonly struct RingGeneratorContext
    {
        public readonly int numberOfChunks;
        public readonly sbyte minX;
        public readonly sbyte maxX;
        public readonly int mapsPerRow;
        public readonly int chunksPerMap;
        public readonly int lastChunkIdx;

        public RingGeneratorContext(int numberOfChunks,sbyte minX,sbyte maxX)
        {
            this.numberOfChunks = numberOfChunks;
            this.minX = minX;
            this.maxX = maxX;
            this.mapsPerRow = maxX - minX + 1;
            this.chunksPerMap = numberOfChunks * numberOfChunks;
            this.lastChunkIdx = numberOfChunks - 1;
        }

    }

    private static readonly sbyte[] clockwiseDx = new sbyte[]{1,1,0,-1,-1,-1,0,1};
    private static readonly sbyte[] clockwiseDy = new sbyte[]{0,-1,-1,-1,0,1,1,1};

    // Cross-face transition lookup for cardinal offsets (BFS hot path).
    // Indexed by [(int)face * 4 + dirIdx], dirIdx: 0=dx+1, 1=dx-1, 2=dy+1, 3=dy-1.
    // Along-edge coordinate t = gy when dx!=0, gx when dy!=0.
    private struct CrossFaceRule
    {
        public readonly FaceId targetFace;
        public readonly bool reversed;         // t -> faceSpan-1-t
        public readonly bool tMapsToTargetGx;  // true: gx=f(t), gy=fixed; false: gy=f(t), gx=fixed
        public readonly bool fixedIsMax;       // true: fixed=faceSpan-1; false: fixed=0
        public CrossFaceRule(FaceId tf, bool r, bool g, bool m)
        { targetFace = tf; reversed = r; tMapsToTargetGx = g; fixedIsMax = m; }
    }
    private static readonly CrossFaceRule[] crossFaceRules = {
        //         target            rev    toGx   fixMax
        // Up (face 0)
        new(FaceId.Right,   true,  true,  true),   // right: gx=F-1-t, gy=F-1
        new(FaceId.Left,    false, true,  true),   // left:  gx=t,     gy=F-1
        new(FaceId.Forward, false, true,  true),   // top:   gx=t,     gy=F-1
        new(FaceId.Back,    true,  true,  true),   // bottom:gx=F-1-t, gy=F-1
        // Down (face 1)
        new(FaceId.Right,   false, true,  false),  // right: gx=t,     gy=0
        new(FaceId.Left,    true,  true,  false),  // left:  gx=F-1-t, gy=0
        new(FaceId.Back,    true,  true,  false),  // top:   gx=F-1-t, gy=0
        new(FaceId.Forward, false, true,  false),  // bottom:gx=t,     gy=0
        // Left (face 2)
        new(FaceId.Forward, false, false, false),  // right: gx=0,     gy=t
        new(FaceId.Back,    false, false, true),   // left:  gx=F-1,   gy=t
        new(FaceId.Up,      false, false, false),  // top:   gx=0,     gy=t
        new(FaceId.Down,    true,  false, false),  // bottom:gx=0,     gy=F-1-t
        // Right (face 3)
        new(FaceId.Back,    false, false, false),  // right: gx=0,     gy=t
        new(FaceId.Forward, false, false, true),   // left:  gx=F-1,   gy=t
        new(FaceId.Up,      true,  false, true),   // top:   gx=F-1,   gy=F-1-t
        new(FaceId.Down,    false, false, true),   // bottom:gx=F-1,   gy=t
        // Forward (face 4)
        new(FaceId.Right,   false, false, false),  // right: gx=0,     gy=t
        new(FaceId.Left,    false, false, true),   // left:  gx=F-1,   gy=t
        new(FaceId.Up,      false, true,  true),   // top:   gx=t,     gy=F-1
        new(FaceId.Down,    false, true,  false),  // bottom:gx=t,     gy=0
        // Back (face 5)
        new(FaceId.Left,    false, false, false),  // right: gx=0,     gy=t
        new(FaceId.Right,   false, false, true),   // left:  gx=F-1,   gy=t
        new(FaceId.Up,      true,  true,  false),  // top:   gx=F-1-t, gy=0
        new(FaceId.Down,    true,  true,  true),   // bottom:gx=F-1-t, gy=F-1
    };

    /// <summary>
    /// Computes the cross-face neighbor position for flat-grid BFS table construction.
    /// Used at init time only — not in the BFS hot loop.
    /// </summary>
    public static void GetCrossFaceNeighborFlat(int ruleIndex, int faceSpan, int t,
        out FaceId targetFace, out int targetGx, out int targetGy)
    {
        CrossFaceRule rule = crossFaceRules[ruleIndex];
        int tMapped = rule.reversed ? faceSpan - 1 - t : t;
        int fixedVal = rule.fixedIsMax ? faceSpan - 1 : 0;

        if (rule.tMapsToTargetGx) { targetGx = tMapped; targetGy = fixedVal; }
        else { targetGx = fixedVal; targetGy = tMapped; }

        targetFace = rule.targetFace;
    }

    private static FaceId GetDominantFace(Vector3 cubePoint)
    {
        float absX = Mathf.Abs(cubePoint.x);
        float absY = Mathf.Abs(cubePoint.y);
        float absZ = Mathf.Abs(cubePoint.z);

        if (absY >= absX && absY >= absZ)
            return cubePoint.y >= 0f ? FaceId.Up : FaceId.Down;

        if (absX >= absZ)
            return cubePoint.x >= 0f ? FaceId.Right : FaceId.Left;

        return cubePoint.z >= 0f ? FaceId.Forward : FaceId.Back;
    }

    private static bool TryProjectCubePointToFace(Vector3 cubePoint, FaceId face, out float percentX, out float percentY)
    {
        FaceIdUtility.GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);

        float upDot = Vector3.Dot(cubePoint, localUp);
        if (upDot <= 1e-6f)
        {
            percentX = 0f;
            percentY = 0f;
            return false;
        }

        Vector3 pointOnUnitCube = cubePoint / upDot;
        Vector3 faceOffset = pointOnUnitCube - localUp;

        percentX = 0.5f + (Vector3.Dot(faceOffset, axisA) * 0.5f);
        percentY = 0.5f + (Vector3.Dot(faceOffset, axisB) * 0.5f);
        return true;
    }

    private static bool TryOffsetChunk(ChunkKey start, int dx, int dy, in RingGeneratorContext ctx, out ChunkKey result)
    {
        ReadFourSBytesFromInt(start.packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        return TryOffsetChunkUnpacked(mapX, mapY, chunkX, chunkY, start.face, dx, dy, in ctx, out result, out _);
    }

    private static bool TryOffsetChunkUnpacked(sbyte mapX, sbyte mapY, sbyte chunkX, sbyte chunkY, FaceId face, int dx, int dy, in RingGeneratorContext ctx, out ChunkKey result, out int globalIndex)
    {
        return TryOffsetChunkUnpacked(mapX, mapY, chunkX, chunkY, face, dx, dy,
            ctx.numberOfChunks, ctx.lastChunkIdx, ctx.minX, ctx.maxX, ctx.mapsPerRow, ctx.chunksPerMap,
            out result, out globalIndex);
    }

    public static bool TryOffsetChunkUnpacked(sbyte mapX, sbyte mapY, sbyte chunkX, sbyte chunkY, FaceId face, int dx, int dy,
        int numberOfChunks, int lastChunkIdx, sbyte minX, sbyte maxX, int mapsPerRow, int chunksPerMap,
        out ChunkKey result, out int globalIndex)
    {
        // Fast path: simple integer arithmetic when neighbor stays on the same face
        int nx = chunkX + dx;
        int ny = chunkY + dy;
        sbyte newHX = mapX;
        sbyte newHY = mapY;

        if (nx < 0) { newHX--; nx += numberOfChunks; }
        else if (nx > lastChunkIdx) { newHX++; nx -= numberOfChunks; }

        if (ny < 0) { newHY--; ny += numberOfChunks; }
        else if (ny > lastChunkIdx) { newHY++; ny -= numberOfChunks; }

        if (newHX >= minX && newHX <= maxX && newHY >= minX && newHY <= maxX)
        {
            int mapFlatIndex = (newHX - minX) * mapsPerRow + (newHY - minX);
            int flatIndex = ny * numberOfChunks + nx;
            int globalFlatIndex = mapFlatIndex * chunksPerMap + flatIndex;
            int flatAngularIdx = globalFlatIndex * 6 + (int)face;

            if ((uint)flatAngularIdx < (uint)slotCountRef)
            {
                result = new ChunkKey(WriteFourSBytesInInt(newHX, newHY, (sbyte)nx, (sbyte)ny), face);
                globalIndex = globalFlatIndex;
                return true;
            }
        }

        int faceSpan = mapsPerRow * numberOfChunks;

        // Cardinal cross-face: deterministic integer lookup (no float math)
        if ((newHX < minX || newHX > maxX || newHY < minX || newHY > maxX)
            && (dx == 0 || dy == 0))
        {
            int dirIdx = dx == 1 ? 0 : dx == -1 ? 1 : dy == 1 ? 2 : 3;
            CrossFaceRule rule = crossFaceRules[(int)face * 4 + dirIdx];

            int t = dx != 0
                ? (mapY - minX) * numberOfChunks + chunkY
                : (mapX - minX) * numberOfChunks + chunkX;

            int tMapped = rule.reversed ? faceSpan - 1 - t : t;
            int fixedVal = rule.fixedIsMax ? faceSpan - 1 : 0;

            int tgtGx, tgtGy;
            if (rule.tMapsToTargetGx) { tgtGx = tMapped; tgtGy = fixedVal; }
            else { tgtGx = fixedVal; tgtGy = tMapped; }

            sbyte targetMapX = (sbyte)(minX + tgtGx / numberOfChunks);
            sbyte targetMapY = (sbyte)(minX + tgtGy / numberOfChunks);
            sbyte targetChunkX = (sbyte)(tgtGx % numberOfChunks);
            sbyte targetChunkY = (sbyte)(tgtGy % numberOfChunks);

            int tMapFlatIndex = (targetMapX - minX) * mapsPerRow + (targetMapY - minX);
            int tFlatIndex = targetChunkY * numberOfChunks + targetChunkX;
            int globalFlatIndex = tMapFlatIndex * chunksPerMap + tFlatIndex;
            int flatAngularIdx = globalFlatIndex * 6 + (int)rule.targetFace;

            if ((uint)flatAngularIdx < (uint)slotCountRef)
            {
                result = new ChunkKey(WriteFourSBytesInInt(targetMapX, targetMapY, targetChunkX, targetChunkY), rule.targetFace);
                globalIndex = globalFlatIndex;
                return true;
            }
            result = default;
            globalIndex = -1;
            return false;
        }

        // Diagonal cross-face fallback: cube-space projection

        //This fallback is so rare that removal might be justified. Debug and see if it every happens during play.
        float globalX = ((mapX - minX) * numberOfChunks) + chunkX + 0.5f;
        float globalY = ((mapY - minX) * numberOfChunks) + chunkY + 0.5f;
        float cellPercent = 1f / faceSpan;
        float percentX = (globalX / faceSpan) + (dx * cellPercent);
        float percentY = (globalY / faceSpan) + (dy * cellPercent);

        FaceIdUtility.GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);
        Vector3 cubePoint = localUp
            + (percentX - 0.5f) * 2f * axisA
            + (percentY - 0.5f) * 2f * axisB;

        FaceId targetFace = GetDominantFace(cubePoint);
        if (!TryProjectCubePointToFace(cubePoint, targetFace, out float targetPercentX, out float targetPercentY))
        {
            result = default;
            globalIndex = -1;
            return false;
        }

        int targetGlobalX = Mathf.Clamp(Mathf.FloorToInt(targetPercentX * faceSpan), 0, faceSpan - 1);
        int targetGlobalY = Mathf.Clamp(Mathf.FloorToInt(targetPercentY * faceSpan), 0, faceSpan - 1);
        sbyte targetMapX2 = (sbyte)(minX + (targetGlobalX / numberOfChunks));
        sbyte targetMapY2 = (sbyte)(minX + (targetGlobalY / numberOfChunks));
        sbyte targetChunkX2 = (sbyte)(targetGlobalX % numberOfChunks);
        sbyte targetChunkY2 = (sbyte)(targetGlobalY % numberOfChunks);
        result = new ChunkKey(WriteFourSBytesInInt(targetMapX2, targetMapY2, targetChunkX2, targetChunkY2), targetFace);
        int tMapFlatIndex2 = (targetMapX2 - minX) * mapsPerRow + (targetMapY2 - minX);
        int tFlatIndex2 = targetChunkY2 * numberOfChunks + targetChunkX2;
        globalIndex = tMapFlatIndex2 * chunksPerMap + tFlatIndex2;
        return true;
    }

    // Cached references for fast-path validation — set once before BFS/ring calls
    [System.ThreadStatic] private static int slotCountRef;

    public static HashSet<ChunkKey> GenerateRings(
    int packedCurrentIndices,
    in RingGeneratorContext ctx,
    FaceId currentFace,
    bool onlyNeighbors = false)
    {
        ChunkKey center = new ChunkKey(packedCurrentIndices, currentFace);
        int limit = onlyNeighbors ? 6 : 7;
        int step = onlyNeighbors ? 2 : 1;
        int maxNeighbors = onlyNeighbors ? 4 : 8;
        HashSet<ChunkKey> results = new HashSet<ChunkKey>(maxNeighbors + 1);

        for (int i = 0; i <= limit; i += step)
        {
            if (TryOffsetChunk(center, clockwiseDx[i], clockwiseDy[i], in ctx, out ChunkKey neighbor))
            {
                results.Add(neighbor);
            }
        }

        return results;
    }

    public static HashSet<ChunkKey> GenerateRings(
        int packedCurrentIndices,
        int numberOfChunks,
        sbyte minX,
        sbyte maxX,
        FaceId currentFace,
        bool onlyNeighbors = false)
    {
        var ctx = new RingGeneratorContext(numberOfChunks, minX, maxX);
        return GenerateRings(packedCurrentIndices, in ctx, currentFace, onlyNeighbors);
    }

    /// <summary>
    /// Sets cached validity array for all subsequent TryOffsetChunk and ring calls.
    /// Call once before a batch of BFS/ring operations.
    /// </summary>
    public static void SetFastLookupArrays(int slotCount)
    {
        // All chunks valid in 6-plane system — no array needed.
        // The bounds check in TryOffsetChunkUnpacked only checks index range.
    }

    /// <summary>
    /// Maps a chunk distance to its LOD level using the distance-by-LOD table.
    /// Index = LOD, Value = max distance for that LOD. Returns maxLOD if beyond all entries.
    /// </summary>
    public static byte LODFromDistance(int distanceFromCenter, float[] chunkDistanceByLOD, byte maxLOD)
    {
        if (chunkDistanceByLOD != null)
        {
            for (int lod = 0; lod < chunkDistanceByLOD.Length; lod++)
            {
                if (distanceFromCenter <= chunkDistanceByLOD[lod])
                    return (byte)lod;
            }
        }
        return maxLOD;
    }

    public struct GlobalIndexCalculator
    {
        private readonly int mapsPerRow;
        private readonly int chunksPerMap;
        private readonly int numberOfChunks;
        private readonly sbyte minX;

        public GlobalIndexCalculator(sbyte minX,sbyte maxX,int numberOfChunks)
        {
            this.minX = minX;
            this.mapsPerRow = maxX-minX + 1;
            this.numberOfChunks = numberOfChunks;
            this.chunksPerMap = numberOfChunks * numberOfChunks;
        }

        public int GetIndex(int packed)
        {
            sbyte mapX = (sbyte)(packed>>24 & 0xFF);
            sbyte mapY = (sbyte)(packed>>16 & 0xFF);
            sbyte c = (sbyte)(packed>>8 & 0xFF);
            sbyte d = (sbyte)(packed & 0xFF);

            int mapFlatIndex = (mapX - minX) * mapsPerRow + (mapY - minX);
            int flatIndex = d * numberOfChunks + c;
            return mapFlatIndex * chunksPerMap + flatIndex;
        }

        public int GetIndex(sbyte mapX, sbyte mapY, sbyte c, sbyte d)
        {
            int mapFlatIndex = (mapX - minX) * mapsPerRow + (mapY - minX);
            int flatIndex = d * numberOfChunks + c;
            return mapFlatIndex * chunksPerMap + flatIndex;
        }
    }

    private static int[] chunkVertCountLUT;
    private static int lutMaxLOD = -1;

    public static void InitializeChunkVertCountLUT(int maxLOD)
    {
        if(lutMaxLOD == maxLOD)return;
        lutMaxLOD = maxLOD;
        chunkVertCountLUT = new int[maxLOD+1];
        for(int lod = 0; lod<=maxLOD;lod++)
        {
            if(lod>0)
            {
                int side = (1 << (maxLOD - lod)) + 1;
                chunkVertCountLUT[lod] = side * side;
            }
            else
            {
                chunkVertCountLUT[lod] = 1 << (2 * (maxLOD - lod));
            }
        }
    }

    public static int chunkVertCount(int lod){return chunkVertCountLUT[lod];}

    public static ushort[,] GetHeightsLodUshort(ushort[,] heights, int desiredLOD)
    {
        int heightmapResolutionY = heights.GetLength(0);
        int heightmapResolutionX = heights.GetLength(1);

        int divisionFactor = 1 << desiredLOD; // Faster than Mathf.Pow

        int newResX = heightmapResolutionX / divisionFactor;
        int newResY = heightmapResolutionY / divisionFactor;
        
        int maxSrcX = heightmapResolutionX - 1;
        int maxSrcY = heightmapResolutionY - 1;
        
        ushort[,] newHeights = new ushort[newResY + 1, newResX + 1];
        
        // Main grid (no clamping needed)
        for (int y = 0; y < newResY; y++)
        {
            int srcY = y * divisionFactor;
            for (int x = 0; x < newResX; x++)
            {
                int srcX = x * divisionFactor;
                newHeights[y, x] = heights[srcY, srcX];
            }
        }
        
        // Edge column (x = newResX)
        for (int y = 0; y <= newResY; y++)
        {
            int srcY = y < newResY ? y * divisionFactor : maxSrcY;
            newHeights[y, newResX] = heights[srcY, maxSrcX];
        }
        
        // Edge row (y = newResY), excluding corner already done
        int lastSrcY = maxSrcY;
        for (int x = 0; x < newResX; x++)
        {
            int srcX = x * divisionFactor;
            newHeights[newResY, x] = heights[lastSrcY, srcX];
        }

        return newHeights;
    }

}
    
/*
This is a message left for about a year later (spring 2027)
This code was edited with old AI and it is very likely the AI came up with methods 
that give high overhead and complexity to fix certain issues
Will pass here when doing the performance improvement pass 

Problems that were fixed with AI that might cause overhead:
- The ring generation methods were changed to use a more complex coordinate system and handle edge cases,
because the original simple method did not correctly handle chunks at the edges of planes
  With some effort, we could return to a much simpler deterministic method, there for sure is a formulaic way to determine the neighbors of a chunk without needing to do complex cube-face projections and checks. The current method, while correct, involves a lot of calculations that might be unnecessary if we can find the right way to encode chunk positions and their neighbors.


  This code was reviewed with Opus so it's better now
  On the hot/cold path split: Leaving it as-is is fine. The cold paths (GenerateRings returning a HashSet, the resultingChunkLOD overloads taking ChunkAngularData?[]) run once or twice per chunk change, not 55k times. Porting them to the fast arrays would save microseconds while touching many signatures. Pure maintenance debt, not a performance concern — only worth cleaning if you're doing a broader refactor pass anyway.
  Consider this for later during a broader refactor just for cleanliness
*/
