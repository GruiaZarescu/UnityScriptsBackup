#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Editor tool that toggles terrain containers between top-face editing and bottom-face editing.
/// When activated: swaps the world positions of the Top and Bottom containers, and mirrors
/// the heightmaps, alphamaps, detail layers, holes, and trees of the four side faces
/// (Left, Right, Forward, Back) so their edges align with the newly-centered face.
///
/// Mirror axes (driven by each container's editor layout orientation, NOT the
/// sphere face-axis system — the mirror has to swap whichever terrain-grid axis
/// represents "near edge to centered face ↔ far edge"):
///   Left, Right → mirror on Z.
///     These containers are laid out along the world Z axis relative to Top/Bot,
///     so their Bot-adjacent edge is at one Z extreme of the container. Mirror Z
///     swaps near/far Z rows, putting Bot-edge data where Top-edge data was.
///   Forward, Back → mirror on X.
///     These containers are laid out along the world X axis relative to Top/Bot,
///     so their Bot-adjacent edge is at one X extreme of the container. Mirror X
///     swaps near/far X columns. (Earlier versions mirrored Z here, which left
///     painted edge data on the wrong row after the round-trip.)
///   Bot face → mirror on Z.
///     Down.axisB = -Z (inverted vs Up.axisB = +Z), so Bot's high-Z terrain row maps
///     to the -Z (Back) sphere direction while Top's high-Z row maps to +Z (Forward).
///     Without a Z mirror on Bot, painting in the flipped editor produces a 180°-rotated
///     result on the sphere relative to Top's editor convention.
///
/// The operation is its own inverse: toggling again restores the original state.
/// Processes one terrain pair per editor tick to avoid freezing Unity.
/// </summary>
public class TerrainFaceSwapper : EditorWindow
{
    [SerializeField] private Transform topContainer;
    [SerializeField] private Transform bottomContainer;
    [SerializeField] private Transform leftContainer;
    [SerializeField] private Transform rightContainer;
    [SerializeField] private Transform frontContainer;
    [SerializeField] private Transform backContainer;

    private bool isFlipped;
    private bool isProcessing;

    // Async work queue
    private struct MirrorJob
    {
        public Terrain tA;
        public Terrain tB; // null when tA == tB (in-place mirror)
        public MirrorAxis axis;
    }

    private Queue<MirrorJob> pendingJobs;
    private int totalJobs;

    [MenuItem("Tools/Terrain/Face Swapper (Top ↔ Bottom)")]
    public static void ShowWindow()
    {
        GetWindow<TerrainFaceSwapper>("Face Swapper");
    }

    private void OnGUI()
    {
        GUILayout.Label("Terrain Face Swapper", EditorStyles.boldLabel);
        GUILayout.Label("Swaps Top ↔ Bottom positions and mirrors side-face terrain data\nso you can seamlessly edit the bottom face in the center.", EditorStyles.wordWrappedMiniLabel);
        GUILayout.Space(6);

        GUI.enabled = !isProcessing;

        topContainer    = (Transform)EditorGUILayout.ObjectField("Top Container",    topContainer,    typeof(Transform), true);
        bottomContainer = (Transform)EditorGUILayout.ObjectField("Bottom Container", bottomContainer, typeof(Transform), true);

        GUILayout.Space(4);
        GUILayout.Label("Side Faces (will be mirrored)", EditorStyles.boldLabel);

        leftContainer   = (Transform)EditorGUILayout.ObjectField("Left",    leftContainer,   typeof(Transform), true);
        rightContainer  = (Transform)EditorGUILayout.ObjectField("Right",   rightContainer,  typeof(Transform), true);
        frontContainer  = (Transform)EditorGUILayout.ObjectField("Forward", frontContainer,  typeof(Transform), true);
        backContainer   = (Transform)EditorGUILayout.ObjectField("Back",    backContainer,   typeof(Transform), true);

        GUILayout.Space(10);

        if (isProcessing)
        {
            int done = totalJobs - pendingJobs.Count;
            EditorGUILayout.HelpBox($"Processing terrain {done}/{totalJobs}...", MessageType.Info);
        }
        else
        {
            string label = isFlipped ? "Restore (Back to Top editing)" : "Swap (Switch to Bottom editing)";
            GUI.backgroundColor = isFlipped ? new Color(1f, 0.7f, 0.7f) : new Color(0.7f, 1f, 0.7f);

            if (GUILayout.Button(label, GUILayout.Height(32)))
            {
                if (!ValidateContainers()) return;
                StartSwap();
            }

            GUI.backgroundColor = Color.white;
        }

        GUI.enabled = true;

        if (isFlipped && !isProcessing)
        {
            EditorGUILayout.HelpBox("Currently in BOTTOM editing mode. Side faces are mirrored. Toggle back before baking or entering play mode.", MessageType.Warning);
        }
    }

