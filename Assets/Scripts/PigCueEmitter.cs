using UnityEngine;

public class PigCueEmitter : MonoBehaviour
{
    [SerializeField] private Camera referenceCamera;
    [SerializeField] private VisualCuePool cuePool;
    [SerializeField] private Transform snoutAnchor;
    [SerializeField] private Vector3 snoutLocalOffset;

    [Header("Snout Oink")]
    [SerializeField, Min(0.01f)] private float oinkLifetime = 0.6f;
    [SerializeField, Min(0f)] private float oinkForwardDistance = 0.85f;
    [SerializeField] private float oinkUpDistance = 0.2f;
    [SerializeField, Min(0.001f)] private float oinkStartScale = 0.35f;
    [SerializeField, Min(0.001f)] private float oinkEndScale = 1.0f;
    [SerializeField] private Vector3 oinkLocalEulerRotation;
    [SerializeField, Min(0f)] private float oinkSideHorizontalOffset = 0.18f;
    [SerializeField, Min(0.001f)] private float oinkSideScaleMultiplier = 1.0f;
    [SerializeField, Range(0f, 90f)] private float oinkSideYawAngle = 45f;
    [SerializeField, Range(0f, 90f)] private float oinkViewSwitchAngle = 45f;

    [Header("Snout Snort")]
    [SerializeField] private Vector3 snortLocalEulerRotation;
    [SerializeField, Min(0.01f)] private float snortLifetime = 0.6f;
    [SerializeField, Min(0f)] private float snortForwardDistance = 0.85f;
    [SerializeField] private float snortUpDistance = 0.2f;
    [SerializeField, Min(0.001f)] private float snortStartScale = 0.35f;
    [SerializeField, Min(0.001f)] private float snortEndScale = 1.0f;
    [SerializeField, Min(0f)] private float snortSideHorizontalOffset = 0.18f;
    [SerializeField, Min(0.001f)] private float snortSideScaleMultiplier = 1.0f;
    [SerializeField, Range(0f, 90f)] private float snortSideYawAngle = 45f;
    [SerializeField, Range(0f, 90f)] private float snortViewSwitchAngle = 45f;

    [Header("Debug")]
    [SerializeField] private bool debugViewAlpha;
    [SerializeField] private float debugHorizontalViewAngle;
    [SerializeField] private float debugCenterVisibility;
    [SerializeField] private float debugSideVisibility;

    private void Awake()
    {
        if (referenceCamera == null)
            referenceCamera = Camera.main;
        if (cuePool == null)
            cuePool = VisualCuePool.Instance;
    }

    private void LateUpdate()
    {
        if (!Application.isPlaying || !debugViewAlpha)
            return;

        UpdateDebugViewAlpha();
    }

    public bool Emit(PigVisualCue cue)
    {
        switch (cue)
        {
            case PigVisualCue.SnoutOinkSpeech:
                return EmitSnoutOinkSpeech();
            case PigVisualCue.SnoutSnortSpeech:
                return EmitSnoutSnortSpeech();
            default:
                Debug.LogWarning($"[PigCueEmitter] Unsupported visual cue '{cue}' on '{name}'.", this);
                return false;
        }
    }

    [ContextMenu("Debug/Validate Cue Setup")]
    private void DebugValidateCueSetup()
    {
        Transform resolvedAnchor = snoutAnchor != null ? snoutAnchor : transform;
        Camera resolvedCamera = ResolveReferenceCamera();
        VisualCuePool resolvedPool = ResolvePool();

        if (snoutAnchor == null)
            Debug.LogWarning($"[PigCueEmitter] '{name}' has no explicit snoutAnchor assigned. Falling back to the emitter transform.", this);

        if (resolvedPool == null)
            Debug.LogWarning($"[PigCueEmitter] '{name}' could not resolve a VisualCuePool.", this);

        if (resolvedCamera == null)
            Debug.LogWarning($"[PigCueEmitter] '{name}' could not resolve a reference camera or Camera.main.", this);

        Debug.Log($"[PigCueEmitter] Debug setup on '{name}': anchor='{resolvedAnchor.name}', pool='{(resolvedPool != null ? resolvedPool.name : "null")}', camera='{(resolvedCamera != null ? resolvedCamera.name : "null")}', snoutLocalOffset={snoutLocalOffset}.", this);
    }

    [ContextMenu("Debug/Emit Oink Cue")]
    private void DebugEmitOinkCue()
    {
        bool emitted = EmitSnoutOinkSpeech();
        Debug.Log($"[PigCueEmitter] Manual oink cue emit on '{name}' returned {emitted}.", this);
    }

    [ContextMenu("Debug/Emit Snort Cue")]
    private void DebugEmitSnortCue()
    {
        bool emitted = EmitSnoutSnortSpeech();
        Debug.Log($"[PigCueEmitter] Manual snort cue emit on '{name}' returned {emitted}.", this);
    }

