using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/*
If many clothing items are expected,
and they cause bloat, consider somehow serializing/deserializing the clothes on request.
They will stay in the inspector in editor, 
but will be removed and serialized in play time automatically, and deserialized when needed.

*/

public class OutfitManager : MonoBehaviour
{
	public const int NoneIndex = -1;
	private static readonly int ShellOffsetId = Shader.PropertyToID("_ShellOffset");
	private static readonly int OutfitShellOffsetId = Shader.PropertyToID("_OutfitShellOffset");
	private static readonly int StencilId = Shader.PropertyToID("_Stencil");
	private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");
	private static readonly int StencilOpId = Shader.PropertyToID("_StencilOp");
	private static readonly int StencilReadMaskId = Shader.PropertyToID("_StencilReadMask");
	private static readonly int StencilWriteMaskId = Shader.PropertyToID("_StencilWriteMask");
	private const string DefaultStencilLayerShaderName = "Shoot The Pig/Outfit Stencil Lit";
	private const int TotalStencilBits = 8;

	private readonly Dictionary<Renderer, RendererMaterialState> _rendererMaterialStates = new Dictionary<Renderer, RendererMaterialState>();
	private bool _sharedMeshVariantsWereApplied;
	private bool _warnedMissingStencilSupport;
	private Shader _resolvedStencilLayerShader;

	[Header("Assign Clothes")]
	[SerializeField] private OutfitArea[] _areas = new OutfitArea[0];

	[Header("Depth Layering")]
	[SerializeField] private bool _applyDepthLayering = true;
	[SerializeField, Tooltip("Render queue used by visual layer 0. Higher visual layers render earlier by subtracting Render Queue Step.")]
	private int _innerClothingRenderQueue = 2000;
	[SerializeField, Min(0), Tooltip("Queue spacing between visual layers. With base 2000 and step 1: layer 2 renders at 1998, layer 1 at 1999, layer 0 at 2000.")]
	private int _renderQueueStep = 1;
	[SerializeField, Tooltip("Optional body renderer(s). With Body Visual Layer -1, the body renders after clothing and benefits from depth rejection where covered.")]
	private Renderer[] _bodyRenderers = Array.Empty<Renderer>();
	[SerializeField, Tooltip("Visual layer for the body. With the default queue settings, -1 resolves to render queue 2001.")]
	private int _bodyVisualLayer = -1;
	[SerializeField, Tooltip("Uses one stencil bit per outfit layer. Higher layers mask lower layers even when the lower mesh is closer to the camera.")]
	private bool _applyStencilLayering = true;
	[SerializeField, Range(0, TotalStencilBits - 1), Tooltip("First stencil bit used by outfit layers. Bits 2 and 3 are reserved by URP for LOD cross-fade, so 4 is the safest default.")]
	private int _stencilLayerBitOffset = 4;
	[SerializeField, Range(1, TotalStencilBits), Tooltip("Number of stencil bits available to outfit layers. Four bits supports visual layers 0-3.")]
	private int _stencilLayerBitCount = 4;
	[SerializeField, Tooltip("Optional shader used for runtime material clones when the original material shader does not expose outfit stencil properties. Leave empty to find Shoot The Pig/Outfit Stencil Lit automatically.")]
	private Shader _stencilLayerShader;

	[Header("Shared Mesh Variants")]
	[SerializeField, Tooltip("Uses the shared mesh variant cache for body and optional clothing mesh users. Lower-layer visibility is resolved by ANDing the visible-below masks of all selected upper layers in each area.")]
	private bool _applySharedMeshVariants = true;
	[SerializeField, Tooltip("Body mesh users that receive the combined hide mask resolved from all selected clothing items above the body.")]
	private SharedMeshVariantUser[] _bodyMeshVariantUsers = Array.Empty<SharedMeshVariantUser>();

	[Header("Startup")]
	[SerializeField] private bool _applySerializedSelectionsOnAwake;

