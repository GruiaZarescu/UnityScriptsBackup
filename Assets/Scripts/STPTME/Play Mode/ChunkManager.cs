using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections;
using CustomTypes;
using Unity.Collections;

public struct ChunkAngularData
{
    public Vector3 centerDir;
    public Vector3 n0, n1, n2, n3; // plane normals
    public float minDot;
    public ChunkAngularData(Vector3 centerDir, Vector3 n0, Vector3 n1, Vector3 n2, Vector3 n3,float minDot)
    {
        this.centerDir = centerDir;
        this.n0 = n0; this.n1 = n1; this.n2 = n2; this.n3 = n3;
        this.minDot =  minDot;
    }
}

/// <summary>
/// Central chunk management: player tracking, generation scheduling, gen version control.
/// Mesh gen delegated to ChunkGenerator and GameObject management delegated to ChunkRegistry. (left to implement)
/// </summary>
public class ChunkManager : MonoBehaviour
{
    public static ChunkManager Instance { get; private set; }
    public ChunkRegistry chunkRegistry;

    private TerrainManagementSettings settings;
    [SerializeField] private GameObject character;
    [Tooltip("Main game camera. Used by VisibilitySystem for frustum culling. If left empty, falls back to Camera.main (must be tagged MainCamera).")]
    [SerializeField] private Camera mainCamera;
    [SerializeField] private Transform chunkPool;
    [SerializeField] private Material tempMat;
    [SerializeField] private Shader terrainShader;
    [SerializeField] private bool enableTextureGeneration = true;
    [SerializeField] private bool shouldGenerateOuterChunks;
    [Tooltip("Index = LOD level, Value = max chunk distance at which that LOD is used. Must be sorted ascending. Beyond the last entry's distance, maxLOD is used.")]
    [SerializeField] private float[] chunkDistanceByLOD;
    [SerializeField] private MapObjectPrototypeRegistry mapObjectRegistry;

    public int CurrentPackedChunk => packedCurrentIndices;
    public FaceId CurrentPackedChunkFace => packedCurrentFace;

    // Legacy accessors (used by ChunkRegistry canopy path — stubs until fully ported)
    public TreePrototypeRegistry TreePrototypes => null;
    public ArraySegment<TreeDecoder.DecodedTreeInstance> GetDecodedTreesForChunk(int packed, FaceId face) => default;

    //Gen state tracking
    public int chunkGenerationVersion = 0;

    //Player tracking
    private bool playerInsideChunkBuffer = false;
    private float chunkLoadingBufferDistance = 5f;
    private int packedCurrentIndices;
    private FaceId packedCurrentFace = FaceId.Up;
    private int packedPreviousIndices;
    private FaceId packedPreviousFace = FaceId.Up;
    private int bufferOriginChunkIndices;
    private FaceId bufferOriginChunkFace = FaceId.Up;

    private Vector3 _lastFramePosition;
    private Vector3 previousChunkLastPosition;

    private bool movingFast;
    private float movingFastSpeed;

    //Weltconfig

    private byte maxLOD;
    private ushort maxVertsPerOuterChunkMesh;
    private ushort maxChunkGenWorkPerFrame;
    private byte maxChunkGenOpsPerFrame;
    private int tilingFactor;
    private int subdivisionsPowerOf2;
    private int heightmapSubdivisions;
    private int numberOfChunks;
    private int originalResolution;
    private int chunkStep;
    private sbyte minX, maxX;
    private float terrainSize;
    private float sphereRadius;
    private float maxHeight;
    private float treeMaxHeight; // maxHeight used during tree baking (settings.maxHeight)
    private Vector3 sphereCenter;
    private int nonBatchedOuterChunkRings;
    private bool debugDisableBatching;
    private int numberOfTerrains;

    /// <summary>Total storage slots across all faces (= totalChunkCount * 6).</summary>
    private int totalStorageSlots;

    //Misc
    private ChunkAngularData?[] angularChunkData;

    // Parallel SoA arrays for VisibilitySystem hot-path queries. Indexed by storage slot.
    // Populated in InitializeAdjacentData; ownership handed to
    // the VisibilitySystem during Awake so it can query without going through the
    // boxed `ChunkAngularData?[]`.
    private Vector3[] visCenterDir;
    private float[] visCosThetaC;
    private float[] visSinThetaC;
    // Vertical bound: center placed at (minH+maxH)/2 above the sphere surface ("alt"),
    // half-extent = (maxH-minH)/2 + TREE_MARGIN/2. Replaces the old visHalfMaxH which
    // (incorrectly) assumed terrain spanned [0..maxH], producing ~1km bounds for
    // chunks on plateaus.
    private float[] visBoundCenterAlt;
    private float[] visBoundHalfH;

    // Player altitude above the reference sphere surface (= |pos - center| - R).
    // Updated each frame in Update(); canonical source for any consumer (FPS HUD,
    // VisibilitySystem). Replaces the hardcoded 8162f calc previously in FPSCounter.
    public float PlayerAltitude { get; private set; }
    
    // Flat arrays indexed by (face * totalMapsPerFace + mapIdx) where
    // mapIdx = (my - minCell) * mapsPerRow + (mx - minCell).
    // mapsPerRow = cells-per-face-axis = sqrt(terrains) * subdPow2,
    // which is LARGER than (maxX - minX + 1) when heightmapSubdivisions > 0.
    private Vector2[] heightmapsStartingPositions;
    private Vector2[] cpuChunkWidthRatio;            // per storage slot, mirrors ImpostorRenderer's copy
    private byte[] cellDsStepsByMapFace;
    private int mapsPerRow;                          // Full cell grid per axis
    private int totalMapsPerFace;                    // = mapsPerRow * mapsPerRow
    private sbyte minCellCoord;                      // Always minX (first subdivided cell key)

    /// <summary>
    /// Returns the flat array index for a (map, face) pair.
    /// </summary>
    private int MapFaceIndex(sbyte mx, sbyte my, FaceId face)
    {
        int fx = mx - minCellCoord;
        int fy = my - minCellCoord;
        if (fx < 0 || fx >= mapsPerRow || fy < 0 || fy >= mapsPerRow)
            return -1;
        int mapIdx = fx * mapsPerRow + fy;
        int faceIdx = (int)face;
        if (faceIdx < 0 || faceIdx >= FaceIdUtility.StorageFaceCount)
            return -1;
        return faceIdx * totalMapsPerFace + mapIdx;
    }
    private STPTMEUtils.GlobalIndexCalculator globalIndexCalculator; 
    private CellReader cellReader;
    private HashSet<ChunkKey> ringPositions = new HashSet<ChunkKey>();
    private TextureStreamer textureStreamer;
    private ChunkMaterialManager chunkMaterialManager;
    private readonly int[] faceOriginalResolution = new int[FaceIdUtility.StorageFaceCount];

    // Reusable managed buffers for GenerateMeshData to avoid per-call array allocations
    private Vector3[] meshGenVertBuffer = Array.Empty<Vector3>();
    private Vector3[] meshGenNormalBuffer = Array.Empty<Vector3>();
    private Vector2[] meshGenUvBuffer = Array.Empty<Vector2>();
    private int[] meshGenTriBuffer = Array.Empty<int>();
    private readonly float[] faceMaxHeight = new float[FaceIdUtility.StorageFaceCount];
    private FlatGridBFS flatGridBFS;

    // Reusable buffers for ManageLoadedHeightmaps to avoid per-call GC
    private readonly List<MapFaceKey> hmKeysToRemove = new List<MapFaceKey>();
    private readonly int[] hmCornerChunks = new int[4];
    private Texture2DArray globalHeightmapArray;

