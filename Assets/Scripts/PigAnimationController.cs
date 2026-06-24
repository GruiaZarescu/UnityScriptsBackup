using System;
using UnityEngine;

[Serializable]
public struct EmotionChocolateClamp
{
    public int emotionState;
    public float minChocolateBalance;
    public float maxChocolateBalance;
}

public class PigAnimationController : MonoBehaviour
{
    private static readonly int IsWalkingHash = Animator.StringToHash("isWalking");
    private static readonly int IsSprintingHash = Animator.StringToHash("isSprinting");
    private static readonly int IsGooglyRunningHash = Animator.StringToHash("IsGooglyRunning");
    private static readonly int IsJumpingHash = Animator.StringToHash("isJumping");
    private static readonly int EmotionStateHash = Animator.StringToHash("EmotionState");
    private static readonly int ChocolateBalanceHash = Animator.StringToHash("ChocolateBalance");

    [SerializeField] private Animator _animator;
    [SerializeField, Range(0f, 1f)] private float googlyRunningChance = 0.1f;
    [SerializeField] private float minChocolateBalance = -1f;
    [SerializeField] private float maxChocolateBalance = 1f;
    [SerializeField] private EmotionChocolateClamp[] chocolateBalanceClampsByEmotion = Array.Empty<EmotionChocolateClamp>();

    private bool _isWalking;
    private bool _wantsSprinting;
    private bool _isSprinting;
    private bool _isGooglyRunning;
    private bool _isJumping;
    private bool _isIdling;
    private int _emotionState;
    private float _chocolateBalance;
    private float _targetChocolateBalance;

    public bool IsIdling => _isIdling;
    public int CurrentEmotionState => _emotionState;

    public event Action<bool> IdleStateChanged;
    public event Action InteractionAnimationFinished;

    private void Awake()
    {
        if (_animator == null)
            _animator = GetComponentInChildren<Animator>();
    }

    public void SetLocomotionState(bool isWalking, bool isSprinting)
    {
        if (_animator == null)
            return;

        if (_isWalking != isWalking)
        {
            _isWalking = isWalking;
            _animator.SetBool(IsWalkingHash, isWalking);
        }

        if (_wantsSprinting != isSprinting)
        {
            _wantsSprinting = isSprinting;
            bool isGooglyRunning = isSprinting && UnityEngine.Random.value < googlyRunningChance;
            SetGooglyRunning(isGooglyRunning);
            SetSprinting(isSprinting && !isGooglyRunning);
        }

        UpdateIdleState();
    }

    public void SetJumping(bool isJumping)
    {
        if (_animator == null || _isJumping == isJumping)
            return;

        _isJumping = isJumping;
        _animator.SetBool(IsJumpingHash, isJumping);
        UpdateIdleState();
    }

    public bool RequestAnimationTrigger(string triggerName)
    {
        if (_animator == null || string.IsNullOrWhiteSpace(triggerName))
            return false;

        triggerName = triggerName.Trim();
        if (!HasTriggerParameter(triggerName))
        {
            Debug.LogError($"[PigAnimationController] Animator trigger '{triggerName}' was requested but does not exist.", this);
            return false;
        }

        _animator.SetTrigger(triggerName);
        return true;
    }

    public void AnimationEvent_OinkFinished()
    {
        AnimationEvent_InteractionAnimationFinished();
    }

    public void AnimationEvent_SnortFinished()
    {
        AnimationEvent_InteractionAnimationFinished();
    }

    public void AnimationEvent_RandomIdleFinished()
    {
        AnimationEvent_InteractionAnimationFinished();
    }

    public void AnimationEvent_InteractionAnimationFinished()
    {
        InteractionAnimationFinished?.Invoke();
    }

    public void SetEmotionState(int emotionState)
    {
        if (_animator == null || _emotionState == emotionState)
            return;

        _emotionState = emotionState;
        _animator.SetInteger(EmotionStateHash, emotionState);
        ApplyChocolateBalance();
    }

    public void SetChocolateBalance(float chocolateBalance)
    {
        if (_animator == null)
            return;

        _targetChocolateBalance = Mathf.Clamp(chocolateBalance, minChocolateBalance, maxChocolateBalance);
        ApplyChocolateBalance();
    }

    public void AddChocolateBalance(float delta)
    {
        SetChocolateBalance(_targetChocolateBalance + delta);
    }

    private void SetGooglyRunning(bool isGooglyRunning)
    {
        if (_isGooglyRunning == isGooglyRunning)
            return;

        _isGooglyRunning = isGooglyRunning;
        _animator.SetBool(IsGooglyRunningHash, isGooglyRunning);
    }

    private void SetSprinting(bool isSprinting)
    {
        if (_isSprinting == isSprinting)
            return;

        _isSprinting = isSprinting;
        _animator.SetBool(IsSprintingHash, isSprinting);
    }

    private void UpdateIdleState()
    {
        bool isIdling = !_isWalking && !_isSprinting && !_isGooglyRunning && !_isJumping;
        if (_isIdling == isIdling)
            return;

        _isIdling = isIdling;
        IdleStateChanged?.Invoke(_isIdling);
    }

    private bool HasTriggerParameter(string triggerName)
    {
        for (int i = 0; i < _animator.parameterCount; i++)
        {
            AnimatorControllerParameter parameter = _animator.GetParameter(i);
            if (parameter.type == AnimatorControllerParameterType.Trigger && parameter.name == triggerName)
                return true;
        }

        return false;
    }

    private void ApplyChocolateBalance()
    {
        float clampedBalance = GetClampedChocolateBalance(_targetChocolateBalance, _emotionState);
        if (Mathf.Approximately(_chocolateBalance, clampedBalance))
            return;

        _chocolateBalance = clampedBalance;
        _animator.SetFloat(ChocolateBalanceHash, clampedBalance);
    }

    private float GetClampedChocolateBalance(float targetChocolateBalance, int emotionState)
    {
        float clampedBalance = Mathf.Clamp(targetChocolateBalance, minChocolateBalance, maxChocolateBalance);

        for (int i = 0; i < chocolateBalanceClampsByEmotion.Length; i++)
        {
            EmotionChocolateClamp clamp = chocolateBalanceClampsByEmotion[i];
            if (clamp.emotionState != emotionState)
                continue;

            float perEmotionMin = Mathf.Min(clamp.minChocolateBalance, clamp.maxChocolateBalance);
            float perEmotionMax = Mathf.Max(clamp.minChocolateBalance, clamp.maxChocolateBalance);
            return Mathf.Clamp(clampedBalance, perEmotionMin, perEmotionMax);
        }

        return clampedBalance;
    }
}