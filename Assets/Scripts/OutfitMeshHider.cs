using System;
using System.Collections.Generic;
using UnityEngine;

public class OutfitMeshHider : MonoBehaviour
{
	private const int MaxUvFloatMaskBits = 24;

	[SerializeField] private SkinnedMeshRenderer _skinnedMeshRenderer;
	[SerializeField] private MeshFilter _meshFilter;
	[SerializeField] private OutfitHideGroup[] _hideGroups = Array.Empty<OutfitHideGroup>();

	private readonly Dictionary<long, int[][]> _filteredTrianglesByMask = new Dictionary<long, int[][]>();

	private Mesh _sourceMesh;
	private Mesh _runtimeMesh;
	private int[][] _sourceSubmeshTriangles;
	private int[][] _hideTriangleMasksBySubmesh;
	private int[][] _transitionTriangleMasksBySubmesh;
	private int _activeHideMask = -1;
	private int _activeTransitionMask = -1;
	private bool _initialized;

	private void Awake()
	{
		EnsureInitialized();
	}

	private void OnDestroy()
	{
		RestoreSourceMesh();
		DestroyRuntimeMesh();
	}

	public void ApplyHideMask(int hideMask)
	{
		ApplyMaskState(hideMask, 0);
	}

	public void ApplyMaskState(int hideMask, int transitionMask)
	{
		EnsureInitialized();

		if (_runtimeMesh == null || _sourceSubmeshTriangles == null || _hideTriangleMasksBySubmesh == null || _transitionTriangleMasksBySubmesh == null)
			return;

		hideMask = Mathf.Max(0, hideMask);
		transitionMask = Mathf.Max(0, transitionMask);
		if (_activeHideMask == hideMask && _activeTransitionMask == transitionMask)
			return;

		int[][] filteredTriangles = GetFilteredTriangles(hideMask, transitionMask);
		for (int submeshIndex = 0; submeshIndex < filteredTriangles.Length; submeshIndex++)
		{
			_runtimeMesh.SetTriangles(filteredTriangles[submeshIndex], submeshIndex, false);
		}

		_activeHideMask = hideMask;
		_activeTransitionMask = transitionMask;
	}

	public void ClearHiddenGroups()
	{
		ApplyMaskState(0, 0);
	}

	public bool TryGetGroupMask(string groupName, out int mask)
	{
		mask = 0;

		if (string.IsNullOrWhiteSpace(groupName) || _hideGroups == null)
			return false;

		for (int i = 0; i < _hideGroups.Length; i++)
		{
			OutfitHideGroup hideGroup = _hideGroups[i];
			if (hideGroup == null || !NamesMatch(hideGroup.Name, groupName))
				continue;

			if (hideGroup.BitIndex < 0 || hideGroup.BitIndex >= MaxUvFloatMaskBits)
			{
				Debug.LogWarning($"Hide group '{hideGroup.Name}' on '{name}' has bit {hideGroup.BitIndex}, but uv2.x supports bits 0-{MaxUvFloatMaskBits - 1} safely.", this);
				return false;
			}

			mask = 1 << hideGroup.BitIndex;
			return true;
		}

		return false;
	}

	private void EnsureInitialized()
	{
		if (_initialized)
			return;

		_initialized = true;

		if (_skinnedMeshRenderer == null)
			_skinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
		if (_meshFilter == null)
			_meshFilter = GetComponent<MeshFilter>();

		_sourceMesh = ResolveSourceMesh();
		if (_sourceMesh == null)
		{
			Debug.LogWarning($"No mesh found for outfit hider '{name}'.", this);
			return;
		}

		Vector2[] uv2 = _sourceMesh.uv2;
		if (uv2 == null || uv2.Length != _sourceMesh.vertexCount)
		{
			Debug.LogWarning($"Mesh '{_sourceMesh.name}' on '{name}' has no valid uv2 hide mask data. Re-run the Blender hide-mask bake and reimport the FBX.", this);
			return;
		}

		_runtimeMesh = Instantiate(_sourceMesh);
		_runtimeMesh.name = $"{_sourceMesh.name} Outfit Runtime";
		_runtimeMesh.MarkDynamic();
		AssignMesh(_runtimeMesh);

		BuildTriangleMaskCache(uv2);
		ApplyMaskState(0, 0);
	}

	private Mesh ResolveSourceMesh()
	{
		if (_skinnedMeshRenderer != null)
			return _skinnedMeshRenderer.sharedMesh;

		return _meshFilter != null ? _meshFilter.sharedMesh : null;
	}

	private void AssignMesh(Mesh mesh)
	{
		if (_skinnedMeshRenderer != null)
		{
			_skinnedMeshRenderer.sharedMesh = mesh;
			return;
		}

		if (_meshFilter != null)
			_meshFilter.sharedMesh = mesh;
	}