	[Header("Context Menu Test Selection")]
	[SerializeField] private int _testAreaIndex;
	[SerializeField] private int _testLayerIndex;
	[SerializeField] private int _testClothingIndex = NoneIndex;

	public OutfitArea[] Areas => _areas;

	private void Awake()
	{
		if (_applySerializedSelectionsOnAwake)
		{
			ApplyAllSelections();
		}
	}

	private void OnDestroy()
	{
		RestoreRuntimeMaterials();
	}

	private void Reset()
	{
		_areas = new[]
		{
			new OutfitArea("Torso", 2),
			new OutfitArea("Legs", 1),
			new OutfitArea("Feet", 1),
			new OutfitArea("Front Of Head", 1),
			new OutfitArea("Top Of Head", 1)
		};
	}

	public void ApplyAllSelections()
	{
		if (_areas == null)
		{
			RefreshOutfitVisuals();
			return;
		}

		for (int areaIndex = 0; areaIndex < _areas.Length; areaIndex++)
		{
			OutfitArea area = _areas[areaIndex];

			if (area == null || area.Layers == null)
			{
				continue;
			}

			for (int layerIndex = 0; layerIndex < area.Layers.Length; layerIndex++)
			{
				OutfitLayer layer = area.Layers[layerIndex];

				if (layer == null)
				{
					continue;
				}

				ApplyLayerSelection(layer, NormalizeClothingIndex(layer, layer.SelectedClothingIndex), false);
			}
		}

		RefreshOutfitVisuals();
	}

	public bool SelectClothing(int areaIndex, int layerIndex, int clothingIndex)
	{
		if (!TryGetLayer(areaIndex, layerIndex, out OutfitLayer layer))
		{
			return false;
		}

		if (clothingIndex == NoneIndex)
		{
			ApplyLayerSelection(layer, NoneIndex);
			return true;
		}

		if (!IsValidClothingIndex(layer, clothingIndex))
		{
			return false;
		}

		ApplyLayerSelection(layer, clothingIndex);
		return true;
	}

	public bool SelectNone(int areaIndex, int layerIndex)
	{
		return SelectClothing(areaIndex, layerIndex, NoneIndex);
	}

	public int GetSelectedClothingIndex(int areaIndex, int layerIndex)
	{
		return TryGetLayer(areaIndex, layerIndex, out OutfitLayer layer) ? layer.SelectedClothingIndex : NoneIndex;
	}

	public string GetSelectedClothingName(int areaIndex, int layerIndex)
	{
		if (!TryGetLayer(areaIndex, layerIndex, out OutfitLayer layer))
		{
			return string.Empty;
		}

		int clothingIndex = layer.SelectedClothingIndex;

		if (!IsValidClothingIndex(layer, clothingIndex))
		{
			return string.Empty;
		}

		ClothingItem clothingItem = layer.Clothes[clothingIndex];
		return clothingItem != null ? clothingItem.Name : string.Empty;
	}


	[ContextMenu("Outfit/Apply All Serialized Selections")]
	private void ContextApplyAllSelections()
	{
		ApplyAllSelections();
	}

	[ContextMenu("Outfit/Refresh Visual Layering")]
	private void ContextRefreshVisualLayering()
	{
		RefreshOutfitVisuals();
	}

	[ContextMenu("Outfit/Test Select Clothing By Index")]
	private void ContextSelectClothingByIndex()
	{
		if (!SelectClothing(_testAreaIndex, _testLayerIndex, _testClothingIndex))
		{
			Debug.LogWarning($"Could not select clothing at area {_testAreaIndex}, layer {_testLayerIndex}, clothing {_testClothingIndex}.", this);
		}
	}

	[ContextMenu("Outfit/Test Select None By Index")]
	private void ContextSelectNoneByIndex()
	{
		if (!SelectNone(_testAreaIndex, _testLayerIndex))
		{
			Debug.LogWarning($"Could not clear outfit layer at area {_testAreaIndex}, layer {_testLayerIndex}.", this);
		}
	}