    void Awake()
    {
        Instance = this;

        settings = TerrainManagementSettings.Instance;
        sphereCenter = settings.sphereCenter;
        sphereRadius = settings.sphereRadius;
        heightmapSubdivisions = settings.heightmapSubdivisions;
        maxHeight = settings.maxHeight;
        treeMaxHeight = settings.maxHeight; // temporary default; overwritten after InitializeAdjacentData
        subdivisionsPowerOf2 = (int)Mathf.Pow(2, heightmapSubdivisions);
        terrainSize = settings.terrainSize;
        tilingFactor = settings.tilingFactor;
        minX = settings.minX;
        maxX = settings.maxX;
        maxLOD = settings.maxLOD;
        STPTMEUtils.InitializeChunkVertCountLUT(maxLOD);

        // numberOfTerrains must be read BEFORE flat array allocation (used for sizing)
        numberOfTerrains = settings.numberOfTerrains;

        // Allocate flat arrays for per-cell data.
        // mapsPerRow = total number of subdivided cells per axis = sqrt(terrains) * subdPow2.
        // This is LARGER than (maxX - minX + 1) when heightmapSubdivisions > 0.
        int terrainGridSize = (int)Mathf.Sqrt(numberOfTerrains);
        mapsPerRow = terrainGridSize * subdivisionsPowerOf2;
        totalMapsPerFace = mapsPerRow * mapsPerRow;
        minCellCoord = minX;
        heightmapsStartingPositions = new Vector2[FaceIdUtility.StorageFaceCount * totalMapsPerFace];
        cellDsStepsByMapFace = new byte[FaceIdUtility.StorageFaceCount * totalMapsPerFace];

        maxVertsPerOuterChunkMesh = settings.maxVertsPerOuterChunkMesh;
        maxChunkGenWorkPerFrame = settings.maxChunkGenWorkPerFrame;
        maxChunkGenOpsPerFrame = settings.maxChunkGenOpsPerFrame;
        nonBatchedOuterChunkRings = settings.nonBatchedOuterChunkRings;
        debugDisableBatching = settings.debugDisableBatching;
        numberOfChunks = tilingFactor / subdivisionsPowerOf2;
        int totalChunkCount = numberOfTerrains * subdivisionsPowerOf2 * subdivisionsPowerOf2 * numberOfChunks * numberOfChunks;
        totalStorageSlots = totalChunkCount * FaceIdUtility.StorageFaceCount;
        cpuChunkWidthRatio = new Vector2[totalStorageSlots];
        for (int i = 0; i < cpuChunkWidthRatio.Length; i++) cpuChunkWidthRatio[i] = Vector2.one;
        angularChunkData = new ChunkAngularData?[totalStorageSlots];
        visCenterDir = new Vector3[totalStorageSlots];
        visCosThetaC = new float[totalStorageSlots];
        visSinThetaC = new float[totalStorageSlots];
        visBoundCenterAlt = new float[totalStorageSlots];
        visBoundHalfH = new float[totalStorageSlots];

        for (int f = 0; f < FaceIdUtility.StorageFaceCount; f++)
        {
            InitializeAdjacentData((FaceId)f);
        }

        

        globalIndexCalculator = new STPTMEUtils.GlobalIndexCalculator(minX,maxX,numberOfChunks);

        // Initialize the global VisibilitySystem now that AdjacentData has been read for
        // every face. Uses the per-face baked maxHeight (td.size.y) and the per-chunk
        // SoA arrays populated by InitializeAdjacentData. Chunk linear size is constant
        // across faces (terrainSize / tilingFactor).
        VisibilitySystem.Initialize(
            sphereCenter, sphereRadius, settings.halfChunkLinearSize,
            visCenterDir, visCosThetaC, visSinThetaC, visBoundCenterAlt, visBoundHalfH,
            new STPTMEUtils.GlobalIndexCalculator(minX, maxX, numberOfChunks),
            settings.horizonCosineMargin,
            settings.horizonRecomputeFrameInterval,
            settings.horizonRecomputePosThreshold,
            settings.horizonRecomputeAltThreshold);

        // Wire the camera explicitly. Camera.main only resolves cameras tagged MainCamera,
        // which is brittle — if the player camera isn't tagged, frustum culling silently
        // disables itself and every chunk passes the visibility test.
        Camera cam = mainCamera != null ? mainCamera : Camera.main;

        cellReader = new CellReader();
        cellReader.Init(subdivisionsPowerOf2, minX);
        BuildGlobalHeightmapArray();
        
        originalResolution = GetFaceOriginalResolution(FaceId.Up);
        maxHeight = GetFaceMaxHeight(FaceId.Up);
        treeMaxHeight = maxHeight; // use baked maxHeight (td.size.y), not settings.maxHeight
        chunkStep = GetFaceChunkStep(FaceId.Up);

        bool texturesEnabled = enableTextureGeneration && settings.enableTextureGeneration;
        if (texturesEnabled)
        {
            textureStreamer = new TextureStreamer();
            textureStreamer.Init(maxLOD);
            textureStreamer.SetBakeParams(minX, subdivisionsPowerOf2);

            chunkMaterialManager = new ChunkMaterialManager();
            chunkMaterialManager.Init(textureStreamer,terrainShader);
            chunkMaterialManager.LoadUniformClassification();
        }
        else
        {
            textureStreamer = null;
            chunkMaterialManager = null;
        }

        // Build flat-grid BFS neighbor table before wiring ChunkRegistry.
        flatGridBFS = new FlatGridBFS(numberOfChunks, minX, maxX);

        chunkRegistry = gameObject.AddComponent<ChunkRegistry>();
        chunkRegistry.Init(
            numberOfChunks, minX,maxX,maxLOD,maxVertsPerOuterChunkMesh,
            nonBatchedOuterChunkRings,maxChunkGenOpsPerFrame,maxChunkGenWorkPerFrame,chunkDistanceByLOD,
            chunkPool, tempMat,totalChunkCount,globalIndexCalculator,
            chunkMaterialManager,textureStreamer,texturesEnabled,debugDisableBatching,
            flatGridBFS
        );

        // Set fast lookup arrays for STPTMEUtils BFS/ring operations
        STPTMEUtils.SetFastLookupArrays(totalStorageSlots);

        // Initialize impostor renderer for all LOD1+ GPU-instanced objects.
        // Loads all blotch data from baked cell files and uploads to GPU.
        
        var impostor = GetComponent<ImpostorRenderer>();
        if (impostor == null)
            impostor = gameObject.AddComponent<ImpostorRenderer>();

        string cellsFolder = System.IO.Path.Combine(
            Application.streamingAssetsPath, "MapAssets/Cells");
        BlotchData[] rawTerrainBlotches = CellBlotchReader.LoadAllBlotches(cellsFolder);

        string cellObjectsFolder = System.IO.Path.Combine(
            Application.streamingAssetsPath, "MapAssets/CellObjects");
        var rawMapObjects = CellObjectReader.LoadAllObjects(cellObjectsFolder);

        // The orchestrator is the single decision point for GPU-buffer eligibility: it prunes
        // terrain blotches whose prototype is never GPU-instanced (they become prefab-only,
        // via TerrainBlotchIndex below), converts GPU-eligible map objects into single-instance
        // blotches, and validates/drops any terrain blotch that's a cluster but not
        // instanceAlways (which would otherwise render as one wrong prefab — see the warning
        // it logs). This is what keeps the GPU buffer free of prototypes that will never be
        // GPU-instanced, rather than uploading everything and filtering at draw time.
        float faceWorldSizeForOrchestrator = settings.faceWorldSize; // see GetFaceWorldSize for why this must not be reinvented locally
        var orchestratorResult = STPTME.MapObjects.MapContentOrchestrator.Build(
            rawTerrainBlotches, rawMapObjects, mapObjectRegistry,
            sphereCenter, settings.halfChunkLinearSize * 2f, faceWorldSizeForOrchestrator,
            numberOfChunks, minX, maxX);

        STPTME.MapObjects.TerrainBlotchIndex.SetIndex(orchestratorResult.TerrainBlotchesByChunk);

        BlotchData[] allBlotches = orchestratorResult.GpuBlotches;

        // Build stub visibility data — real implementation pending ChunkManager integration.
        var chunkVisData = BuildChunkVisibilityData();
        var cellStartPositions = BuildChunkCellStartPositions(); 

        
        Vector3 halfExtent = new Vector3(sphereRadius * 1.5f, sphereRadius * 1.5f, sphereRadius * 1.5f);
        impostor.Initialize(mapObjectRegistry, sphereCenter, sphereRadius, allBlotches, chunkVisData,
            cellStartPositions,   // NEW
            settings.halfChunkLinearSize, halfExtent, minX, numberOfChunks, mapsPerRow,
            globalHeightmapArray, terrainGridSize);

        allBlotches = null; // Free RAM copy (keep only in VRAM)
        rawTerrainBlotches = null;
        rawMapObjects = null;
    

        ChunkKey initialChunk = GetCurrentProjectedChunk(character.transform.position);
        packedCurrentIndices = initialChunk.packed;
        packedCurrentFace = initialChunk.face;
        _lastFramePosition = character.transform.position;
        packedPreviousIndices = packedCurrentIndices;
        packedPreviousFace = packedCurrentFace;

        chunkRegistry.SetCurrentCenter(packedCurrentIndices, packedCurrentFace);

        ringPositions = STPTMEUtils.GenerateRings(packedCurrentIndices, numberOfChunks, minX, maxX, packedCurrentFace);
        chunkGenerationVersion++;

        SetHeightmapsToLoad(true);
        StartCoroutine(ChunkManagementLoop());

        // Load the baked canopy UV cache if it exists. Until the first bake the cache is
        // absent and ChunkBatcher falls back to mesh-projection (slower but correct).
        var uvCache = CanopyUVCache.LoadFromStreamingAssets(totalStorageSlots);
        if (uvCache != null)
            chunkRegistry.SetCanopyUVCache(uvCache);

    }


    private void Update()
    {
        // Keep altitude current for HUD / diagnostics. Visibility prep itself is invoked
        // from TreeRenderer right before drawing so it sees the final camera transform
        // after Player.LateUpdate moves the camera.
        Vector3 pos = character.transform.position;
        PlayerAltitude = (pos - sphereCenter).magnitude - sphereRadius;
    }

    private void LateUpdate()
    {
        // Drive VisibilitySystem.PrepareFrame here so it has an unconditional heartbeat,
        // independent of whether TreeRenderer has any registered chunks. Previously
        // PrepareFrame only ran from TreeRenderer.DrawAllTrees, which early-returns when
        // populatedIndices.Count == 0 — that froze batch visibility (and the SkipHorizon /
        // SkipFrustum debug toggles) whenever the player was somewhere without registered
        // trees. Runs in LateUpdate so the camera transform is final for this frame.
        PrepareVisibilityForCurrentFrame();
    }

    public void PrepareVisibilityForCurrentFrame()
    {
        if (!VisibilitySystem.IsReady)
            return;

        Vector3 pos = character.transform.position;
        PlayerAltitude = (pos - sphereCenter).magnitude - sphereRadius;
        VisibilitySystem.Instance.PrepareFrame(pos, PlayerAltitude);

        // Drive GPU-side visibility for the impostor renderer.
        var impostor = GetComponent<ImpostorRenderer>();
        if (impostor != null && impostor.SystemEnabled)
            impostor.PrepareFrame(pos, PlayerAltitude);
    }

