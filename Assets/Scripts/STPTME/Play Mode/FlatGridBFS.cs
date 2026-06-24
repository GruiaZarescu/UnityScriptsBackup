using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Precomputed flat-grid BFS over the 6-face cube-sphere chunk grid.
/// All 6 faces are mapped into a contiguous flat array where neighbor lookups
/// are a single array read — no coordinate math at runtime.
///
/// Layout: flatIndex = face * faceArea + gy * faceSpan + gx
///   where gx = (mapX - minX) * numberOfChunks + chunkX
///         gy = (mapY - minX) * numberOfChunks + chunkY
/// </summary>
public class FlatGridBFS
{
    public readonly int faceSpan;       // chunks per face side = mapsPerRow * numberOfChunks
    public readonly int faceArea;       // faceSpan * faceSpan
    public readonly int totalCells;     // 6 * faceArea

    private readonly int numberOfChunks;
    private readonly sbyte minX;

    // Precomputed neighbor table: neighbors[i*8+d] = flat index of geometric neighbor, or -1
    // Directions: d=0 right(+gx), d=1 left(-gx), d=2 up(+gy), d=3 down(-gy)
    //             d=4 right-up, d=5 right-down, d=6 left-up, d=7 left-down
    private readonly int[] neighbors;

    // Precomputed conversion tables
    private readonly int[] flatToPacked;      // flat index → packed ChunkKey int
    private readonly int[] flatToStorage;     // flat index → storage index (globalIndex*6+face)
    private readonly byte[] flatToFace;       // flat index → (byte)FaceId (avoids division in GetFace)
    private readonly bool[] flatValid;        // flat index → chunk validity

    // Reusable BFS containers (allocated once, reused every call)
    private readonly int[] queueBuffer;
    public readonly int[] resultBuffer;
    private readonly bool[] visited;
    private readonly int[] depthBuffer;    // BFS depth per cell (set during RunBFS)
    public int resultCount;

    public FlatGridBFS(int numberOfChunks, sbyte minX, sbyte maxX)
    {
        this.numberOfChunks = numberOfChunks;
        this.minX = minX;
        int mapsPerRow = maxX - minX + 1;
        faceSpan = mapsPerRow * numberOfChunks;
        faceArea = faceSpan * faceSpan;
        totalCells = 6 * faceArea;
        int chunksPerMap = numberOfChunks * numberOfChunks;

        // Build conversion tables
        flatToPacked = new int[totalCells];
        flatToStorage = new int[totalCells];
        flatToFace = new byte[totalCells];
        flatValid = new bool[totalCells];

        for (int face = 0; face < 6; face++)
        {
            int faceOffset = face * faceArea;
            for (int gy = 0; gy < faceSpan; gy++)
            {
                int mapIdxY = gy / numberOfChunks;
                int chunkY = gy - mapIdxY * numberOfChunks;
                sbyte sMapY = (sbyte)(minX + mapIdxY);

                for (int gx = 0; gx < faceSpan; gx++)
                {
                    int mapIdxX = gx / numberOfChunks;
                    int chunkX = gx - mapIdxX * numberOfChunks;
                    sbyte sMapX = (sbyte)(minX + mapIdxX);

                    int flatIdx = faceOffset + gy * faceSpan + gx;
                    flatToPacked[flatIdx] = STPTMEUtils.WriteFourSBytesInInt(sMapX, sMapY, (sbyte)chunkX, (sbyte)chunkY);

                    int mapFlatIndex = mapIdxX * mapsPerRow + mapIdxY;
                    int chunkFlatIndex = chunkY * numberOfChunks + chunkX;
                    int globalIndex = mapFlatIndex * chunksPerMap + chunkFlatIndex;
                    flatToStorage[flatIdx] = globalIndex * 6 + face;
                }
            }
        }

        for (int i = 0; i < totalCells; i++)
        {
            flatValid[i] = true;
            flatToFace[i] = (byte)(i / faceArea);
        }

        // Build neighbor table (geometric adjacency, validity checked at BFS time)
        neighbors = new int[totalCells * 8];
        BuildNeighborTable();

        // Allocate BFS containers
        queueBuffer = new int[totalCells];
        resultBuffer = new int[totalCells];
        visited = new bool[totalCells];
        depthBuffer = new int[totalCells];
    }

