using System.Collections.Generic;
using UnityEngine;
using CustomTypes;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class BlotchOverrideGizmoDrawer : MonoBehaviour
{
    [SerializeField] private Transform[] faceContainers = new Transform[6];
    [SerializeField] private BlotchOverrideDatabase database;

    [Header("Distance Tiers")]
    [SerializeField] private float nearDistance = 60f;   // 3D sphere handles, pickable
    [SerializeField] private float mediumDistance = 250f; // batched cross markers

    [Header("Density Color Range")]
    [Tooltip("White at density 0, red at this density and above.")]
    [SerializeField] private float densityColorMax = 30f;
    [SerializeField] private Color defaultPrototypeColor = new Color(0.55f, 0.55f, 0.55f, 0.5f);
    [SerializeField] private Color hoverColor = Color.yellow;
    [SerializeField] private Color selectedColor = new Color(0.2f, 1f, 0.4f);

    [Header("Picking")]
    [SerializeField] private bool enablePicking = false;
    [SerializeField] private float pickMaxDistance = 500f;

    [Header("Placement Defaults")]
    [Tooltip("Radius applied to newly-placed trees, until changed here.")]
    [SerializeField] private float pendingPlacementRadius = 5f;
    [Tooltip("Density applied to newly-placed trees, until changed here.")]
    [SerializeField] private float pendingPlacementDensity = 10f;
    [Tooltip("Only auto-register newly placed trees whose prototype index is in this list. " +
             "Leave empty to apply to every newly placed tree.")]
    [SerializeField] private List<int> placementPrototypeFilter = new List<int>();
    private Rect _selectedPanelScreenRect;
    private bool _selectedPanelVisible;

    [Header("Mass Override (draw an enclosing polygon, set every tree inside at once)")]
    [Tooltip("While on, left-click draws polygon points instead of the normal picking/edit " +
             "behavior. The polygon is always treated as CLOSED — the last point connects back " +
             "to the first automatically, so there's no need to click precisely back on your " +
             "starting point.")]
    [SerializeField] private bool massOverrideModeEnabled = false;
    [SerializeField] private float massOverrideRadius = 0f;
    [SerializeField] private float massOverrideDensity = 1f;
    [Tooltip("-1 = match every prototype. Set to a specific registry index to only affect " +
             "trees of that prototype within the drawn area.")]
    [SerializeField] private int massOverridePrototypeFilter = -1;
    private readonly List<Vector3> _massOverridePolygon = new List<Vector3>();

    private struct TerrainCache
    {
        public Terrain terrain;
        public FaceId face;
        public sbyte gridX, gridY;
        public Vector3 boundsCenter;
        public float boundsRadius;
        public Vector3[] treeWorldPos;
        public uint[] treeSeeds;
        public int[] treePrototypeIdx;
        public bool hasAnyOverride;
    }

    private List<TerrainCache> _cache;
    private int[] _cachedTreeCounts;
    private bool _cacheBuilt;

    private List<Vector3> _medLines = new List<Vector3>();
    private List<Color> _medLineColors = new List<Color>(); // parallel; Handles.DrawLines has no per-segment color, so medium tier draws per-color-group instead (see below)
    private Vector3 _lastCamPos;
    private bool _linesDirty = true;

    // Near-tier sphere entries rebuilt alongside line batches; drawn/picked in OnSceneGUI.
    private struct SphereEntry { public int terrainIdx; public int treeIdx; public Vector3 pos; public float radius; public Color color; }
    private List<SphereEntry> _nearSpheres = new List<SphereEntry>();

    private int _hoverTerrain = -1, _hoverTree = -1;
    private int _selTerrain = -1, _selTree = -1;

#if UNITY_EDITOR
    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.update += PollForTreeChanges;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        EditorApplication.update -= PollForTreeChanges;
    }

    [ContextMenu("Rebuild Tree Cache")]
    public void RebuildCache()
    {
        _cache = new List<TerrainCache>();
        for (int f = 0; f < faceContainers.Length && f < 6; f++)
        {
            if (faceContainers[f] == null) continue;
            FaceId face = (FaceId)f;
            Terrain[] terrains = faceContainers[f].GetComponentsInChildren<Terrain>();
            if (terrains.Length == 0) continue;

            float terrainSize = terrains[0].terrainData.size.x;
            float minX = float.MaxValue, minZ = float.MaxValue;
            foreach (var t in terrains)
            {
                Vector3 p = t.GetPosition();
                minX = Mathf.Min(minX, p.x);
                minZ = Mathf.Min(minZ, p.z);
            }

            foreach (var terrain in terrains)
            {
                var td = terrain.terrainData;
                if (td == null) continue;
                Vector3 pos = terrain.GetPosition();
                sbyte gridX = (sbyte)Mathf.RoundToInt((pos.x - minX) / terrainSize);
                sbyte gridY = (sbyte)Mathf.RoundToInt((pos.z - minZ) / terrainSize);

                var tc = BuildTerrainCache(terrain, face, gridX, gridY);
                _cache.Add(tc);
            }
        }

        _cachedTreeCounts = new int[_cache.Count];
        for (int i = 0; i < _cache.Count; i++)
            _cachedTreeCounts[i] = _cache[i].terrain.terrainData.treeInstances.Length;

        _cacheBuilt = true;
        _linesDirty = true;
    }

    private TerrainCache BuildTerrainCache(Terrain terrain, FaceId face, sbyte gridX, sbyte gridY)
    {
        var td = terrain.terrainData;
        Vector3 pos = terrain.GetPosition();
        var trees = td.treeInstances;
        var worldPos = new Vector3[trees.Length];
        var seeds = new uint[trees.Length];
        var protoIdx = new int[trees.Length];
        bool anyOverride = false;

        for (int i = 0; i < trees.Length; i++)
        {
            var tr = trees[i];
            worldPos[i] = pos + Vector3.Scale(tr.position, td.size);
            uint seed = BlotchHash.PositionSeed(tr.position, tr.prototypeIndex);
            seeds[i] = seed;
            protoIdx[i] = tr.prototypeIndex;
            if (database != null && database.TryGetOverride(face, gridX, gridY, seed, out _))
                anyOverride = true;
        }

        Vector3 center = pos + new Vector3(td.size.x, td.size.y, td.size.z) * 0.5f;
        float radius = new Vector3(td.size.x, td.size.y, td.size.z).magnitude * 0.5f;

        return new TerrainCache
        {
            terrain = terrain, face = face, gridX = gridX, gridY = gridY,
            boundsCenter = center, boundsRadius = radius,
            treeWorldPos = worldPos, treeSeeds = seeds, treePrototypeIdx = protoIdx,
            hasAnyOverride = anyOverride
        };
    }

    /// <summary>
    /// Cheap per-frame poll (O(1) length read per terrain). When a terrain's tree count
    /// INCREASES, the newly added tree(s) are diffed out and auto-registered in the
    /// database with the currently configured pending radius/density — this is what
    /// makes "place with pre-selected parameters" work without hooking Unity's paint tool
    /// directly, which has no public per-placement callback.
    /// </summary>
    private void PollForTreeChanges()
    {
        if (!_cacheBuilt || _cache == null) return;

        for (int i = 0; i < _cache.Count; i++)
        {
            var tc = _cache[i];
            if (tc.terrain == null || tc.terrain.terrainData == null) continue;

            var currentTrees = tc.terrain.terrainData.treeInstances;
            int currentCount = currentTrees.Length;
            int prevCount = _cachedTreeCounts[i];
            if (currentCount == prevCount) continue;

            if (currentCount > prevCount)
                RegisterNewlyPlacedTrees(tc, currentTrees, prevCount);

            var rebuilt = BuildTerrainCache(tc.terrain, tc.face, tc.gridX, tc.gridY);
            _cache[i] = rebuilt;
            _cachedTreeCounts[i] = currentCount;
            _linesDirty = true;
            SceneView.RepaintAll();
        }
    }

    /// <summary>
    /// Unity appends newly painted trees to the END of treeInstances, so anything at
    /// index >= prevCount is new. Registers each against the database with the current
    /// pending placement radius/density, subject to the optional prototype filter.
    /// </summary>
    private void RegisterNewlyPlacedTrees(TerrainCache tc, TreeInstance[] currentTrees, int prevCount)
    {
        if (database == null) return;

        if (pendingPlacementRadius <= 0f && pendingPlacementDensity <= 0f)
            Debug.LogWarning("[BlotchOverrideGizmoDrawer] Placing with (radius=0, density=0) pending values. " +
                "Convention for single-instance blotches is (radius=0, density=1) — if this was unintentional, " +
                "set Pending Placement Density before painting.");

        for (int i = prevCount; i < currentTrees.Length; i++)
        {
            var tr = currentTrees[i];
            if (placementPrototypeFilter.Count > 0 && !placementPrototypeFilter.Contains(tr.prototypeIndex))
                continue;

            uint seed = BlotchHash.PositionSeed(tr.position, tr.prototypeIndex);

            // Skip writing an override entirely if the pending values match this prototype's
            // default — keeps the override table small (only exceptions get an entry), which
            // is the entire point of separating defaults from per-instance overrides.
            bool matchesDefault = database.TryGetPrototypeDefault(tr.prototypeIndex, out var def)
                && Mathf.Approximately(def.radius, pendingPlacementRadius)
                && Mathf.Approximately(def.density, pendingPlacementDensity);
            if (matchesDefault) continue;

            Undo.RecordObject(database, "Register Placed Tree Override");
            database.SetOverride(tc.face, tc.gridX, tc.gridY, seed, pendingPlacementRadius, pendingPlacementDensity);
            EditorUtility.SetDirty(database);
        }
    }


    private void RebuildDrawBatches(Vector3 camPos)
    {
        _medLines.Clear();
        _nearSpheres.Clear();

        for (int ti = 0; ti < _cache.Count; ti++)
        {
            var tc = _cache[ti];
            float distToBounds = Vector3.Distance(camPos, tc.boundsCenter) - tc.boundsRadius;
            if (distToBounds > mediumDistance) continue;

            for (int i = 0; i < tc.treeWorldPos.Length; i++)
            {
                Vector3 wp = tc.treeWorldPos[i];
                float d = Vector3.Distance(camPos, wp);
                if (d > mediumDistance) continue;

                BlotchOverrideDatabase.Entry ov = default;
                bool hasOverride = database != null &&
                    database.TryGetOverride(tc.face, tc.gridX, tc.gridY, tc.treeSeeds[i], out ov);
                float radius = hasOverride ? ov.radius : GetDefaultRadius(tc.treePrototypeIdx[i]);
                float density = hasOverride ? ov.density : GetDefaultDensity(tc.treePrototypeIdx[i]);
                if (radius <= 0.01f) radius = 0.5f;

                Color color = hasOverride ? DensityColor(density) : defaultPrototypeColor;

                if (d <= nearDistance)
                {
                    _nearSpheres.Add(new SphereEntry { terrainIdx = ti, treeIdx = i, pos = wp, radius = radius, color = color });
                }
                else
                {
                    AppendCross(_medLines, wp, Mathf.Min(radius, 2f));
                    // Medium tier: color isn't per-segment in a single DrawLines call, so we
                    // draw medium markers grouped by a coarse color bucket instead of true gradient.
                }
            }
        }
    }

    private Color DensityColor(float density)
    {
        float t = densityColorMax > 0f ? Mathf.Clamp01(density / densityColorMax) : 0f;
        return Color.Lerp(Color.white, Color.red, t);
    }

    private static void AppendCross(List<Vector3> lines, Vector3 center, float size)
    {
        lines.Add(center + new Vector3(-size, 0f, 0f)); lines.Add(center + new Vector3(size, 0f, 0f));
        lines.Add(center + new Vector3(0f, 0f, -size)); lines.Add(center + new Vector3(0f, 0f, size));
    }

    private void DrawMediumTierLines()
    {
        if (_medLines.Count == 0) return;
        Handles.color = defaultPrototypeColor;
        Handles.DrawLines(_medLines.ToArray());
    }

    private void DrawFarTerrainMarkers(Vector3 camPos)
    {
        foreach (var tc in _cache)
        {
            float dist = Vector3.Distance(camPos, tc.boundsCenter) - tc.boundsRadius;
            if (dist <= mediumDistance || !tc.hasAnyOverride) continue;

            Handles.color = new Color(1f, 0.6f, 0f, 0.4f);
            Vector3 size = tc.terrain.terrainData.size;
            Handles.DrawWireCube(tc.boundsCenter, size);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Mass Override — draw an enclosing polygon, set every tree inside at once.
    // Operates on the flat pre-bake authoring terrain (real Unity TerrainCollider,
    // no play-mode dependency) — a different domain from the runtime sphere tools.
    // ═══════════════════════════════════════════════════════════════════════

    private void HandleMassOverrideMode(SceneView view)
    {
        Event e = Event.current;

        int controlID = GUIUtility.GetControlID(FocusType.Passive);
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(controlID);

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Escape)
            {
                _massOverridePolygon.Clear();
                e.Use(); view.Repaint(); return;
            }
            if (e.keyCode == KeyCode.Backspace && _massOverridePolygon.Count > 0)
            {
                _massOverridePolygon.RemoveAt(_massOverridePolygon.Count - 1);
                e.Use(); view.Repaint(); return;
            }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                CommitMassOverride();
                e.Use(); view.Repaint(); return;
            }
        }

        int matchCount = DrawMassOverridePreview();
        DrawMassOverrideBanner(matchCount);

        if (e.type == EventType.MouseDown && e.button == 0 && HandleUtility.nearestControl == controlID)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
            {
                _massOverridePolygon.Add(hit.point);
                e.Use();
                view.Repaint();
            }
        }
    }

    /// <summary>Even-odd point-in-polygon test on the X/Z plane. The polygon is treated as
    /// implicitly closed (j starts at the last index, wrapping to the first) — the user never
    /// needs to click back on their starting point.</summary>
    private static bool PointInPolygonXZ(Vector3 point, List<Vector3> polygon)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = polygon[i].x, zi = polygon[i].z;
            float xj = polygon[j].x, zj = polygon[j].z;
            bool crosses = ((zi > point.z) != (zj > point.z)) &&
                (point.x < (xj - xi) * (point.z - zi) / (zj - zi) + xi);
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private void GetPolygonBoundsXZ(out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = minZ = float.MaxValue;
        maxX = maxZ = float.MinValue;
        foreach (var p in _massOverridePolygon)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }
    }

    /// <summary>Draws the polygon-in-progress and highlights every currently-matched tree.
    /// Returns the match count so the banner doesn't need a second, duplicate scan. Terrains
    /// are cheaply rejected by bounding-circle-vs-bbox BEFORE testing individual trees — a
    /// naive per-frame scan of all ~200k trees across all 216 terrains would reintroduce the
    /// exact performance problem this component's tiered rendering exists to avoid.</summary>
    private int DrawMassOverridePreview()
    {
        for (int i = 0; i < _massOverridePolygon.Count; i++)
        {
            Handles.color = Color.yellow;
            Handles.DrawWireDisc(_massOverridePolygon[i], Vector3.up, 0.4f);
        }

        if (_massOverridePolygon.Count >= 2)
        {
            Handles.color = new Color(1f, 0.6f, 0.1f);
            var loop = new Vector3[_massOverridePolygon.Count + 1];
            _massOverridePolygon.CopyTo(loop);
            loop[loop.Length - 1] = _massOverridePolygon[0]; // draw the implicit closing edge
            Handles.DrawAAPolyLine(3f, loop);
        }

        if (_massOverridePolygon.Count < 3 || _cache == null) return 0;

        GetPolygonBoundsXZ(out float minX, out float maxX, out float minZ, out float maxZ);

        int count = 0;
        Handles.color = Color.cyan;
        foreach (var tc in _cache)
        {
            if (tc.boundsCenter.x + tc.boundsRadius < minX || tc.boundsCenter.x - tc.boundsRadius > maxX) continue;
            if (tc.boundsCenter.z + tc.boundsRadius < minZ || tc.boundsCenter.z - tc.boundsRadius > maxZ) continue;

            for (int i = 0; i < tc.treeWorldPos.Length; i++)
            {
                if (massOverridePrototypeFilter >= 0 && tc.treePrototypeIdx[i] != massOverridePrototypeFilter) continue;
                if (!PointInPolygonXZ(tc.treeWorldPos[i], _massOverridePolygon)) continue;

                Handles.DrawWireDisc(tc.treeWorldPos[i], Vector3.up, 0.6f);
                count++;
            }
        }
        return count;
    }

    private void CommitMassOverride()
    {
        if (_massOverridePolygon.Count < 3 || database == null || _cache == null)
        {
            _massOverridePolygon.Clear();
            return;
        }

        GetPolygonBoundsXZ(out float minX, out float maxX, out float minZ, out float maxZ);

        // ONE RecordObject call before the whole batch — every SetOverride below collapses
        // into a single undo step, so Ctrl+Z reverts the entire mass edit at once.
        Undo.RecordObject(database, "Mass Override Blotches");

        int changed = 0;
        foreach (var tc in _cache)
        {
            if (tc.boundsCenter.x + tc.boundsRadius < minX || tc.boundsCenter.x - tc.boundsRadius > maxX) continue;
            if (tc.boundsCenter.z + tc.boundsRadius < minZ || tc.boundsCenter.z - tc.boundsRadius > maxZ) continue;

            for (int i = 0; i < tc.treeWorldPos.Length; i++)
            {
                if (massOverridePrototypeFilter >= 0 && tc.treePrototypeIdx[i] != massOverridePrototypeFilter) continue;
                if (!PointInPolygonXZ(tc.treeWorldPos[i], _massOverridePolygon)) continue;

                database.SetOverride(tc.face, tc.gridX, tc.gridY, tc.treeSeeds[i], massOverrideRadius, massOverrideDensity);
                changed++;
            }
        }

        EditorUtility.SetDirty(database);
        Debug.Log($"[BlotchOverrideGizmoDrawer] Mass override: set {changed} tree(s) to " +
            $"radius={massOverrideRadius:F2}, density={massOverrideDensity:F2}.");

        _massOverridePolygon.Clear();
        _linesDirty = true; // refresh gizmo colors so the change is visible immediately
    }

    private void DrawMassOverrideBanner(int matchCount)
    {
        Handles.BeginGUI();
        var rect = new Rect(10, 10, 380, 64);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.2f, 0.55f, 0.85f));
        GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
            "● MASS OVERRIDE — draw an enclosing polygon", EditorStyles.whiteBoldLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 24, rect.width - 16, 18),
            "Click = point · Backspace = undo · Enter = apply · Esc = cancel", EditorStyles.whiteMiniLabel);
        GUI.Label(new Rect(rect.x + 8, rect.y + 44, rect.width - 16, 18),
            $"Matches: {matchCount}  →  radius={massOverrideRadius:F2}  density={massOverrideDensity:F2}",
            EditorStyles.whiteMiniLabel);
        Handles.EndGUI();
    }

    private void OnSceneGUI(SceneView view)
    {
        if (!_cacheBuilt) RebuildCache();

        if (massOverrideModeEnabled)
        {
            HandleMassOverrideMode(view);
            return; // owns all interaction while active — normal picking/edit panel is skipped entirely
        }

        Vector3 camPos = view.camera.transform.position;
        if (_linesDirty || (camPos - _lastCamPos).sqrMagnitude > 1f)
        {
            RebuildDrawBatches(camPos);
            _lastCamPos = camPos;
            _linesDirty = false;
        }

        if (Event.current.type == EventType.Repaint)
        {
            DrawNearSpheres();
            DrawMediumTierLines();
            DrawFarTerrainMarkers(camPos);
        }

        // Draw the panel first so its rect is current before we decide whether to pick.
        DrawSelectedPanel();

        if (!enablePicking || !_cacheBuilt) { _hoverTerrain = -1; return; }

        Event e = Event.current;

        // If the cursor is over the floating panel, let its own controls handle the event —
        // don't raycast/deselect underneath it. This is what let clicks on the panel "fall
        // through" to the 3D pick logic and immediately deselect before the button could work.
        bool overPanel = _selectedPanelVisible && _selectedPanelScreenRect.Contains(e.mousePosition);
        if (overPanel)
        {
            _hoverTerrain = -1; // don't highlight a tree gizmo while interacting with its own panel
            return;
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        int bestTerrain = -1, bestTree = -1;
        float bestT = float.MaxValue;

        foreach (var s in _nearSpheres)
        {
            if (Vector3.Distance(ray.origin, s.pos) > pickMaxDistance) continue;
            if (RaySphereIntersect(ray, s.pos, Mathf.Max(s.radius, 0.5f), out float t) && t < bestT)
            {
                bestT = t; bestTerrain = s.terrainIdx; bestTree = s.treeIdx;
            }
        }

        if (bestTerrain < 0)
        {
            if(_cache==null) RebuildCache();
            for (int ti = 0; ti < _cache.Count; ti++)
            {
                var tc = _cache[ti];
                if (!RaySphereIntersect(ray, tc.boundsCenter, tc.boundsRadius, out _)) continue;

                for (int i = 0; i < tc.treeWorldPos.Length; i++)
                {
                    Vector3 wp = tc.treeWorldPos[i];
                    if (Vector3.Distance(ray.origin, wp) > pickMaxDistance) continue;

                    BlotchOverrideDatabase.Entry ov = default;
                    bool hasOverride = database != null &&
                        database.TryGetOverride(tc.face, tc.gridX, tc.gridY, tc.treeSeeds[i], out ov);
                    float radius = hasOverride ? ov.radius : GetDefaultRadius(tc.treePrototypeIdx[i]);
                    if (radius <= 0.01f) radius = 0.75f;

                    if (RaySphereIntersect(ray, wp, Mathf.Max(radius, 0.5f), out float t) && t < bestT)
                    {
                        bestT = t; bestTerrain = ti; bestTree = i;
                    }
                }
            }
        }

        if (bestTerrain != _hoverTerrain || bestTree != _hoverTree)
        {
            _hoverTerrain = bestTerrain; _hoverTree = bestTree;
            view.Repaint();
        }

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (bestTerrain >= 0)
            {
                _selTerrain = bestTerrain; _selTree = bestTree;
                e.Use();
            }
            else
            {
                _selTerrain = -1; _selTree = -1;
            }
            view.Repaint();
        }
    }

    private void DrawNearSpheres()
    {
        foreach (var s in _nearSpheres)
        {
            bool isHover = s.terrainIdx == _hoverTerrain && s.treeIdx == _hoverTree;
            bool isSel   = s.terrainIdx == _selTerrain   && s.treeIdx == _selTree;

            Color c = isSel ? selectedColor : (isHover ? hoverColor : s.color);
            float r = (isHover || isSel) ? s.radius * 1.08f : s.radius;

            // Three orthogonal wire discs = clearly 3D wireframe sphere, no occlusion.
            Handles.color = c;
            Handles.DrawWireDisc(s.pos, Vector3.up,      r);  // XZ plane (horizontal ring)
            Handles.DrawWireDisc(s.pos, Vector3.right,    r);  // YZ plane
            Handles.DrawWireDisc(s.pos, Vector3.forward,  r);  // XY plane
        }
    }

    private static bool RaySphereIntersect(Ray ray, Vector3 center, float radius, out float t)
    {
        Vector3 oc = ray.origin - center;
        float b = Vector3.Dot(oc, ray.direction);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;
        if (disc < 0f) { t = 0f; return false; }
        t = -b - Mathf.Sqrt(disc);
        if (t < 0f) t = -b + Mathf.Sqrt(disc);
        return t >= 0f;
    }

    private float GetDefaultRadius(int protoIdx)
    => database != null && database.TryGetPrototypeDefault(protoIdx, out var d) ? d.radius : 0f;

    private float GetDefaultDensity(int protoIdx)
        => database != null && database.TryGetPrototypeDefault(protoIdx, out var d) ? d.density : 0f;

    private void DrawSelectedPanel()
    {
        _selectedPanelVisible = false;

        if(_cache==null) RebuildCache();
        if (_selTerrain < 0 || _selTerrain >= _cache.Count) return;
        var tc = _cache[_selTerrain];
        if (_selTree < 0 || _selTree >= tc.treeWorldPos.Length) return;

        Vector3 wp = tc.treeWorldPos[_selTree];
        uint seed = tc.treeSeeds[_selTree];
        int proto = tc.treePrototypeIdx[_selTree];

        BlotchOverrideDatabase.Entry ov = default;
        bool hasOverride = database != null && database.TryGetOverride(tc.face, tc.gridX, tc.gridY, seed, out ov);
        float curRadius = hasOverride ? ov.radius : GetDefaultRadius(proto);
        float curDensity = hasOverride ? ov.density : GetDefaultDensity(proto);

        Vector2 guiPos = HandleUtility.WorldToGUIPoint(wp + Vector3.up * (curRadius + 1f));
        Rect panelRect = new Rect(guiPos.x - 90, guiPos.y - 90, 180, 90);

        _selectedPanelScreenRect = panelRect;
        _selectedPanelVisible = true;

        Handles.BeginGUI();
        GUILayout.BeginArea(panelRect, GUI.skin.box);
        GUILayout.Label($"Proto {proto}  seed 0x{seed:X8}");

        GUILayout.BeginHorizontal();
        GUILayout.Label("R", GUILayout.Width(12));
        float newRadius = EditorGUILayout.FloatField(curRadius, GUILayout.Width(55));
        GUILayout.Label("D", GUILayout.Width(12));
        float newDensity = EditorGUILayout.FloatField(curDensity, GUILayout.Width(55));
        GUILayout.EndHorizontal();

        if (!Mathf.Approximately(newRadius, curRadius) || !Mathf.Approximately(newDensity, curDensity))
        {
            Undo.RecordObject(database, "Edit Blotch Override");
            database.SetOverride(tc.face, tc.gridX, tc.gridY, seed, newRadius, newDensity);
            EditorUtility.SetDirty(database);
            _linesDirty = true;
        }

        if (hasOverride && GUILayout.Button("Remove Override"))
        {
            Undo.RecordObject(database, "Remove Blotch Override");
            database.RemoveOverride(tc.face, tc.gridX, tc.gridY, seed);
            EditorUtility.SetDirty(database);
            _linesDirty = true;
            _selTerrain = -1; _selTree = -1;
            _selectedPanelVisible = false;
        }

        GUILayout.EndArea();
        Handles.EndGUI();

        // Absorb any mouse event still within the panel bounds so it never reaches the
        // scene view's own click handling (which would otherwise deselect/move the camera focus).
        Event e = Event.current;
        if (panelRect.Contains(e.mousePosition) &&
            (e.type == EventType.MouseDown || e.type == EventType.MouseUp || e.type == EventType.MouseDrag))
        {
            e.Use();
        }
    }

#endif
}