    private void BuildGlobalHeightmapArray()
    {
        int terrainGridSize = (int)Mathf.Sqrt(numberOfTerrains);
        int slicesPerFace = terrainGridSize * terrainGridSize;
        int totalSlices = slicesPerFace * FaceIdUtility.StorageFaceCount;

        // Determine the LOD1 cell resolution from the face with the highest original resolution.
        int lod1CellRes = 0;
        for (int f = 0; f < FaceIdUtility.StorageFaceCount; f++)
        {
            int faceRes = GetFaceOriginalResolution((FaceId)f);
            if (faceRes <= 0) continue;
            int cellBaseRes = ((faceRes - 1) / subdivisionsPowerOf2) + 1;
            int cellLod1Res = ((cellBaseRes - 1) >> 1) + 1;
            if (cellLod1Res > lod1CellRes) lod1CellRes = cellLod1Res;
        }
        if (lod1CellRes == 0) lod1CellRes = 256;

        int sliceResolution = lod1CellRes * subdivisionsPowerOf2;
        sliceResolution = Mathf.Min(sliceResolution, 1024); 

        globalHeightmapArray = new Texture2DArray(
            sliceResolution, sliceResolution, totalSlices,
            TextureFormat.RHalf, false, true);
        globalHeightmapArray.filterMode = FilterMode.Bilinear;
        globalHeightmapArray.wrapMode = TextureWrapMode.Clamp;

        int subPow2 = subdivisionsPowerOf2;
        float cellWorldSize = terrainSize / subPow2;

        // OPTIMIZATION: Allocate the array ONCE outside the loop!
        ushort[] sliceData = new ushort[sliceResolution * sliceResolution];

        for (int f = 0; f < FaceIdUtility.StorageFaceCount; f++)
        {
            FaceId face = (FaceId)f;
            float fMaxHeight = GetFaceMaxHeight(face);
            float facePixelDistanceFull = GetFacePixelDistance(face);

            for (int tgY = 0; tgY < terrainGridSize; tgY++)
            {
                for (int tgX = 0; tgX < terrainGridSize; tgX++)
                {
                    int sliceIndex = f * slicesPerFace + tgY * terrainGridSize + tgX;
                    
                    // Clear the array before filling it for this slice
                    System.Array.Clear(sliceData, 0, sliceData.Length);

                    for (int scY = 0; scY < subPow2; scY++)
                    {
                        for (int scX = 0; scX < subPow2; scX++)
                        {
                            sbyte mapX = (sbyte)(minX + tgX * subPow2 + scX);
                            sbyte mapY = (sbyte)(minX + tgY * subPow2 + scY);

                            CellReader.CellData data = cellReader.GetOrLoadSync(new Vector2SByte(mapX, mapY), face);
                            if (data?.heights == null) continue;

                            byte dsSteps = GetCellDsSteps(new Vector2SByte(mapX, mapY), face);
                            float facePixelDistance = facePixelDistanceFull * (1 << dsSteps);

                            float cellOriginX = scX * cellWorldSize;
                            float cellOriginY = scY * cellWorldSize;

                            int blockBaseX = scX * (sliceResolution / subPow2);
                            int blockBaseY = scY * (sliceResolution / subPow2);
                            int blockW = sliceResolution / subPow2;
                            int blockH = sliceResolution / subPow2;

                            for (int ly = 0; ly < blockH; ly++)
                            {
                                int sliceY = blockBaseY + ly;
                                if (sliceY >= sliceResolution) break;

                                float v = (float)sliceY / (sliceResolution - 1);
                                float planeY = v * terrainSize;
                                float localPlaneY = planeY - cellOriginY;
                                float contY = localPlaneY / facePixelDistance;
                                
                                int hmY = Mathf.Clamp(Mathf.FloorToInt(contY), 0, data.heights.GetLength(0) - 1);

                                for (int lx = 0; lx < blockW; lx++)
                                {
                                    int sliceX = blockBaseX + lx;
                                    if (sliceX >= sliceResolution) break;

                                    float u = (float)sliceX / (sliceResolution - 1);
                                    float planeX = u * terrainSize;
                                    float localPlaneX = planeX - cellOriginX;
                                    float contX = localPlaneX / facePixelDistance;
                                    
                                    int hmX = Mathf.Clamp(Mathf.FloorToInt(contX), 0, data.heights.GetLength(1) - 1);

                                    float heightMeters = (data.heights[hmY, hmX] / 65535f) * fMaxHeight;
                                    sliceData[sliceY * sliceResolution + sliceX] = Mathf.FloatToHalf(heightMeters);
                                }
                            }
                            
                            // ADD THIS: Evict the cell from CPU RAM to prevent the 16GB spike!
                            cellReader.Evict(new Vector2SByte(mapX, mapY), face);
                        }
                    }
                    globalHeightmapArray.SetPixelData(sliceData, 0, sliceIndex);
                }
            }
        }
        globalHeightmapArray.Apply();

        var impostor = GetComponent<ImpostorRenderer>();
        if (impostor != null)
        {
            impostor.SetGlobalHeightmap(globalHeightmapArray, terrainGridSize);
        }
    }

    private IEnumerator ChunkManagementLoop()
    {
        int framesToWait = 1;//Global declaration later on. Don't go lower than 1, infinite loop. Ideally, should be serialized with min value 1 for safety
        while (true)
        {
            SetHeightmapsToLoad();

            for(int i=0;i<framesToWait;i++)
            {
                yield return null;
            }
        }
    }

    //==== ADJACENT DATA INITIALIZATION AND HELPER METHODS =====

    private void InitializeAdjacentData(FaceId face)
    {
        string prefix = FaceIdUtility.GetFilePrefix(face);
        string folderPath = Path.Combine(Application.streamingAssetsPath, "MapAssets/AdjacentData");
        string filePath = Path.Combine(folderPath, $"AdjacentData_{prefix}.bytes");
        if (!File.Exists(filePath))
        {
            Debug.LogWarning($"[ChunkManager] Adjacent data for face '{face}' not found at {filePath}. Skipping face initialization.");
            return;
        }

        int mapsPerRow = maxX - minX + 1;

        using (BinaryReader reader = new BinaryReader(File.OpenRead(filePath)))
        {
            int headerOriginalResolution = reader.ReadInt32();
            float headerMaxHeight = reader.ReadSingle();
            faceOriginalResolution[(int)face] = headerOriginalResolution;
            faceMaxHeight[(int)face] = headerMaxHeight;

            if (face == FaceId.Up || originalResolution <= 0)
            {
                originalResolution = headerOriginalResolution;
                maxHeight = headerMaxHeight;
            }

            int validChunksPerMapCount = reader.ReadInt32();

            for(int i=0; i< validChunksPerMapCount; i++)
            {
                Vector2SByte map = new Vector2SByte(reader.ReadSByte(),reader.ReadSByte());    
                int mapFlatIndex = (map.x-minX) * mapsPerRow + (map.y-minX);
                int arrIdx = MapFaceIndex(map.x, map.y, face);
                if (arrIdx < 0)
                {
                    Debug.LogError($"[ChunkManager] Map ({map.x},{map.y}) out of range for face {face} (minX={minX}, maxX={maxX}). Skipping entry.");
                    // Skip remaining data for this cell: startPos(8) + dsSteps(1) + chunks
                    reader.ReadSingle(); reader.ReadSingle(); // start position
                    reader.ReadByte(); // dsSteps
                    int totalChunkEntries = numberOfChunks * numberOfChunks;
                    for (int skip = 0; skip < totalChunkEntries; skip++)
                    {
                        if (reader.ReadBoolean())
                        {
                            // Skip 28 bytes per valid chunk: centerDir(12) + n0(12) + n1(12) + n2(12) + n3(12) + minDot(4) + maxH(2) + minH(2) = 68
                            reader.ReadBytes(68);
                        }
                    }
                    continue;
                }

                heightmapsStartingPositions[arrIdx] = new Vector2(reader.ReadSingle(),reader.ReadSingle());

                // Per-cell downsampling level (0 = full res). Written by MeshSaver right after
                // the start position. Determines the cell's effective heightmap resolution and
                // therefore its per-cell chunkStep / pixelDistance / baseRes at runtime.
                byte dsSteps = reader.ReadByte();
                cellDsStepsByMapFace[arrIdx] = dsSteps;

                for(int c=0;c < numberOfChunks; c++)
                {
                    for(int d=0; d< numberOfChunks; d++)
                    {
                        bool isValid = reader.ReadBoolean();
                        
                        if(isValid)
                        {
                            Vector3 centerDir = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                            Vector3 n0 = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                            Vector3 n1 = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                            Vector3 n2 = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                            Vector3 n3 = new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                            float minDot = reader.ReadSingle();
                            // Per-chunk min/max raw heights appended by MeshSaver after minDot.
                            // Decoded to meters using the face's bakedMaxHeight. Together they
                            // give a tight vertical interval for the chunk's terrain.
                            ushort chunkMaxHRaw = reader.ReadUInt16();
                            ushort chunkMinHRaw = reader.ReadUInt16();

                            int flatIndex = c* numberOfChunks +d;
                            int globalFlatIndex = mapFlatIndex * numberOfChunks * numberOfChunks + flatIndex;
                            int flatAngularIdx = FaceIdUtility.GetStorageIndex(globalFlatIndex, face);
                            
                            angularChunkData[flatAngularIdx] =
                            new ChunkAngularData(centerDir, n0, n1, n2, n3,minDot);

                            // Populate VisibilitySystem SoA. cosThetaC / sinThetaC encode the
                            // chunk's contribution to the analytic horizon test. Bound center
                            // and half-height encode the chunk's tight vertical interval for
                            // the frustum bounding-sphere test. TREE_MARGIN is added at runtime
                            // (top-side only) so it can be tuned without re-baking.
                            float chunkMaxHMeters = (chunkMaxHRaw / 65535f) * headerMaxHeight;
                            float chunkMinHMeters = (chunkMinHRaw / 65535f) * headerMaxHeight;
                            float effectiveMaxH = chunkMaxHMeters + VisibilitySystem.TREE_MARGIN;
                            float cosC = sphereRadius / (sphereRadius + effectiveMaxH);
                            float s2 = 1f - cosC * cosC;
                            float sinC = s2 > 0f ? Mathf.Sqrt(s2) : 0f;
                            visCenterDir[flatAngularIdx] = centerDir;
                            visCosThetaC[flatAngularIdx] = cosC;
                            visSinThetaC[flatAngularIdx] = sinC;
                            visBoundCenterAlt[flatAngularIdx] = 0.5f * (chunkMinHMeters + effectiveMaxH);
                            visBoundHalfH[flatAngularIdx] = 0.5f * (effectiveMaxH - chunkMinHMeters);
                        }

                    }    
                }
            }
        }
    }

    private ChunkVisibilityData[] BuildChunkVisibilityData()
    {
        int totalSlots = totalStorageSlots;
        var data = new ChunkVisibilityData[totalSlots];


        int storageFaceCount = FaceIdUtility.StorageFaceCount;
        int mapsPerRow   = (maxX - minX + 1);
        int chunksPerMap = numberOfChunks * numberOfChunks;

        for (int slot = 0; slot < totalSlots; slot++)
        {
            // Invert FaceIdUtility.GetStorageIndex(globalFlatIdx, face) = globalFlatIdx * storageFaceCount + face
            int face = slot % storageFaceCount;
            int globalFlatIdx = slot / storageFaceCount;

            // Invert globalIndexCalculator.GetIndex(packed) -> packed.
            // GlobalIndexCalculator encodes (mapX, mapY, chunkX, chunkY) into a flat index;
            // you need the reverse. Two options:
            //   (a) If STPTMEUtils.GlobalIndexCalculator exposes a GetPacked(globalIdx) -> use it.
            //   (b) Otherwise rebuild the packed int from (mapX, mapY, chunkX, chunkY) using the
            //       same arithmetic the calculator uses internally.
            //
            // The flat index space is laid out as:
            //   globalFlatIdx = ((mapX - minX) * (maxX - minX + 1) + (mapY - minX)) * numberOfChunks * numberOfChunks
            //                 + chunkX * numberOfChunks + chunkY;
            // (Adjust if your GlobalIndexCalculator uses a different ordering — verify in STPTMEUtils.)
            
            int mapFlat      = globalFlatIdx / chunksPerMap;
            int chunkFlat    = globalFlatIdx % chunksPerMap;

            sbyte mapX  = (sbyte)(minX + mapFlat / mapsPerRow);
            sbyte mapY  = (sbyte)(minX + mapFlat % mapsPerRow);
            sbyte chunkX = (sbyte)(chunkFlat / numberOfChunks);
            sbyte chunkY = (sbyte)(chunkFlat % numberOfChunks);
            int packed = STPTMEUtils.WriteFourSBytesInInt(mapX, mapY, chunkX, chunkY);

            var cd = visCenterDir[slot];
            data[slot] = new ChunkVisibilityData
            {
                centerDirX     = cd.x,
                centerDirY     = cd.y,
                centerDirZ     = cd.z,
                cosThetaC      = visCosThetaC[slot],
                sinThetaC      = visSinThetaC[slot],
                boundCenterAlt = visBoundCenterAlt[slot],
                boundHalfH     = visBoundHalfH[slot],
                chunkPacked    = packed,
            };
        }
        return data;
    }

