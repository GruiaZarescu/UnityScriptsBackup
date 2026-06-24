using System;
using UnityEngine;

[Serializable]
public struct PlaneTerrainData
{
    public ushort[] heightmap; //Quantized to ushort for performance, flattened heightmap of the terrain
    //No heightmap resolution field, all terrains must be square and of the same resolution, which is stored in the Planet class. This simplifies data management and GPU upload.
    //Will leave it a struct for further expansion
}

[Serializable]
public class FaceTerrainContainer
{
    public Transform terrainParent;
}

[Serializable]
public class FaceHeightmapGridData
{
    public PlaneTerrainData[] heightmaps;
    public int gridSize;
}

public class Planet : MonoBehaviour
{

    [SerializeField,HideInInspector]
    MeshFilter[] meshFilters;
    TerrainFace[] terrainFaces;

    public FaceHeightmapGridData[] faceHeightmapGrids = new FaceHeightmapGridData[6];

    [SerializeField,Tooltip("Per-face terrain parent containers. Assign 6 parents in the order: up, down, left, right, forward, back. All child terrains under each parent will be collected automatically.")]
    public FaceTerrainContainer[] faces = new FaceTerrainContainer[6];
    private int terrainResolution;
    private int faceGridSize;
    private int faceResolution;
    private float terrainHeight;
    private float terrainSize;
    private float radius;
    [SerializeField]
    private Transform planetParent; //Parent transform for the planet meshes, can be set to the same GameObject or a child for organization   

    [ContextMenu("Update Planet")]
    void UpdatePlanet()
    {
        InitializeHeightmapDatas();
        Initialize();
        generateMesh();
    }

    [ContextMenu("Clear Planet Meshes")]
    void ClearPlanetMeshes()
    {
        if (meshFilters != null)
        {
            foreach (var mf in meshFilters)
            {
                if (mf != null)
                {
                    DestroyImmediate(mf.gameObject);
                }
            }
        }
        meshFilters = null;
        terrainFaces = null;
    }

    void Initialize()
    {

        if(faceHeightmapGrids == null || faceHeightmapGrids.Length != 6)
        {
            faceHeightmapGrids = new FaceHeightmapGridData[6];
        }

        if(meshFilters == null || meshFilters.Length == 0)
        {
            meshFilters = new MeshFilter[6];
        }
        terrainFaces = new TerrainFace[6];

        Vector3[] directions = new Vector3[] {
            Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back
        };
        Transform meshParent = planetParent != null ? planetParent : transform;
        
        for(int i=0;i<6;i++)
        {
            if(meshFilters[i] == null)
            {
                GameObject meshObj = new GameObject($"Mesh_{directions[i]}");
                meshObj.transform.SetParent(meshParent, false);

                Shader urpLitShader = Shader.Find("Universal Render Pipeline/Lit");
                meshObj.AddComponent<MeshRenderer>().sharedMaterial = new Material(urpLitShader);
                meshFilters[i] = meshObj.AddComponent<MeshFilter>();
                meshFilters[i].sharedMesh = new Mesh();
            }

            terrainFaces[i] = new TerrainFace(
                meshFilters[i].sharedMesh,
                faceResolution,
                directions[i],
                radius,
                terrainHeight,
                terrainResolution,
                faceHeightmapGrids[i]
            );
        }
    }

    void generateMesh()
    {   
        for(int i=0;i<6;i++)
        {
            if (terrainFaces[i] != null)
            {
                terrainFaces[i].ConstructMesh();
            }
        }
    }

    void InitializeHeightmapDatas()
    {
        terrainResolution = 0;
        faceGridSize = 0;
        faceResolution = 0;
        terrainHeight = 0f;
        terrainSize = 0f;
        radius = 0f;

        for(int i=0;i<6;i++)
        {
            if (faces == null || i >= faces.Length || faces[i] == null || faces[i].terrainParent == null)
            {
                Debug.LogWarning($"[Planet] Face {i} has no terrain parent assigned. Heightmap data will be empty.");
                faceHeightmapGrids[i] = new FaceHeightmapGridData
                {
                    heightmaps = Array.Empty<PlaneTerrainData>(),
                    gridSize = 0
                };
                continue;
            }

            Terrain[] faceTerrains = faces[i].terrainParent.GetComponentsInChildren<Terrain>(true);
            if (faceTerrains == null || faceTerrains.Length == 0)
            {
                Debug.LogWarning($"[Planet] Face {i} parent '{faces[i].terrainParent.name}' contains no child terrains. Heightmap data will be empty.");
                faceHeightmapGrids[i] = new FaceHeightmapGridData
                {
                    heightmaps = Array.Empty<PlaneTerrainData>(),
                    gridSize = 0
                };
                continue;
            }

            Terrain[] sortedTerrains = SortTerrainsIntoGrid(faceTerrains, out int currentGridSize);
            if (sortedTerrains == null)
            {
                faceHeightmapGrids[i] = new FaceHeightmapGridData
                {
                    heightmaps = Array.Empty<PlaneTerrainData>(),
                    gridSize = 0
                };
                continue;
            }

            PlaneTerrainData[] extractedHeightmaps = new PlaneTerrainData[sortedTerrains.Length];
            for (int terrainIndex = 0; terrainIndex < sortedTerrains.Length; terrainIndex++)
            {
                Terrain terrain = sortedTerrains[terrainIndex];
                TerrainData td = terrain.terrainData;

                if (terrainResolution == 0)
                {
                    terrainResolution = td.heightmapResolution;
                    terrainHeight = td.size.y;
                    terrainSize = td.size.x;
                    faceGridSize = currentGridSize;
                    faceResolution = (terrainResolution - 1) * faceGridSize + 1;
                    radius = (terrainSize * faceGridSize) * 0.5f;
                }
                else if (td.heightmapResolution != terrainResolution || !Mathf.Approximately(td.size.y, terrainHeight) || !Mathf.Approximately(td.size.x, terrainSize) || currentGridSize != faceGridSize)
                {
                    Debug.LogError($"[Planet] Face {i} contains terrain data inconsistent with the first face. All faces must share resolution, terrain size, terrain height, and grid size.");
                    return;
                }

                extractedHeightmaps[terrainIndex] = ExtractHeightmap(td, terrainHeight);
            }

            faceHeightmapGrids[i] = new FaceHeightmapGridData
            {
                heightmaps = extractedHeightmaps,
                gridSize = currentGridSize
            };
        }
    }