    public bool EmitSnoutOinkSpeech()
    {
        Transform anchor = snoutAnchor != null ? snoutAnchor : transform;
        Transform cueParent = anchor;
        Vector3 start = cueParent.InverseTransformPoint(anchor.position + anchor.TransformVector(snoutLocalOffset));
        Vector3 motion = cueParent.InverseTransformDirection(anchor.forward * oinkForwardDistance + anchor.up * oinkUpDistance);
        Vector3 direction = motion.sqrMagnitude > 1e-6f ? motion.normalized : anchor.forward;
        Quaternion localRotation = Quaternion.Euler(oinkLocalEulerRotation);
        float sideScale = Mathf.Max(0.001f, oinkSideScaleMultiplier);
        VisualCuePool pool = ResolvePool();
        if (pool == null)
        {
            Debug.LogWarning($"[PigCueEmitter] Could not resolve a VisualCuePool for '{name}' while emitting '{PigVisualCue.SnoutOinkSpeech}'.", this);
            return false;
        }

        bool emitted = false;
        emitted |= pool.Emit(PigVisualCue.SnoutOinkSpeech, CreateOinkRequest(start, motion, direction, cueParent, anchor, Vector3.zero, localRotation, oinkStartScale, oinkEndScale, false));

        Vector3 sideOffset = cueParent.InverseTransformDirection(anchor.right * oinkSideHorizontalOffset);
        Quaternion leftYaw = Quaternion.AngleAxis(-oinkSideYawAngle, Vector3.up);
        Quaternion rightYaw = Quaternion.AngleAxis(oinkSideYawAngle, Vector3.up);
        Vector3 leftMotion = leftYaw * motion;
        Vector3 rightMotion = rightYaw * motion;
        Vector3 leftDirection = leftMotion.sqrMagnitude > 1e-6f ? leftMotion.normalized : leftYaw * Vector3.forward;
        Vector3 rightDirection = rightMotion.sqrMagnitude > 1e-6f ? rightMotion.normalized : rightYaw * Vector3.forward;

        emitted |= pool.Emit(PigVisualCue.SnoutOinkSpeech, CreateOinkRequest(start, leftMotion, leftDirection, cueParent, anchor, -sideOffset, leftYaw * localRotation, oinkStartScale * sideScale, oinkEndScale * sideScale, true));
        emitted |= pool.Emit(PigVisualCue.SnoutOinkSpeech, CreateOinkRequest(start, rightMotion, rightDirection, cueParent, anchor, sideOffset, rightYaw * localRotation, oinkStartScale * sideScale, oinkEndScale * sideScale, true));

        if (!emitted)
            Debug.LogWarning($"[PigCueEmitter] All '{PigVisualCue.SnoutOinkSpeech}' emission attempts failed on '{name}'. Check VisualCuePool warnings for the blocking reason.", this);

        return emitted;
    }

    public bool EmitSnoutSnortSpeech()
    {
        Transform anchor = snoutAnchor != null ? snoutAnchor : transform;
        Transform cueParent = anchor;
        Vector3 start = cueParent.InverseTransformPoint(anchor.position + anchor.TransformVector(snoutLocalOffset));
        Vector3 motion = cueParent.InverseTransformDirection((-anchor.forward * snortForwardDistance) + (anchor.up * snortUpDistance));
        Vector3 direction = motion.sqrMagnitude > 1e-6f ? motion.normalized : Vector3.back;
        Quaternion localRotation = Quaternion.Euler(snortLocalEulerRotation);
        float sideScale = Mathf.Max(0.001f, snortSideScaleMultiplier);
        VisualCuePool pool = ResolvePool();
        if (pool == null)
        {
            Debug.LogWarning($"[PigCueEmitter] Could not resolve a VisualCuePool for '{name}' while emitting '{PigVisualCue.SnoutSnortSpeech}'.", this);
            return false;
        }

        bool emitted = false;
        //emitted |= pool.Emit(PigVisualCue.SnoutSnortSpeech, CreateSnortRequest(start, motion, direction, cueParent, anchor, Vector3.zero, localRotation, snortStartScale, snortEndScale));

        Vector3 sideOffset = cueParent.InverseTransformDirection(anchor.right * snortSideHorizontalOffset);
        Quaternion leftYaw = Quaternion.AngleAxis(-snortSideYawAngle, Vector3.up);
        Quaternion rightYaw = Quaternion.AngleAxis(snortSideYawAngle, Vector3.up);
        Vector3 leftMotion = leftYaw * motion;
        Vector3 rightMotion = rightYaw * motion;
        Vector3 leftDirection = leftMotion.sqrMagnitude > 1e-6f ? leftMotion.normalized : leftYaw * Vector3.back;
        Vector3 rightDirection = rightMotion.sqrMagnitude > 1e-6f ? rightMotion.normalized : rightYaw * Vector3.back;

        emitted |= pool.Emit(PigVisualCue.SnoutSnortSpeech, CreateSnortRequest(start, leftMotion, leftDirection, cueParent, anchor, -sideOffset, leftYaw * localRotation, snortStartScale * sideScale, snortEndScale * sideScale));
        emitted |= pool.Emit(PigVisualCue.SnoutSnortSpeech, CreateSnortRequest(start, rightMotion, rightDirection, cueParent, anchor, sideOffset, rightYaw * localRotation, snortStartScale * sideScale, snortEndScale * sideScale));

        if (!emitted)
            Debug.LogWarning($"[PigCueEmitter] All '{PigVisualCue.SnoutSnortSpeech}' emission attempts failed on '{name}'. Check VisualCuePool warnings for the blocking reason.", this);

        return emitted;
    }