    private bool ValidateContainers()
    {
        if (topContainer == null || bottomContainer == null ||
            leftContainer == null || rightContainer == null ||
            frontContainer == null || backContainer == null)
        {
            EditorUtility.DisplayDialog("Face Swapper", "All six containers must be assigned.", "OK");
            return false;
        }
        return true;
    }

    // Returns the world-space XZ position of the terrain with the smallest X and Z
    // within the container — the (0,0) corner of the face grid in world space.
    private static Vector2 GetFaceOriginXZ(Transform container)
    {
        float minX = float.MaxValue, minZ = float.MaxValue;
        foreach (var t in container.GetComponentsInChildren<Terrain>(true))
        {
            Vector3 p = t.GetPosition();
            if (p.x < minX) minX = p.x;
            if (p.z < minZ) minZ = p.z;
        }
        return new Vector2(minX, minZ);
    }

    private void StartSwap()
    {
        // Move each container by the XZ delta needed to bring its terrain-grid origin
        // to where the other container's terrain-grid origin currently is.
        // This is robust against containers having arbitrary local transforms.
        Vector2 topOrigin    = GetFaceOriginXZ(topContainer);
        Vector2 bottomOrigin = GetFaceOriginXZ(bottomContainer);

        Undo.RecordObject(topContainer,    "Swap Face Position");
        Undo.RecordObject(bottomContainer, "Swap Face Position");
        topContainer.position    += new Vector3(bottomOrigin.x - topOrigin.x,    0f, bottomOrigin.y - topOrigin.y);
        bottomContainer.position += new Vector3(topOrigin.x    - bottomOrigin.x, 0f, topOrigin.y    - bottomOrigin.y);

        // Build job queue. Mirror axis depends on each container's editor layout:
        // Left/Right are arranged along world Z relative to Top/Bot → mirror Z.
        // Forward/Back are arranged along world X relative to Top/Bot → mirror X.
        // The Bot container also mirrors Z to compensate for Down.axisB = -Z.
        pendingJobs = new Queue<MirrorJob>();
        EnqueueContainer(bottomContainer, MirrorAxis.Z);
        EnqueueContainer(leftContainer,   MirrorAxis.Z);
        EnqueueContainer(rightContainer,  MirrorAxis.Z);
        EnqueueContainer(frontContainer,  MirrorAxis.X);
        EnqueueContainer(backContainer,   MirrorAxis.X);

        totalJobs = pendingJobs.Count;
        if (totalJobs == 0)
        {
            isFlipped = !isFlipped;
            return;
        }

        isProcessing = true;
        EditorApplication.update += ProcessNextJob;
    }