    private Vector2[] BuildChunkCellStartPositions()
    {
        int totalSlots = totalStorageSlots;
        var data = new Vector2[totalSlots];

        int storageFaceCount = FaceIdUtility.StorageFaceCount;
        int mapsPerRowLocal = (maxX - minX + 1);
        int chunksPerMap = numberOfChunks * numberOfChunks;

        for (int slot = 0; slot < totalSlots; slot++)
        {
            int face = slot % storageFaceCount;
            int globalFlatIdx = slot / storageFaceCount;
            int mapFlat = globalFlatIdx / chunksPerMap;

            sbyte mapX = (sbyte)(minX + mapFlat / mapsPerRowLocal);
            sbyte mapY = (sbyte)(minX + mapFlat % mapsPerRowLocal);

            // MapFaceIndex resolves the same baked per-cell origin GenerateMeshData reads via
            // TryGetStartPosition — includes the bake-time pixel-snap border correction.
            int idx = MapFaceIndex(mapX, mapY, (FaceId)face);
            data[slot] = (idx >= 0 && idx < heightmapsStartingPositions.Length)
                ? heightmapsStartingPositions[idx]
                : Vector2.zero;
        }
        return data;
    }

    // ======== SCHEDULING =======

    private void SetHeightmapsToLoad(bool initialGeneration = false)
    {

        Vector3 position = character.transform.position;
        ChunkKey previousChunk = new ChunkKey(packedCurrentIndices, packedCurrentFace);
        ChunkKey currentChunk = GetCurrentProjectedChunk(position);
        packedCurrentIndices = currentChunk.packed;
        packedCurrentFace = currentChunk.face;
        
        //Debug.Log($"[ChunkManager::SetHeightmapsToLoad] Current chunk: {FormatChunkKey(currentChunk)} (packed=0x{currentChunk.packed:X8}), pos={position}");

        chunkRegistry.SetCurrentCenter(packedCurrentIndices, packedCurrentFace);

        if (currentChunk != previousChunk)
        {
            packedPreviousIndices = previousChunk.packed;
            packedPreviousFace = previousChunk.face;

            Debug.Log($"[ChunkManager] CHUNK CHANGE: {FormatChunkKey(previousChunk)} → {FormatChunkKey(currentChunk)}");

            ringPositions = STPTMEUtils.GenerateRings(packedCurrentIndices, numberOfChunks, minX, maxX, packedCurrentFace);

            // Collision ring will be handled by new system.
            // Placeholder for future chunk-based collider management.
            if (!playerInsideChunkBuffer)
            {
                bufferOriginChunkIndices = packedPreviousIndices;
                bufferOriginChunkFace = packedPreviousFace;
            }
            playerInsideChunkBuffer = true;
            previousChunkLastPosition = _lastFramePosition;

            EnsureChunkSync(packedCurrentIndices, packedCurrentFace, 0);
        }

        bool shouldTriggerGeneration = initialGeneration ||
        (playerInsideChunkBuffer && Vector3.Distance(position, previousChunkLastPosition) > chunkLoadingBufferDistance);

        if (shouldTriggerGeneration)
        {
            if (!initialGeneration)
            {
                playerInsideChunkBuffer = false;
            }
            
            if (initialGeneration || packedCurrentIndices != bufferOriginChunkIndices || packedCurrentFace != bufferOriginChunkFace)
            {
                // Initial generation — collider ring handled by new system.
                if (initialGeneration)
                    EnsureChunkSync(packedCurrentIndices, packedCurrentFace, 0);

                chunkGenerationVersion++;
                int thisGen = chunkGenerationVersion;

                if(shouldGenerateOuterChunks)
                {
                    //Debug.Log($"New chunk center: {FormatChunkKey(currentChunk)}. Triggering generation cycle {thisGen} with ring positions count: {ringPositions.Count}");
                    chunkRegistry.StartGenerationCycle(packedCurrentIndices, packedCurrentFace, thisGen, ringPositions);
                }
            }
        }

        _lastFramePosition = character.transform.position;
    }

    private static string FormatChunkKey(ChunkKey chunk)
    {
        STPTMEUtils.ReadFourSBytesFromInt(chunk.packed, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        return $"({mapX},{mapY},{chunkX},{chunkY},{chunk.face})";
    }

    // ====SYNC CHUNK CREATION PATHWAY====

    private bool TryGetStartPosition(Vector2SByte map, FaceId face, out Vector2 startPosition)
    {
        int idx = MapFaceIndex(map.x, map.y, face);
        if (idx < 0 || idx >= heightmapsStartingPositions.Length)
        {
            startPosition = default;
            return false;
        }
        startPosition = heightmapsStartingPositions[idx];
        return true;
    }

    private float ResolvePlaneX(float defaultPx, int sampleX, int maxSampleX, int localChunkX,
        sbyte heightmapX, sbyte heightmapY, FaceId face, float faceWorldSize)
    {
        if (sampleX == 0 && heightmapX == minX && localChunkX == 0)
            return 0f;

        if (sampleX == maxSampleX && localChunkX == numberOfChunks - 1)
        {
            if (heightmapX == maxX)
                return faceWorldSize;

            if (heightmapX + 1 <= maxX && TryGetStartPosition(new Vector2SByte((sbyte)(heightmapX + 1), heightmapY), face, out Vector2 nextXStart))
                return nextXStart.x;
        }

        return defaultPx;
    }

    private float ResolvePlaneZ(float defaultPz, int sampleY, int maxSampleY, int localChunkY,
        sbyte heightmapX, sbyte heightmapY, FaceId face, float faceWorldSize)
    {
        if (sampleY == 0 && heightmapY == minX && localChunkY == 0)
            return 0f;

        if (sampleY == maxSampleY && localChunkY == numberOfChunks - 1)
        {
            if (heightmapY == maxX)
                return faceWorldSize;

            if (heightmapY + 1 <= maxX && TryGetStartPosition(new Vector2SByte(heightmapX, (sbyte)(heightmapY + 1)), face, out Vector2 nextYStart))
                return nextYStart.y;
        }

        return defaultPz;
    }

    public float GetFaceWorldSize()
    {
        // settings.faceWorldSize (= sqrt(numberOfTerrains) * terrainSize) is algebraically
        // identical to mapsPerRow * (terrainSize / subdivisionsPowerOf2) — the value
        // ComputeExactInstanceDir actually uses — but the local "mapsPerRow = maxX-minX+1"
        // this used to compute was a DIFFERENT, wrong quantity that only coincides with the
        // real mapsPerRow when heightmapSubdivisions == 0. That mismatch was silent for
        // anything reading live worldPosition directly (nothing ever noticed), but became a
        // real, visible bug for baked map objects, whose position/orientation is fully
        // RECONSTRUCTED from chunk-local coordinates at load time via ComputeExactInstanceDir.
        return settings.faceWorldSize;
    }

    private Vector3 ComputeFaceSphereCorner(FaceId face, float planeX, float planeY)
    {
        return FaceIdUtility.ProjectFacePlanePoint(face, planeX, planeY, GetFaceWorldSize(), sphereCenter, sphereRadius);
    }

    private int GetFaceOriginalResolution(FaceId face)
    {
        int value = faceOriginalResolution[(int)face];
        return value > 0 ? value : originalResolution;
    }

    private float GetFaceMaxHeight(FaceId face)
    {
        float value = faceMaxHeight[(int)face];
        return value > 0f ? value : maxHeight;
    }

    private int GetFaceChunkStep(FaceId face)
    {
        int resolution = GetFaceOriginalResolution(face);
        return resolution > 1 ? Mathf.Max(1, (resolution - 1) / tilingFactor) : 1;
    }

    private float GetFacePixelDistance(FaceId face)
    {
        int resolution = GetFaceOriginalResolution(face);
        return resolution > 1 ? terrainSize / (resolution - 1) : terrainSize;
    }

    /// <summary>
    /// Returns the per-cell downsampling level baked into the heightmap.
    /// 0 = full resolution. Each step halves the cell's heightmap resolution and so halves the
    /// per-cell chunkStep / doubles the per-cell pixelDistance. Returns 0 if no entry exists
    /// (e.g. cell not present in AdjacentData), keeping callers safe.
    /// </summary>
    private byte GetCellDsSteps(Vector2SByte map, FaceId face)
    {
        int idx = MapFaceIndex(map.x, map.y, face);
        if (idx >= 0 && idx < cellDsStepsByMapFace.Length)
            return cellDsStepsByMapFace[idx];
        return 0;
    }

    private void EnsureChunkSync(int packed, FaceId face, byte lod)
    {
        if (chunkRegistry.HasChunk(packed, face, lod))
        {
            return;
        }

        Vector2SByte heightmap = STPTMEUtils.ReadHeightmapFromPackedInt(packed);

        if (!cellReader.IsCached(heightmap, face))
            cellReader.GetOrLoadSync(heightmap, face);

        if (textureStreamer != null)
        {
            byte tier = textureStreamer.GetTierForLOD(lod);
            textureStreamer.GetOrLoadSync(heightmap, tier, face);
        }

        var meshData = GenerateMeshData(packed, lod, face);
        if (meshData.verts == null) return;

        chunkRegistry.CreateChunk(packed, face, lod, ref meshData);
    }

    public struct MeshData : IDisposable
    {
        public NativeArray<Vector3> verts;
        public NativeArray<Vector3> normals;
        public NativeArray<int> tris;
        public NativeArray<Vector2> uvs;
        public NativeArray<Vector4> uv1;
        public int vertCount;
        public int triCount;
        // Grid dimensions used by skirt extraction. edgeWidth = maxJ + 1, edgeHeight = maxI + 1.
        // Verts are stored row-major: index = y * edgeWidth + x.
        public ushort edgeWidth;
        public ushort edgeHeight;

        // Sample-space placement of this chunk within its cell's heightmap. Needed because
        // chunk widths are NOT uniform: GetFaceChunkStep uses integer division, so the LAST
        // chunk in each cell absorbs the remainder and spans one sample more or fewer than its
        // neighbours (see the maxJ/maxI switch in GenerateMeshData). Mesh UVs are x/maxJ, so
        // that chunk's UV [0,1] covers a different physical width than the others — any
        // texture mapping that assumes an even 1/chunksPerAxis split therefore stretches its
        // textures by exactly one heightmap sample, producing a visible one-texel seam against
        // its neighbour. This is the same anomaly the blotch widthRatio correction exists for,
        // just with texture sampling as the consumer instead of instance placement.
        //   sampleOffsetX/Y — this chunk's first sample index within the cell
        //   cellSamplesX/Y  — total samples across the whole cell (heightmapRes - 1)
        //   span            — edgeWidth - 1 / edgeHeight - 1 (i.e. maxJ / maxI)
        public ushort sampleOffsetX;
        public ushort sampleOffsetY;
        public ushort cellSamplesX;
        public ushort cellSamplesY;

        public bool isValid => verts.IsCreated && vertCount>0;

        public MeshData(int maxVerts,int maxTris,Allocator allocator = Allocator.TempJob)
        {
            verts = new NativeArray<Vector3>(maxVerts, allocator,NativeArrayOptions.UninitializedMemory);
            normals = new NativeArray<Vector3>(maxVerts, allocator,NativeArrayOptions.UninitializedMemory);
            tris = new NativeArray<int>(maxTris, allocator,NativeArrayOptions.UninitializedMemory);
            uvs = new NativeArray<Vector2>(maxVerts, allocator,NativeArrayOptions.UninitializedMemory);
            uv1 = default;//only allocated by batched, not by GenerateMeshData
            vertCount = 0;
            triCount = 0;
            edgeWidth = 0;
            edgeHeight = 0;
            sampleOffsetX = 0;
            sampleOffsetY = 0;
            cellSamplesX = 0;
            cellSamplesY = 0;
        }
        
        public void Dispose()
        {
            if (verts.IsCreated) verts.Dispose();
            if (normals.IsCreated) normals.Dispose();
            if (tris.IsCreated) tris.Dispose();
            if (uvs.IsCreated) uvs.Dispose();
            if(uv1.IsCreated) uv1.Dispose();
        }
    }

    private byte ClampMeshLod(byte requestedLod, FaceId face, byte cellDsSteps)
    {
        // Per-cell effective chunkStep: each downsampling step at bake halves it.
        int faceChunkStep = GetFaceChunkStep(face);
        int cellChunkStep = Mathf.Max(1, faceChunkStep >> cellDsSteps);
        if (cellChunkStep <= 1)
            return 0;

        byte maxValidLod = 0;
        int samplesPerSide = cellChunkStep;
        while (samplesPerSide > 1)
        {
            samplesPerSide >>= 1;
            maxValidLod++;
        }

        return requestedLod < maxValidLod ? requestedLod : maxValidLod;
    }

    public MeshData GenerateMeshData(int packed, byte lod, FaceId face)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte heightmapX, out sbyte heightmapY, out sbyte chunkX, out sbyte chunkY);
        Vector2SByte currentHeightmap = new Vector2SByte(heightmapX, heightmapY);