    private VisualCueRequest CreateOinkRequest(
        Vector3 start,
        Vector3 motion,
        Vector3 direction,
        Transform cueParent,
        Transform anchor,
        Vector3 offset,
        Quaternion localRotation,
        float startScale,
        float endScale,
        bool invertHorizontalViewAlpha)
    {
        return new VisualCueRequest
        {
            parentTransform = cueParent,
            horizontalViewReference = anchor,
            startPosition = start + offset,
            endPosition = start + offset + motion,
            referenceCamera = ResolveReferenceCamera(),
            rotation = Quaternion.LookRotation(direction, Vector3.up),
            localRotationOffset = localRotation,
            horizontalViewAlphaOrigin = start,
            horizontalViewAlphaForward = Vector3.forward,
            horizontalViewAlphaUp = Vector3.up,
            horizontalViewAlphaSwitchAngle = oinkViewSwitchAngle,
            lifetime = oinkLifetime,
            startScale = startScale,
            endScale = endScale,
            useHorizontalViewAlpha = true,
            invertHorizontalViewAlpha = invertHorizontalViewAlpha,
        };
    }

    private VisualCueRequest CreateSnortRequest(
        Vector3 start,
        Vector3 motion,
        Vector3 direction,
        Transform cueParent,
        Transform anchor,
        Vector3 offset,
        Quaternion localRotation,
        float startScale,
        float endScale)
    {
        return new VisualCueRequest
        {
            parentTransform = cueParent,
            horizontalViewReference = anchor,
            startPosition = start + offset,
            endPosition = start + offset + motion,
            referenceCamera = ResolveReferenceCamera(),
            rotation = Quaternion.LookRotation(direction, Vector3.up),
            localRotationOffset = localRotation,
            lifetime = snortLifetime,
            startScale = startScale,
            endScale = endScale,
            useHorizontalViewAlpha = false,
            invertHorizontalViewAlpha = false,
        };
    }

    private void UpdateDebugViewAlpha()
    {
        Transform anchor = snoutAnchor != null ? snoutAnchor : transform;
        if (!TryGetHorizontalViewAngle(anchor, out float angleToAxis))
        {
            debugHorizontalViewAngle = -1f;
            debugCenterVisibility = 1f;
            debugSideVisibility = 1f;
            return;
        }

        debugHorizontalViewAngle = angleToAxis;
        debugCenterVisibility = EvaluateHorizontalViewVisibility(angleToAxis, false);
        debugSideVisibility = EvaluateHorizontalViewVisibility(angleToAxis, true);
    }

    private float EvaluateHorizontalViewVisibility(float angleToAxis, bool invertHorizontalViewAlpha)
    {
        bool isSideView = angleToAxis >= oinkViewSwitchAngle;
        bool visible = invertHorizontalViewAlpha ? !isSideView : isSideView;
        return visible ? 1f : 0f;
    }

    private bool TryGetHorizontalViewAngle(Transform anchor, out float angleToAxis)
    {
        angleToAxis = 0f;

        Camera camera = ResolveReferenceCamera();
        if (camera == null)
            return false;

        Vector3 up = anchor.up.sqrMagnitude > 1e-6f ? anchor.up.normalized : Vector3.up;
        Vector3 forwardOnPlane = Vector3.ProjectOnPlane(anchor.forward, up);
        if (forwardOnPlane.sqrMagnitude <= 1e-6f)
            return false;

        Vector3 viewDirectionOnPlane = Vector3.ProjectOnPlane(-camera.transform.forward, up);
        if (viewDirectionOnPlane.sqrMagnitude <= 1e-6f)
        {
            Vector3 origin = anchor.position + anchor.TransformVector(snoutLocalOffset);
            viewDirectionOnPlane = Vector3.ProjectOnPlane(camera.transform.position - origin, up);
        }

        if (viewDirectionOnPlane.sqrMagnitude <= 1e-6f)
            return false;

        angleToAxis = Mathf.Abs(Vector3.SignedAngle(forwardOnPlane, viewDirectionOnPlane, up));
        if (angleToAxis > 90f)
            angleToAxis = 180f - angleToAxis;

        return true;
    }

    private Camera ResolveReferenceCamera()
    {
        if (referenceCamera == null)
            referenceCamera = Camera.main;

        return referenceCamera;
    }

    private VisualCuePool ResolvePool()
    {
        if (cuePool == null)
            cuePool = VisualCuePool.Instance;

        return cuePool;
    }
}