	private bool TryGetArea(int areaIndex, out OutfitArea area)
	{
		area = null;

		if (_areas == null || areaIndex < 0 || areaIndex >= _areas.Length)
		{
			return false;
		}

		area = _areas[areaIndex];
		return area != null;
	}

	private bool TryGetLayer(int areaIndex, int layerIndex, out OutfitLayer layer)
	{
		layer = null;

		if (!TryGetArea(areaIndex, out OutfitArea area) || area.Layers == null || layerIndex < 0 || layerIndex >= area.Layers.Length)
		{
			return false;
		}

		layer = area.Layers[layerIndex];
		return layer != null;
	}

	private void ApplyLayerSelection(OutfitLayer layer, int selectedClothingIndex, bool refreshOutfitVisuals = true)
	{
		layer.SelectedClothingIndex = selectedClothingIndex;

		if (layer.Clothes == null)
		{
			return;
		}

		for (int i = 0; i < layer.Clothes.Length; i++)
		{
			ClothingItem clothingItem = layer.Clothes[i];

			if (clothingItem == null || clothingItem.ClothingObject == null)
			{
				continue;
			}

			clothingItem.ClothingObject.SetActive(i == selectedClothingIndex);
		}

		if (refreshOutfitVisuals)
			RefreshOutfitVisuals();
	}

	private void RefreshOutfitVisuals()
	{
		RefreshRenderLayers();
		RefreshSharedMeshVariantState();
	}

	private void RefreshSharedMeshVariantState()
	{
		if (_applySharedMeshVariants)
		{
			RefreshSharedMeshVariants();
			_sharedMeshVariantsWereApplied = true;
			return;
		}

		if (_sharedMeshVariantsWereApplied)
		{
			ClearSharedMeshVariants();
			_sharedMeshVariantsWereApplied = false;
		}
	}

	private void RefreshRenderLayers()
	{
		if (!_applyDepthLayering)
		{
			RestoreRuntimeMaterials();
			return;
		}

		if (!Application.isPlaying || _areas == null)
			return;

		for (int areaIndex = 0; areaIndex < _areas.Length; areaIndex++)
		{
			OutfitArea area = _areas[areaIndex];
			if (area?.Layers == null)
				continue;

			for (int layerIndex = 0; layerIndex < area.Layers.Length; layerIndex++)
			{
				OutfitLayer layer = area.Layers[layerIndex];
				if (layer?.Clothes == null || !IsValidClothingIndex(layer, layer.SelectedClothingIndex))
					continue;

				ApplyRenderLayer(layer.Clothes[layer.SelectedClothingIndex], layerIndex);
			}
		}

		ApplyBodyRenderLayer();
	}

	private void ApplyRenderLayer(ClothingItem clothingItem, int visualLayer)
	{
		if (clothingItem == null || clothingItem.ClothingObject == null)
			return;

		Renderer[] renderers = ResolveRenderers(clothingItem);
		ApplyRenderLayer(renderers, visualLayer, clothingItem.ShellOffsetMeters);
	}

	private void ApplyBodyRenderLayer()
	{
		if (_bodyRenderers == null || _bodyRenderers.Length == 0)
			return;

		ApplyRenderLayer(_bodyRenderers, _bodyVisualLayer, 0f);
	}

	private void ApplyRenderLayer(Renderer[] renderers, int visualLayer, float shellOffsetMeters)
	{
		if (renderers == null)
			return;

		int renderQueue = ResolveRenderQueue(visualLayer);

		for (int i = 0; i < renderers.Length; i++)
		{
			Renderer renderer = renderers[i];
			if (renderer == null)
				continue;

			Material[] materials = GetOrCreateRuntimeMaterials(renderer);
			for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
			{
				Material material = materials[materialIndex];
				if (material == null)
					continue;

				material.renderQueue = renderQueue;
				ApplyStencilShaderIfNeeded(material);
				ApplyShellOffset(material, shellOffsetMeters);
				ApplyStencilLayer(material, visualLayer);
			}
		}
	}

