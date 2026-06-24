using System;
using System.Collections.Generic;
using UnityEngine;

public class SharedMeshVariantCache : MonoBehaviour
{
	private static SharedMeshVariantCache _instance;

	private readonly Dictionary<MeshVariantKey, Mesh> _variantsByKey = new Dictionary<MeshVariantKey, Mesh>();
	private readonly Dictionary<Mesh, SourceMeshData> _sourceDataByMesh = new Dictionary<Mesh, SourceMeshData>();
	private readonly List<Mesh> _ownedVariantMeshes = new List<Mesh>();

	public static SharedMeshVariantCache Instance
	{
		get
		{
			if (_instance != null)
				return _instance;

			_instance = FindFirstObjectByType<SharedMeshVariantCache>();
			if (_instance != null)
				return _instance;

			GameObject cacheObject = new GameObject(nameof(SharedMeshVariantCache));
			_instance = cacheObject.AddComponent<SharedMeshVariantCache>();
			DontDestroyOnLoad(cacheObject);
			return _instance;
		}
	}

	private void Awake()
	{
		if (_instance != null && _instance != this)
		{
			Destroy(gameObject);
			return;
		}

		_instance = this;
		DontDestroyOnLoad(gameObject);
	}

	private void OnDestroy()
	{
		if (_instance != this)
			return;

		for (int i = 0; i < _ownedVariantMeshes.Count; i++)
		{
			Mesh variantMesh = _ownedVariantMeshes[i];
			if (variantMesh == null)
				continue;

			if (Application.isPlaying)
				Destroy(variantMesh);
			else
				DestroyImmediate(variantMesh);
		}

		_ownedVariantMeshes.Clear();
		_variantsByKey.Clear();
		_sourceDataByMesh.Clear();

		if (_instance == this)
			_instance = null;
	}

	public Mesh GetOrCreateVariant(Mesh sourceMesh, uint hideMask)
	{
		if (sourceMesh == null || hideMask == 0u)
			return sourceMesh;

		// Keep variant generation lazy for now. Once real crowd scenes exist, add an explicit
		// prewarm list based on measured common requests instead of guessing which masks matter.
		MeshVariantKey key = new MeshVariantKey(sourceMesh.GetInstanceID(), hideMask);
		if (_variantsByKey.TryGetValue(key, out Mesh cachedVariant))
			return cachedVariant;

		SourceMeshData sourceData = GetOrCreateSourceData(sourceMesh);
		if (!sourceData.IsValid)
			return sourceMesh;

		Mesh variantMesh = BuildVariantMesh(sourceData, hideMask);
		_variantsByKey.Add(key, variantMesh);
		_ownedVariantMeshes.Add(variantMesh);
		return variantMesh;
	}

	private SourceMeshData GetOrCreateSourceData(Mesh sourceMesh)
	{
		if (_sourceDataByMesh.TryGetValue(sourceMesh, out SourceMeshData cachedSourceData))
			return cachedSourceData;

		SourceMeshData sourceData = SourceMeshData.Build(sourceMesh);
		_sourceDataByMesh.Add(sourceMesh, sourceData);
		return sourceData;
	}

	private static Mesh BuildVariantMesh(SourceMeshData sourceData, uint hideMask)
	{
		Mesh variantMesh = Instantiate(sourceData.SourceMesh);
		variantMesh.name = $"{sourceData.SourceMesh.name} Shared Variant 0x{hideMask:X}";

		for (int submeshIndex = 0; submeshIndex < sourceData.SourceTrianglesBySubmesh.Length; submeshIndex++)
		{
			int[] filteredTriangles = BuildFilteredTriangles(
				sourceData.SourceTrianglesBySubmesh[submeshIndex],
				sourceData.AreaMasksBySubmesh[submeshIndex],
				hideMask);
			variantMesh.SetTriangles(filteredTriangles, submeshIndex, false);
		}

		return variantMesh;
	}

