/*#if UNITY_EDITOR
using UnityEngine;
using System.Collections;
using System.Runtime.CompilerServices;

public class MeshObjectGenerator : MonoBehaviour
{
    [SerializeField] private GameObject topHalfTerrainContainer;
    [SerializeField] private GameObject bottomHalfTerrainContainer;
    [SerializeField] private Material material;
    [SerializeField] private Vector3 sphereCenter;
    [SerializeField] private float radius;


    [SerializeField, Tooltip("Number of chunks into which each terrain should be split, result will be n^2 chunks")]
    private int ChunkingFactor = 16;

    [SerializeField, Tooltip("Screen-relative transition heights for each LOD level, in percentages.")]
    private float[] lodDistances = new float[] { 100f, 90f, 80f };

    [ContextMenu("Bake Terrains to Meshes")]
    private void GenerateObjects()
    {
        Debug.Log("MeshObjectGenerator: Starting bake process...");
        EditorCoroutineRunner.StartEditorCoroutine(GenerateMeshesCoroutine());
    }

    private IEnumerator GenerateMeshesCoroutine()
    {
        if (topHalfTerrainContainer != null)
            yield return CreateMeshHierarchyAsync(topHalfTerrainContainer, "Top Half Meshes");
            
        else
            Debug.Log("MeshObjectGenerator: No top half assigned, skipping...");

        if (bottomHalfTerrainContainer != null)
            yield return CreateMeshHierarchyAsync(bottomHalfTerrainContainer, "Bottom Half Meshes");
        else
            Debug.Log("MeshObjectGenerator: No bottom half assigned, skipping...");

        Debug.Log("MeshObjectGenerator: Bake finished.");
    }

    private IEnumerator CreateMeshHierarchyAsync(GameObject terrainRoot, string newRootName)
    {
        GameObject existingRoot = GameObject.Find(newRootName);
        if (existingRoot != null) GameObject.DestroyImmediate(existingRoot);

        GameObject meshRoot = new GameObject(newRootName);
        yield return ProcessNodeRecursiveAsync(terrainRoot.transform, meshRoot.transform);
    }

    private IEnumerator ProcessNodeRecursiveAsync(Transform source, Transform targetParent)
    {
        foreach (Transform child in source)
        {
            GameObject newChild = new GameObject(child.name);
            newChild.transform.SetParent(targetParent);

            Terrain terrain = child.GetComponent<Terrain>();
            if (terrain != null)
            {
                bool isTerrainUpperHemisphere = false;
                if (topHalfTerrainContainer != null)
                {
                    if (terrain.transform.IsChildOf(topHalfTerrainContainer.transform)) isTerrainUpperHemisphere = true;
                }
                // Generate mesh tiles with all LODs per tile
                yield return MeshBaker.GenerateTiledMeshesAsync(
                    terrain,
                    ChunkingFactor,
                    isUpperHemisphere:isTerrainUpperHemisphere,
                    sphereRadius: radius,
                    SphereCenter: sphereCenter,
                    lodMeshes =>
                    {
                        if (lodMeshes != null)
                        {
                            CreateTileWithLODGroup(lodMeshes, newChild.transform);
                        }
                    });
            }
            else
            {
                // Recurse into children
                yield return ProcessNodeRecursiveAsync(child, newChild.transform);
            }
        }
    }

    /// <summary>
    /// Creates a GameObject for a single tile and attaches a LODGroup using the provided LOD meshes.
    /// </summary
    private void CreateTileWithLODGroup(Mesh[] lodMeshes, Transform parent)
    {
        float[] transitions = new float[lodMeshes.Length];
        for (int i = 0; i < lodMeshes.Length; i++)
        {
            transitions[i] = lodDistances[i]/100f;
         }

        if (lodMeshes == null || lodMeshes.Length == 0)
        {
            Debug.LogWarning("MeshObjectGenerator: No LOD meshes received for tile.");
            return;
        }

        // Root object for this tile
        GameObject tileGO = new GameObject(lodMeshes[0].name.Split('_')[0]); // Name based on tile coords
        tileGO.transform.SetParent(parent);

        // --- 3. Build LOD array and children ---
        LOD[] lodArray = new LOD[lodMeshes.Length];
        for (int i = 0; i < lodMeshes.Length; i++)
        {
            GameObject lodObj = new GameObject($"LOD{i}");
            lodObj.transform.SetParent(tileGO.transform);

            MeshFilter mf = lodObj.AddComponent<MeshFilter>();
            MeshRenderer mr = lodObj.AddComponent<MeshRenderer>();
            mr.material = material;
            mf.sharedMesh = lodMeshes[i];
            // TODO: Assign materials if needed

            lodArray[i] = new LOD(transitions[i], new Renderer[] { mr });
        }

        // --- 4. Apply LODGroup ---
        LODGroup lodGroup = tileGO.AddComponent<LODGroup>();
        lodGroup.SetLODs(lodArray);
        lodGroup.RecalculateBounds();
    }

}
#endif
*/