	private Renderer[] ResolveRenderers(ClothingItem clothingItem)
	{
		Renderer[] explicitRenderers = clothingItem.Renderers;
		if (explicitRenderers != null && explicitRenderers.Length > 0)
			return explicitRenderers;

		return clothingItem.ClothingObject.GetComponentsInChildren<Renderer>(true);
	}

	private int ResolveRenderQueue(int visualLayer)
	{
		int renderQueue = _innerClothingRenderQueue - visualLayer * Mathf.Max(0, _renderQueueStep);
		return Mathf.Clamp(renderQueue, 0, 5000);
	}

	private Material[] GetOrCreateRuntimeMaterials(Renderer renderer)
	{
		if (_rendererMaterialStates.TryGetValue(renderer, out RendererMaterialState existingState))
		{
			if (existingState.RuntimeMaterials != null)
			{
				renderer.sharedMaterials = existingState.RuntimeMaterials;
				return existingState.RuntimeMaterials;
			}
		}

		Material[] originalMaterials = renderer.sharedMaterials;
		Material[] runtimeMaterials = new Material[originalMaterials.Length];
		for (int i = 0; i < originalMaterials.Length; i++)
		{
			Material sourceMaterial = originalMaterials[i];
			if (sourceMaterial == null)
				continue;

			Material runtimeMaterial = new Material(sourceMaterial)
			{
				name = $"{sourceMaterial.name} (Outfit Runtime)",
				hideFlags = HideFlags.DontSave
			};
			runtimeMaterials[i] = runtimeMaterial;
		}

		renderer.sharedMaterials = runtimeMaterials;
		_rendererMaterialStates[renderer] = new RendererMaterialState(originalMaterials, runtimeMaterials);
		return runtimeMaterials;
	}

	private static void ApplyShellOffset(Material material, float shellOffsetMeters)
	{
		if (material.HasProperty(ShellOffsetId))
			material.SetFloat(ShellOffsetId, shellOffsetMeters);

		if (material.HasProperty(OutfitShellOffsetId))
			material.SetFloat(OutfitShellOffsetId, shellOffsetMeters);
	}

	private void ApplyStencilLayer(Material material, int visualLayer)
	{
		if (!_applyStencilLayering)
		{
			DisableStencilLayering(material);
			return;
		}

		if (!HasStencilProperties(material))
		{
			if (!_warnedMissingStencilSupport)
			{
				Debug.LogWarning("Outfit stencil layering needs materials whose shader exposes _Stencil, _StencilComp, _StencilOp, _StencilReadMask, and _StencilWriteMask. Assign the outfit stencil shader if automatic shader lookup cannot find it.", this);
				_warnedMissingStencilSupport = true;
			}

			return;
		}

		if (visualLayer < 0)
		{
			int allOutfitLayerBits = GetAllOutfitLayerBits();
			ApplyStencilState(
				material,
				0,
				CompareFunction.Equal,
				StencilOp.Keep,
				allOutfitLayerBits,
				0);
			return;
		}

		int stencilLayerBitCount = GetStencilLayerBitCount();
		if (visualLayer >= stencilLayerBitCount)
		{
			Debug.LogWarning($"Outfit visual layer {visualLayer} cannot use stencil layering with the current bit range. Layers 0-{stencilLayerBitCount - 1} are supported.", this);
			DisableStencilLayering(material);
			return;
		}

		int ownLayerBit = 1 << (_stencilLayerBitOffset + visualLayer);
		int higherLayerMask = GetHigherLayerMask(visualLayer);
		ApplyStencilState(
			material,
			ownLayerBit,
			CompareFunction.Equal,
			StencilOp.Replace,
			higherLayerMask,
			ownLayerBit);
	}

