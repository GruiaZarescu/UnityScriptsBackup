#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using CustomTypes;


using UnityEditor;


public class MeshSaver : MonoBehaviour
{
    private struct TerrainBakeInfo
    {
        public Terrain terrain;
        public int terrainGridX;
        public int terrainGridY;
    }

    private TerrainManagementSettings settings;
    [SerializeField, Tooltip("Assign the root prefab whose children are the 6 face containers in fixed order: Terrains_top, Terrains_bot, Terrains_left, Terrains_right, Terrains_forward, Terrains_back.")]
    private GameObject mainTerrainPrefab;
    [Header("Face Heightmap Orientation")]
    [SerializeField, Tooltip("Pre-bake rotation for the Up face heightmap before writing cells.")]
    private FaceContainerRotation upFaceRotation = FaceContainerRotation.Identity;
    [SerializeField, Tooltip("Optional mirror applied AFTER rotation, in plane space. Use to flip a face along its seam with the bottom face.")]
    private FaceContainerMirror upFaceMirror = FaceContainerMirror.None;
    [SerializeField, Tooltip("Pre-bake rotation for the Down face heightmap before writing cells.")]
    private FaceContainerRotation downFaceRotation = FaceContainerRotation.Identity;
    [SerializeField, Tooltip("Optional mirror applied AFTER rotation, in plane space.")]
    private FaceContainerMirror downFaceMirror = FaceContainerMirror.None;
    [SerializeField, Tooltip("Pre-bake rotation for the Left face heightmap before writing cells.")]
    private FaceContainerRotation leftFaceRotation = FaceContainerRotation.Rot180;
    [SerializeField, Tooltip("Optional mirror applied AFTER rotation, in plane space.")]
    private FaceContainerMirror leftFaceMirror = FaceContainerMirror.None;
    [SerializeField, Tooltip("Pre-bake rotation for the Right face heightmap before writing cells.")]
    private FaceContainerRotation rightFaceRotation = FaceContainerRotation.Identity;
    [SerializeField, Tooltip("Optional mirror applied AFTER rotation, in plane space.")]
    private FaceContainerMirror rightFaceMirror = FaceContainerMirror.None;
    [SerializeField, Tooltip("Pre-bake rotation for the Forward face heightmap before writing cells.")]
    private FaceContainerRotation forwardFaceRotation = FaceContainerRotation.RotCW;
    [SerializeField, Tooltip("Optional mirror applied AFTER rotation, in plane space.")]
    private FaceContainerMirror forwardFaceMirror = FaceContainerMirror.None;
    [SerializeField, Tooltip("Pre-bake rotation for the Back face heightmap before writing cells.")]
    private FaceContainerRotation backFaceRotation = FaceContainerRotation.RotCCW;
    [SerializeField, Tooltip("Optional mirror applied AFTER rotation, in plane space.")]
    private FaceContainerMirror backFaceMirror = FaceContainerMirror.None;
    [SerializeField] private MapObjectPrototypeRegistry prototypeRegistryForBake;
    [Header("Splatmap Bake Settings")]
    [SerializeField, Tooltip("Per-tier splatmap resolutions. Index = splat tier. Use 0 for native, positive for absolute pixels, negative for fractions of native (e.g., -2 = half).")]
    private int[] splatTierResolutions = new int[] { -1, -2, -4, -8 };
    [SerializeField, Tooltip("Index = mesh LOD, value = splat tier index. Controls which baked splatmap tier each runtime chunk LOD samples.")]
    private int[] lodToSplatTier = new int[] { 0, 0, 1, 1, 2, 2, 3, 3 };
    [SerializeField, Min(1), Tooltip("Resolution used for the shared terrain layer diffuse textures baked into the layer texture array.")]
    private int layerTextureResolution = 512;
    [SerializeField, Min(0), Tooltip("Border overlap in pixels for splatmaps at tier 0 resolution. Prevents seams when sampling near chunk edges.")]
    private int splatBorderPixels = 1;
    [Header("Heightmap Normal Bake Settings")]
    [SerializeField] private bool bakeHeightmapNormals = true;
    [SerializeField, Tooltip("Per-tier resolution for heightmap-derived normal maps. Index = normal tier.")]
    private int[] normalTierResolutions = new int[] { 256, 128, 64, 32 };
    [SerializeField, Tooltip("Index = mesh LOD, value = heightmap-normal tier index.")]
    private int[] lodToNormalTier = new int[] { 0, 0, 1, 1, 2, 3, 3 };
    [SerializeField, Tooltip("Border overlap in pixels for baked heightmap normal maps.")]
    private int normalBorderPixels = 1;

    [Header("Variable Heightmap Resolution")]
    [SerializeField, Tooltip("Parent transform whose direct children are world-space cubes marking subdivided heightmap regions to spare from downsampling. AABB of each cube (Renderer.bounds if present, else lossyScale around position) intersected against subdivided cell footprints determines protected cells per face.")]
    private Transform protectionCubesParent;
    [SerializeField, Min(0), Tooltip("Number of downsampling steps applied to non-protected subdivided heightmaps. 0 = no downsampling. 1 = halve resolution. 2 = quarter, etc. Each step uses the same decimation algorithm the runtime uses for higher LODs (STPTMEUtils.GetHeightsLodUshort).")]
    private int downsamplingSteps = 0;

    // ── Splatmap cache — extracted per-face, written after all faces processed ──
    private Dictionary<(int tx, int ty), List<(sbyte mx, sbyte my, byte[] data)>>[] faceBlobsCache;
    private Dictionary<(sbyte mx, sbyte my), sbyte>[] faceClassificationsCache;

    private int tilingFactor;
    private float sphereRadius;
    private Vector3 sphereCenter;
    private int heightmapSubdivisions;
    private float maxHeight;
    private int maxLOD;//Optimization: maxLOD also byte, we'll never have a lod higher than 255

    private const float maxShiftMeters = 64f;//This should go to settings

    // Accumulates tree density across both hemispheres for collider pool sizing.
    // Populated inside GenerateAssets, consumed after both calls.
    private int[] _bakeTreesPerPrototype;
    private int _bakeValidChunkCount;//All chunks are valid with 6 planes, this should be able to go

    private void OnValidate()
    {
        // Validation of prefab structure happens at bake time
    }

    [SerializeField, Tooltip("Check this before Generate Assets to force a full re-bake, ignoring the BakeManifest.")]
    private bool forceRebake = false;

    [ContextMenu("Generate Assets")]
    public void SortTerrainsForGeneration()
    {
        bool force = forceRebake;
        forceRebake = false; // Reset after reading so next bake is incremental again.
        
        // CRITICAL: Clear Selection and focus to break ObjectField/Inspector UI references
        Selection.activeObject = null;
        EditorGUIUtility.hotControl = 0;
        EditorGUIUtility.keyboardControl = 0;
        
        // Close all Inspector windows to force them to release their ObjectField UI state
        // The UIElements.ObjectField holds callbacks in EventWithPerformanceTracker that
        // keep references to the prefab even after Selection is cleared.
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (window != null && window.GetType().Name.Contains("Inspector"))
                window.Close();
        }
        
