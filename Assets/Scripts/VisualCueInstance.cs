using System;
using UnityEngine;

public class VisualCueInstance : MonoBehaviour
{
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int UnlitColorId = Shader.PropertyToID("_UnlitColor");
    private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
    private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    [SerializeField] private Renderer[] renderers;
    [SerializeField, Range(0f, 0.1f)] private float invisibleAlphaThreshold = 0.01f;

    private VisualCuePool owner;
    private VisualCueInstance prefabKey;
    private VisualCueRequest request;
    private Vector3 baseScale;
    private float elapsed;
    private bool playing;
    private Camera cachedCamera;
    private RendererState[] rendererStates = Array.Empty<RendererState>();
    private bool hasFadeRenderers;

    private struct RendererState
    {
        public Renderer renderer;
        public MaterialSlotState[] materialSlots;
    }

    private struct MaterialSlotState
    {
        public int materialIndex;
        public int colorPropertyId;
        public Color baseColor;
        public int emissionPropertyId;
        public Color baseEmissionColor;
        public MaterialPropertyBlock block;
        public bool hasColorProperty;
        public bool hasEmissionProperty;
    }

    private void Awake()
    {
        baseScale = transform.localScale;

        if (renderers == null || renderers.Length == 0)
            renderers = GetComponentsInChildren<Renderer>(true);

        CacheRendererState();
    }

    public void Play(VisualCuePool owner, VisualCueInstance prefabKey, VisualCueRequest request)
    {
        this.owner = owner;
        this.prefabKey = prefabKey;
        this.request = request;
        elapsed = 0f;
        playing = true;

        Transform parent = request.parentTransform != null ? request.parentTransform : owner != null ? owner.transform : null;
        transform.SetParent(parent, false);
        transform.SetLocalPositionAndRotation(request.startPosition, request.rotation * request.localRotationOffset);
        transform.localScale = baseScale * request.startScale;
        gameObject.SetActive(true);

        cachedCamera = null;

        if (hasFadeRenderers)
            SetAlpha(GetHorizontalViewAlphaMultiplier());
    }

    private void Update()
    {
        if (!playing)
            return;

        elapsed += Time.deltaTime;
        float t = request.lifetime > 0f ? Mathf.Clamp01(elapsed / request.lifetime) : 1f;
        float eased = Mathf.SmoothStep(0f, 1f, t);

        transform.localPosition = Vector3.LerpUnclamped(request.startPosition, request.endPosition, eased);
        transform.localScale = baseScale * Mathf.LerpUnclamped(request.startScale, request.endScale, eased);

        if (hasFadeRenderers)
            SetAlpha((1f - t) * GetHorizontalViewAlphaMultiplier());

        if (t >= 1f)
            Complete();
    }

    private float GetHorizontalViewAlphaMultiplier()
    {
        if (!request.useHorizontalViewAlpha)
            return 1f;

        if (!TryGetHorizontalViewAngle(out float angleToAxis))
            return 1f;

        bool isSideView = angleToAxis >= request.horizontalViewAlphaSwitchAngle;
        bool visible = request.invertHorizontalViewAlpha ? !isSideView : isSideView;
        return visible ? 1f : 0f;
    }

    private bool TryGetHorizontalViewAngle(out float angleToAxis)
    {
        angleToAxis = 0f;

        Camera referenceCamera = GetReferenceCamera();
        if (referenceCamera == null)
            return false;

        Transform reference = request.horizontalViewReference;
        Vector3 up = reference != null
            ? reference.up
            : (request.horizontalViewAlphaUp.sqrMagnitude > 1e-6f ? request.horizontalViewAlphaUp.normalized : Vector3.up);
        Vector3 forward = reference != null ? reference.forward : request.horizontalViewAlphaForward;
        Vector3 origin = reference != null ? reference.TransformPoint(request.horizontalViewAlphaOrigin) : request.horizontalViewAlphaOrigin;

        Vector3 forwardOnPlane = Vector3.ProjectOnPlane(forward, up);
        if (forwardOnPlane.sqrMagnitude <= 1e-6f)
            return false;

        Vector3 viewDirectionOnPlane = Vector3.ProjectOnPlane(-referenceCamera.transform.forward, up);
        if (viewDirectionOnPlane.sqrMagnitude <= 1e-6f)
            viewDirectionOnPlane = Vector3.ProjectOnPlane(referenceCamera.transform.position - origin, up);
        if (viewDirectionOnPlane.sqrMagnitude <= 1e-6f)
            return false;

        angleToAxis = Mathf.Abs(Vector3.SignedAngle(forwardOnPlane, viewDirectionOnPlane, up));
        if (angleToAxis > 90f)
            angleToAxis = 180f - angleToAxis;

        return true;
    }