	private void ApplyStencilShaderIfNeeded(Material material)
	{
		if (!_applyStencilLayering || HasStencilProperties(material))
			return;

		Shader stencilShader = ResolveStencilLayerShader();
		if (stencilShader == null)
			return;

		material.shader = stencilShader;
	}

	private Shader ResolveStencilLayerShader()
	{
		if (_stencilLayerShader != null)
			return _stencilLayerShader;

		if (_resolvedStencilLayerShader == null)
			_resolvedStencilLayerShader = Shader.Find(DefaultStencilLayerShaderName);

		return _resolvedStencilLayerShader;
	}

	private int GetHigherLayerMask(int visualLayer)
	{
		int ownAndLowerBits = ((1 << (visualLayer + 1)) - 1) << _stencilLayerBitOffset;
		return GetAllOutfitLayerBits() & ~ownAndLowerBits;
	}

	private int GetAllOutfitLayerBits()
	{
		int stencilLayerBitCount = GetStencilLayerBitCount();
		return ((1 << stencilLayerBitCount) - 1) << _stencilLayerBitOffset;
	}

	private int GetStencilLayerBitCount()
	{
		return Mathf.Clamp(_stencilLayerBitCount, 1, TotalStencilBits - _stencilLayerBitOffset);
	}

	private static void DisableStencilLayering(Material material)
	{
		if (!HasStencilProperties(material))
			return;

		ApplyStencilState(
			material,
			0,
			CompareFunction.Always,
			StencilOp.Keep,
			0,
			0);
	}

	private static bool HasStencilProperties(Material material)
	{
		return material != null
			&& material.HasProperty(StencilId)
			&& material.HasProperty(StencilCompId)
			&& material.HasProperty(StencilOpId)
			&& material.HasProperty(StencilReadMaskId)
			&& material.HasProperty(StencilWriteMaskId);
	}

	private static void ApplyStencilState(Material material, int stencilRef, CompareFunction compareFunction, StencilOp stencilOp, int readMask, int writeMask)
	{
		material.SetInt(StencilId, stencilRef);
		material.SetInt(StencilCompId, (int)compareFunction);
		material.SetInt(StencilOpId, (int)stencilOp);
		material.SetInt(StencilReadMaskId, readMask);
		material.SetInt(StencilWriteMaskId, writeMask);
	}

	private void RestoreRuntimeMaterials()
	{
		foreach (KeyValuePair<Renderer, RendererMaterialState> entry in _rendererMaterialStates)
		{
			Renderer renderer = entry.Key;
			RendererMaterialState state = entry.Value;
			if (renderer != null && state.OriginalSharedMaterials != null)
				renderer.sharedMaterials = state.OriginalSharedMaterials;

			DestroyRuntimeMaterials(state.RuntimeMaterials);
		}

		_rendererMaterialStates.Clear();
	}

	private static void DestroyRuntimeMaterials(Material[] materials)
	{
		if (materials == null)
			return;

		for (int i = 0; i < materials.Length; i++)
		{
			Material material = materials[i];
			if (material == null)
				continue;

			if (Application.isPlaying)
				Destroy(material);
			else
				DestroyImmediate(material);
		}
	}

	private void RefreshSharedMeshVariants()
	{
		uint combinedBodyHideMask = 0u;

		for (int areaIndex = 0; areaIndex < _areas.Length; areaIndex++)
		{
			OutfitArea area = _areas[areaIndex];
			if (area?.Layers == null)
				continue;

			uint areaMask = area.SectionMask;
			if (areaMask == 0u)
				continue;

			for (int layerIndex = 0; layerIndex < area.Layers.Length; layerIndex++)
			{
				if (!TryGetSelectedClothing(area.Layers[layerIndex], out ClothingItem clothingItem))
					continue;

				uint visibleMaskFromAbove = ResolveVisibleMaskFromUpperLayers(area.Layers, layerIndex + 1, areaMask);
				uint hideMask = areaMask & ~visibleMaskFromAbove;
				ApplySharedMeshHideMask(clothingItem.MeshVariantUsers, hideMask);
			}

			uint visibleMaskOnBody = ResolveVisibleMaskFromUpperLayers(area.Layers, 0, areaMask);
			combinedBodyHideMask |= areaMask & ~visibleMaskOnBody;
		}

		ApplySharedMeshHideMask(_bodyMeshVariantUsers, combinedBodyHideMask);
	}