        SortTerrainsForGenerationInternal(force);
    }

    [ContextMenu("Force Re-bake (ignore manifest)")]
    private void ForceRebake()
    {
        SortTerrainsForGenerationInternal(true);
    }

    private void SortTerrainsForGenerationInternal(bool forceRebake)
    {
        settings = TerrainManagementSettings.Instance;
        tilingFactor = settings.tilingFactor;
        sphereRadius = settings.sphereRadius;
        sphereCenter = settings.sphereCenter;
        heightmapSubdivisions = settings.heightmapSubdivisions;
        maxHeight = settings.maxHeight;
        maxLOD = settings.maxLOD;

        string cellFolderPath = Path.Combine(Application.streamingAssetsPath, "MapAssets/Cells");
        string adjacentDataPath = Path.Combine(Application.streamingAssetsPath, "MapAssets/AdjacentData");

        if (!Directory.Exists(cellFolderPath))
            Directory.CreateDirectory(cellFolderPath);
        if (!Directory.Exists(adjacentDataPath))
            Directory.CreateDirectory(adjacentDataPath);

        // Load existing manifest for change detection.
        // When forceRebake is true, skip the manifest entirely — treat all terrains as dirty.
        BakeManifest.Entry[] priorManifest = forceRebake ? System.Array.Empty<BakeManifest.Entry>() : BakeManifest.Load();

        // Collect entries for terrains that actually changed. Written back at the end.
        var newManifest = new System.Collections.Generic.List<BakeManifest.Entry>();

        // Track which faces have at least one dirty terrain (need AdjacentData rebuild).
        bool[] faceIsDirty = new bool[FaceIdUtility.StorageFaceCount];

        // Initialise tree-density accumulators (sized to prototype count, or 256 max if no registry assigned)
        int _bakeProtoCount = (prototypeRegistryForBake != null && prototypeRegistryForBake.entries != null)
            ? prototypeRegistryForBake.entries.Length
            : 256;
        _bakeTreesPerPrototype = new int[_bakeProtoCount];
        _bakeValidChunkCount = 0;

        // ── Immediate Layer Baking with Aggressive Memory Release ──
        // Extract layers from the first available terrain and bake them immediately.
        // This prevents TerrainData assets from being held in memory throughout the entire process.
        // 
        // CRITICAL: The Undo system holds references to all destroyed objects, preventing GC.
        // We must clear Undo after destroying the temporary prefab instance.
        if (mainTerrainPrefab != null)
        {
            // Instantiate WITHOUT recording Undo (we'll clear it all later anyway)
            GameObject layerTemp = Instantiate(mainTerrainPrefab);
            Terrain firstTerrain = layerTemp.GetComponentInChildren<Terrain>();
            if (firstTerrain != null && firstTerrain.terrainData != null)
            {
                // Copy TerrainLayer array BEFORE destroying to prevent deep references
                TerrainLayer[] layers = firstTerrain.terrainData.terrainLayers;
                var tbSettings = TextureBaker.TextureBakeSettings.Default((byte)maxLOD);
                if (!TryApplyMeshSaverTextureBakeSettings((byte)maxLOD, ref tbSettings))
                    tbSettings = TextureBaker.TextureBakeSettings.Default((byte)maxLOD);

                string layerFolder = Path.Combine(Application.streamingAssetsPath, "MapAssets", "TerrainLayers");
                if (!Directory.Exists(layerFolder)) Directory.CreateDirectory(layerFolder);
                
                TextureBaker.BakeTerrainLayers(layers, layerFolder, tbSettings);
                Debug.Log("[MeshSaver] Layer textures baked immediately to release TerrainData references.");
            }
            
            // Destroy WITHOUT Undo (allowDestroyingAssets=true in case prefab is in scene)
            DestroyImmediate(layerTemp, true);
            
            // CRITICAL: Clear Undo buffer to break reference chain
            // The Undo system was holding references to the destroyed GameObject and its TerrainData,
            // preventing the IsPersistent Texture2D objects from being garbage collected.
            Undo.ClearAll();
            
            // Force immediate unload of unused assets (excludes Mono script references)
            EditorUtility.UnloadUnusedAssetsImmediate(false);
            Debug.Log("[MeshSaver] Memory cleanup: Undo cleared, unused assets unloaded.");
        }

        // Force re-bake: wipe all cell and adjacent data upfront so every face is regenerated.
        if (forceRebake)
        {
            foreach (string file in System.IO.Directory.GetFiles(cellFolderPath, "CellGroup_*.bytes"))
                System.IO.File.Delete(file);
            foreach (string file in System.IO.Directory.GetFiles(adjacentDataPath, "AdjacentData_*.bytes"))
                System.IO.File.Delete(file);
            for (int f = 0; f < faceIsDirty.Length; f++) faceIsDirty[f] = true;
            Debug.Log("[MeshSaver] Force re-bake: wiped all existing cell and adjacent data.");
        }

        // Initialise splatmap cache arrays (one per face, filled during per-face loop)
        faceBlobsCache = new Dictionary<(int, int), List<(sbyte, sbyte, byte[])>>[6];
        faceClassificationsCache = new Dictionary<(sbyte, sbyte), sbyte>[6];
        for (int f = 0; f < 6; f++)
        {
            faceBlobsCache[f] = new Dictionary<(int, int), List<(sbyte, sbyte, byte[])>>();
            faceClassificationsCache[f] = new Dictionary<(sbyte, sbyte), sbyte>();
        }
        // Prefab structure validation happens during terrain collection

        // Dump the per-face orientation values that this bake will actually use.
        // If this log doesn't reflect the inspector values, the bake is not seeing them
        // (script reload / serialization issue) and the rotation will silently appear to
        // do nothing regardless of what's typed in the inspector.
        var sb = new System.Text.StringBuilder("[MeshSaver] Bake orientations: ");
        for (int f = 0; f < FaceIdUtility.StorageFaceCount; f++)
            sb.Append(((FaceId)f)).Append('=').Append(GetFaceOrientation((FaceId)f)).Append(' ');
        Debug.Log(sb.ToString());

        for (int faceIndex = 0; faceIndex < FaceIdUtility.StorageFaceCount; faceIndex++)
        {
            if (mainTerrainPrefab == null)
            {
                Debug.LogError("[MeshSaver] mainTerrainPrefab not assigned. Cannot bake.");
                break;
            }

            FaceId face = (FaceId)faceIndex;

            // Instantiate the prefab once per face so only one face's terrains are loaded at a time.
            GameObject prefabInstance = Instantiate(mainTerrainPrefab);
            Transform faceContainer = null;
            int childIdx = 0;
            foreach (Transform child in prefabInstance.transform)
            {
                if (childIdx == faceIndex) { faceContainer = child; break; }
                childIdx++;
            }

            if (faceContainer == null)
            {
                Debug.LogWarning($"[MeshSaver] Face '{face}' child not found in prefab (child index {faceIndex}). Skipping.");
                DestroyImmediate(prefabInstance);
                Undo.ClearAll();
                EditorUtility.UnloadUnusedAssetsImmediate(false);
                GC.Collect();
                continue;
            }

            // Collect all Terrain components under this face container
            TerrainBakeInfo[] terrainInfos = CollectTerrainsForFace(faceContainer, face);
            if (terrainInfos.Length == 0)
            {
                Debug.LogWarning($"[MeshSaver] Face '{face}' has no terrains assigned. Skipping.");
                DestroyImmediate(prefabInstance);
                Undo.ClearAll();
                EditorUtility.UnloadUnusedAssetsImmediate(false);
                GC.Collect();
                continue;
            }

            // Check each terrain against the manifest. If ALL are unchanged, skip this face entirely.
            bool faceNeedsBake = false;
            for (int ti = 0; ti < terrainInfos.Length; ti++)
            {
                ref var info = ref terrainInfos[ti];
                byte gx = (byte)info.terrainGridX;
                byte gy = (byte)info.terrainGridY;
                if (BakeManifest.IsUnchanged(info.terrain, info.terrain.terrainData, face, gx, gy, priorManifest))
                {
                    // Reuse last manifest entry for this terrain.
                    for (int pi = 0; pi < priorManifest.Length; pi++)
                    {
                        var pe = priorManifest[pi];
                        if (pe.face == face && pe.terrainGridX == gx && pe.terrainGridY == gy)
                        {
                            newManifest.Add(pe);
                            break;
                        }
                    }
                    Debug.Log($"[MeshSaver] Skipping unchanged terrain ({face}, {gx}, {gy}).");
                }
                else
                {
                    faceNeedsBake = true;
                    faceIsDirty[(int)face] = true;
                }
            }

            if (!faceNeedsBake)
            {
                Debug.Log($"[MeshSaver] Face '{face}' fully unchanged — skipping.");
                
                // Still need to unload TerrainData to free persistent textures
                TerrainData[] tdToUnload = new TerrainData[terrainInfos.Length];
                for (int ti = 0; ti < terrainInfos.Length; ti++)
                {
                    if (terrainInfos[ti].terrain != null && terrainInfos[ti].terrain.terrainData != null)
                    {
                        tdToUnload[ti] = terrainInfos[ti].terrain.terrainData;
                        terrainInfos[ti].terrain.terrainData = null;
                    }
                }
                
                DestroyImmediate(prefabInstance);
                
                for (int ti = 0; ti < tdToUnload.Length; ti++)
                {
                    if (tdToUnload[ti] != null)
                        Resources.UnloadAsset(tdToUnload[ti]);
                        AssetDatabase.Refresh();
                }
                
                Undo.ClearAll();
                EditorUtility.UnloadUnusedAssetsImmediate(false);
                GC.Collect();
                continue;
            }

            // Delete only this face's cell files (not all cells) so unchanged faces keep their data.
            string facePrefix = FaceIdUtility.GetFilePrefix(face);
            foreach (string file in Directory.GetFiles(cellFolderPath, $"CellGroup_{facePrefix}_*.bytes"))
                File.Delete(file);

            // Also delete the stale AdjacentData for this face — GenerateAssets will recreate it.
            string adjFile = Path.Combine(adjacentDataPath, $"AdjacentData_{facePrefix}.bytes");
            if (File.Exists(adjFile)) File.Delete(adjFile);

            GenerateAssets(terrainInfos, face, cellFolderPath, adjacentDataPath);

            // Add manifest entries for all terrains in this face (baked successfully).
            for (int ti = 0; ti < terrainInfos.Length; ti++)
            {
                ref var info = ref terrainInfos[ti];
                var td = info.terrain.terrainData;
                uint treeHash = BakeManifest.HashTrees(td);
                BakeManifest.GetContentHashSplit(td, out ulong lo, out ulong hi);
                newManifest.Add(new BakeManifest.Entry
                {
                    face = face,
                    terrainGridX = (byte)info.terrainGridX,
                    terrainGridY = (byte)info.terrainGridY,
                    contentHashLo = lo,
                    contentHashHi = hi,
                    treeHash = treeHash,
                });
            }

            // ── Extract splatmap data into cache before destroying terrains ──
            var settings = TerrainManagementSettings.Instance;
            var bakeSettings = TextureBaker.TextureBakeSettings.Default(settings.maxLOD);
            if (!TryApplyMeshSaverTextureBakeSettings(settings.maxLOD, ref bakeSettings))
                bakeSettings = TextureBaker.TextureBakeSettings.Default(settings.maxLOD);

            // Convert TerrainBakeInfo[] to List<TerrainInfo> for TextureBaker
            var tiList = new List<TextureBaker.TerrainInfo>();
            for (int ti = 0; ti < terrainInfos.Length; ti++)
                tiList.Add(new TextureBaker.TerrainInfo
                {
                    terrain = terrainInfos[ti].terrain,
                    gridX = (sbyte)terrainInfos[ti].terrainGridX,
                    gridY = (sbyte)terrainInfos[ti].terrainGridY
                });

            var (blobs, classifications) = TextureBaker.BakeFaceSplatmaps(
                tiList, face, GetFaceOrientation(face),
                (1 << settings.heightmapSubdivisions), settings.minX, bakeSettings);

            // ── Write this face's splatmap files IMMEDIATELY, then free the memory ──
            TextureBaker.WriteFaceSplatmaps((int)face, blobs, classifications);
            blobs = null;
            classifications = null;

            // ── Destroy the entire prefab instance NOW to free memory ──
            // CRITICAL: TerrainData is a disk-based ScriptableObject asset that holds persistent native Texture2D objects.
            // Simply clearing references is not enough - we must explicitly unload the TerrainData asset itself.
            
            // Step 1: Extract TerrainData asset references BEFORE destroying to unload them explicitly
            TerrainData[] terrainDataToUnload = new TerrainData[terrainInfos.Length];
            for (int ti = 0; ti < terrainInfos.Length; ti++)
            {
                if (terrainInfos[ti].terrain != null && terrainInfos[ti].terrain.terrainData != null)
                {
                    terrainDataToUnload[ti] = terrainInfos[ti].terrain.terrainData;
                    terrainInfos[ti].terrain.terrainData = null;
                }
            }
            
            // Step 2: Destroy the prefab instance
            DestroyImmediate(prefabInstance);
            
            // Step 3: Explicitly unload the TerrainData assets to release their persistent Texture2D objects
            // Resources.UnloadAsset() forces native memory to be released
            for (int ti = 0; ti < terrainDataToUnload.Length; ti++)
            {
                if (terrainDataToUnload[ti] != null)
                {
                    Resources.UnloadAsset(terrainDataToUnload[ti]);
                    AssetDatabase.Refresh();
                }
            }
            terrainDataToUnload = null;
            
            // Step 4: Clear Undo buffer AGAIN (per-face cleanup)
            // The per-face instantiation and processing may have recorded new undo operations
            Undo.ClearAll();
            
            // Step 5: Force immediate unload with aggressive settings
            EditorUtility.UnloadUnusedAssetsImmediate(false);
            
            // Step 6: Triple GC to ensure finalization
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            Debug.Log($"[MeshSaver] Freed terrains for face '{face}' (TerrainData assets explicitly unloaded).");
        }

        // Write density stats for the collider pool manager
        string statsPath = Path.Combine(Application.streamingAssetsPath, "MapAssets/TreeColliderStats.bytes");
        TreeBaker.WriteColliderStats(_bakeTreesPerPrototype, _bakeValidChunkCount, statsPath);

        // Only bake textures if at least one face changed (otherwise they're still correct).
        bool anyFaceDirty = false;
        for (int f = 0; f < faceIsDirty.Length; f++) if (faceIsDirty[f]) { anyFaceDirty = true; break; }
        if (anyFaceDirty)
        {
            // Layer textures are baked immediately during prefab instantiation in the face loop above.
            // Splatmaps were already written per-face during the loop.
            Debug.Log("[MeshSaver] Texture bake complete (layers baked immediately, splatmaps per-face).");
        }
        else
        {
            Debug.Log("[MeshSaver] No faces changed — skipping texture bake.");
        }

        // Write manifest so subsequent bakes can skip unchanged terrains.
        BakeManifest.Save(newManifest.ToArray());

        // ── Final Aggressive Memory Cleanup ──
        // The Undo system has been recording all our operations. Even though we cleared it once
        // at the beginning, new operations may have been recorded during the face loop.
        // We must clear it AGAIN before we exit to ensure all references to destroyed objects
        // and their TerrainData assets are completely released.
        Debug.Log("[MeshSaver] Performing final memory cleanup...");
        
        // Clear all caches
        faceBlobsCache = null;
        faceClassificationsCache = null;
        
        // CRITICAL: Null out mainTerrainPrefab to break the InspectorWindow cache chain
        // The SerializableJsonDictionary in the InspectorWindow caches the serialized state
        // of this MeshSaver component, which includes mainTerrainPrefab. This cache holds
        // a reference to the prefab even after all instances are destroyed, preventing GC
        // of the TerrainData assets. By temporarily nulling the field, we force the Inspector
        // cache to release the prefab reference when it next serializes.
        GameObject cachedPrefab = mainTerrainPrefab;
        mainTerrainPrefab = null;
        EditorUtility.SetDirty(this);
        
        // Clear Undo one more time to break reference chains from face processing
        Undo.ClearAll();
        
        // Force immediate unload: Pass false to exclude Mono references, allowing GC to reclaim native memory
        EditorUtility.UnloadUnusedAssetsImmediate(false);
        
        // Explicit garbage collection to finalize cleanup
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        // CRITICAL: Keep Selection/focus cleared to prevent Inspector ObjectField from re-caching the prefab
        Selection.activeObject = null;
        EditorGUIUtility.hotControl = 0;
        EditorGUIUtility.keyboardControl = 0;
        
        // Now restore the prefab reference (no longer cached by Inspector at this point)
        mainTerrainPrefab = cachedPrefab;
        EditorUtility.SetDirty(this);
        
        Debug.Log("[MeshSaver] *** BAKE COMPLETE *** All TerrainData references released. Memory should stabilize.");
        
    }
    /// <summary>Returns the user-configured orientation for the given face, falling back to
    /// the centralized default in <see cref="FaceContainerOrientations.Get"/> if the
    /// serialized array is missing or too short.</summary>
    private FaceContainerOrientation GetFaceOrientation(FaceId face)
    {
        return face switch
        {
            FaceId.Up      => FaceContainerOrientations.Compose(upFaceRotation,      upFaceMirror),
            FaceId.Down    => FaceContainerOrientations.Compose(downFaceRotation,    downFaceMirror),
            FaceId.Left    => FaceContainerOrientations.Compose(leftFaceRotation,    leftFaceMirror),
            FaceId.Right   => FaceContainerOrientations.Compose(rightFaceRotation,   rightFaceMirror),
            FaceId.Forward => FaceContainerOrientations.Compose(forwardFaceRotation, forwardFaceMirror),
            FaceId.Back    => FaceContainerOrientations.Compose(backFaceRotation,    backFaceMirror),
            _ => FaceContainerOrientations.Get(face),
        };
    }

    private FaceContainerOrientation[] GetBakeFaceOrientations()
    {
        return new[]
        {
            FaceContainerOrientations.Compose(upFaceRotation,      upFaceMirror),
            FaceContainerOrientations.Compose(downFaceRotation,    downFaceMirror),
            FaceContainerOrientations.Compose(leftFaceRotation,    leftFaceMirror),
            FaceContainerOrientations.Compose(rightFaceRotation,   rightFaceMirror),
            FaceContainerOrientations.Compose(forwardFaceRotation, forwardFaceMirror),
            FaceContainerOrientations.Compose(backFaceRotation,    backFaceMirror),
        };
    }

    private TerrainBakeInfo[] CollectTerrainsForFace(Transform terrainParent, FaceId face)
    {
        if (terrainParent == null)
            return System.Array.Empty<TerrainBakeInfo>();

        Terrain[] terrains = terrainParent.GetComponentsInChildren<Terrain>(true);
        if (terrains == null || terrains.Length == 0)
            return System.Array.Empty<TerrainBakeInfo>();

        // Filter out terrains with missing terrainData (corrupted or partially destroyed)
        var validTerrains = new List<Terrain>();
        foreach (var t in terrains)
        {
            if (t != null && t.terrainData != null)
                validTerrains.Add(t);
        }
        if (validTerrains.Count == 0)
            return System.Array.Empty<TerrainBakeInfo>();

        FaceContainerOrientation orientation = GetFaceOrientation(face);
        int gridSize = (int)Mathf.Sqrt(validTerrains.Count);
        if(gridSize * gridSize != validTerrains.Count)
        {
            Debug.LogError($"[MeshSaver] Expected a square number of terrains under '{terrainParent.name}' for proper grid inference, but found {validTerrains.Count}. Found terrains must be arranged in a complete grid, square");
            return System.Array.Empty<TerrainBakeInfo>();
        }

        float terrainWorldSize = validTerrains[0].terrainData.size.x;//All terrains of all faces ought to be the same size. This should apply to all terrains given a correct config
        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        foreach (Terrain terrain in validTerrains)
        {
            Vector3 position = terrain.GetPosition();
            minX = Mathf.Min(minX, position.x);
            minZ = Mathf.Min(minZ, position.z);
        }

        TerrainBakeInfo[] sorted = new TerrainBakeInfo[validTerrains.Count];
        bool[] occupied = new bool[validTerrains.Count];
        float tolerance = terrainWorldSize * 0.01f;

        foreach (Terrain terrain in validTerrains)
        {
            Vector3 position = terrain.GetPosition();
            float normalizedX = (position.x - minX) / terrainWorldSize;
            float normalizedZ = (position.z - minZ) / terrainWorldSize;
            int worldGridX = Mathf.RoundToInt(normalizedX);
            int worldGridY = Mathf.RoundToInt(normalizedZ);

            if (Mathf.Abs(normalizedX - worldGridX) > tolerance / terrainWorldSize || Mathf.Abs(normalizedZ - worldGridY) > tolerance / terrainWorldSize)
            {
                Debug.LogError($"[MeshSaver] Terrain '{terrain.name}' under '{terrainParent.name}' is not aligned to the inferred face grid.");
                return System.Array.Empty<TerrainBakeInfo>();
            }

            // Convert from container/world grid to plane grid using the face's container orientation.
            // All downstream code (cell keys, plane positions, runtime addressing) expects plane-space.
            FaceContainerOrientations.GridWorldToPlane(orientation, worldGridX, worldGridY, gridSize, out int planeGridX, out int planeGridY);

            int flatIndex = planeGridY * gridSize + planeGridX;
            if (flatIndex < 0 || flatIndex >= sorted.Length || occupied[flatIndex])
            {
                Debug.LogError($"[MeshSaver] Duplicate or invalid terrain slot world=({worldGridX}, {worldGridY}) plane=({planeGridX}, {planeGridY}) under '{terrainParent.name}'.");
                return System.Array.Empty<TerrainBakeInfo>();
            }

            occupied[flatIndex] = true;
            sorted[flatIndex] = new TerrainBakeInfo
            {
                terrain = terrain,
                terrainGridX = planeGridX,
                terrainGridY = planeGridY,
            };
        }

        return sorted;

        //Upon proof reading, method seems in order functionally
    }

    private void GenerateAssets(TerrainBakeInfo[] terrains, FaceId face, string cellFolderPath, string adjacentDataPath)
    {
        // Generate all chunks to get their corners and center dir, determine validity,
        // extract trees, and write combined cell files
        //If bake is wrong for 6 planes, this method has the highest likelyhood of containing the error. All others have been proof read accurately

        string prefix = FaceIdUtility.GetFilePrefix(face);
        int originalResolution = -1;

        Dictionary<Vector2SByte, bool[]> validChunksPerMap = new Dictionary<Vector2SByte, bool[]>();//All chunks are valid with 6 planes, this is either redundant or name should change
        Dictionary<Vector2SByte, ChunkAngularData[]> angularChunksPerMap = new Dictionary<Vector2SByte, ChunkAngularData[]>();//ChunkAngularData is still relevant for the new chunks, but is it still correctly calculated?
        // Per-cell array of per-chunk max heights (raw ushort, [0..65535] mapped to
        // [0..bakedMaxHeight] meters). Written into AdjacentData for the runtime
        // VisibilitySystem; never used elsewhere at bake time.
        Dictionary<Vector2SByte, ushort[]> chunkMaxHeightPerMap = new Dictionary<Vector2SByte, ushort[]>();
        Dictionary<Vector2SByte, ushort[]> chunkMinHeightPerMap = new Dictionary<Vector2SByte, ushort[]>();
        Dictionary<Vector2SByte, Vector3> heightmapsStartingPositions = new Dictionary<Vector2SByte, Vector3>();
        Dictionary<Vector2SByte, TreeBaker.CellBuildBuffer> cellBuffers = new Dictionary<Vector2SByte, TreeBaker.CellBuildBuffer>();
        Dictionary<Vector2SByte, byte> cellDsStepsPerMap = new Dictionary<Vector2SByte, byte>();
        // Per-cell blotch data: each tree flagged as a blotch is serialized here.
        Dictionary<Vector2SByte, List<BlotchData>> blotchBuffers = new Dictionary<Vector2SByte, List<BlotchData>>();

        int subdivisionsPowerOf2 = (int)Mathf.Pow(2, heightmapSubdivisions);
        int numberOfChunks = tilingFactor / subdivisionsPowerOf2;//Correct, tilingFactor represents the number of chunks per line per original unsubdivided terrain. subddivisionsPowerOf2 represents the number of terrains per line we'll have after subdivision
        int terrainGridSize = Mathf.RoundToInt(Mathf.Sqrt(terrains.Length));

        // Build the per-face set of subdivided cells protected from downsampling.
        // A cell is protected if any cube under protectionCubesParent has a world-space AABB
        // that overlaps the cell's XZ footprint on any terrain belonging to this face.
        HashSet<Vector2SByte> protectedCells = BuildProtectedCellSet(terrains, subdivisionsPowerOf2, face);

        // Derived from td.size.y on the first terrain; written into AdjacentData so the
        // runtime never has to trust settings.maxHeight for decode correctness.
        float bakedMaxHeight = -1f;

        FaceContainerOrientation orientation = GetFaceOrientation(face);

        // Phase 1: Process all terrains - extract heightmaps and determine valid chunks
        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain terrain = terrains[i].terrain;
            TerrainData td = terrain.terrainData;
            float terrainSize = td.size.x;
            float terrainHeight = td.size.y;
            if (bakedMaxHeight < 0f)
                bakedMaxHeight = terrainHeight;
            else if (!Mathf.Approximately(bakedMaxHeight, terrainHeight))
                Debug.LogWarning($"[MeshSaver] Terrain size.y mismatch on face {face}: expected {bakedMaxHeight}, got {terrainHeight}. Using first terrain value.");
            int heightRes = td.heightmapResolution;
            originalResolution = heightRes;
            // Re-orient the source heightmap from container/world axes into face plane axes.
            // Downstream sub-cell sampling indexes [planeY, planeX]; OrientHeights returns the
            // original array unchanged for Identity orientation (no allocation).
            float[,] heights = FaceContainerOrientations.OrientHeights(
                td.GetHeights(0, 0, heightRes, heightRes),
                orientation,
                heightRes);
            if (i == 0)
                Debug.Log($"[MeshSaver] Baking face {face} with orientation {orientation} (heightRes={heightRes}, terrainGridSize={terrainGridSize}).");

            int resolutionStep = heightRes / subdivisionsPowerOf2;
            float pixelDistance = terrainSize / (heightRes - 1);
            int chunkStep = heightRes / tilingFactor;

            sbyte terrainGridX = (sbyte)(settings.minX + terrains[i].terrainGridX * subdivisionsPowerOf2);/*Now this is odd. What exactly is this formula and why?
            First off, why hardcode minX in settings? 
            This is correct, minX and maxX are not hardcoded, they have custom getters that calculate them off other system parameters

            */
            sbyte terrainGridZ = (sbyte)(settings.minX + terrains[i].terrainGridY * subdivisionsPowerOf2);
            float terrainPlaneStartX = terrains[i].terrainGridX * terrainSize;
            float terrainPlaneStartY = terrains[i].terrainGridY * terrainSize;
            float faceWorldSize = terrainGridSize * terrainSize;

            for (sbyte j = 0; j < subdivisionsPowerOf2; j++)
            {
                for (sbyte k = 0; k < subdivisionsPowerOf2; k++)
                {
                    int startX = k == 0 ? 0 : (resolutionStep * k) - 1;
                    int endX = k == subdivisionsPowerOf2 - 1 ? resolutionStep * (k + 1) + 1 : resolutionStep * (k + 1);
                    int startZ = j == 0 ? 0 : (resolutionStep * j) - 1;
                    int endZ = j == subdivisionsPowerOf2 - 1 ? resolutionStep * (j + 1) + 1 : resolutionStep * (j + 1);

                    Vector2SByte cell = new Vector2SByte(terrainGridX, terrainGridZ);
                    cell.x += k;
                    cell.y += j;

                    int samplesBeforeX = startX;
                    int samplesBeforeZ = startZ;

                    float startXWorld = terrainPlaneStartX + (samplesBeforeX * pixelDistance);
                    float startZWorld = terrainPlaneStartY + (samplesBeforeZ * pixelDistance);
                    Vector3 currentStartingPosition = new Vector3(startXWorld, terrain.GetPosition().y, startZWorld);

                    int heightmapResX = endX - startX;
                    int heightmapResZ = endZ - startZ;

                    ushort[,] currentHeightmapHeights = new ushort[heightmapResZ, heightmapResX];
                    // Hoisted scale: each input sample is normalized [0,1] of terrainHeight,
                    // and we want it to ushort-quantize against bakedMaxHeight. Combine the
                    // two scales into one constant so the inner loop is a single multiply.
                    float heightToUshort = terrainHeight / bakedMaxHeight * 65535f;
                    for (int z = 0; z < heightmapResZ; z++)
                    {
                        for (int x = 0; x < heightmapResX; x++)
                        {
                            float v = heights[startZ + z, startX + x] * heightToUshort;
                            if (v < 0f) v = 0f; else if (v > 65535f) v = 65535f;
                            currentHeightmapHeights[z, x] = (ushort)v;
                        }
                    }
                    //Simple splitting into heightmaps, seems correct up to this point

                    int baseRes = (heightRes - 1) / subdivisionsPowerOf2;
                    int lastChunkIdx = (tilingFactor / subdivisionsPowerOf2) - 1;

                    bool[] validChunks = new bool[numberOfChunks * numberOfChunks];
                    ChunkAngularData[] angularChunks = new ChunkAngularData[numberOfChunks * numberOfChunks];
                    // Per-chunk max height (raw ushort) computed BEFORE any downsampling so
                    // the value is a true conservative upper bound on terrain height. Used by
                    // the runtime VisibilitySystem to derive cosThetaC for analytic horizon
                    // culling. Indexed by the same flatIndex as validChunks/angularChunks.
                    ushort[] chunkMaxHeightRaw = new ushort[numberOfChunks * numberOfChunks];
                    ushort[] chunkMinHeightRaw = new ushort[numberOfChunks * numberOfChunks];

                    for (short c = 0; c < numberOfChunks; c++)
                    {
                        for (short d = 0; d < numberOfChunks; d++)
                        {
                            Vector2Int currentChunk = new Vector2Int(d, c);

                            int maxJ = heightmapResX switch
                            {
                                var res when res == baseRes =>
                                    (currentChunk.x == lastChunkIdx) ? chunkStep - 1 : chunkStep,
                                var res when res == baseRes + 2 =>
                                    (currentChunk.x == lastChunkIdx) ? chunkStep + 1 : chunkStep,
                                _ => chunkStep
                            };

                            int maxI = heightmapResZ switch
                            {
                                var res when res == baseRes =>
                                    (currentChunk.y == lastChunkIdx) ? chunkStep - 1 : chunkStep,
                                var res when res == baseRes + 2 =>
                                    (currentChunk.y == lastChunkIdx) ? chunkStep + 1 : chunkStep,
                                _ => chunkStep
                            };

                            Vector3[] corners = new Vector3[4];
                            int[,] indices = new int[4, 2] { { 0, 0 }, { maxI, 0 }, { maxI, maxJ }, { 0, maxJ } };

                            int flatIndex = c * numberOfChunks + d;

                            for (int e = 0; e < 4; e++)
                            {
                                int m = indices[e, 0];
                                int n = indices[e, 1];

                                float planeX = currentStartingPosition.x + pixelDistance * (chunkStep * currentChunk.x + n);
                                float planeY = currentStartingPosition.z + pixelDistance * (chunkStep * currentChunk.y + m);
                                corners[e] = FaceIdUtility.ProjectFacePlanePoint(face, planeX, planeY, faceWorldSize, sphereCenter, sphereRadius);
                            }
                            /*Now here's a very possible error. 


                            We are not adding the height to the corner projection, this is right on the sphere surface.
                            After adding the height, won't the corners be in a different place, in world space, and thus have different normals?
                            With the current system, is chunk angular data valid?
                            */

                            Vector3[] dirs = new Vector3[4];
                            for (int e = 0; e < 4; e++)
                            {
                                dirs[e] = (corners[e] - sphereCenter).normalized;
                            }

                            Vector3 centerDir = (dirs[0] + dirs[1] + dirs[2] + dirs[3]).normalized;

                            Vector3[] planeNormals = new Vector3[4];
                            for (int e = 0; e < 4; e++)
                            {
                                Vector3 a = dirs[e];
                                Vector3 b = dirs[(e + 1) % 4];
                                Vector3 normal = Vector3.Cross(a, b).normalized;
                                if (Vector3.Dot(normal, centerDir) < 0f)
                                    normal = -normal;
                                planeNormals[e] = normal;
                            }

                            float minDot = float.MaxValue;
                            for (int e = 0; e < 4; e++)
                            {
                                float dot = Vector3.Dot(centerDir, dirs[e]);
                                if (dot < minDot)
                                    minDot = dot;
                            }

                            ChunkAngularData chunkData = new ChunkAngularData(
                                centerDir,
                                planeNormals[0],
                                planeNormals[1],
                                planeNormals[2],
                                planeNormals[3],
                                minDot
                            );

                            angularChunks[flatIndex] = chunkData;
                            validChunks[flatIndex] = true;

                            // Scan the chunk's footprint in the (still full-res) cell heightmap
                            // for its max raw height. Done BEFORE downsampling so we get a true
                            // conservative upper bound — decimation can drop the tallest sample.
                            // Footprint clamped to array bounds for safety on edge cells.
                            int z0 = currentChunk.y * chunkStep;
                            int z1 = z0 + maxI;
                            int x0 = currentChunk.x * chunkStep;
                            int x1 = x0 + maxJ;
                            if (z0 < 0) z0 = 0;
                            if (x0 < 0) x0 = 0;
                            if (z1 > heightmapResZ - 1) z1 = heightmapResZ - 1;
                            if (x1 > heightmapResX - 1) x1 = heightmapResX - 1;
                            ushort chunkMaxH = 0;
                            ushort chunkMinH = ushort.MaxValue;
                            for (int zz = z0; zz <= z1; zz++)
                            {
                                for (int xx = x0; xx <= x1; xx++)
                                {
                                    ushort v = currentHeightmapHeights[zz, xx];
                                    if (v > chunkMaxH) chunkMaxH = v;
                                    if (v < chunkMinH) chunkMinH = v;
                                }
                            }
                            // Guard against the (degenerate) empty-footprint case where the
                            // inner loop ran zero iterations: clamp min to <= max.
                            if (chunkMinH > chunkMaxH) chunkMinH = chunkMaxH;
                            chunkMaxHeightRaw[flatIndex] = chunkMaxH;
                            chunkMinHeightRaw[flatIndex] = chunkMinH;
                        }
                    }

                    validChunksPerMap[cell] = validChunks;
                    angularChunksPerMap[cell] = angularChunks;
                    chunkMaxHeightPerMap[cell] = chunkMaxHeightRaw;
                    chunkMinHeightPerMap[cell] = chunkMinHeightRaw;
                    heightmapsStartingPositions[cell] = currentStartingPosition;

                    // Apply per-cell downsampling AFTER angular-data computation. Protected cells
                    // stay full-res. Non-protected cells use the same decimation algorithm the
                    // runtime uses for higher LODs (STPTMEUtils.GetHeightsLodUshort), so the
                    // runtime can treat the stored heightmap as if "LOD = dsSteps already applied".
                    // Angular data is downsampling-invariant because chunk world positions only
                    // depend on pixelDistance * chunkStep, which stays constant.
                    byte cellDsSteps = 0;
                    if (downsamplingSteps > 0 && !protectedCells.Contains(cell))
                    {
                        currentHeightmapHeights = STPTMEUtils.GetHeightsLodUshort(currentHeightmapHeights, downsamplingSteps);
                        heightmapResZ = currentHeightmapHeights.GetLength(0);
                        heightmapResX = currentHeightmapHeights.GetLength(1);
                        cellDsSteps = (byte)downsamplingSteps;
                    }
                    cellDsStepsPerMap[cell] = cellDsSteps;

                    // Create CellBuildBuffer for this cell
                    var cellBuffer = new TreeBaker.CellBuildBuffer(cell, face, numberOfChunks);
                    cellBuffer.heightResX = (ushort)heightmapResX;
                    cellBuffer.heightResY = (ushort)heightmapResZ;
                    cellBuffer.heights = currentHeightmapHeights;
                    cellBuffer.hasValidChunks = true;
                    cellBuffers[cell] = cellBuffer;
                }
            }
        }

        // Phase 2: Extract trees from all terrains into cell buffers
        {
            float treeFaceWorldSize = terrainGridSize * terrains[0].terrain.terrainData.size.x;
            float ts = terrains[0].terrain.terrainData.size.x;
            int subPow2 = (int)Mathf.Pow(2, heightmapSubdivisions);
            foreach (var terrain in terrains)
            {
                // terrainGridX/Y are already plane-space (set by CollectTerrainsForFace).
                float planeTerrainOriginX = terrain.terrainGridX * ts;
                float planeTerrainOriginZ = terrain.terrainGridY * ts;
                sbyte cellKeyBaseX = (sbyte)(settings.minX + terrain.terrainGridX * subPow2);
                sbyte cellKeyBaseZ = (sbyte)(settings.minX + terrain.terrainGridY * subPow2);

                TreeBaker.ExtractTreesFromTerrain(
                    terrain.terrain,
                    face,
                    orientation,
                    cellBuffers,
                    subdivisionsPowerOf2,
                    numberOfChunks,
                    tilingFactor,
                    sphereCenter,
                    sphereRadius,
                    bakedMaxHeight,
                    treeFaceWorldSize,
                    planeTerrainOriginX,
                    planeTerrainOriginZ,
                    cellKeyBaseX,
                    cellKeyBaseZ
                );
            }
        }

        // Phase 2.5: Extract blotches from terrain trees (procedural foliage markers).
        // Each terrain tree whose prototype has blotch parameters is serialized
        // as a BlotchData instead of going through the tree instancing pipeline.
        {
            float ts = terrains[0].terrain.terrainData.size.x;
            float faceWorldSizeForBlotch = terrainGridSize * ts;
            int subPow2 = (int)Mathf.Pow(2, heightmapSubdivisions);
            float cellSizeForBlotch = ts / subPow2;

            foreach (var terrain in terrains)
            {
                float planeTerrainOriginX = terrain.terrainGridX * ts;
                float planeTerrainOriginZ = terrain.terrainGridY * ts;
                sbyte cellKeyBaseX = (sbyte)(settings.minX + terrain.terrainGridX * subPow2);
                sbyte cellKeyBaseZ = (sbyte)(settings.minX + terrain.terrainGridY * subPow2);

                BlotchBaker.ExtractBlotchesFromTerrain(
                    terrain.terrain,
                    face,
                    orientation,
                    blotchBuffers,
                    subdivisionsPowerOf2,
                    numberOfChunks,
                    tilingFactor,
                    sphereCenter,
                    sphereRadius,
                    bakedMaxHeight,
                    faceWorldSizeForBlotch,
                    planeTerrainOriginX,
                    planeTerrainOriginZ,
                    cellKeyBaseX,
                    cellKeyBaseZ,
                    prototypeRegistryForBake,
                    cellSizeForBlotch
                );
            }
        }

        // Phase 3: Write grouped cell files (one file per original unsubdivided terrain).
        // Group cells by original terrain grid position. Each terrain contributes
        // subdivisionsPowerOf2² subcells, all written into a single group file.
        var cellsByTerrain = new Dictionary<(int tgX, int tgY), List<TreeBaker.SubCellData>>();
        foreach (var kvp in cellBuffers)
        {
            Vector2SByte cell = kvp.Key;
            int tgX = (cell.x - settings.minX) / subdivisionsPowerOf2;
            int tgY = (cell.y - settings.minX) / subdivisionsPowerOf2;

            var key = (tgX, tgY);
            if (!cellsByTerrain.TryGetValue(key, out var list))
            {
                list = new List<TreeBaker.SubCellData>();
                cellsByTerrain[key] = list;
            }
            byte ds = cellDsStepsPerMap.TryGetValue(cell, out byte dsv) ? dsv : (byte)0;
            List<BlotchData> cellBlotches = blotchBuffers.TryGetValue(cell, out var blist) ? blist : null;
            list.Add(new TreeBaker.SubCellData
            {
                buffer = kvp.Value,
                validChunks = validChunksPerMap[cell],
                dsSteps = ds,
                blotches = cellBlotches
            });
        }

        int filesWritten = 0;
        int totalTreesWritten = 0;
        foreach (var grp in cellsByTerrain)
        {
            var (tgX, tgY) = grp.Key;
            var subcells = grp.Value.ToArray();
            string fileName = $"CellGroup_{prefix}_{tgX}_{tgY}.bytes";
            string filePath = Path.Combine(cellFolderPath, fileName);

            TreeBaker.WriteGroupCellFile(
                filePath, subcells,
                (byte)face, (byte)tgX, (byte)tgY,
                (byte)subdivisionsPowerOf2, (ushort)numberOfChunks);

            filesWritten++;
            foreach (var sc in subcells)
                totalTreesWritten += sc.buffer.GetTotalTreeCount();
        }

        // Accumulate tree density stats for collider pool sizing (combined across hemispheres)
        foreach (var valid in validChunksPerMap.Values)
            foreach (bool v in valid)
                if (v) _bakeValidChunkCount++;

        foreach (var buffer in cellBuffers.Values)
            foreach (var chunkTrees in buffer.treesPerChunk)
                foreach (var tree in chunkTrees)
                    if (tree.prototypeIndex < _bakeTreesPerPrototype.Length)
                        _bakeTreesPerPrototype[tree.prototypeIndex]++;

        // Phase 4: Write adjacent data (unchanged format)
        string adjFileName = $"AdjacentData_{prefix}.bytes";
        string adjFinalPath = Path.Combine(adjacentDataPath, adjFileName);
        using (BinaryWriter writer = new BinaryWriter(File.Open(adjFinalPath, FileMode.Create)))
        {
            writer.Write(originalResolution);
            writer.Write(bakedMaxHeight);
            writer.Write(validChunksPerMap.Count);
            foreach (var kvp in validChunksPerMap)
            {
                Vector2SByte map = kvp.Key;

                writer.Write(map.x);
                writer.Write(map.y);

                Vector3 currentStartPos = heightmapsStartingPositions[map];
                writer.Write(currentStartPos.x);
                writer.Write(currentStartPos.z);

                // Per-cell downsampling level (0 = full res). Used by the runtime to derive
                // per-cell chunkStep / pixelDistance / baseRes without loading the cell file.
                byte dsSteps = cellDsStepsPerMap.TryGetValue(map, out byte dsVal) ? dsVal : (byte)0;
                writer.Write(dsSteps);

                bool[] valid = kvp.Value;
                ChunkAngularData[] angularData = angularChunksPerMap[map];
                ushort[] chunkMaxH = chunkMaxHeightPerMap[map];
                ushort[] chunkMinH = chunkMinHeightPerMap[map];
                for (int c = 0; c < numberOfChunks; c++)
                {
                    for (int d = 0; d < numberOfChunks; d++)
                    {
                        int flatIndex = c * numberOfChunks + d;
                        if (valid[flatIndex])
                        {
                            writer.Write(true);
                            ChunkAngularData data = angularData[flatIndex];

                            Vector3 centerDir = data.centerDir;
                            Vector3 n0 = data.n0;
                            Vector3 n1 = data.n1;
                            Vector3 n2 = data.n2;
                            Vector3 n3 = data.n3;

                            writer.Write(centerDir.x);
                            writer.Write(centerDir.y);
                            writer.Write(centerDir.z);

                            writer.Write(n0.x); writer.Write(n0.y); writer.Write(n0.z);
                            writer.Write(n1.x); writer.Write(n1.y); writer.Write(n1.z);
                            writer.Write(n2.x); writer.Write(n2.y); writer.Write(n2.z);
                            writer.Write(n3.x); writer.Write(n3.y); writer.Write(n3.z);

                            writer.Write(data.minDot);
                            // Per-chunk min/max raw heights for VisibilitySystem bound
                            // computation. Decoded at runtime as (val/65535) * bakedMaxHeight.
                            // Min is needed so plateaus/mountains don't produce bound spheres
                            // that span all the way down to the sphere surface.
                            writer.Write(chunkMaxH[flatIndex]);
                            writer.Write(chunkMinH[flatIndex]);
                        }
                        else
                        {
                            writer.Write(false);
                        }
                    }
                }
            }
        }
    }


    public void BakeTextures()
    {
        // Layer textures are now baked immediately during SortTerrainsForGenerationInternal
        // when the prefab is instantiated. This method is kept for API compatibility but is
        // now a no-op since layer baking happens immediately to break reference chains.
        Debug.Log("[MeshSaver] BakeTextures skipped (layer textures baked immediately during prefab instantiation).");
    }

    private bool TryApplyMeshSaverTextureBakeSettings(byte maxLOD, ref TextureBaker.TextureBakeSettings bakeSettings)
    {
        if (!TryApplyMeshSaverSplatBakeSettings(maxLOD, ref bakeSettings))
            return false;

        return TryApplyMeshSaverNormalBakeSettings(maxLOD, ref bakeSettings);
    }

    private bool TryApplyMeshSaverSplatBakeSettings(byte maxLOD, ref TextureBaker.TextureBakeSettings bakeSettings)
    {
        if (splatTierResolutions == null || splatTierResolutions.Length == 0)
        {
            Debug.LogError("[MeshSaver] splatTierResolutions is null or empty.");
            return false;
        }

        if (lodToSplatTier == null || lodToSplatTier.Length != maxLOD + 1)
        {
            Debug.LogError($"[MeshSaver] lodToSplatTier must have length maxLOD+1 ({maxLOD + 1}), but was {(lodToSplatTier == null ? 0 : lodToSplatTier.Length)}.");
            return false;
        }

        byte[] convertedLodToSplatTier = new byte[lodToSplatTier.Length];
        int maxTierIndex = splatTierResolutions.Length - 1;
        for (int i = 0; i < lodToSplatTier.Length; i++)
        {
            int tier = lodToSplatTier[i];
            if (tier < 0 || tier > maxTierIndex)
            {
                Debug.LogError($"[MeshSaver] lodToSplatTier[{i}]={tier} is outside the valid range [0, {maxTierIndex}].");
                return false;
            }

            convertedLodToSplatTier[i] = (byte)tier;
        }

        for (int i = 0; i < splatTierResolutions.Length; i++)
        {
            if (splatTierResolutions[i] < 0)
            {
                Debug.LogError($"[MeshSaver] splatTierResolutions[{i}] cannot be negative.");
                return false;
            }
        }

        bakeSettings.tierResolutions = (int[])splatTierResolutions.Clone();
        bakeSettings.lodToTier = convertedLodToSplatTier;
        bakeSettings.layerTextureResolution = Mathf.Max(1, layerTextureResolution);
        bakeSettings.borderPixels = Mathf.Max(0, splatBorderPixels);
        return true;
    }

    private bool TryApplyMeshSaverNormalBakeSettings(byte maxLOD, ref TextureBaker.TextureBakeSettings bakeSettings)
    {
        bakeSettings.bakeHeightmapNormals = bakeHeightmapNormals;

        if (!bakeHeightmapNormals)
            return true;

        if (normalTierResolutions == null || normalTierResolutions.Length == 0)
        {
            Debug.LogError("[MeshSaver] normalTierResolutions is null or empty.");
            return false;
        }

        if (lodToNormalTier == null || lodToNormalTier.Length != maxLOD + 1)
        {
            Debug.LogError($"[MeshSaver] lodToNormalTier must have length maxLOD+1 ({maxLOD + 1}), but was {(lodToNormalTier == null ? 0 : lodToNormalTier.Length)}.");
            return false;
        }

        byte[] convertedLodToNormalTier = new byte[lodToNormalTier.Length];
        int maxTierIndex = normalTierResolutions.Length - 1;
        for (int i = 0; i < lodToNormalTier.Length; i++)
        {
            int tier = lodToNormalTier[i];
            if (tier < 0 || tier > maxTierIndex)
            {
                Debug.LogError($"[MeshSaver] lodToNormalTier[{i}]={tier} is outside the valid range [0, {maxTierIndex}].");
                return false;
            }

            convertedLodToNormalTier[i] = (byte)tier;
        }

        bakeSettings.normalTierResolutions = (int[])normalTierResolutions.Clone();
        bakeSettings.lodToNormalTier = convertedLodToNormalTier;
        bakeSettings.normalBorderPixels = Mathf.Max(0, normalBorderPixels);
        return true;
    }

    /// <summary>
    /// Builds the set of subdivided cell coordinates protected from downsampling for the given
    /// face. A cell is protected if any direct child of <see cref="protectionCubesParent"/> has a
    /// world-space AABB (Renderer.bounds when present, else lossyScale around position) that
    /// overlaps the cell's XZ footprint on any terrain belonging to this face.
    /// Returns an empty set when no parent is assigned or downsampling is disabled.
    /// </summary>
    private HashSet<Vector2SByte> BuildProtectedCellSet(TerrainBakeInfo[] terrains, int subdivisionsPowerOf2, FaceId face)
    {
        var result = new HashSet<Vector2SByte>();
        if (protectionCubesParent == null || downsamplingSteps <= 0 || terrains == null || terrains.Length == 0)
            return result;

        FaceContainerOrientation orientation = GetFaceOrientation(face);

        int childCount = protectionCubesParent.childCount;
        if (childCount == 0)
            return result;

        // Cache per-cube world XZ bounds.
        var cubeBounds = new List<(float minX, float maxX, float minZ, float maxZ)>(childCount);
        for (int i = 0; i < childCount; i++)
        {
            Transform cube = protectionCubesParent.GetChild(i);
            if (cube == null || !cube.gameObject.activeInHierarchy) continue;

            Bounds aabb;
            Renderer r = cube.GetComponent<Renderer>();
            if (r != null)
            {
                aabb = r.bounds;
            }
            else
            {
                Vector3 s = cube.lossyScale;
                aabb = new Bounds(cube.position, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
            }
            cubeBounds.Add((aabb.min.x, aabb.max.x, aabb.min.z, aabb.max.z));
        }

        if (cubeBounds.Count == 0) return result;

        sbyte minXSetting = settings.minX;
        for (int t = 0; t < terrains.Length; t++)
        {
            Terrain terrain = terrains[t].terrain;
            Vector3 terrainPos = terrain.GetPosition();
            float terrainSize = terrain.terrainData.size.x;
            float cellSize = terrainSize / subdivisionsPowerOf2;
            sbyte terrainGridX = (sbyte)(minXSetting + terrains[t].terrainGridX * subdivisionsPowerOf2);
            sbyte terrainGridZ = (sbyte)(minXSetting + terrains[t].terrainGridY * subdivisionsPowerOf2);

            float terrainMinX = terrainPos.x;
            float terrainMaxX = terrainPos.x + terrainSize;
            float terrainMinZ = terrainPos.z;
            float terrainMaxZ = terrainPos.z + terrainSize;

            foreach (var b in cubeBounds)
            {
                if (b.maxX < terrainMinX || b.minX > terrainMaxX) continue;
                if (b.maxZ < terrainMinZ || b.minZ > terrainMaxZ) continue;

                int kMin = Mathf.Clamp(Mathf.FloorToInt((b.minX - terrainMinX) / cellSize), 0, subdivisionsPowerOf2 - 1);
                int kMax = Mathf.Clamp(Mathf.FloorToInt((b.maxX - terrainMinX) / cellSize), 0, subdivisionsPowerOf2 - 1);
                int jMin = Mathf.Clamp(Mathf.FloorToInt((b.minZ - terrainMinZ) / cellSize), 0, subdivisionsPowerOf2 - 1);
                int jMax = Mathf.Clamp(Mathf.FloorToInt((b.maxZ - terrainMinZ) / cellSize), 0, subdivisionsPowerOf2 - 1);

                for (int j = jMin; j <= jMax; j++)
                {
                    for (int k = kMin; k <= kMax; k++)
                    {
                        // (k, j) are world sub-cell indices on the source terrain. Transform to
                        // plane sub-cell indices so the cell key matches what the bake writes.
                        FaceContainerOrientations.GridWorldToPlane(orientation, k, j, subdivisionsPowerOf2, out int kp, out int jp);
                        result.Add(new Vector2SByte(
                            (sbyte)(terrainGridX + kp),
                            (sbyte)(terrainGridZ + jp)));
                    }
                }
            }
        }

        return result;
    }

}

#endif