    private void BuildNeighborTable()
    {
        for (int i = 0; i < neighbors.Length; i++)
            neighbors[i] = -1;

        for (int face = 0; face < 6; face++)
        {
            int faceOffset = face * faceArea;

            for (int gy = 0; gy < faceSpan; gy++)
            {
                for (int gx = 0; gx < faceSpan; gx++)
                {
                    int flatIdx = faceOffset + gy * faceSpan + gx;
                    int nbBase = flatIdx * 8;

                    bool hasRight = gx + 1 < faceSpan;
                    bool hasLeft  = gx > 0;
                    bool hasUp    = gy + 1 < faceSpan;
                    bool hasDown  = gy > 0;

                    // d=0: right (gx+1)
                    if (hasRight)
                        neighbors[nbBase] = flatIdx + 1;
                    else
                        neighbors[nbBase] = GetCrossFaceNeighbor((FaceId)face, gx, gy, 1, 0);

                    // d=1: left (gx-1)
                    if (hasLeft)
                        neighbors[nbBase + 1] = flatIdx - 1;
                    else
                        neighbors[nbBase + 1] = GetCrossFaceNeighbor((FaceId)face, gx, gy, -1, 0);

                    // d=2: up (gy+1)
                    if (hasUp)
                        neighbors[nbBase + 2] = flatIdx + faceSpan;
                    else
                        neighbors[nbBase + 2] = GetCrossFaceNeighbor((FaceId)face, gx, gy, 0, 1);

                    // d=3: down (gy-1)
                    if (hasDown)
                        neighbors[nbBase + 3] = flatIdx - faceSpan;
                    else
                        neighbors[nbBase + 3] = GetCrossFaceNeighbor((FaceId)face, gx, gy, 0, -1);

                    // d=4: right-up (gx+1, gy+1) — only within same face
                    if (hasRight && hasUp)
                        neighbors[nbBase + 4] = flatIdx + 1 + faceSpan;

                    // d=5: right-down (gx+1, gy-1)
                    if (hasRight && hasDown)
                        neighbors[nbBase + 5] = flatIdx + 1 - faceSpan;

                    // d=6: left-up (gx-1, gy+1)
                    if (hasLeft && hasUp)
                        neighbors[nbBase + 6] = flatIdx - 1 + faceSpan;

                    // d=7: left-down (gx-1, gy-1)
                    if (hasLeft && hasDown)
                        neighbors[nbBase + 7] = flatIdx - 1 - faceSpan;
                }
            }
        }
    }

    private int GetCrossFaceNeighbor(FaceId face, int gx, int gy, int dx, int dy)
    {
        int dirIdx = dx == 1 ? 0 : dx == -1 ? 1 : dy == 1 ? 2 : 3;
        int t = dx != 0 ? gy : gx;

        STPTMEUtils.GetCrossFaceNeighborFlat((int)face * 4 + dirIdx, faceSpan, t,
            out FaceId targetFace, out int targetGx, out int targetGy);

        if (targetGx < 0 || targetGx >= faceSpan || targetGy < 0 || targetGy >= faceSpan)
            return -1;

        return (int)targetFace * faceArea + targetGy * faceSpan + targetGx;
    }