	private void ClearSharedMeshVariants()
	{
		ApplySharedMeshHideMask(_bodyMeshVariantUsers, 0u);
		ForEachClothingItem((_, _, clothingItem) => ApplySharedMeshHideMask(clothingItem.MeshVariantUsers, 0u));
	}

	private static uint ResolveVisibleMaskFromUpperLayers(OutfitLayer[] layers, int firstUpperLayerIndex, uint areaMask)
	{
		uint visibleMask = areaMask;

		if (layers == null)
			return visibleMask;

		for (int layerIndex = firstUpperLayerIndex; layerIndex < layers.Length; layerIndex++)
		{
			if (!TryGetSelectedClothing(layers[layerIndex], out ClothingItem clothingItem))
				continue;

			visibleMask &= clothingItem.VisibleOnLayersBelowMask;
		}

		return visibleMask;
	}

	private static bool TryGetSelectedClothing(OutfitLayer layer, out ClothingItem clothingItem)
	{
		clothingItem = null;

		if (layer?.Clothes == null || !IsValidClothingIndex(layer, layer.SelectedClothingIndex))
			return false;

		clothingItem = layer.Clothes[layer.SelectedClothingIndex];
		return clothingItem != null;
	}

	private static void ApplySharedMeshHideMask(SharedMeshVariantUser[] meshVariantUsers, uint hideMask)
	{
		if (meshVariantUsers == null)
			return;

		for (int i = 0; i < meshVariantUsers.Length; i++)
		{
			SharedMeshVariantUser meshVariantUser = meshVariantUsers[i];
			if (meshVariantUser == null)
				continue;

			meshVariantUser.ApplyHideMask(hideMask);
		}
	}

	private void ForEachClothingItem(Action<OutfitLayer, int, ClothingItem> visitor)
	{
		if (_areas == null || visitor == null)
			return;

		for (int areaIndex = 0; areaIndex < _areas.Length; areaIndex++)
		{
			OutfitArea area = _areas[areaIndex];
			if (area?.Layers == null)
				continue;

			for (int layerIndex = 0; layerIndex < area.Layers.Length; layerIndex++)
			{
				OutfitLayer layer = area.Layers[layerIndex];
				if (layer?.Clothes == null)
					continue;

				for (int clothingIndex = 0; clothingIndex < layer.Clothes.Length; clothingIndex++)
				{
					ClothingItem clothingItem = layer.Clothes[clothingIndex];
					if (clothingItem == null)
						continue;

					visitor(layer, clothingIndex, clothingItem);
				}
			}
		}
	}

	private int NormalizeClothingIndex(OutfitLayer layer, int clothingIndex)
	{
		return IsValidClothingIndex(layer, clothingIndex) ? clothingIndex : NoneIndex;
	}

	private static bool IsValidClothingIndex(OutfitLayer layer, int clothingIndex)
	{
		return layer != null && layer.Clothes != null && clothingIndex >= 0 && clothingIndex < layer.Clothes.Length;
	}

	private sealed class RendererMaterialState
	{
		public readonly Material[] OriginalSharedMaterials;
		public readonly Material[] RuntimeMaterials;

		public RendererMaterialState(Material[] originalSharedMaterials, Material[] runtimeMaterials)
		{
			OriginalSharedMaterials = originalSharedMaterials;
			RuntimeMaterials = runtimeMaterials;
		}
	}

}