    private void EnqueueContainer(Transform container, MirrorAxis axis)
    {
        Terrain[] terrains = container.GetComponentsInChildren<Terrain>(true);
        if (terrains.Length == 0) return;

        int gridSize = Mathf.RoundToInt(Mathf.Sqrt(terrains.Length));
        if (gridSize * gridSize != terrains.Length)
        {
            Debug.LogError($"[Face Swapper] '{container.name}' has {terrains.Length} terrains, not a perfect square grid.");
            return;
        }

        float terrainWorldSize = terrains[0].terrainData.size.x;
        float minX = float.MaxValue, minZ = float.MaxValue;
        foreach (var t in terrains)
        {
            Vector3 p = t.GetPosition();
            if (p.x < minX) minX = p.x;
            if (p.z < minZ) minZ = p.z;
        }

        Terrain[,] grid = new Terrain[gridSize, gridSize];
        foreach (var t in terrains)
        {
            Vector3 p = t.GetPosition();
            int gx = Mathf.RoundToInt((p.x - minX) / terrainWorldSize);
            int gy = Mathf.RoundToInt((p.z - minZ) / terrainWorldSize);
            grid[gy, gx] = t;
        }

        bool[,] processed = new bool[gridSize, gridSize];

        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                if (processed[gy, gx]) continue;

                int mirrorGx = axis == MirrorAxis.X ? gridSize - 1 - gx : gx;
                int mirrorGy = axis == MirrorAxis.Z ? gridSize - 1 - gy : gy;

                Terrain tA = grid[gy, gx];
                Terrain tB = grid[mirrorGy, mirrorGx];

                if (tA == null || tB == null)
                {
                    Debug.LogError($"[Face Swapper] Null terrain at grid ({gx},{gy}) or ({mirrorGx},{mirrorGy}) in '{container.name}'.");
                    return;
                }

                processed[gy, gx] = true;
                processed[mirrorGy, mirrorGx] = true;

                if (tA == tB)
                    pendingJobs.Enqueue(new MirrorJob { tA = tA, tB = null, axis = axis });
                else
                    pendingJobs.Enqueue(new MirrorJob { tA = tA, tB = tB, axis = axis });
            }
        }
    }

    private void ProcessNextJob()
    {
        if (pendingJobs == null || pendingJobs.Count == 0)
        {
            FinishSwap();
            return;
        }

        int done = totalJobs - pendingJobs.Count;
        if (EditorUtility.DisplayCancelableProgressBar("Face Swapper",
            $"Mirroring terrain {done + 1}/{totalJobs}...",
            (float)done / totalJobs))
        {
            // User cancelled — stop but don't flip state (data is partially mirrored,
            // user must re-run to complete or undo manually)
            Debug.LogWarning("[Face Swapper] Cancelled. Terrain data may be partially mirrored. Run again to finish or undo position swap.");
            FinishSwap();
            return;
        }

        var job = pendingJobs.Dequeue();

        if (job.tB == null)
            MirrorTerrainData(job.tA, job.axis);
        else
            SwapAndMirrorTerrainData(job.tA, job.tB, job.axis);

        Repaint();
    }

    private void FinishSwap()
    {
        EditorApplication.update -= ProcessNextJob;
        EditorUtility.ClearProgressBar();
        isProcessing = false;
        isFlipped = !isFlipped;
        Debug.Log($"[Face Swapper] {(isFlipped ? "Swapped to Bottom editing mode." : "Restored to Top editing mode.")}");
        Repaint();
    }

    private enum MirrorAxis { X, Z }

    /// <summary>Mirrors a single terrain's data in place.</summary>
    private static void MirrorTerrainData(Terrain t, MirrorAxis axis)
    {
        TerrainData td = t.terrainData;

        MirrorHeightmap(td, axis);
        MirrorAlphamaps(td, axis);
        MirrorDetailLayers(td, axis);
        MirrorHoles(td, axis);
        MirrorTrees(td, axis);

        td.SyncHeightmap();
        EditorUtility.SetDirty(td);
    }

    /// <summary>Swaps and mirrors data between two terrain tiles.</summary>
    private static void SwapAndMirrorTerrainData(Terrain tA, Terrain tB, MirrorAxis axis)
    {
        TerrainData tdA = tA.terrainData;
        TerrainData tdB = tB.terrainData;

        SwapAndMirrorHeightmaps(tdA, tdB, axis);
        SwapAndMirrorAlphamaps(tdA, tdB, axis);
        SwapAndMirrorDetailLayers(tdA, tdB, axis);
        SwapAndMirrorHoles(tdA, tdB, axis);
        SwapAndMirrorTrees(tdA, tdB, axis);

        tdA.SyncHeightmap();
        tdB.SyncHeightmap();
        EditorUtility.SetDirty(tdA);
        EditorUtility.SetDirty(tdB);
    }

    // ============ HEIGHTMAP ============

    private static void MirrorHeightmap(TerrainData td, MirrorAxis axis)
    {
        int res = td.heightmapResolution;
        float[,] h = td.GetHeights(0, 0, res, res);

        if (axis == MirrorAxis.Z)
        {
            for (int z = 0; z < res / 2; z++)
                for (int x = 0; x < res; x++)
                    (h[z, x], h[res - 1 - z, x]) = (h[res - 1 - z, x], h[z, x]);
        }
        else
        {
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res / 2; x++)
                    (h[z, x], h[z, res - 1 - x]) = (h[z, res - 1 - x], h[z, x]);
        }

        td.SetHeights(0, 0, h);
    }

    private static void SwapAndMirrorHeightmaps(TerrainData tdA, TerrainData tdB, MirrorAxis axis)
    {
        int res = tdA.heightmapResolution;
        float[,] hA = tdA.GetHeights(0, 0, res, res);
        float[,] hB = tdB.GetHeights(0, 0, res, res);
        float[,] mA = new float[res, res];
        float[,] mB = new float[res, res];

        int rLast = res - 1;
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
            {
                if (axis == MirrorAxis.Z)
                {
                    mA[z, x] = hB[rLast - z, x];
                    mB[z, x] = hA[rLast - z, x];
                }
                else
                {
                    mA[z, x] = hB[z, rLast - x];
                    mB[z, x] = hA[z, rLast - x];
                }
            }
        }

        tdA.SetHeights(0, 0, mA);
        tdB.SetHeights(0, 0, mB);
    }

    // ============ ALPHAMAPS ============

    private static void MirrorAlphamaps(TerrainData td, MirrorAxis axis)
    {
        int res = td.alphamapResolution;
        int layers = td.alphamapLayers;
        if (layers == 0) return;

        float[,,] a = td.GetAlphamaps(0, 0, res, res);

        if (axis == MirrorAxis.Z)
        {
            for (int z = 0; z < res / 2; z++)
                for (int x = 0; x < res; x++)
                    for (int l = 0; l < layers; l++)
                        (a[z, x, l], a[res - 1 - z, x, l]) = (a[res - 1 - z, x, l], a[z, x, l]);
        }
        else
        {
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res / 2; x++)
                    for (int l = 0; l < layers; l++)
                        (a[z, x, l], a[z, res - 1 - x, l]) = (a[z, res - 1 - x, l], a[z, x, l]);
        }

        td.SetAlphamaps(0, 0, a);
    }

    private static void SwapAndMirrorAlphamaps(TerrainData tdA, TerrainData tdB, MirrorAxis axis)
    {
        int res = tdA.alphamapResolution;
        int layers = tdA.alphamapLayers;
        if (layers == 0) return;

        float[,,] aA = tdA.GetAlphamaps(0, 0, res, res);
        float[,,] aB = tdB.GetAlphamaps(0, 0, res, res);
        float[,,] mA = new float[res, res, layers];
        float[,,] mB = new float[res, res, layers];

        int rLast = res - 1;
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                int sz = axis == MirrorAxis.Z ? rLast - z : z;
                int sx = axis == MirrorAxis.X ? rLast - x : x;
                for (int l = 0; l < layers; l++)
                {
                    mA[z, x, l] = aB[sz, sx, l];
                    mB[z, x, l] = aA[sz, sx, l];
                }
            }

        tdA.SetAlphamaps(0, 0, mA);
        tdB.SetAlphamaps(0, 0, mB);
    }

    // ============ DETAIL LAYERS ============

    private static void MirrorDetailLayers(TerrainData td, MirrorAxis axis)
    {
        int res = td.detailResolution;
        if (res == 0) return;

        for (int layer = 0; layer < td.detailPrototypes.Length; layer++)
        {
            int[,] d = td.GetDetailLayer(0, 0, res, res, layer);

            if (axis == MirrorAxis.Z)
            {
                for (int z = 0; z < res / 2; z++)
                    for (int x = 0; x < res; x++)
                        (d[z, x], d[res - 1 - z, x]) = (d[res - 1 - z, x], d[z, x]);
            }
            else
            {
                for (int z = 0; z < res; z++)
                    for (int x = 0; x < res / 2; x++)
                        (d[z, x], d[z, res - 1 - x]) = (d[z, res - 1 - x], d[z, x]);
            }

            td.SetDetailLayer(0, 0, layer, d);
        }
    }

    private static void SwapAndMirrorDetailLayers(TerrainData tdA, TerrainData tdB, MirrorAxis axis)
    {
        int res = tdA.detailResolution;
        if (res == 0) return;

        int rLast = res - 1;
        for (int layer = 0; layer < tdA.detailPrototypes.Length; layer++)
        {
            int[,] dA = tdA.GetDetailLayer(0, 0, res, res, layer);
            int[,] dB = tdB.GetDetailLayer(0, 0, res, res, layer);
            int[,] mA = new int[res, res];
            int[,] mB = new int[res, res];

            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                {
                    if (axis == MirrorAxis.Z)
                    {
                        mA[z, x] = dB[rLast - z, x];
                        mB[z, x] = dA[rLast - z, x];
                    }
                    else
                    {
                        mA[z, x] = dB[z, rLast - x];
                        mB[z, x] = dA[z, rLast - x];
                    }
                }

            tdA.SetDetailLayer(0, 0, layer, mA);
            tdB.SetDetailLayer(0, 0, layer, mB);
        }
    }

    // ============ HOLES ============

    private static void MirrorHoles(TerrainData td, MirrorAxis axis)
    {
        int res = td.holesResolution;
        if (res == 0) return;

        bool[,] h = td.GetHoles(0, 0, res, res);

        if (axis == MirrorAxis.Z)
        {
            for (int z = 0; z < res / 2; z++)
                for (int x = 0; x < res; x++)
                    (h[z, x], h[res - 1 - z, x]) = (h[res - 1 - z, x], h[z, x]);
        }
        else
        {
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res / 2; x++)
                    (h[z, x], h[z, res - 1 - x]) = (h[z, res - 1 - x], h[z, x]);
        }

        td.SetHoles(0, 0, h);
    }

    private static void SwapAndMirrorHoles(TerrainData tdA, TerrainData tdB, MirrorAxis axis)
    {
        int resA = tdA.holesResolution;
        int resB = tdB.holesResolution;
        if (resA == 0 && resB == 0) return;

        bool[,] hA = tdA.GetHoles(0, 0, resA, resA);
        bool[,] hB = tdB.GetHoles(0, 0, resB, resB);
        bool[,] mA = new bool[resA, resA];
        bool[,] mB = new bool[resB, resB];

        int res = resA;
        int rLast = res - 1;
        for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                if (axis == MirrorAxis.Z)
                {
                    mA[z, x] = hB[rLast - z, x];
                    mB[z, x] = hA[rLast - z, x];
                }
                else
                {
                    mA[z, x] = hB[z, rLast - x];
                    mB[z, x] = hA[z, rLast - x];
                }
            }

        tdA.SetHoles(0, 0, mA);
        tdB.SetHoles(0, 0, mB);
    }

    // ============ TREES ============

    private static void MirrorTrees(TerrainData td, MirrorAxis axis)
    {
        TreeInstance[] trees = td.treeInstances;
        if (trees.Length == 0) return;

        for (int i = 0; i < trees.Length; i++)
        {
            Vector3 p = trees[i].position;
            if (axis == MirrorAxis.Z)
                p.z = 1f - p.z;
            else
                p.x = 1f - p.x;
            trees[i].position = p;
        }

        td.SetTreeInstances(trees, true);
    }

    private static void SwapAndMirrorTrees(TerrainData tdA, TerrainData tdB, MirrorAxis axis)
    {
        TreeInstance[] treesA = tdA.treeInstances;
        TreeInstance[] treesB = tdB.treeInstances;

        for (int i = 0; i < treesA.Length; i++)
        {
            Vector3 p = treesA[i].position;
            if (axis == MirrorAxis.Z) p.z = 1f - p.z; else p.x = 1f - p.x;
            treesA[i].position = p;
        }
        for (int i = 0; i < treesB.Length; i++)
        {
            Vector3 p = treesB[i].position;
            if (axis == MirrorAxis.Z) p.z = 1f - p.z; else p.x = 1f - p.x;
            treesB[i].position = p;
        }

        tdA.SetTreeInstances(treesB, true);
        tdB.SetTreeInstances(treesA, true);
    }
}
#endif
