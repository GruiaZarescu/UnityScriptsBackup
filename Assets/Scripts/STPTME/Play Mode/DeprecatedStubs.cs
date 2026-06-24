using UnityEngine;
using System;
using System.Collections.Generic;

// ---------------------------------------------------------------------------
// BACKWARD-COMPATIBILITY STUBS
// These stub types allow ChunkRegistry, VisibilityDebugger, and other files
// that reference the deprecated TreeRenderer, TreePrototypeRegistry,
// TreeColliderManager, and TreeDecoder systems to compile without errors
// during the migration to ImpostorRenderer + MapObjectPrototypeRegistry.
//
// All methods are no-ops. All properties return safe defaults.
// Remove this file once all references are ported to the new system.
// ---------------------------------------------------------------------------

public class TreeRenderer : MonoBehaviour
{
    public static TreeRenderer Instance { get; private set; }
    public static bool HasActiveSystem => false;
    public bool SystemEnabled => false;
    public IReadOnlyList<int> PopulatedStorageIndices => Array.Empty<int>();

    public void Init(object registry, Vector3 center, float radius, int count, object calc) { }
    public void RegisterChunk(int packed, FaceId face, byte lod, int dist, bool ring) { }
    public void UnregisterChunk(int packed, FaceId face) { }
    public int GetRegisteredLOD(int packed, FaceId face) => -1;
    public bool HasChunkData(int packed, FaceId face) => false;
    public void RefreshDistance(int packed, FaceId face, int dist, bool ring) { }
    public void BeginBFSCullPass() { }
    public void FlushUnvisitedChunks() { }
    public void ClearAll() { }
}

public class TreePrototypeRegistry : ScriptableObject
{
    public Color[] canopyPalette = Array.Empty<Color>();
    public TreePrototypeEntry[] prototypes = Array.Empty<TreePrototypeEntry>();

    [Serializable]
    public class TreePrototypeEntry
    {
        public string name;
        public Mesh[] lodMeshes;
        public Material material;
        public float baseWidth = 1f;
        public float baseHeight = 1f;
        public float heightOffset = 0f;
        public GameObject sourcePrefab;
        public bool shouldInstance = true;
        public bool instanceAlways = false;
        public float blotchRadius = 0f;
        public float blotchDensity = 1f;
        public byte conflictCategory = 4;
        public byte cullLOD = 255;
        public bool canopyOverlayEnabled = false;
        public int canopyPaletteIndex = 0;
        public CanopyMaskSettings[] canopyMaskByLOD = System.Array.Empty<CanopyMaskSettings>();

        public bool IsValid => false;
        public Mesh GetMeshForLOD(int lod) => null;
        public CanopyMaskSettings GetCanopyMaskSettingsForLOD(int lod) => null;
        public void CacheMeshData() { }
    }

    public int canopyMaskAtlasSize = 512;
}

public class TreeColliderManager : MonoBehaviour
{
    public void Initialize(object manager, object registry, string path) { }
    public void SetDesiredCollisionRing(int chunk, FaceId face, HashSet<ChunkKey> rings) { }
    public void OnCollisionChunkReady(int packed, FaceId face) { }
    public void OnCollisionRingChanged(int chunk, FaceId face, HashSet<ChunkKey> rings) { }
}

public static class TreeDecoder
{
    public struct DecodedTreeInstance
    {
        public Vector3 worldPosition;
        public float widthScale;
        public float heightScale;
        public float rotationRadians;
        public byte prototypeIndex;
    }

    public struct ChunkGeometry
    {
        public Vector3 chunkCenter;
        public Vector3 tangentNorth;
        public Vector3 tangentEast;
        public float maxPolarDistance;
        public bool IsValid => false;
    }

    public static ChunkGeometry ComputeChunkGeometry(Vector3 c00, Vector3 c10, Vector3 c01, Vector3 c11,
        Vector3 sphereCenter, float sphereRadius) => default;

    public static DecodedTreeInstance DecodeTree(
        object tree, ChunkGeometry geo, Vector3 center, float radius, float maxH) => default;

    public static void DecodeTreeBatch(
        ArraySegment<object> trees, DecodedTreeInstance[] output, ChunkGeometry geo,
        Vector3 center, float radius, float maxH) { }
}