    private static PlaneTerrainData ExtractHeightmap(TerrainData terrainData, float maxTerrainHeight)
    {
        int resolution = terrainData.heightmapResolution;
        float[,] heights = terrainData.GetHeights(0, 0, resolution, resolution);
        ushort[] heightmap = new ushort[resolution * resolution];

        for (int y = 0; y < resolution; y++)
        {
            for (int x = 0; x < resolution; x++)
            {
                float normalizedHeight = heights[y, x];
                float actualHeight = normalizedHeight * maxTerrainHeight;
                int flattenedIndex = y * resolution + x;
                heightmap[flattenedIndex] = (ushort)Mathf.Clamp((actualHeight / maxTerrainHeight) * ushort.MaxValue, 0f, ushort.MaxValue);
            }
        }

        return new PlaneTerrainData
        {
            heightmap = heightmap
        };
    }

    private Terrain[] SortTerrainsIntoGrid(Terrain[] terrains, out int gridSize)
    {
        gridSize = 0;
        if (terrains == null || terrains.Length == 0)
        {
            return null;
        }

        float root = Mathf.Sqrt(terrains.Length);
        gridSize = Mathf.RoundToInt(root);
        if (gridSize * gridSize != terrains.Length)
        {
            Debug.LogError($"[Planet] Terrain count {terrains.Length} is not a square number. Each face must contain 1, 4, 9, 16, ... terrains.");
            return null;
        }

        Terrain firstTerrain = terrains[0];
        if (firstTerrain == null)
        {
            Debug.LogError("[Planet] Face contains a null terrain reference.");
            return null;
        }

        float currentTerrainSize = firstTerrain.terrainData.size.x;
        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain terrain = terrains[i];
            if (terrain == null)
            {
                Debug.LogError("[Planet] Face contains a null terrain reference.");
                return null;
            }

            Vector3 position = terrain.GetPosition();
            if (position.x < minX)
            {
                minX = position.x;
            }
            if (position.z < minZ)
            {
                minZ = position.z;
            }
        }

        Terrain[] sortedTerrains = new Terrain[terrains.Length];
        bool[] occupiedSlots = new bool[terrains.Length];
        float tolerance = currentTerrainSize * 0.01f;

        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain terrain = terrains[i];
            TerrainData terrainData = terrain.terrainData;
            if (!Mathf.Approximately(terrainData.size.x, currentTerrainSize) || !Mathf.Approximately(terrainData.size.z, currentTerrainSize))
            {
                Debug.LogError("[Planet] All terrains must be square and share the same size.");
                return null;
            }

            Vector3 position = terrain.GetPosition();
            float normalizedX = (position.x - minX) / currentTerrainSize;
            float normalizedZ = (position.z - minZ) / currentTerrainSize;
            int gridX = Mathf.RoundToInt(normalizedX);
            int gridY = Mathf.RoundToInt(normalizedZ);

            if (Mathf.Abs(normalizedX - gridX) > tolerance / currentTerrainSize || Mathf.Abs(normalizedZ - gridY) > tolerance / currentTerrainSize)
            {
                Debug.LogError($"[Planet] Terrain '{terrain.name}' does not align to the inferred face grid.");
                return null;
            }

            if (gridX < 0 || gridX >= gridSize || gridY < 0 || gridY >= gridSize)
            {
                Debug.LogError($"[Planet] Terrain '{terrain.name}' resolved to out-of-range grid coordinates ({gridX}, {gridY}).");
                return null;
            }

            int flatIndex = gridY * gridSize + gridX;
            if (occupiedSlots[flatIndex])
            {
                Debug.LogError($"[Planet] Duplicate terrain placement detected at grid coordinate ({gridX}, {gridY}).");
                return null;
            }

            occupiedSlots[flatIndex] = true;
            sortedTerrains[flatIndex] = terrain;
        }

        for (int i = 0; i < occupiedSlots.Length; i++)
        {
            if (!occupiedSlots[i])
            {
                Debug.LogError($"[Planet] Missing terrain at inferred grid slot {i}.");
                return null;
            }
        }

        return sortedTerrains;
    }
}