	private void BuildTriangleMaskCache(Vector2[] uv2)
	{
		int submeshCount = _sourceMesh.subMeshCount;
		_sourceSubmeshTriangles = new int[submeshCount][];
		_hideTriangleMasksBySubmesh = new int[submeshCount][];
		_transitionTriangleMasksBySubmesh = new int[submeshCount][];

		for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
		{
			int[] triangles = _sourceMesh.GetTriangles(submeshIndex);
			int triangleCount = triangles.Length / 3;
			int[] hideTriangleMasks = new int[triangleCount];
			int[] transitionTriangleMasks = new int[triangleCount];

			for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
			{
				int index = triangleIndex * 3;
				Vector2 firstVertexMask = uv2[triangles[index]];
				Vector2 secondVertexMask = uv2[triangles[index + 1]];
				Vector2 thirdVertexMask = uv2[triangles[index + 2]];

				hideTriangleMasks[triangleIndex] = Mathf.RoundToInt(firstVertexMask.x)
					& Mathf.RoundToInt(secondVertexMask.x)
					& Mathf.RoundToInt(thirdVertexMask.x);
				transitionTriangleMasks[triangleIndex] = Mathf.RoundToInt(firstVertexMask.y)
					& Mathf.RoundToInt(secondVertexMask.y)
					& Mathf.RoundToInt(thirdVertexMask.y);
			}

			_sourceSubmeshTriangles[submeshIndex] = triangles;
			_hideTriangleMasksBySubmesh[submeshIndex] = hideTriangleMasks;
			_transitionTriangleMasksBySubmesh[submeshIndex] = transitionTriangleMasks;
		}
	}

	private int[][] GetFilteredTriangles(int hideMask, int transitionMask)
	{
		long cacheKey = ComposeMaskCacheKey(hideMask, transitionMask);
		if (_filteredTrianglesByMask.TryGetValue(cacheKey, out int[][] filteredTriangles))
			return filteredTriangles;

		filteredTriangles = new int[_sourceSubmeshTriangles.Length][];
		for (int submeshIndex = 0; submeshIndex < _sourceSubmeshTriangles.Length; submeshIndex++)
		{
			filteredTriangles[submeshIndex] = BuildFilteredSubmeshTriangles(
				_sourceSubmeshTriangles[submeshIndex],
				_hideTriangleMasksBySubmesh[submeshIndex],
				_transitionTriangleMasksBySubmesh[submeshIndex],
				hideMask,
				transitionMask);
		}

		_filteredTrianglesByMask.Add(cacheKey, filteredTriangles);
		return filteredTriangles;
	}

	private static long ComposeMaskCacheKey(int hideMask, int transitionMask)
	{
		return ((long)hideMask << 32) | (uint)transitionMask;
	}

	private static int[] BuildFilteredSubmeshTriangles(int[] sourceTriangles, int[] hideTriangleMasks, int[] transitionTriangleMasks, int hideMask, int transitionMask)
	{
		if (hideMask == 0)
			return sourceTriangles;

		int keptIndexCount = 0;
		for (int triangleIndex = 0; triangleIndex < hideTriangleMasks.Length; triangleIndex++)
		{
			if (!ShouldHideTriangle(hideTriangleMasks[triangleIndex], transitionTriangleMasks[triangleIndex], hideMask, transitionMask))
				keptIndexCount += 3;
		}

		int[] filteredTriangles = new int[keptIndexCount];
		int writeIndex = 0;
		for (int triangleIndex = 0; triangleIndex < hideTriangleMasks.Length; triangleIndex++)
		{
			if (ShouldHideTriangle(hideTriangleMasks[triangleIndex], transitionTriangleMasks[triangleIndex], hideMask, transitionMask))
				continue;

			int sourceIndex = triangleIndex * 3;
			filteredTriangles[writeIndex++] = sourceTriangles[sourceIndex];
			filteredTriangles[writeIndex++] = sourceTriangles[sourceIndex + 1];
			filteredTriangles[writeIndex++] = sourceTriangles[sourceIndex + 2];
		}

		return filteredTriangles;
	}

	private static bool ShouldHideTriangle(int hideTriangleMask, int transitionTriangleMask, int hideMask, int transitionMask)
	{
		if ((hideTriangleMask & hideMask) == 0)
			return false;

		if ((transitionTriangleMask & transitionMask) != 0)
			return false;

		return true;
	}

	private void RestoreSourceMesh()
	{
		if (_sourceMesh == null)
			return;

		if (_skinnedMeshRenderer != null && _skinnedMeshRenderer.sharedMesh == _runtimeMesh)
			_skinnedMeshRenderer.sharedMesh = _sourceMesh;
		else if (_meshFilter != null && _meshFilter.sharedMesh == _runtimeMesh)
			_meshFilter.sharedMesh = _sourceMesh;
	}

	private void DestroyRuntimeMesh()
	{
		if (_runtimeMesh == null)
			return;

		if (Application.isPlaying)
			Destroy(_runtimeMesh);
		else
			DestroyImmediate(_runtimeMesh);

		_runtimeMesh = null;
	}

	private static bool NamesMatch(string firstName, string secondName)
	{
		return string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase);
	}
}

[Serializable]
public class OutfitHideGroup
{
	[SerializeField] private string _name;
	[SerializeField, Range(0, 23)] private int _bitIndex;

	public string Name => _name;
	public int BitIndex => _bitIndex;
}