    private Camera GetReferenceCamera()
    {
        if (cachedCamera == null)
            cachedCamera = request.referenceCamera != null ? request.referenceCamera : Camera.main;

        return cachedCamera;
    }

    private void Complete()
    {
        playing = false;
        owner?.Release(this, prefabKey);
    }

    private void CacheRendererState()
    {
        rendererStates = new RendererState[renderers.Length];
        hasFadeRenderers = false;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            Material[] materials = renderer != null ? renderer.sharedMaterials : null;
            MaterialSlotState[] materialSlots = materials != null ? new MaterialSlotState[materials.Length] : Array.Empty<MaterialSlotState>();

            for (int materialIndex = 0; materialIndex < materialSlots.Length; materialIndex++)
            {
                Material material = materials[materialIndex];

                int colorPropertyId = 0;
                Color baseColor = Color.white;
                int emissionPropertyId = 0;
                Color baseEmissionColor = Color.black;
                if (material != null)
                {
                    if (TryGetColorProperty(material, out colorPropertyId))
                        baseColor = material.GetColor(colorPropertyId);

                    if (TryGetEmissionProperty(material, out emissionPropertyId))
                        baseEmissionColor = material.GetColor(emissionPropertyId);
                }

                bool hasColorProperty = colorPropertyId != 0;
                bool hasEmissionProperty = emissionPropertyId != 0;
                hasFadeRenderers |= hasColorProperty || hasEmissionProperty;

                materialSlots[materialIndex] = new MaterialSlotState
                {
                    materialIndex = materialIndex,
                    colorPropertyId = colorPropertyId,
                    baseColor = baseColor,
                    emissionPropertyId = emissionPropertyId,
                    baseEmissionColor = baseEmissionColor,
                    block = new MaterialPropertyBlock(),
                    hasColorProperty = hasColorProperty,
                    hasEmissionProperty = hasEmissionProperty,
                };
            }

            rendererStates[i] = new RendererState
            {
                renderer = renderer,
                materialSlots = materialSlots,
            };
        }
    }

    private static bool TryGetColorProperty(Material material, out int propertyId)
    {
        if (material.HasProperty(BaseColorId))
        {
            propertyId = BaseColorId;
            return true;
        }

        if (material.HasProperty(ColorId))
        {
            propertyId = ColorId;
            return true;
        }

        if (material.HasProperty(UnlitColorId))
        {
            propertyId = UnlitColorId;
            return true;
        }

        if (material.HasProperty(TintColorId))
        {
            propertyId = TintColorId;
            return true;
        }

        propertyId = 0;
        return false;
    }

    private static bool TryGetEmissionProperty(Material material, out int propertyId)
    {
        if (material.HasProperty(EmissiveColorId))
        {
            propertyId = EmissiveColorId;
            return true;
        }

        if (material.HasProperty(EmissionColorId))
        {
            propertyId = EmissionColorId;
            return true;
        }

        propertyId = 0;
        return false;
    }

    private void SetAlpha(float alpha)
    {
        if (rendererStates == null)
            return;

        float clampedAlpha = Mathf.Clamp01(alpha);
        for (int i = 0; i < rendererStates.Length; i++)
        {
            RendererState state = rendererStates[i];
            if (state.renderer == null)
                continue;

            state.renderer.enabled = clampedAlpha > invisibleAlphaThreshold;

            for (int slotIndex = 0; slotIndex < state.materialSlots.Length; slotIndex++)
            {
                MaterialSlotState slot = state.materialSlots[slotIndex];
                if (!slot.hasColorProperty && !slot.hasEmissionProperty)
                    continue;

                if (slot.hasColorProperty)
                {
                    Color color = slot.baseColor;
                    color.a *= clampedAlpha;
                    slot.block.SetColor(slot.colorPropertyId, color);
                }

                if (slot.hasEmissionProperty)
                {
                    Color emissionColor = slot.baseEmissionColor * clampedAlpha;
                    slot.block.SetColor(slot.emissionPropertyId, emissionColor);
                }

                state.renderer.SetPropertyBlock(slot.block, slot.materialIndex);
            }
        }
    }
}