        byte cellDsSteps = GetCellDsSteps(currentHeightmap, face);
        byte effectiveLod = ClampMeshLod(lod, face, cellDsSteps);

        ushort[,] currentHeightmapHeights = cellReader.GetHeights(currentHeightmap, effectiveLod, face, sync: true);
        if (currentHeightmapHeights == null)
            return default;

        
        if (!TryGetStartPosition(currentHeightmap, face, out Vector2 startPos2))
            return default;

        Vector3 currentStartingPosition = new Vector3(startPos2.x, 0, startPos2.y);

        float nominalCellSize = terrainSize / subdivisionsPowerOf2;
        float nominalStartX = ((heightmapX - minX)) * nominalCellSize;

        int faceOriginalResolution = GetFaceOriginalResolution(face);
        // Per-cell base values: each bake-time downsampling step halves the cell's stored
        // resolution. Runtime LOD is requested ON TOP of that, so total decimation factor is
        // (1 << (cellDsSteps + effectiveLod)). chunkStep / pixelDistance / baseRes for the
        // cell are derived by halving / doubling the per-face originals by cellDsSteps.
        // The runtime LOD is then applied via lodAdjustedChunkStep / lodAdjustedPixelDistance
        // exactly as before.
        int faceChunkStepFull = GetFaceChunkStep(face);
        float facePixelDistanceFull = GetFacePixelDistance(face);
        int faceChunkStep = Mathf.Max(1, faceChunkStepFull >> cellDsSteps);
        float facePixelDistance = facePixelDistanceFull * (1 << cellDsSteps);
        float faceHeightScale = GetFaceMaxHeight(face) / 65535f;

        int baseRes = ((faceOriginalResolution - 1) / subdivisionsPowerOf2) >> cellDsSteps;
        int heightmapResX = currentHeightmapHeights.GetLength(1);
        int heightmapResZ = currentHeightmapHeights.GetLength(0);

        int lastChunkIdx = (tilingFactor / subdivisionsPowerOf2) - 1;
        int lodDivisionFactor = 1 << effectiveLod;
        int lodAdjustedChunkStep = Mathf.Max(1, faceChunkStep / lodDivisionFactor);

        int yOffset = lodAdjustedChunkStep * chunkY;
        int xOffset = lodAdjustedChunkStep * chunkX;

        int maxJ;
        int maxI;
        if (effectiveLod == 0)
        {
            maxJ = heightmapResX switch
            {
                var res when res == baseRes => (chunkX == lastChunkIdx) ? faceChunkStep - 1 : faceChunkStep,
                var res when res == baseRes + 2 => (chunkX == lastChunkIdx) ? faceChunkStep + 1 : faceChunkStep,
                _ => faceChunkStep
            };
            maxI = heightmapResZ switch
            {
                var res when res == baseRes => (chunkY == lastChunkIdx) ? faceChunkStep - 1 : faceChunkStep,
                var res when res == baseRes + 2 => (chunkY == lastChunkIdx) ? faceChunkStep + 1 : faceChunkStep,
                _ => faceChunkStep
            };
        }
        else
        {
            maxJ = (chunkX == lastChunkIdx) ? heightmapResX - 1 - xOffset : lodAdjustedChunkStep;
            maxI = (chunkY == lastChunkIdx) ? heightmapResZ - 1 - yOffset : lodAdjustedChunkStep;
        }

        if (maxI <= 0 || maxJ <= 0)
            return default;

        if (lod == 0 && ImpostorRenderer.Instance != null)
        {
            int slot = FaceIdUtility.GetStorageIndex(globalIndexCalculator.GetIndex(packed), face);
            ImpostorRenderer.Instance.SetActiveLOD0Heightmap(slot, currentHeightmapHeights, GetFaceMaxHeight(face),xOffset, yOffset, maxJ + 1, maxI + 1);

            // Some chunks (the last chunk along X or Y within a cell) get a sample count that
            // deviates from faceChunkStep due to the bake's cell-border overlap logic (MeshSaver's
            // -1/+1 sample snap). ComputeExactInstanceDir on the GPU assumes every chunk spans the
            // NOMINAL chunkWorldSize; for those edge chunks that's wrong by a sub-chunk fraction,
            // which is invisible on flat ground but shows up as floating/sinking grass on slopes.
            // This ratio corrects it: 1.0 for every ordinary chunk, only deviating exactly where
            // the bake's sample count deviates.
            float ratioX = (float)maxJ / faceChunkStep;
            float ratioZ = (float)maxI / faceChunkStep;
            ImpostorRenderer.Instance.SetChunkWidthRatio(slot, ratioX, ratioZ);

            // Also cache CPU-side: GetBlotchWorldPosition needs the identical ratio to place
            // LOD0 prefab-spawned blotches at the same position the GPU computes for instanced
            // ones. Previously this value only ever reached the GPU, so the two paths disagreed.
            if (cpuChunkWidthRatio != null && slot >= 0 && slot < cpuChunkWidthRatio.Length)
                cpuChunkWidthRatio[slot] = new Vector2(ratioX, ratioZ);
            
        }


        int rowWidth = maxJ + 1;
        int vertexCount = (maxI + 1) * rowWidth;
        int triCount = maxI * maxJ * 6;
        float lodAdjustedPixelDistance = facePixelDistance * lodDivisionFactor;

        MeshData meshData = new MeshData(vertexCount, triCount, Allocator.Persistent);

        // Use managed arrays for all writes, then bulk-copy to NativeArrays at the end.
        // This replaces 131k bounds-checked NativeArray SetItem calls with cheap managed writes + 3 memcpys.
        if (meshGenVertBuffer.Length < vertexCount)
        {
            meshGenVertBuffer = new Vector3[vertexCount];
            meshGenNormalBuffer = new Vector3[vertexCount];
            meshGenUvBuffer = new Vector2[vertexCount];
        }
        Vector3[] vertArray = meshGenVertBuffer;
        Vector3[] normalArray = meshGenNormalBuffer;
        Vector2[] uvArray = meshGenUvBuffer;

        // Per-vertex sphere projection: each vertex is projected directly from its
        // flat-plane coordinates, avoiding bilinear interpolation error between sphere corners.
        float faceWorldSize = GetFaceWorldSize();
        float basePx = currentStartingPosition.x + lodAdjustedPixelDistance * lodAdjustedChunkStep * chunkX;
        float basePz = currentStartingPosition.z + lodAdjustedPixelDistance * lodAdjustedChunkStep * chunkY;