	private static int[] BuildFilteredTriangles(int[] sourceTriangles, uint[] triangleAreaMasks, uint hideMask)
	{
		int keptIndexCount = 0;
		for (int triangleIndex = 0; triangleIndex < triangleAreaMasks.Length; triangleIndex++)
		{
			if ((triangleAreaMasks[triangleIndex] & hideMask) == 0u)
				keptIndexCount += 3;
		}

		if (keptIndexCount == sourceTriangles.Length)
			return sourceTriangles;

		int[] filteredTriangles = new int[keptIndexCount];
		int writeIndex = 0;
		for (int triangleIndex = 0; triangleIndex < triangleAreaMasks.Length; triangleIndex++)
		{
			if ((triangleAreaMasks[triangleIndex] & hideMask) != 0u)
				continue;

			int sourceIndex = triangleIndex * 3;
			filteredTriangles[writeIndex++] = sourceTriangles[sourceIndex];
			filteredTriangles[writeIndex++] = sourceTriangles[sourceIndex + 1];
			filteredTriangles[writeIndex++] = sourceTriangles[sourceIndex + 2];
		}

		return filteredTriangles;
	}

	private readonly struct MeshVariantKey : IEquatable<MeshVariantKey>
	{
		private readonly int _sourceMeshId;
		private readonly uint _hideMask;

		public MeshVariantKey(int sourceMeshId, uint hideMask)
		{
			_sourceMeshId = sourceMeshId;
			_hideMask = hideMask;
		}

		public bool Equals(MeshVariantKey other)
		{
			return _sourceMeshId == other._sourceMeshId && _hideMask == other._hideMask;
		}

		public override bool Equals(object obj)
		{
			return obj is MeshVariantKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(_sourceMeshId, _hideMask);
		}
	}

	private sealed class SourceMeshData
	{
		public readonly Mesh SourceMesh;
		public readonly int[][] SourceTrianglesBySubmesh;
		public readonly uint[][] AreaMasksBySubmesh;

		public bool IsValid => SourceMesh != null && SourceTrianglesBySubmesh != null && AreaMasksBySubmesh != null;

		private SourceMeshData(Mesh sourceMesh, int[][] sourceTrianglesBySubmesh, uint[][] areaMasksBySubmesh)
		{
			SourceMesh = sourceMesh;
			SourceTrianglesBySubmesh = sourceTrianglesBySubmesh;
			AreaMasksBySubmesh = areaMasksBySubmesh;
		}

		public static SourceMeshData Build(Mesh sourceMesh)
		{
			if (sourceMesh == null)
				return new SourceMeshData(null, null, null);

			Vector2[] uv2 = sourceMesh.uv2;
			if (uv2 == null || uv2.Length != sourceMesh.vertexCount)
			{
				Debug.LogWarning($"Mesh '{sourceMesh.name}' has no valid uv2 area-mask data for shared variant generation. Returning the source mesh until the Blender export is updated.");
				return new SourceMeshData(sourceMesh, null, null);
			}

			int submeshCount = sourceMesh.subMeshCount;
			int[][] sourceTrianglesBySubmesh = new int[submeshCount][];
			uint[][] areaMasksBySubmesh = new uint[submeshCount][];

			for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
			{
				int[] triangles = sourceMesh.GetTriangles(submeshIndex);
				int triangleCount = triangles.Length / 3;
				uint[] triangleAreaMasks = new uint[triangleCount];

				for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
				{
					int triangleStart = triangleIndex * 3;
					uint firstMask = EncodeAreaMask(uv2[triangles[triangleStart]].x);
					uint secondMask = EncodeAreaMask(uv2[triangles[triangleStart + 1]].x);
					uint thirdMask = EncodeAreaMask(uv2[triangles[triangleStart + 2]].x);
					triangleAreaMasks[triangleIndex] = firstMask & secondMask & thirdMask;
				}

				sourceTrianglesBySubmesh[submeshIndex] = triangles;
				areaMasksBySubmesh[submeshIndex] = triangleAreaMasks;
			}

			return new SourceMeshData(sourceMesh, sourceTrianglesBySubmesh, areaMasksBySubmesh);
		}

		private static uint EncodeAreaMask(float value)
		{
			int roundedValue = Mathf.RoundToInt(value);
			return roundedValue <= 0 ? 0u : (uint)roundedValue;
		}
	}
}