[Serializable]
public class OutfitArea
{
	[SerializeField] private string _name;
	[SerializeField, HideInInspector, Min(0)]
	private int _sectionMask;
	[SerializeField, Tooltip("Each entry enables one shared section bit for this area. The runtime combines the selected bit indices into the final mask automatically.")]
	private BitIndexMask _sectionBits = new BitIndexMask();
	[SerializeField] private OutfitLayer[] _layers = new OutfitLayer[0];

	public string Name => _name;
	public uint SectionMask => _sectionBits.HasBits ? _sectionBits.Value : (uint)Mathf.Max(0, _sectionMask);
	public OutfitLayer[] Layers => _layers;

	public OutfitArea(string name, int layerCount)
	{
		_name = name;
		_layers = new OutfitLayer[layerCount];

		for (int i = 0; i < _layers.Length; i++)
		{
			_layers[i] = new OutfitLayer($"Layer {i}");
		}
	}
}

[Serializable]
public class OutfitLayer
{
	[SerializeField] private string _name;
	[Tooltip("Use -1 for no clothing selected in this layer.")]
	[SerializeField] private int _selectedClothingIndex = OutfitManager.NoneIndex;
	[SerializeField] private ClothingItem[] _clothes = new ClothingItem[0];

	public string Name => _name;
	public int SelectedClothingIndex
	{
		get => _selectedClothingIndex;
		set => _selectedClothingIndex = value;
	}
	public ClothingItem[] Clothes => _clothes;

	public OutfitLayer(string name)
	{
		_name = name;
	}
}

[Serializable]
public class ClothingItem
{
	[SerializeField] private string _name;
	[SerializeField] private GameObject _clothingObject;
	[SerializeField, Min(0f), Tooltip("Reserved for a future clothing shader vertex normal offset. Standard URP Lit ignores this value.")]
	private float _shellOffsetMeters;
	[SerializeField, Tooltip("Optional explicit renderers. If empty, all child renderers under the clothing object are used.")]
	private Renderer[] _renderers = Array.Empty<Renderer>();
	[SerializeField, Tooltip("Optional shared mesh variant users on this clothing item. When there are selected upper layers, this clothing mesh will hide everything except the sections left visible by all of them.")]
	private SharedMeshVariantUser[] _meshVariantUsers = Array.Empty<SharedMeshVariantUser>();
	[SerializeField, HideInInspector, Min(0)]
	private int _visibleOnLayersBelowMask;
	[SerializeField, Tooltip("Each entry enables one shared section bit that remains visible on lower layers beneath this clothing item. The runtime combines the selected bit indices into the final mask automatically.")]
	private BitIndexMask _visibleOnLayersBelowBits = new BitIndexMask();

	public string Name => _name;
	public GameObject ClothingObject => _clothingObject;
	public float ShellOffsetMeters => _shellOffsetMeters;
	public Renderer[] Renderers => _renderers;
	public SharedMeshVariantUser[] MeshVariantUsers => _meshVariantUsers;
	public uint VisibleOnLayersBelowMask => _visibleOnLayersBelowBits.HasBits ? _visibleOnLayersBelowBits.Value : (uint)Mathf.Max(0, _visibleOnLayersBelowMask);
}

[Serializable]
public class BitIndexMask
{
	[SerializeField] private int[] _bitIndices = Array.Empty<int>();

	public bool HasBits => _bitIndices != null && _bitIndices.Length > 0;
	public uint Value => BuildMask(_bitIndices);

	public static uint BuildMask(IReadOnlyList<int> bitIndices)
	{
		if (bitIndices == null)
			return 0u;

		uint mask = 0u;
		for (int i = 0; i < bitIndices.Count; i++)
		{
			int bitIndex = Mathf.Clamp(bitIndices[i], 0, 31);
			mask |= 1u << bitIndex;
		}

		return mask;
	}
}