        // Hoist face axes and constants outside the vertex loop — they're invariant per chunk
        FaceIdUtility.GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);
        float invFaceSize = faceWorldSize > 0f ? 1f / faceWorldSize : 0f;
        float scCx = sphereCenter.x, scCy = sphereCenter.y, scCz = sphereCenter.z;
        float sR = sphereRadius;

        // Precompute edge-resolution flags for ResolvePlaneX/Z inlining
        bool isLeftEdgeChunk  = heightmapX == minX && chunkX == 0;
        bool isRightEdgeChunk = chunkX == numberOfChunks - 1;
        bool isBottomEdgeChunk = heightmapY == minX && chunkY == 0;
        bool isTopEdgeChunk   = chunkY == numberOfChunks - 1;

        float rightEdgePx = faceWorldSize; // default if heightmapX == maxX
        if (isRightEdgeChunk && heightmapX != maxX && heightmapX + 1 <= maxX
            && TryGetStartPosition(new Vector2SByte((sbyte)(heightmapX + 1), heightmapY), face, out Vector2 nextXStart))
            rightEdgePx = nextXStart.x;

        float topEdgePz = faceWorldSize; // default if heightmapY == maxX
        if (isTopEdgeChunk && heightmapY != maxX && heightmapY + 1 <= maxX
            && TryGetStartPosition(new Vector2SByte(heightmapX, (sbyte)(heightmapY + 1)), face, out Vector2 nextYStart))
            topEdgePz = nextYStart.y;

        float invMaxJ = 1f / maxJ;
        float invMaxI = 1f / maxI;

        for (int y = 0; y <= maxI; y++)
        {
            int yAccess = y * rowWidth;
            int iOffset = y + yOffset;
            float defaultPz = basePz + lodAdjustedPixelDistance * y;

            // Inlined ResolvePlaneZ
            float pz;
            if (y == 0 && isBottomEdgeChunk)
                pz = 0f;
            else if (y == maxI && isTopEdgeChunk)
                pz = topEdgePz;
            else
                pz = defaultPz;

            float percentY = pz * invFaceSize;
            float factorB = (percentY - 0.5f) * 2f;
            float uvY = y * invMaxI;

            for (int x = 0; x <= maxJ; x++)
            {
                float defaultPx = basePx + lodAdjustedPixelDistance * x;

                // Inlined ResolvePlaneX
                float px;
                if (x == 0 && isLeftEdgeChunk)
                    px = 0f;
                else if (x == maxJ && isRightEdgeChunk)
                    px = rightEdgePx;
                else
                    px = defaultPx;

                // Inlined ComputeFaceSphereCorner + ProjectFacePlanePoint
                float percentX = px * invFaceSize;
                float factorA = (percentX - 0.5f) * 2f;

                float cubeX = localUp.x + factorA * axisA.x + factorB * axisB.x;
                float cubeY = localUp.y + factorA * axisA.y + factorB * axisB.y;
                float cubeZ = localUp.z + factorA * axisA.z + factorB * axisB.z;

                float cubeMag = Mathf.Sqrt(cubeX * cubeX + cubeY * cubeY + cubeZ * cubeZ);
                if (cubeMag <= 1e-6f)
                {
                    meshData.Dispose();
                    return default;
                }
                float invCubeMag = 1f / cubeMag;

                // Height read directly from heightmap — no intermediate array
                float h = currentHeightmapHeights[iOffset, x + xOffset] * faceHeightScale;
                float totalRadius = sR + h;

                float dirX = cubeX * invCubeMag;
                float dirY = cubeY * invCubeMag;
                float dirZ = cubeZ * invCubeMag;

                int idx = yAccess + x;
                vertArray[idx] = new Vector3(
                    scCx + dirX * totalRadius,
                    scCy + dirY * totalRadius,
                    scCz + dirZ * totalRadius);
                uvArray[idx] = new Vector2(x * invMaxJ, uvY);
            }
        }

        // Compute terrain-following normals from the vertex grid using finite differences.
        // For each vertex, take the cross product of the tangent vectors formed by its
        // neighbors. This gives normals that reflect actual terrain shape (peaks, valleys)
        // instead of the smooth sphere direction, which is critical for NdotL shading at
        // all LOD levels.
        for (int y = 0; y <= maxI; y++)
        {
            int row = y * rowWidth;
            for (int x = 0; x <= maxJ; x++)
            {
                int idx = row + x;

                // Neighbors: clamp to grid edges
                int xm = x > 0 ? idx - 1 : idx;
                int xp = x < maxJ ? idx + 1 : idx;
                int ym = y > 0 ? idx - rowWidth : idx;
                int yp = y < maxI ? idx + rowWidth : idx;

                // Tangent vectors along grid axes
                float tx = vertArray[xp].x - vertArray[xm].x;
                float ty = vertArray[xp].y - vertArray[xm].y;
                float tz = vertArray[xp].z - vertArray[xm].z;

                float bx = vertArray[yp].x - vertArray[ym].x;
                float by = vertArray[yp].y - vertArray[ym].y;
                float bz = vertArray[yp].z - vertArray[ym].z;

                // Cross product: tangentX × tangentY
                float nx = ty * bz - tz * by;
                float ny = tz * bx - tx * bz;
                float nz = tx * by - ty * bx;

                float mag = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (mag > 1e-8f)
                {
                    float invMag = 1f / mag;
                    nx *= invMag; ny *= invMag; nz *= invMag;

                    // Ensure normal points outward from sphere center
                    Vector3 v = vertArray[idx];
                    float rx = v.x - scCx, ry = v.y - scCy, rz = v.z - scCz;
                    if (nx * rx + ny * ry + nz * rz < 0f)
                    {
                        nx = -nx; ny = -ny; nz = -nz;
                    }
                    normalArray[idx] = new Vector3(nx, ny, nz);
                }
                else
                {
                    // Fallback to sphere direction for degenerate cases
                    Vector3 v = vertArray[idx];
                    float dx = v.x - scCx, dy = v.y - scCy, dz = v.z - scCz;
                    float dm = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                    normalArray[idx] = dm > 1e-6f
                        ? new Vector3(dx / dm, dy / dm, dz / dm)
                        : new Vector3(0, 1, 0);
                }
            }
        }

        NativeArray<Vector3>.Copy(vertArray, 0, meshData.verts, 0, vertexCount);
        NativeArray<Vector3>.Copy(normalArray, 0, meshData.normals, 0, vertexCount);
        NativeArray<Vector2>.Copy(uvArray, 0, meshData.uvs, 0, vertexCount);
        meshData.vertCount = vertexCount;

        int ti = 0;
        if (meshGenTriBuffer.Length < triCount)
            meshGenTriBuffer = new int[triCount];
        int[] triArray = meshGenTriBuffer;
        bool reverseWinding = face == FaceId.Up || face == FaceId.Down;
        for (int y = 0; y < maxI; y++)
        {
            int rowStart = y * rowWidth;
            int nextRowStart = rowStart + rowWidth;
            for (int x = 0; x < maxJ; x++)
            {
                int bl = rowStart + x;
                int br = bl + 1;
                int tl = nextRowStart + x;
                int tr = tl + 1;
                
                if (reverseWinding)
                {
                    triArray[ti++] = bl; triArray[ti++] = tl; triArray[ti++] = tr;
                    triArray[ti++] = bl; triArray[ti++] = tr; triArray[ti++] = br;
                }
                else
                {
                    triArray[ti++] = bl; triArray[ti++] = tr; triArray[ti++] = tl;
                    triArray[ti++] = bl; triArray[ti++] = br; triArray[ti++] = tr;
                }
            }
        }
        NativeArray<int>.Copy(triArray, 0, meshData.tris, 0, triCount);
        meshData.triCount = triCount;
        meshData.edgeWidth = (ushort)rowWidth;
        meshData.edgeHeight = (ushort)(maxI + 1);
        meshData.sampleOffsetX = (ushort)xOffset;
        meshData.sampleOffsetY = (ushort)yOffset;
        meshData.cellSamplesX = (ushort)(heightmapResX - 1);
        meshData.cellSamplesY = (ushort)(heightmapResZ - 1);

        return meshData;
    }

    public IEnumerator GenerateMeshDataAsync(int packed, byte lod, FaceId face,
        int genVersion, Action<MeshData> onComplete)
    {
        if (chunkGenerationVersion != genVersion) yield break;

        Vector2SByte heightmap = STPTMEUtils.ReadHeightmapFromPackedInt(packed);

        if (!cellReader.IsCached(heightmap, face))
        {
            bool loaded = false;
            yield return cellReader.LoadCellCoroutine(heightmap, face, (data) => loaded = (data != null));
            if (!loaded || chunkGenerationVersion != genVersion) yield break;
        }

        var meshData = GenerateMeshData(packed, lod, face);
        onComplete?.Invoke(meshData);
    }

    public Coroutine StartGenChunkOnlyMeshData(int packed, FaceId face, byte lod,
        Action<MeshData> onComplete)
    {
        return StartCoroutine(GenerateMeshDataAsync(packed, lod, face, chunkGenerationVersion,
            data => onComplete?.Invoke(data)));
    }

    public bool TryGenerateChunkOnlyMeshDataSync(int packed, FaceId packedFace, byte lod,
        out MeshData meshData)
    {
        Vector2SByte heightmap = STPTMEUtils.ReadHeightmapFromPackedInt(packed);

        if (!cellReader.IsCached(heightmap, packedFace))
        {
            meshData = default;
            return false;
        }

        meshData = GenerateMeshData(packed, lod, packedFace);
        return meshData.isValid;
    }

    public void ManageLoadedHeightmaps()
    {
        // CellReader caches at cell level (not per-LOD), so we evict entire cells
        // that are no longer needed based on player position.

        hmKeysToRemove.Clear();
        int lastChunkIdx = numberOfChunks - 1;

        foreach (var key in cellReader.CachedKeys)
        {
            Vector2SByte map = key.map;

            // Check if any corner chunk of this cell is still in range
            hmCornerChunks[0] = STPTMEUtils.WriteFourSBytesInInt(map.x, map.y, 0, 0);
            hmCornerChunks[1] = STPTMEUtils.WriteFourSBytesInInt(map.x, map.y, 0, (sbyte)lastChunkIdx);
            hmCornerChunks[2] = STPTMEUtils.WriteFourSBytesInInt(map.x, map.y, (sbyte)lastChunkIdx, 0);
            hmCornerChunks[3] = STPTMEUtils.WriteFourSBytesInInt(map.x, map.y, (sbyte)lastChunkIdx, (sbyte)lastChunkIdx);

            bool anyValidCorner = false;

            foreach (int cornerPacked in hmCornerChunks)
            {
                int cornerGlobalIdx = globalIndexCalculator.GetIndex(cornerPacked);
                if (angularChunkData[FaceIdUtility.GetStorageIndex(cornerGlobalIdx, key.face)].HasValue)
                {
                    anyValidCorner = true;
                    break;
                }
            }

            if (!anyValidCorner)
            {
                hmKeysToRemove.Add(key);
            }
        }

        // Evict cells that are no longer needed
        foreach (var key in hmKeysToRemove)
        {
            cellReader.Evict(key.map, key.face);
            
            // Also evict corresponding splatmap cache if textures are enabled
            if (textureStreamer != null)
            {
                for (byte tier = 0; tier < textureStreamer.TierCount; tier++)
                {
                    textureStreamer.Evict(key.map, tier, key.face);
                }
            }
        }
    }

    // ==== PLAYER POSITION ====

    private ChunkKey GetProjectedChunkFromWorldPosition(Vector3 position)
    {
        float subdividedChunkSize = terrainSize / tilingFactor;
        int faceSpanInChunks = (maxX - minX + 1) * numberOfChunks;

        FaceId face = FaceIdUtility.GetClosestFace(position, sphereCenter);
        if (!FaceIdUtility.TryProjectWorldPointToFacePlane(position, face, GetFaceWorldSize(), sphereCenter, out Vector2 planePosition))
        {
            return new ChunkKey(packedCurrentIndices, face);
        }

        int globalChunkX = Mathf.Clamp(Mathf.FloorToInt(planePosition.x / subdividedChunkSize), 0, faceSpanInChunks - 1);
        int globalChunkY = Mathf.Clamp(Mathf.FloorToInt(planePosition.y / subdividedChunkSize), 0, faceSpanInChunks - 1);

        sbyte currentHeightmapX = (sbyte)(minX + (globalChunkX / numberOfChunks));
        sbyte currentHeightmapY = (sbyte)(minX + (globalChunkY / numberOfChunks));
        sbyte currentChunkX = (sbyte)(globalChunkX % numberOfChunks);
        sbyte currentChunkY = (sbyte)(globalChunkY % numberOfChunks);

        return new ChunkKey(
            STPTMEUtils.WriteFourSBytesInInt(currentHeightmapX, currentHeightmapY, currentChunkX, currentChunkY),
            face);
    }

    private ChunkKey GetCurrentProjectedChunk(Vector3 playerPosition)
    {
        //Check player position vs last frame position
        //If player moved, check if player is still inside most recent chunk 
        //If player ain't inside chunk, check ring positions
        //If he ain't there, generate BFS queue from corresponding flat chunk and check all others in order. 
        ChunkKey projectedChunk = GetProjectedChunkFromWorldPosition(playerPosition);

        if((playerPosition-_lastFramePosition).sqrMagnitude < 1e-6f)
        {
            return new ChunkKey(packedCurrentIndices, packedCurrentFace);
        }
        else
        {
            int currentGlobalFlatIdx = globalIndexCalculator.GetIndex(packedCurrentIndices); 

            if(IsPlayerInsideChunk(currentGlobalFlatIdx, playerPosition, packedCurrentFace))
            {
                return new ChunkKey(packedCurrentIndices, packedCurrentFace);
            }
            else
            {
                foreach(var position in ringPositions)//ringPositions will have to generate neighbors correctly at edge, and both on top and bottom plane
                {
                    int ringPositionGlobalIdx = globalIndexCalculator.GetIndex(position.packed);
                    if(IsPlayerInsideChunk(ringPositionGlobalIdx, playerPosition, position.face))
                    {
                        return position;
                    }
                }

                //slow fallback — flat-grid BFS (no per-step coordinate math)
                flatGridBFS.RunBFS(flatGridBFS.ChunkKeyToFlat(projectedChunk.packed, projectedChunk.face));
                for (int bi = 0; bi < flatGridBFS.resultCount; bi++)
                {
                    int flatIdx = flatGridBFS.resultBuffer[bi];
                    int queuedChunkGlobalIdx = flatGridBFS.GetStorageIndex(flatIdx) / 6;
                    FaceId queuedFace = flatGridBFS.GetFace(flatIdx);
                    if (IsPlayerInsideChunk(queuedChunkGlobalIdx, playerPosition, queuedFace))
                    {
                        return new ChunkKey(flatGridBFS.GetPacked(flatIdx), queuedFace);
                    }
                }
                
                return projectedChunk;
            }
        }
    }

    private bool IsPlayerInsideChunk(int globalFlatIndex, Vector3 playerPosition, FaceId face)
    {
        var chunk = angularChunkData[FaceIdUtility.GetStorageIndex(globalFlatIndex, face)];
        if (!chunk.HasValue)
            return false;

        ChunkAngularData data = chunk.Value;

        Vector3 D = (playerPosition - sphereCenter).normalized;

        if (Vector3.Dot(D, data.centerDir) < data.minDot)
            return false;

        if (Vector3.Dot(data.n0, D) < 0) return false;
        if (Vector3.Dot(data.n1, D) < 0) return false;
        if (Vector3.Dot(data.n2, D) < 0) return false;
        if (Vector3.Dot(data.n3, D) < 0) return false;

        return true;
    }

    public bool isGenerationVersionValid(int generationVersion) { return this.chunkGenerationVersion == generationVersion ? true:false;}

    /// <summary>
    /// Gets the baked chunk center direction for a given storage slot.
    /// Used by CPU prefab pipeline to match GPU spherical interpolation.
    /// </summary>
    public Vector3 GetChunkCenterDirection(int storageSlot)
    {
        if (visCenterDir == null || storageSlot < 0 || storageSlot >= visCenterDir.Length)
            return Vector3.up;
        return visCenterDir[storageSlot];
    }

