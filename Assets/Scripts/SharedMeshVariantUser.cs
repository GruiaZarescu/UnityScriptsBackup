using UnityEngine;

public class SharedMeshVariantUser : MonoBehaviour
{
	[SerializeField] private SkinnedMeshRenderer _skinnedMeshRenderer;
	[SerializeField] private MeshFilter _meshFilter;
	[SerializeField, Tooltip("Optional source mesh override. Leave empty to use the current shared mesh as the variant source.")]
	private Mesh _sourceMeshOverride;

	private Mesh _sourceMesh;
	private uint _activeHideMask = uint.MaxValue;
	private bool _initialized;

	private void Awake()
	{
		EnsureInitialized();
	}

	private void OnDestroy()
	{
		RestoreSourceMesh();
	}

	public void ApplyHideMask(uint hideMask)
	{
		EnsureInitialized();

		if (_sourceMesh == null || _activeHideMask == hideMask)
			return;

		Mesh meshToAssign = SharedMeshVariantCache.Instance.GetOrCreateVariant(_sourceMesh, hideMask);
		AssignMesh(meshToAssign);
		_activeHideMask = hideMask;
	}

	public void ClearHideMask()
	{
		ApplyHideMask(0u);
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

		_sourceMesh = _sourceMeshOverride != null ? _sourceMeshOverride : ResolveCurrentMesh();
		if (_sourceMesh == null)
			Debug.LogWarning($"No source mesh found for shared mesh variant user '{name}'.", this);
	}

	private Mesh ResolveCurrentMesh()
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

	private void RestoreSourceMesh()
	{
		if (_sourceMesh == null)
			return;

		if (_skinnedMeshRenderer != null)
			_skinnedMeshRenderer.sharedMesh = _sourceMesh;
		else if (_meshFilter != null)
			_meshFilter.sharedMesh = _sourceMesh;
	}
}