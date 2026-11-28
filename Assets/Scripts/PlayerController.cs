using KinematicCharacterController;
using UnityEngine;
using UnityEngine.InputSystem;

public struct PlayerInputs
{
    public float MoveAxisForward;
    public float MoveAxisRight;
    public Quaternion CameraRotation;
    public bool JumpPressed;
    public bool isSprinting;
 }

public class PlayerController : MonoBehaviour, ICharacterController
{
    [SerializeField] private KinematicCharacterMotor _motor;
    [SerializeField] private PigAnimationController _animationController;

    [SerializeField] private float _maxStableMoveSpeed = 1.944444f;
    [SerializeField] private float _maxStableMoveSpeedSprint = 4.5f;

    [SerializeField] private float _stableMovementSharpness = 15f;
    [SerializeField] private float _orientationSharpness = 10f;
    [SerializeField] private float jumpSpeed = 10f;
    [SerializeField] private float gravityStrength = 30f;
    
    private Vector3 _moveInputVector;
    private Vector3 _lookInputVector;
    private Vector3 _characterPosition;
    private Vector3 _newUpVector;


    [Tooltip("Point from which gravity is cast")]
    public Vector3 sphereCenter;

    private bool _jumpRequested;
    private bool _sprinting;

    // ── DEV FLY MODE (temporary utility) ─────────────────────────────────────
    // F toggles. While flying: WASD moves along the CAMERA's full direction (pitch included),
    // Space = up, Left Shift = down (along the sphere normal), hold Left Ctrl = fast.
    // No gravity, no ground snapping and (by default) no collisions, so fences/trees/terrain
    // are flown straight through. Only active in the Editor and Development builds.
    [Header("Dev Fly Mode (Editor / Development builds only)")]
    [SerializeField] private bool _flyEnabledAtStart = false;
    [SerializeField] private Key _flyToggleKey = Key.F;
    [SerializeField] private float _flySpeed = 20f;
    [SerializeField] private float _flyFastMultiplier = 5f;
    [SerializeField] private float _flySharpness = 10f;
    [Tooltip("Fly through everything. Turn off to keep capsule collisions while flying.")]
    [SerializeField] private bool _flyNoClip = true;

    private bool _flying;
    private Vector3 _flyVelocityTarget;
    public bool IsFlying => _flying;

    private const float MinUpMagnitude = 1e-6f;

    private void Start()
    {
        _motor.CharacterController = this;
        RefreshCharacterUp();

        if (_animationController == null)
            _animationController = GetComponent<PigAnimationController>();

        if (_flyEnabledAtStart) SetFlying(true);
    }

    private static bool FlyModeAllowed =>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        true;
#else
        false;
#endif

    private void SetFlying(bool flying)
    {
        if (!FlyModeAllowed) flying = false;
        _flying = flying;

        bool collide = !flying || !_flyNoClip;
        _motor.SetCapsuleCollisionsActivation(collide);
        _motor.SetMovementCollisionsSolvingActivation(collide);
        _motor.SetGroundSolvingActivation(!flying);   // no ground snapping while flying
        if (flying) _motor.ForceUnground();

        _jumpRequested = false;
        _animationController?.SetJumping(false);
        Debug.Log($"[PlayerController] Dev fly mode {(flying ? "ON" : "OFF")}");
    }

    /// <summary>Reads fly controls straight from the keyboard (no InputActions asset changes),
    /// returns the desired fly velocity built from the camera's full orientation.</summary>
    private Vector3 ComputeFlyVelocity(ref PlayerInputs inputs)
    {
        var kb = Keyboard.current;
        float vertical = 0f;
        bool fast = false;
        if (kb != null)
        {
            if (kb.spaceKey.isPressed)     vertical += 1f;
            if (kb.leftShiftKey.isPressed) vertical -= 1f;
            fast = kb.leftCtrlKey.isPressed;
        }

        Vector3 camForward = inputs.CameraRotation * Vector3.forward;   // includes pitch: look down + W = dive
        Vector3 camRight   = inputs.CameraRotation * Vector3.right;
        Vector3 dir = camForward * inputs.MoveAxisForward
                    + camRight   * inputs.MoveAxisRight
                    + _newUpVector * vertical;                          // up = away from the planet centre
        dir = Vector3.ClampMagnitude(dir, 1f);

        float speed = _flySpeed * (fast ? _flyFastMultiplier : 1f);
        return dir * speed;
    }

    private void RefreshCharacterUp()
    {
        _characterPosition = transform.position;
        Vector3 radial = _characterPosition - sphereCenter;
        _newUpVector = radial.sqrMagnitude > MinUpMagnitude
            ? radial.normalized
            : Vector3.up;
    }

