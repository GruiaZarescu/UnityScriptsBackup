using UnityEngine;

public class ChunkPoolGenerator : MonoBehaviour
{
    [SerializeField] private int numberOfChunksInPool;
    [ContextMenu("Generate Chunk Pool")]
    private void GenerateChunkPool()
    {
        // Create parent GameObject
        GameObject parent = new GameObject("ChunkPool");

        for (int i = 0; i < numberOfChunksInPool; i++)
        {
            GameObject chunk = new GameObject(i.ToString());
            chunk.transform.parent = parent.transform;

            // Add components
            chunk.AddComponent<MeshFilter>();
            chunk.AddComponent<MeshRenderer>();
            chunk.AddComponent<MeshCollider>();
        }

        Debug.Log($"Chunk pool with {numberOfChunksInPool} children created.");
    }
}