#if false
    // ===== TREE DECODING SUPPORT (deprecated — replaced by ImpostorRenderer blotch system) =====

    public TreeDecoder.ChunkGeometry GetChunkGeometryForTrees(int packed, FaceId face)
    {
        STPTMEUtils.ReadFourSBytesFromInt(packed, out sbyte heightmapX, out sbyte heightmapY, out sbyte chunkX, out sbyte chunkY);
        Vector2SByte map = new Vector2SByte(heightmapX, heightmapY);

        if (!TryGetStartPosition(map, face, out Vector2 startPos2))
            return default;

        Vector3 startPos = new Vector3(startPos2.x, 0, startPos2.y);

        float chunkWorldSize = GetFacePixelDistance(face) * GetFaceChunkStep(face);

        Vector3[] corners = new Vector3[4];

        corners[0] = ComputeChunkCorner(startPos, chunkX, chunkY, 0, 0, chunkWorldSize,
            heightmapX, heightmapY, face);
        corners[1] = ComputeChunkCorner(startPos, chunkX, chunkY, 0, 1, chunkWorldSize,
            heightmapX, heightmapY, face);
        corners[2] = ComputeChunkCorner(startPos, chunkX, chunkY, 1, 0, chunkWorldSize,
            heightmapX, heightmapY, face);
        corners[3] = ComputeChunkCorner(startPos, chunkX, chunkY, 1, 1, chunkWorldSize,
            heightmapX, heightmapY, face);

        return TreeDecoder.ComputeChunkGeometry(corners[0], corners[1], corners[2], corners[3],
            sphereCenter, sphereRadius);
    }

    private Vector3 ComputeChunkCorner(Vector3 startPos, int chunkX, int chunkY, 
        int cornerY, int cornerX, float chunkWorldSize,
        sbyte heightmapX, sbyte heightmapY, FaceId face)
    {
        float faceWorldSize = GetFaceWorldSize();
        float defaultPx = startPos.x + chunkWorldSize * chunkX + chunkWorldSize * cornerX;
        float defaultPz = startPos.z + chunkWorldSize * chunkY + chunkWorldSize * cornerY;

        float px = ResolvePlaneX(defaultPx, cornerX, 1, chunkX, heightmapX, heightmapY, face, faceWorldSize);
        float pz = ResolvePlaneZ(defaultPz, cornerY, 1, chunkY, heightmapX, heightmapY, face, faceWorldSize);

        return ComputeFaceSphereCorner(face, px, pz);
    }

    private TreeDecoder.DecodedTreeInstance[] decodedTreeBuffer = Array.Empty<TreeDecoder.DecodedTreeInstance>();

    public ArraySegment<TreeDecoder.DecodedTreeInstance> GetDecodedTreesForChunk(int packed, FaceId face)
    {
        var treesSegment = cellReader.GetTreesForPackedChunk(packed, face, numberOfChunks);
        if (treesSegment.Count == 0)
            return default;

        var geometry = GetChunkGeometryForTrees(packed, face);
        
        if (!geometry.IsValid)
            return default;

        int count = treesSegment.Count;
        if (decodedTreeBuffer.Length < count)
            decodedTreeBuffer = new TreeDecoder.DecodedTreeInstance[count * 2];

        TreeDecoder.DecodeTreeBatch(treesSegment, decodedTreeBuffer, geometry, sphereCenter, sphereRadius, treeMaxHeight);
        return new ArraySegment<TreeDecoder.DecodedTreeInstance>(decodedTreeBuffer, 0, count);
    }
