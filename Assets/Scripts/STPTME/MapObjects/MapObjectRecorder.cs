#if false
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Scans LOD placement containers (direct children only) to build and save a
/// <see cref="MapObjectSnapshot"/>, and can align placed objects so their local-up
/// axis points radially outward from the sphere surface.
///
/// Workflow:
///   1. Add this component to a persistent scene GameObject.
///   2. Create empty Transforms for each LOD level (LOD0, LOD1, …) and assign them to
///      <see cref="_lodContainers"/> in order of ascending LOD index.
///   3. Drag prefab instances under the relevant LOD container in the Editor.
///   4. Use the inspector buttons to align objects to the sphere and save the snapshot.
/// </summary>
public class MapObjectRecorder : MonoBehaviour
{
    [SerializeField] private MapObjectSnapshot _targetSnapshot;

    [SerializeField,
     Tooltip("Placement containers in LOD order. Index 0 = LOD0 (highest detail). " +
             "Direct children of these transforms are snapshotted by Save To Snapshot.")]
    private Transform[] _lodContainers;

    [SerializeField,
     Tooltip("Sphere center used for surface-alignment. Leave at (0,0,0) for the default world origin.")]
    private Vector3 _sphereCenter = Vector3.zero;

    [SerializeField,
     Tooltip("Asset path used to auto-create a snapshot when none is assigned. Must start with Assets/.")]
    private string _newSnapshotPath = "Assets/MapObjectSnapshot.asset";

    // ── Public accessors for the custom editor ─────────────────────────────────────────────
    public MapObjectSnapshot Snapshot     => _targetSnapshot;
    public Transform[]       LodContainers => _lodContainers;

    // ───────────────────────────────────────────────────────────────────────────────────────
    // SPHERE ALIGNMENT
    // ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rotates every direct child of every LOD container so that the object's local-up axis
    /// (world Y before alignment) points radially outward from the sphere surface at the
    /// object's current world position. The object's facing direction (forward projected onto
    /// the tangent plane) is preserved.
    /// </summary>
    [ContextMenu("Align All To Sphere Surface")]
    public void AlignAllToSphereSurface()
    {
        if (_lodContainers == null || _lodContainers.Length == 0)
        {
            Debug.LogWarning("[MapObjectRecorder] No LOD containers assigned.", this);
            return;
        }

        int count = 0;
        foreach (Transform container in _lodContainers)
        {
            if (container == null) continue;
            for (int i = 0; i < container.childCount; i++)
            {
                AlignTransformToSphere(container.GetChild(i));
                count++;
            }
        }

        Debug.Log($"[MapObjectRecorder] Aligned {count} object(s) to sphere surface.");
    }

    /// <summary>
    /// Aligns a single Transform so its up-axis is the sphere surface normal at its position.
    /// The forward component (projected onto the tangent plane) is preserved as the facing direction.
    /// </summary>
    public void AlignTransformToSphere(Transform t)
    {
        if (t == null) return;

        Vector3 surfaceNormal = (t.position - _sphereCenter).normalized;
        if (surfaceNormal.sqrMagnitude < 0.001f)
        {
            Debug.LogWarning($"[MapObjectRecorder] '{t.name}' is too close to sphere center — skipping alignment.", t);
            return;
        }

        // Project current forward onto the tangent plane to preserve facing direction.
        Vector3 forward = t.forward;
        Vector3 projForward = Vector3.ProjectOnPlane(forward, surfaceNormal);
        if (projForward.sqrMagnitude < 0.001f)
        {
            // Forward is nearly parallel to the normal — fall back to projected right.
            projForward = Vector3.ProjectOnPlane(t.right, surfaceNormal);
        }
        if (projForward.sqrMagnitude < 0.001f)
        {
            // Completely degenerate — just set up with no rotation change.
            projForward = Vector3.ProjectOnPlane(Vector3.forward, surfaceNormal);
        }

#if UNITY_EDITOR
        Undo.RecordObject(t, "Align To Sphere Surface");
#endif
        t.rotation = Quaternion.LookRotation(projForward.normalized, surfaceNormal);
    }

    // ───────────────────────────────────────────────────────────────────────────────────────
    // SNAPSHOT SAVE
    // ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans all LOD containers for prefab instances and overwrites the target snapshot.
    /// </summary>
    [ContextMenu("Save To Snapshot")]
    public void SaveToSnapshot()
    {
#if UNITY_EDITOR
        EnsureSnapshot();

        if (_targetSnapshot == null)
        {
            Debug.LogError("[MapObjectRecorder] Could not find or create a snapshot asset.", this);
            return;
        }

        if (_lodContainers == null || _lodContainers.Length == 0)
        {
            Debug.LogWarning("[MapObjectRecorder] No LOD containers assigned — nothing to save.", this);
            return;
        }

        var entries = new List<PlacedObjectEntry>();
        int skipped = 0;

        for (int lodLevel = 0; lodLevel < _lodContainers.Length; lodLevel++)
        {
            Transform container = _lodContainers[lodLevel];
            if (container == null) continue;

            for (int i = 0; i < container.childCount; i++)
            {
                GameObject child = container.GetChild(i).gameObject;

                GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromOriginalSource(child);
                if (prefabAsset == null)
                {
                    Debug.LogWarning(
                        $"[MapObjectRecorder] '{child.name}' under LOD{lodLevel} is not a prefab instance — skipped.", child);
                    skipped++;
                    continue;
                }

                string assetPath = AssetDatabase.GetAssetPath(prefabAsset);
                if (string.IsNullOrEmpty(assetPath))
                {
                    Debug.LogWarning(
                        $"[MapObjectRecorder] Prefab source for '{child.name}' has no asset path — skipped.", child);
                    skipped++;
                    continue;
                }

                entries.Add(new PlacedObjectEntry
                {
                    prefabGuid    = AssetDatabase.AssetPathToGUID(assetPath),
                    prefabName    = prefabAsset.name,
                    worldPosition = child.transform.position,
                    worldRotation = child.transform.rotation,
                    localScale    = child.transform.localScale,
                    lodLevel      = lodLevel,
                });
            }
        }

        _targetSnapshot.entries = entries.ToArray();
        EditorUtility.SetDirty(_targetSnapshot);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[MapObjectRecorder] Saved {entries.Count} object(s) to '{_targetSnapshot.name}'." +
            (skipped > 0 ? $" {skipped} non-prefab instance(s) skipped." : string.Empty));
#else
        Debug.LogWarning("[MapObjectRecorder] Save To Snapshot is only available in the Unity Editor.");
#endif
    }

    // ───────────────────────────────────────────────────────────────────────────────────────

    private void EnsureSnapshot()
    {
#if UNITY_EDITOR
        if (_targetSnapshot != null) return;

        _targetSnapshot = AssetDatabase.LoadAssetAtPath<MapObjectSnapshot>(_newSnapshotPath);
        if (_targetSnapshot != null) return;

        _targetSnapshot = ScriptableObject.CreateInstance<MapObjectSnapshot>();
        AssetDatabase.CreateAsset(_targetSnapshot, _newSnapshotPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[MapObjectRecorder] Created new snapshot asset at '{_newSnapshotPath}'.");
#endif
    }
}
#endif
