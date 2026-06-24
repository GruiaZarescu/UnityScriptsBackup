using UnityEngine;

public class PlayerCamera : MonoBehaviour
{
    [SerializeField] private float _defaultDistance = 4f;
    [SerializeField] private float _minDistance = 3f;
    [SerializeField] private float _maxdistance = 10f;
    [SerializeField] private float _distanceMovementSpeed = 5f;
    [SerializeField] private float _distanceMovementSharpness = 10f;
    [SerializeField] private float _rotationSpeed = 10f;
    [SerializeField] private float _rotationSharpness = 10000f;
    [SerializeField] private float _followSharpness = 10000f;
    [SerializeField] private float _minVerticalAngle = -90f;
    [SerializeField] private float _maxVerticalAngle = 90f;
    [SerializeField] private float _defaultVerticalAngle = 20f;

    private Transform _followTransform;
    private Vector3 _currentFollowPosition;
    private Vector3 _planarDirection;
    private float _targetVerticalAngle;
    private float _currentDistance;
    private float _targetDistance;
    private void Awake()
    {
        _currentDistance = _defaultDistance;
        _targetDistance = _currentDistance;
        _targetVerticalAngle = 0f;
        _planarDirection = Vector3.forward;
    }
    public void SetFollowTransform(Transform t)
    {
        _followTransform = t;
        _currentFollowPosition = t.position;
        _planarDirection = t.forward;
    }
    private void OnValidate()
    {
        _defaultDistance = Mathf.Clamp(_defaultDistance, _minDistance, _maxdistance);
        _defaultVerticalAngle = Mathf.Clamp(_defaultVerticalAngle, _minVerticalAngle, _maxVerticalAngle);
    }

    private void HandleRotationInput(float deltaTime, Vector3 rotationInput, out Quaternion targetRotation)
    {
        // Re-project planar direction onto the current tangent plane so it stays
        // perpendicular to the character's up as we walk around the sphere.
        _planarDirection = Vector3.ProjectOnPlane(_planarDirection, _followTransform.up).normalized;
        if (_planarDirection.sqrMagnitude < 0.1f)
            _planarDirection = Vector3.ProjectOnPlane(transform.forward, _followTransform.up).normalized;

        Quaternion rotationFromInput = Quaternion.Euler(_followTransform.up * (rotationInput.x * _rotationSpeed));
        _planarDirection = rotationFromInput * _planarDirection;
        Quaternion planarRot = Quaternion.LookRotation(_planarDirection, _followTransform.up);

        _targetVerticalAngle -= (rotationInput.y * _rotationSpeed);
        //_targetVerticalAngle = Mathf.Clamp(_targetVerticalAngle, _minVerticalAngle, _maxVerticalAngle);
        Quaternion verticalRot = Quaternion.Euler(_targetVerticalAngle, 0, 0);

        targetRotation = Quaternion.Slerp(transform.rotation, planarRot * verticalRot, _rotationSharpness * deltaTime);
        transform.rotation = targetRotation;
    }

    /*private void HandleRotationInput(float deltaTime, Vector3 rotationInput, out Quaternion targetRotation)
    {
        // Horizontal rotation: rotate planar direction around the local up axis
        Quaternion horizontalRot = Quaternion.AngleAxis(rotationInput.x * _rotationSpeed, _followTransform.up);
        _planarDirection = horizontalRot * _planarDirection;
        _planarDirection = Vector3.ProjectOnPlane(_planarDirection, _followTransform.up).normalized;

        // Clamp vertical angle relative to the local up
        _targetVerticalAngle -= rotationInput.y * _rotationSpeed;
        _targetVerticalAngle = Mathf.Clamp(_targetVerticalAngle, _minVerticalAngle, _maxVerticalAngle);

        // Calculate the right axis for vertical rotation (perpendicular to up and forward)
        Vector3 cameraRight = Vector3.Cross(_followTransform.up, _planarDirection).normalized;

        // Vertical rotation: rotate around the camera's right axis
        Quaternion verticalRot = Quaternion.AngleAxis(_targetVerticalAngle, cameraRight);

        // Combine rotations: first planar (horizontal), then vertical
        Quaternion planarRot = Quaternion.LookRotation(_planarDirection, _followTransform.up);
        targetRotation = Quaternion.Slerp(transform.rotation, verticalRot * planarRot, _rotationSharpness * deltaTime);
        transform.rotation = targetRotation;
    }*/

    private void HandlePosition(float deltaTime, float zoomInput, Quaternion targetRotation)
    {
        _targetDistance += zoomInput * _distanceMovementSpeed;
        _targetDistance = Mathf.Clamp(_targetDistance, _minDistance, _maxdistance);

        _currentFollowPosition =
         Vector3.Lerp(_currentFollowPosition, _followTransform.position, 1f - Mathf.Exp(-_followSharpness * deltaTime));

        Vector3 targetPosition = _currentFollowPosition - ((targetRotation * Vector3.forward) * _currentDistance);

        _currentDistance = Mathf.Lerp(_currentDistance, _targetDistance, 1 - Mathf.Exp(-_distanceMovementSharpness * deltaTime));
        transform.position = targetPosition;
    }

    public void UpdateWithInput(float deltaTime, float zoomInput, Vector3 rotationInput)
    {
        if (_followTransform)
        {
            HandleRotationInput(deltaTime, rotationInput, out Quaternion targetRotation);
            HandlePosition(deltaTime, zoomInput, targetRotation);
         }
     }

}