    private void AlignMotorUpToSphere()
    {
        Vector3 planarForward = Vector3.ProjectOnPlane(_motor.CharacterForward, _newUpVector);
        if (planarForward.sqrMagnitude < 1e-6f)
            planarForward = Vector3.ProjectOnPlane(transform.forward, _newUpVector);
        if (planarForward.sqrMagnitude < 1e-6f)
            planarForward = Vector3.ProjectOnPlane(Vector3.forward, _newUpVector);

        if (planarForward.sqrMagnitude < 1e-6f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(planarForward.normalized, _newUpVector);
        _motor.SetRotation(targetRotation, false);
    }

    public void SetInputs(ref PlayerInputs inputs)
    {
        RefreshCharacterUp();

        var kbToggle = Keyboard.current;
        if (FlyModeAllowed && kbToggle != null && kbToggle[_flyToggleKey].wasPressedThisFrame)
            SetFlying(!_flying);

        if (_flying)
        {
            _flyVelocityTarget = ComputeFlyVelocity(ref inputs);
            // Face the horizontal part of the travel direction so the pig stays upright.
            Vector3 planar = Vector3.ProjectOnPlane(_flyVelocityTarget, _newUpVector);
            _lookInputVector = planar.sqrMagnitude > 1e-4f ? planar.normalized : Vector3.zero;
            _moveInputVector = Vector3.zero;
            _jumpRequested = false;
            _animationController?.SetLocomotionState(false, false);
            return;
        }


        Vector3 moveInputVector = Vector3.ClampMagnitude(new Vector3(inputs.MoveAxisRight, 0f, inputs.MoveAxisForward), 1f);
        Vector3 cameraPlanarDirection = Vector3.ProjectOnPlane(inputs.CameraRotation * Vector3.forward, _newUpVector).normalized;

        if (cameraPlanarDirection.sqrMagnitude == 0f)
        {
            cameraPlanarDirection = Vector3.ProjectOnPlane(inputs.CameraRotation * Vector3.up, _newUpVector).normalized;
        }

        Quaternion cameraPlanarRotation = Quaternion.LookRotation(cameraPlanarDirection, _newUpVector);

        _moveInputVector = cameraPlanarRotation * moveInputVector;
        _lookInputVector = _moveInputVector.normalized;

        if (inputs.JumpPressed && _motor.GroundingStatus.IsStableOnGround)
        {
            _jumpRequested = true;
        }
        if (inputs.isSprinting)
        {
            _sprinting = true;
        }
        else
        {
            _sprinting = false;
        }

        bool isMoving = moveInputVector.sqrMagnitude != 0f;
        _animationController?.SetLocomotionState(isMoving, isMoving && _sprinting);
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        Vector3 characterUp = _motor.CharacterUp;
        if (_lookInputVector.sqrMagnitude > 0f && _orientationSharpness > 0f)
        {
            Vector3 SmoothedLookInputDirection =
            Vector3.Slerp(_motor.CharacterForward, _lookInputVector, 1 - Mathf.Exp(-_orientationSharpness * deltaTime)).normalized;
            currentRotation = Quaternion.LookRotation(SmoothedLookInputDirection, characterUp);
         }
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_flying)
        {
            currentVelocity = Vector3.Lerp(currentVelocity, _flyVelocityTarget,
                1f - Mathf.Exp(-_flySharpness * deltaTime));
            return;
        }

        Vector3 characterUp = _motor.CharacterUp;
        Vector3 _gravity;
        if (_motor.GroundingStatus.IsStableOnGround)
        {
            float currentVelocityMagnitude = currentVelocity.magnitude;
            Vector3 effectiveGroundNormal = _motor.GroundingStatus.GroundNormal;

            currentVelocity = _motor.GetDirectionTangentToSurface(currentVelocity, effectiveGroundNormal) * currentVelocityMagnitude;

            Vector3 inputRight = Vector3.Cross(_moveInputVector, characterUp);
            Vector3 reorientedInput = Vector3.Cross(effectiveGroundNormal, inputRight).normalized * _moveInputVector.magnitude;
            Vector3 TargetMovementVelocity = !_sprinting ? reorientedInput * _maxStableMoveSpeed : reorientedInput * _maxStableMoveSpeedSprint;

            currentVelocity = Vector3.Lerp(currentVelocity, TargetMovementVelocity, 1f - Mathf.Exp(-_stableMovementSharpness * deltaTime));
        }
        else
        {

            _gravity = -characterUp * gravityStrength;
            currentVelocity += _gravity * deltaTime;
        }
        
        if (_motor.GroundingStatus.IsStableOnGround && !_jumpRequested)
        {
            _animationController?.SetJumping(false);
        }

        if (_jumpRequested && _motor.GroundingStatus.IsStableOnGround)
        {
            currentVelocity += (characterUp * jumpSpeed) - Vector3.Project(currentVelocity, characterUp);
            _animationController?.SetJumping(true);
            _jumpRequested = false;
            _motor.ForceUnground();
        }
        
    }

    public void BeforeCharacterUpdate(float deltaTime)
    {
        RefreshCharacterUp();
        AlignMotorUpToSphere();
    }

    public void AfterCharacterUpdate(float deltaTime)
    {

    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {

    }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {

    }

    public void PostGroundingUpdate(float deltaTime)
    {

    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        return !(_flying && _flyNoClip);
    }

    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {

    }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 innerHitPoint, Quaternion hitRotation, ref HitStabilityReport hitStabilityReport)
    {

    }
}