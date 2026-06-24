#if UNITY_EDITOR
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SplineSampler : MonoBehaviour
{
    [SerializeField] private SplineContainer m_splineContainer;
    [SerializeField] private int m_splineIndex;
    [SerializeField] private float riverWidth = 2f;
    [SerializeField] private PlayerController playerController;
    [SerializeField, Range(2, 5000)] private int resolution = 20;

    private MeshFilter meshFilter;

    [ContextMenu("Generate River Mesh")]
    void GenerateRiverMesh()
    {
        if (m_splineContainer == null || resolution < 2 || playerController == null) return;

        meshFilter = GetComponent<MeshFilter>();
        Mesh mesh = new Mesh();
        mesh.name = "RiverMesh";

        List<Vector3> vertices = new();
        List<int> triangles = new();
        List<Vector2> uvs = new();

        float step = 1f / (resolution - 1);

        for (int i = 0; i < resolution; i++)
        {
            float t = i * step;

            m_splineContainer.Evaluate(m_splineIndex, t, out float3 localPos, out float3 localFwd, out _);
            Transform trs = m_splineContainer.transform;
            float3 position = trs.TransformPoint(localPos);
            Vector3 vector3Position = position;
            float3 forward = trs.TransformDirection(localFwd);

            float3 upVector = (vector3Position - playerController.sphereCenter).normalized;
            float3 right = math.normalize(math.cross(upVector, forward));

            float3 leftEdge = position - right * riverWidth * 0.5f;
            float3 rightEdge = position + right * riverWidth * 0.5f;

            vertices.Add(leftEdge);
            vertices.Add(rightEdge);
            uvs.Add(new Vector2(0, t));
            uvs.Add(new Vector2(1, t));

            if (i < resolution - 1)
            {
                int idx = i * 2;

                triangles.Add(idx);
                triangles.Add(idx + 1);
                triangles.Add(idx + 2);

                triangles.Add(idx + 1);
                triangles.Add(idx + 3);
                triangles.Add(idx + 2);
            }
        }


        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.SetUVs(0, uvs);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        meshFilter.sharedMesh = mesh;

        Debug.Log("✅ River mesh generated.");
        Debug.Log($"Vertices: {vertices.Count}, Triangles: {triangles.Count}");
        string path = "Assets/Water/Models/ExportedRiverMesh.obj";
        SaveMeshAsOBJ(mesh, path);
        AssetDatabase.Refresh();

    }
    void SaveMeshAsOBJ(Mesh mesh, string filePath)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("# Exported River Mesh");
        
        // Write vertices
        foreach (Vector3 v in mesh.vertices)
        {
            sb.AppendLine($"v {v.x} {v.y} {v.z}");
        }

        // Write UVs
        foreach (Vector2 uv in mesh.uv)
        {
            sb.AppendLine($"vt {uv.x} {uv.y}");
        }

        // Write normals
        foreach (Vector3 n in mesh.normals)
        {
            sb.AppendLine($"vn {n.x} {n.y} {n.z}");
        }

        // Write faces (triangles)
        for (int i = 0; i < mesh.triangles.Length; i += 3)
        {
            int v1 = mesh.triangles[i] + 1;
            int v2 = mesh.triangles[i + 1] + 1;
            int v3 = mesh.triangles[i + 2] + 1;
            sb.AppendLine($"f {v1}/{v1}/{v1} {v2}/{v2}/{v2} {v3}/{v3}/{v3}");
        }

        File.WriteAllText(filePath, sb.ToString());
        Debug.Log($"OBJ saved to: {filePath}");
    }

}
#endif