    /// <summary>
    /// Runs BFS from the given flat index. Results are in resultBuffer[0..resultCount-1],
    /// ordered by BFS distance from start (nearest first).
    /// depthBuffer[flatIdx] contains the BFS depth (Chebyshev distance) for each visited cell.
    /// </summary>
    public void RunBFS(int startFlatIdx)
    {
        Array.Clear(visited, 0, totalCells);
        int qHead = 0, qTail = 0;
        resultCount = 0;

        visited[startFlatIdx] = true;
        depthBuffer[startFlatIdx] = 0;
        queueBuffer[qTail++] = startFlatIdx;

        while (qHead < qTail)
        {
            int current = queueBuffer[qHead++];
            resultBuffer[resultCount++] = current;
            int nextDepth = depthBuffer[current] + 1;

            int nbBase = current * 8;
            int nb0 = neighbors[nbBase];
            int nb1 = neighbors[nbBase + 1];
            int nb2 = neighbors[nbBase + 2];
            int nb3 = neighbors[nbBase + 3];
            int nb4 = neighbors[nbBase + 4];
            int nb5 = neighbors[nbBase + 5];
            int nb6 = neighbors[nbBase + 6];
            int nb7 = neighbors[nbBase + 7];

            if (nb0 >= 0 && !visited[nb0] && flatValid[nb0]) { visited[nb0] = true; depthBuffer[nb0] = nextDepth; queueBuffer[qTail++] = nb0; }
            if (nb1 >= 0 && !visited[nb1] && flatValid[nb1]) { visited[nb1] = true; depthBuffer[nb1] = nextDepth; queueBuffer[qTail++] = nb1; }
            if (nb2 >= 0 && !visited[nb2] && flatValid[nb2]) { visited[nb2] = true; depthBuffer[nb2] = nextDepth; queueBuffer[qTail++] = nb2; }
            if (nb3 >= 0 && !visited[nb3] && flatValid[nb3]) { visited[nb3] = true; depthBuffer[nb3] = nextDepth; queueBuffer[qTail++] = nb3; }
            if (nb4 >= 0 && !visited[nb4] && flatValid[nb4]) { visited[nb4] = true; depthBuffer[nb4] = nextDepth; queueBuffer[qTail++] = nb4; }
            if (nb5 >= 0 && !visited[nb5] && flatValid[nb5]) { visited[nb5] = true; depthBuffer[nb5] = nextDepth; queueBuffer[qTail++] = nb5; }
            if (nb6 >= 0 && !visited[nb6] && flatValid[nb6]) { visited[nb6] = true; depthBuffer[nb6] = nextDepth; queueBuffer[qTail++] = nb6; }
            if (nb7 >= 0 && !visited[nb7] && flatValid[nb7]) { visited[nb7] = true; depthBuffer[nb7] = nextDepth; queueBuffer[qTail++] = nb7; }
        }
    }