#endif

    [ContextMenu("Reload All Outer Chunks")]
    private void ReloadAllOuterChunks()
    {
        if(chunkRegistry != null)
        {
            chunkRegistry.ReloadAll();
        }
    }

    // ===== CANOPY UV CACHE BAKE =====

    /// <summary>
    /// Trigger the one-time canopy UV cache bake from the Inspector context menu (Play mode only).
    /// Right-click the ChunkManager component header → “Bake Canopy UV Cache”.
    /// Iterates every valid chunk in the world, loads its tree data, projects each tree onto
    /// the chunk mesh to find its splatmap UV, quantizes to 2 bytes, and saves the result to
    /// StreamingAssets/MapAssets/CanopyUVCache.bytes.
    /// After saving, the new cache is hot-reloaded so subsequent generation cycles use it
    /// immediately without restarting Play mode.
    /// </summary>
    [ContextMenu("Bake Canopy UV Cache")]
    public void BakeCanopyUVCacheMenu()
    {
        if (!Application.isPlaying)
        {
            Debug.LogError("[CanopyUVCache] Bake must be triggered in Play mode.");
            return;
        }
        StartCoroutine(BakeCanopyUVCacheCoroutine());
    }

    public System.Collections.IEnumerator BakeCanopyUVCacheCoroutine()
    {
        Debug.Log("[CanopyUVCache] Bake started — iterating all valid chunks...");
        int totalSlots = totalStorageSlots;
        int[] offsets   = new int[totalSlots];
        for (int i = 0; i < totalSlots; i++) offsets[i] = -1;

        // Use a List<byte> as a growable UV data stream; convert to array at the end.
        var uvList = new System.Collections.Generic.List<byte>(1 << 21); // pre-alloc 2 MB

        // Reusable managed arrays for mesh projection (same pattern as ChunkBatcher).
        int[]     trisStaging  = Array.Empty<int>();
        Vector3[] vertsStaging = Array.Empty<Vector3>();
        Vector2[] uvsStaging   = Array.Empty<Vector2>();

        int processedChunks = 0;
        int totalTrees      = 0;
        int skippedChunks   = 0;

        int mapsPerRow = maxX - minX + 1;

        var allFaces = (FaceId[])Enum.GetValues(typeof(FaceId));

        for (sbyte mapX = minX; mapX <= maxX; mapX++)
        {
            for (sbyte mapY = minX; mapY <= maxX; mapY++)
            {
                foreach (FaceId face in allFaces)
                {
                    for (sbyte cY = 0; cY < numberOfChunks; cY++)
                    {
                        for (sbyte cX = 0; cX < numberOfChunks; cX++)
                        {
                            int packed     = STPTMEUtils.WriteFourSBytesInInt(mapX, mapY, cX, cY);
                            int globalIdx  = globalIndexCalculator.GetIndex(packed);
                            int slot       = FaceIdUtility.GetStorageIndex(globalIdx, face);

                            if (!angularChunkData[slot].HasValue) continue;

                            // Sync-load raw trees (loads cell from disk if not cached).
                            var rawTrees = cellReader.GetTreesForPackedChunkSync(packed, face, numberOfChunks);
                            if (rawTrees.Count == 0) { processedChunks++; continue; }

                            // Generate LOD0 mesh for UV projection.
                            var meshData = GenerateMeshData(packed, 0, face);
                            if (!meshData.isValid)
                            {
                                meshData.Dispose();
                                skippedChunks++;
                                continue;
                            }

                            // Copy NativeArrays to managed once per chunk.
                            if (trisStaging.Length  < meshData.triCount)  trisStaging  = new int[meshData.triCount];
                            if (vertsStaging.Length < meshData.vertCount) vertsStaging = new Vector3[meshData.vertCount];
                            if (uvsStaging.Length   < meshData.vertCount) uvsStaging   = new Vector2[meshData.vertCount];
                            NativeArray<int>.Copy(meshData.tris,   trisStaging,  meshData.triCount);
                            NativeArray<Vector3>.Copy(meshData.verts, vertsStaging, meshData.vertCount);
                            NativeArray<Vector2>.Copy(meshData.uvs,   uvsStaging,   meshData.vertCount);
                            int savedTriCount  = meshData.triCount;
                            int savedVertCount = meshData.vertCount;
                            meshData.Dispose();

                            // Tree decoding moved to blotch system — stub.
                            var geo = default(TreeDecoder.ChunkGeometry);

                            offsets[slot] = uvList.Count;

                            int srcEnd = rawTrees.Offset + rawTrees.Count;
                            for (int ti = rawTrees.Offset; ti < srcEnd; ti++)
                            {
                                var decoded = TreeDecoder.DecodeTree(
                                    rawTrees.Array[ti], geo, sphereCenter, sphereRadius, treeMaxHeight);

                                Vector2 uv;
                                if (!ChunkRegistry.TryProjectPointToChunkUV(
                                        decoded.worldPosition,
                                        savedTriCount, trisStaging,
                                        savedVertCount, vertsStaging, uvsStaging,
                                        out uv))
                                {
                                    uv = new Vector2(0.5f, 0.5f); // centre fallback
                                }

                                uvList.Add((byte)Mathf.Clamp(Mathf.RoundToInt(uv.x * 255f), 0, 255));
                                uvList.Add((byte)Mathf.Clamp(Mathf.RoundToInt(uv.y * 255f), 0, 255));
                                totalTrees++;
                            }

                            processedChunks++;

                            // Yield every 25 chunks to keep the editor responsive.
                            if (processedChunks % 25 == 0)
                            {
                                Debug.Log($"[CanopyUVCache] Baked {processedChunks} chunks, " +
                                          $"{totalTrees:N0} trees so far...");
                                yield return null;
                            }
                        }
                    }
                }
            }
        }

        byte[] uvData = uvList.ToArray();
        string savePath = System.IO.Path.Combine(
            Application.streamingAssetsPath, CanopyUVCache.ASSET_RELATIVE_PATH);
        CanopyUVCache.Save(savePath, offsets, uvData);

        Debug.Log($"[CanopyUVCache] Bake complete: {processedChunks} chunks, " +
                  $"{totalTrees:N0} trees, {skippedChunks} skipped, " +
                  $"{uvData.Length / 1024} KB — saved to {savePath}");

        // Hot-reload the new cache so the running session benefits immediately.
        var newCache = CanopyUVCache.LoadFromStreamingAssets(totalSlots);
        if (newCache != null && chunkRegistry != null)
        {
            chunkRegistry.SetCanopyUVCache(newCache);
            Debug.Log("[CanopyUVCache] Cache hot-reloaded into ChunkRegistry.");
        }
    }

    /// <summary>
    /// Exact cube-projected direction from sphereCenter for a point at (localX, localZ)
    /// meters within the given chunk. Ported from ImpostorSolver.compute's
    /// ComputeExactInstanceDir — see that function's comments for why this must be exact
    /// rather than a per-chunk tangent-plane approximation (the approximation only agrees
    /// with the terrain mesh at chunk centre and diverges with distance from it).
    ///
    /// Public and reusable on purpose: both blotch placement (below) and terrain-anchored
    /// map object unpacking (MapObjectCompactFormat/CellObjectReader) need this identical
    /// computation, and duplicating it a third time is exactly the kind of drift that has
    /// caused most of the positional bugs chased in this system.
    /// </summary>
    public Vector3 ComputeExactInstanceDir(int chunkPacked, FaceId face, float localXMeters, float localZMeters)
    {
        STPTMEUtils.ReadFourSBytesFromInt(chunkPacked, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);

        int globalFlatIdx = globalIndexCalculator.GetIndex(chunkPacked);
        int storageSlot = FaceIdUtility.GetStorageIndex(globalFlatIdx, face);

        float chunkSize = terrainSize / tilingFactor;
        float chunkWorldSize = settings.halfChunkLinearSize * 2f;
        float cellWorldSize = chunkWorldSize * numberOfChunks;
        // Deliberately derived from mapsPerRow, NOT a faceWorldSize parameter: mapsPerRow is
        // the true cells-per-face-axis and differs from (maxX-minX+1) when
        // heightmapSubdivisions > 0. The GPU uploads mapsPerRow, so using it here is what
        // keeps the two paths bit-comparable.
        float exactFaceWorldSize = mapsPerRow * cellWorldSize;

        int cellIdx = MapFaceIndex(mapX, mapY, face);
        Vector2 cellStart = (cellIdx >= 0 && cellIdx < heightmapsStartingPositions.Length)
            ? heightmapsStartingPositions[cellIdx]
            : Vector2.zero;

        Vector2 widthRatio = (cpuChunkWidthRatio != null && storageSlot >= 0 && storageSlot < cpuChunkWidthRatio.Length)
            ? cpuChunkWidthRatio[storageSlot]
            : Vector2.one;

        float dispLX = localXMeters / chunkSize;
        float dispLZ = localZMeters / chunkSize;

        float px = cellStart.x + chunkWorldSize * chunkX + dispLX * chunkWorldSize * widthRatio.x;
        float pz = cellStart.y + chunkWorldSize * chunkY + dispLZ * chunkWorldSize * widthRatio.y;

        float percentX = exactFaceWorldSize > 0f ? px / exactFaceWorldSize : 0f;
        float percentY = exactFaceWorldSize > 0f ? pz / exactFaceWorldSize : 0f;
        float factorA = (percentX - 0.5f) * 2f;
        float factorB = (percentY - 0.5f) * 2f;

        FaceIdUtility.GetFaceAxes(face, out Vector3 localUp, out Vector3 axisA, out Vector3 axisB);
        return (localUp + factorA * axisA + factorB * axisB).normalized;
    }

    /// <summary>
    /// Terrain height (meters above sphereRadius) at (localX, localZ) within the given chunk,
    /// via exact per-triangle interpolation matching the terrain mesh's real triangulation
    /// (bl-tr diagonal) — NOT bilinear. Public/reusable for the same reason as
    /// ComputeExactInstanceDir above.
    /// </summary>
    public float SampleTerrainHeight(int chunkPacked, FaceId face, float localXMeters, float localZMeters)
    {
        STPTMEUtils.ReadFourSBytesFromInt(chunkPacked, out sbyte mapX, out sbyte mapY, out sbyte chunkX, out sbyte chunkY);
        Vector2SByte map = new Vector2SByte(mapX, mapY);

        if (!cellReader.IsCached(map, face))
            cellReader.GetOrLoadSync(map, face);

        ushort[,] heights = cellReader.GetHeights(map, 0, face, sync: true);
        if (heights == null) return 0f;

        byte cellDsSteps = GetCellDsSteps(map, face);
        int faceChunkStepFull = GetFaceChunkStep(face);
        int faceChunkStep = Mathf.Max(1, faceChunkStepFull >> cellDsSteps);
        float facePixelDistanceFull = GetFacePixelDistance(face);
        float facePixelDistance = facePixelDistanceFull * (1 << cellDsSteps);
        float faceHeightScale = GetFaceMaxHeight(face) / 65535f;

        int xOffset = faceChunkStep * chunkX;
        int yOffset = faceChunkStep * chunkY;

        float contX = localXMeters / facePixelDistance + xOffset;
        float contY = localZMeters / facePixelDistance + yOffset;

        int x0 = Mathf.FloorToInt(contX);
        int y0 = Mathf.FloorToInt(contY);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        int maxX = heights.GetLength(1) - 1;
        int maxY = heights.GetLength(0) - 1;
        x0 = Mathf.Clamp(x0, 0, maxX);
        x1 = Mathf.Clamp(x1, 0, maxX);
        y0 = Mathf.Clamp(y0, 0, maxY);
        y1 = Mathf.Clamp(y1, 0, maxY);

        float fracX = contX - x0;
        float fracY = contY - y0;

        float h00 = heights[y0, x0] * faceHeightScale;
        float h10 = heights[y0, x1] * faceHeightScale;
        float h01 = heights[y1, x0] * faceHeightScale;
        float h11 = heights[y1, x1] * faceHeightScale;

        // Per-triangle planar interpolation matching the terrain mesh's actual triangulation
        // (bl-tr diagonal) — see GenerateMeshData / ImpostorSolver.compute's TriInterp.
        if (fracY <= fracX)
            return h00 + (h10 - h00) * fracX + (h11 - h10) * fracY;   // triangle {bl,br,tr}
        return h00 + (h11 - h01) * fracX + (h01 - h00) * fracY;       // triangle {bl,tr,tl}
    }

    public Vector3 GetBlotchWorldPosition(BlotchData blob, float faceWorldSize)
    {
        float chunkSize = terrainSize / tilingFactor;
        blob.GetLocalPosition(chunkSize, out float localX, out float localZ);

        Vector3 instanceDir = ComputeExactInstanceDir(blob.chunkPacked, blob.Face, localX, localZ);
        float h = SampleTerrainHeight(blob.chunkPacked, blob.Face, localX, localZ);

        return sphereCenter + instanceDir * (sphereRadius + h);
    }

    private void OnDestroy()
    {
        chunkMaterialManager?.Dispose();
        textureStreamer?.Dispose();
    }
}