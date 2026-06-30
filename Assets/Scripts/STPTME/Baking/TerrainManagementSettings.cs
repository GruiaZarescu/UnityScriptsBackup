using UnityEngine;

[CreateAssetMenu(fileName = "TerrainManagementSettings", menuName = "Scriptable Objects/TerrainManagementSettings")]
public class TerrainManagementSettings : ScriptableObject
{
    private static TerrainManagementSettings _instance;

    public static TerrainManagementSettings Instance
    {
        get
        {
            if (_instance == null)
            {
                // Loads the asset from Resources folder
                _instance = Resources.Load<TerrainManagementSettings>("TerrainManagementSettings");

                if (_instance == null)
                {
                    Debug.LogError("TerrainManagementSettings asset not found in Resources!");
                }
            }
            return _instance;
        }
    }

    [Header("Terrain Settings")]

    public int numberOfChunks
    {
        get
        {
            int denom = 1 << heightmapSubdivisions;
            return tilingFactor / denom;
        }
    }
    public sbyte maxX
    {
        get
        {
            return (sbyte)(Mathf.Sqrt(numberOfTerrains)-1);
        }
    }
    public sbyte minX
    {
        get
        {
            return (sbyte)(-1 * (int)Mathf.Sqrt(numberOfTerrains));
        }
    }
    public float terrainSize = 2387.5f;//Should be baked
    public int numberOfTerrains = 36;//Can't we actually calculate number of terrains
    public int tilingFactor = 16;
    public float sphereRadius = 7162f;//Should be baked, it's the side length of one plane. Could be calculated as terrainSize * Mathf.Sqrt(numberOfTerrains)
    public Vector3 sphereCenter = Vector3.zero;
    public int heightmapSubdivisions = 1;
    public float maxHeight = 10000f;//Is already baked, should stop using eveywhere else
    public byte maxLOD =7;//Should be derived in the code, not hardcoded
    public ushort maxVertsPerOuterChunkMesh = 32768;
    public ushort maxChunkGenWorkPerFrame = 16384;
    public byte maxChunkGenOpsPerFrame = 10;
    public int nonBatchedOuterChunkRings = 3;
    [Range(-1f, 1f)]
    [Tooltip("Cosine-space bias applied to horizon culling. Positive values make horizon culling looser; negative values make it tighter.")]
    public float horizonCosineMargin = 0f;

    [Header("Horizon Recompute Throttling")]
    [Tooltip("Minimum number of frames between per-batch horizon recomputations. 1 = every frame. Higher values reduce CPU cost at the price of slightly stale horizon classifications.")]
    [Min(1)]
    public int horizonRecomputeFrameInterval = 1;
    [Tooltip("Minimum player horizontal movement (meters) since last horizon recompute required to trigger a new one. Set to 0 to disable movement gating.")]
    [Min(0f)]
    public float horizonRecomputePosThreshold = 10f;
    [Tooltip("Minimum player altitude change (meters) since last horizon recompute required to trigger a new one. Set to 0 to disable altitude gating.")]
    [Min(0f)]
    public float horizonRecomputeAltThreshold = 2f;
    /// <summary>
    /// DEBUG ONLY — forces every chunk to be a standalone GameObject (no batching).
    /// Useful for isolating whether a visual/logic fault is caused by the batching system.
    /// Must be false during normal gameplay; severely impacts draw-call count.
    /// </summary>
    public bool debugDisableBatching = true;
    public bool debugLoadFullSphere = false;

    [Header("Feature Toggles")]
    public bool enableTextureGeneration = true;

    [Header("Far-LOD Canopy Overlay")]
    [Tooltip("Minimum chunk LOD at which the runtime canopy overlay system activates. LODs below this "
           + "(typically LOD0) show individual tree geometry instead. Overlay only on LOD1+ ensures trees at "
           + "close range are not hidden. Default: 1 (overlay on LOD1-maxLOD).")]
    [Range(0, 7)]
    public byte canopyStartLOD = 1;
    public float faceWorldSize => Mathf.Sqrt(numberOfTerrains) * terrainSize;
    
    /// <summary>
    /// Half the linear size of a chunk in world units.
    /// Derived from terrainSize / tilingFactor * 0.5.
    /// Used by visibility system, impostor renderer, and CPU prefab positioning.
    /// </summary>
    public float halfChunkLinearSize => (terrainSize / tilingFactor) * 0.5f;
}
