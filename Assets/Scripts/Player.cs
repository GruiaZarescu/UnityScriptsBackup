using UnityEngine;
using UnityEngine.InputSystem;

public class Player : MonoBehaviour
{
    [SerializeField] private PlayerCamera _playerCamera;
    [SerializeField] private Transform _cameraFollowPoint;
    [SerializeField] private PlayerController _playerController;
    [SerializeField] private PigInteractionController _interactionController;

    private PlayerInputActions _inputActions;
    private InputAction _oinkAction;
    private InputAction _snortAction;
    private InputAction _neutralAction;
    private InputAction _sadAction;
    private InputAction _happyAction;
    private InputAction _scaredAction;
    private InputAction _excitedAction;
    private InputAction _angryAction;
    private InputAction _confusedAction;
    private InputAction _addWhiteChocolateAction;
    private InputAction _addBlackChocolateAction;

    // Cached input state
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private float _zoomInput;
    private bool _jumpPressed;   // set by callback, consumed once per Update
    private bool _sprinting;

    private void Awake()
    {
        _inputActions = new PlayerInputActions();
        _oinkAction = _inputActions.asset.FindAction("Player/Oink", throwIfNotFound: false);
        _snortAction = _inputActions.asset.FindAction("Player/Snort", throwIfNotFound: false);
        _neutralAction = _inputActions.asset.FindAction("Player/Neutral", throwIfNotFound: false);
        _sadAction = _inputActions.asset.FindAction("Player/Sad", throwIfNotFound: false);
        _happyAction = _inputActions.asset.FindAction("Player/Happy", throwIfNotFound: false);
        _scaredAction = _inputActions.asset.FindAction("Player/Scared", throwIfNotFound: false);
        _excitedAction = _inputActions.asset.FindAction("Player/Excited", throwIfNotFound: false);
        _angryAction = _inputActions.asset.FindAction("Player/Angry", throwIfNotFound: false);
        _confusedAction = _inputActions.asset.FindAction("Player/Confused", throwIfNotFound: false);
        _addWhiteChocolateAction = _inputActions.asset.FindAction("Player/AddWhiteChocolate", throwIfNotFound: false);
        _addBlackChocolateAction = _inputActions.asset.FindAction("Player/AddBlackChocolate", throwIfNotFound: false);
        if (_interactionController == null)
            _interactionController = GetComponent<PigInteractionController>();
        if (_interactionController == null && _playerController != null)
            _interactionController = _playerController.GetComponent<PigInteractionController>();
    }

    private void OnEnable()
    {
        _inputActions.Player.Enable();
        _inputActions.Player.Jump.performed   += OnJump;
        _inputActions.Player.Sprint.performed += OnSprintStart;
        _inputActions.Player.Sprint.canceled  += OnSprintEnd;

        if (_oinkAction != null)
            _oinkAction.performed += OnOink;
        if (_snortAction != null)
            _snortAction.performed += OnSnort;
        if (_neutralAction != null)
            _neutralAction.performed += OnNeutral;
        if (_sadAction != null)
            _sadAction.performed += OnSad;
        if (_happyAction != null)
            _happyAction.performed += OnHappy;
        if (_scaredAction != null)
            _scaredAction.performed += OnScared;
        if (_excitedAction != null)
            _excitedAction.performed += OnExcited;
        if (_angryAction != null)
            _angryAction.performed += OnAngry;
        if (_confusedAction != null)
            _confusedAction.performed += OnConfused;
        if (_addWhiteChocolateAction != null)
            _addWhiteChocolateAction.performed += OnAddWhiteChocolate;
        if (_addBlackChocolateAction != null)
            _addBlackChocolateAction.performed += OnAddBlackChocolate;
    }

    private void OnDisable()
    {
        _inputActions.Player.Jump.performed   -= OnJump;
        _inputActions.Player.Sprint.performed -= OnSprintStart;
        _inputActions.Player.Sprint.canceled  -= OnSprintEnd;

        if (_oinkAction != null)
            _oinkAction.performed -= OnOink;
        if (_snortAction != null)
            _snortAction.performed -= OnSnort;
        if (_neutralAction != null)
            _neutralAction.performed -= OnNeutral;
        if (_sadAction != null)
            _sadAction.performed -= OnSad;
        if (_happyAction != null)
            _happyAction.performed -= OnHappy;
        if (_scaredAction != null)
            _scaredAction.performed -= OnScared;
        if (_excitedAction != null)
            _excitedAction.performed -= OnExcited;
        if (_angryAction != null)
            _angryAction.performed -= OnAngry;
        if (_confusedAction != null)
            _confusedAction.performed -= OnConfused;
        if (_addWhiteChocolateAction != null)
            _addWhiteChocolateAction.performed -= OnAddWhiteChocolate;
        if (_addBlackChocolateAction != null)
            _addBlackChocolateAction.performed -= OnAddBlackChocolate;

        _inputActions.Player.Disable();
    }

    private void OnJump(InputAction.CallbackContext ctx)   => _jumpPressed = true;
    private void OnSprintStart(InputAction.CallbackContext ctx) => _sprinting = true;
    private void OnSprintEnd(InputAction.CallbackContext ctx)   => _sprinting = false;
    private void OnOink(InputAction.CallbackContext ctx)
    {
        _interactionController?.Oink();
    }

    private void OnSnort(InputAction.CallbackContext ctx)
    {
        _interactionController?.Snort();
    }

    private void OnNeutral(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigNeutral();
    }

    private void OnSad(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigSad();
    }

    private void OnHappy(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigHappy();
    }

    private void OnScared(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigScared();
    }

    private void OnExcited(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigExcited();
    }

    private void OnAngry(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigAngry();
    }

    private void OnConfused(InputAction.CallbackContext ctx)
    {
        _interactionController?.MakePigConfused();
    }

    private void OnAddWhiteChocolate(InputAction.CallbackContext ctx)
    {
        _interactionController?.AddWhiteChocolateToPig();
    }

    private void OnAddBlackChocolate(InputAction.CallbackContext ctx)
    {
        _interactionController?.AddBlackChocolateToPig();
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        _playerCamera.SetFollowTransform(_cameraFollowPoint);
    }

    void Update()
    {
        _moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
        _lookInput = _inputActions.Player.Look.ReadValue<Vector2>();
        _zoomInput = _inputActions.Player.Zoom.ReadValue<Vector2>().y;

        HandleCharacterInputs();
    }

    private void LateUpdate()
    {
        HandleCameraInput();
    }

    private void HandleCameraInput()
    {
        float scrollInput = -_zoomInput;
        Vector3 lookInputVector = new Vector3(_lookInput.x, _lookInput.y, 0f);
        _playerCamera.UpdateWithInput(Time.deltaTime, scrollInput, lookInputVector);
    }

    private void HandleCharacterInputs()
    {
        PlayerInputs inputs = new PlayerInputs();
        inputs.MoveAxisForward = _moveInput.y;
        inputs.MoveAxisRight   = _moveInput.x;
        inputs.JumpPressed     = _jumpPressed;
        inputs.isSprinting     = _sprinting;
        inputs.CameraRotation  = _playerCamera.transform.rotation;
        _playerController.SetInputs(ref inputs);

        _jumpPressed = false; // consume after forwarding to controller
    }
}
