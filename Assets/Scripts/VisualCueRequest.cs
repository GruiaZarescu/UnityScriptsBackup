using UnityEngine;

public struct VisualCueRequest
{
    public Transform parentTransform;
    public Transform horizontalViewReference;
    public Vector3 startPosition;
    public Vector3 endPosition;
    public Camera referenceCamera;
    public Quaternion rotation;
    public Quaternion localRotationOffset;
    public Vector3 horizontalViewAlphaOrigin;
    public Vector3 horizontalViewAlphaForward;
    public Vector3 horizontalViewAlphaUp;
    public float horizontalViewAlphaSwitchAngle;
    public float lifetime;
    public float startScale;
    public float endScale;
    public bool useHorizontalViewAlpha;
    public bool invertHorizontalViewAlpha;
}