    /// <summary>
    /// Runs depth-limited BFS from the given flat index. Only visits cells up to maxDepth
    /// steps from start. Results are in resultBuffer[0..resultCount-1].
    /// depthBuffer[flatIdx] contains the BFS depth for each visited cell.
    /// </summary>
    public void RunBFS(int startFlatIdx, int maxDepth)
    {
        Array.Clear(visited, 0, totalCells);
        int qHead = 0, qTail = 0;
        resultCount = 0;

        visited[startFlatIdx] = true;
        depthBuffer[startFlatIdx] = 0;
        queueBuffer[qTail++] = startFlatIdx;

        while (qHead < qTail)
        {
            int current = queueBuffer[qHead++];
            resultBuffer[resultCount++] = current;
            int currentDepth = depthBuffer[current];

            if (currentDepth >= maxDepth)
                continue; // don't expand neighbors beyond max depth

            int nextDepth = currentDepth + 1;

            int nbBase = current * 8;
            int nb0 = neighbors[nbBase];
            int nb1 = neighbors[nbBase + 1];
            int nb2 = neighbors[nbBase + 2];
            int nb3 = neighbors[nbBase + 3];
            int nb4 = neighbors[nbBase + 4];
            int nb5 = neighbors[nbBase + 5];
            int nb6 = neighbors[nbBase + 6];
            int nb7 = neighbors[nbBase + 7];

            if (nb0 >= 0 && !visited[nb0] && flatValid[nb0]) { visited[nb0] = true; depthBuffer[nb0] = nextDepth; queueBuffer[qTail++] = nb0; }
            if (nb1 >= 0 && !visited[nb1] && flatValid[nb1]) { visited[nb1] = true; depthBuffer[nb1] = nextDepth; queueBuffer[qTail++] = nb1; }
            if (nb2 >= 0 && !visited[nb2] && flatValid[nb2]) { visited[nb2] = true; depthBuffer[nb2] = nextDepth; queueBuffer[qTail++] = nb2; }
            if (nb3 >= 0 && !visited[nb3] && flatValid[nb3]) { visited[nb3] = true; depthBuffer[nb3] = nextDepth; queueBuffer[qTail++] = nb3; }
            if (nb4 >= 0 && !visited[nb4] && flatValid[nb4]) { visited[nb4] = true; depthBuffer[nb4] = nextDepth; queueBuffer[qTail++] = nb4; }
            if (nb5 >= 0 && !visited[nb5] && flatValid[nb5]) { visited[nb5] = true; depthBuffer[nb5] = nextDepth; queueBuffer[qTail++] = nb5; }
            if (nb6 >= 0 && !visited[nb6] && flatValid[nb6]) { visited[nb6] = true; depthBuffer[nb6] = nextDepth; queueBuffer[qTail++] = nb6; }
            if (nb7 >= 0 && !visited[nb7] && flatValid[nb7]) { visited[nb7] = true; depthBuffer[nb7] = nextDepth; queueBuffer[qTail++] = nb7; }
        }
    }

    /// <summary>
    /// Returns the BFS depth (grid distance) from the last RunBFS start to the given flat index.
    /// Only valid for cells visited during the most recent RunBFS call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBFSDepth(int flatIdx) => depthBuffer[flatIdx];

    /// <summary>
    /// Returns true if the given flat index was visited during the most recent RunBFS call.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WasVisited(int flatIdx) => visited[flatIdx];

    /// <summary>
    /// Computes Chebyshev distance between two flat indices.
    /// Same face: max(|dx|, |dy|). Cross face: falls back to BFS depth sum (approximate).
    /// </summary>
    public int ChebyshevDistance(int flatIdxA, int flatIdxB)
    {
        int faceA = flatIdxA / faceArea;
        int faceB = flatIdxB / faceArea;

        int localA = flatIdxA - faceA * faceArea;
        int localB = flatIdxB - faceB * faceArea;

        int gxA = localA % faceSpan;
        int gyA = localA / faceSpan;
        int gxB = localB % faceSpan;
        int gyB = localB / faceSpan;

        if (faceA == faceB)
        {
            int dx = gxA - gxB;
            int dy = gyA - gyB;
            if (dx < 0) dx = -dx;
            if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }

        // Cross-face: use BFS depth as best available approximation
        return depthBuffer[flatIdxB];
    }

    /// <summary>Converts a ChunkKey to a flat grid index.</summary>
    public int ChunkKeyToFlat(int packed, FaceId face)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        int gx = (mapX - minX) * numberOfChunks + chunkX;
        int gy = (mapY - minX) * numberOfChunks + chunkY;
        return (int)face * faceArea + gy * faceSpan + gx;
    }

    /// <summary>Gets the packed ChunkKey int for a flat grid index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPacked(int flatIdx) => flatToPacked[flatIdx];

    /// <summary>Gets the FaceId for a flat grid index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FaceId GetFace(int flatIdx) => (FaceId)flatToFace[flatIdx];

    /// <summary>Gets the storage index (globalIndex*6+face) for a flat grid index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetStorageIndex(int flatIdx) => flatToStorage[flatIdx];
}

//Maybe BFS could be even further improved, but it's already very fast. Leave for later