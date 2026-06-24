using System;
using System.Collections;
using UnityEngine;

[Serializable]
public struct RandomIdleInteractionEntry
{
    public string interactionName;
    [Min(0f)] public float chance;
}

[Serializable]
public struct RandomIdleEmotionRule
{
    public int emotionState;
    public bool forbidAllInteractions;
    public string[] forbiddenInteractionNames;
}

public class PigInteractionController : MonoBehaviour
{
    [SerializeField] private InteractionDatabase interactionDatabase;
    [SerializeField] private PigAnimationController animationController;
    [SerializeField] private PigSpeechController speechController;
    [SerializeField] private PigCueEmitter cueEmitter;
    [SerializeField] private byte localSpeakerId;
    [SerializeField] private string defaultOinkInteraction = "Oink";
    [SerializeField] private string defaultSnortInteraction = "Snort";
    [SerializeField] private float chocolateStep = 0.05f;
    [SerializeField, Min(0.01f)] private float ticksPerSecond = 60f;
    [SerializeField] private Vector2 idleInteractionDelayRange = new Vector2(4f, 8f);
    [SerializeField] private RandomIdleInteractionEntry[] randomIdleInteractions = Array.Empty<RandomIdleInteractionEntry>();
    [SerializeField] private RandomIdleEmotionRule[] randomIdleEmotionRules = Array.Empty<RandomIdleEmotionRule>();
    [SerializeField, Min(0.05f)] private float animationLockTimeoutSeconds = 3f;

    private Coroutine activeInteractionCoroutine;
    private Coroutine animationLockCoroutine;
    private bool isAnimationLocked;
    private bool isIdle;
    private float nextIdleInteractionTime;
    private string activeAnimationTrigger;

    private void Awake()
    {
        if (interactionDatabase == null)
            interactionDatabase = InteractionDatabase.Instance;

        if (animationController == null)
            animationController = GetComponent<PigAnimationController>();
        if (speechController == null)
            speechController = GetComponent<PigSpeechController>();
        if (cueEmitter == null)
            cueEmitter = GetComponent<PigCueEmitter>();
    }

    private void OnEnable()
    {
        if (animationController != null)
        {
            animationController.InteractionAnimationFinished += HandleInteractionAnimationFinished;
            animationController.IdleStateChanged += HandleIdleStateChanged;

            isIdle = animationController.IsIdling;
            if (isIdle)
                ScheduleNextIdleInteraction(Time.time);
        }
    }

    private void OnDisable()
    {
        if (animationController != null)
        {
            animationController.InteractionAnimationFinished -= HandleInteractionAnimationFinished;
            animationController.IdleStateChanged -= HandleIdleStateChanged;
        }

        if (activeInteractionCoroutine != null)
        {
            StopCoroutine(activeInteractionCoroutine);
            activeInteractionCoroutine = null;
        }

        ReleaseAnimationLock();
        ResetIdleInteractionSchedule();
    }

    private void Update()
    {
        if (!isIdle || activeInteractionCoroutine != null)
            return;

        if (Time.time < nextIdleInteractionTime)
            return;

        TryRunRandomIdleInteraction(Time.time);
    }

    public bool Oink()
    {
        return RunInteraction(defaultOinkInteraction);
    }

    public bool Snort()
    { 
        return RunInteraction(defaultSnortInteraction);
    }

    public void MakePigNeutral() => SetEmotionState(0);
    public void MakePigSad() => SetEmotionState(1);
    public void MakePigHappy() => SetEmotionState(2);
    public void MakePigScared() => SetEmotionState(3);
    public void MakePigExcited() => SetEmotionState(4);
    public void MakePigAngry() => SetEmotionState(5);
    public void MakePigConfused() => SetEmotionState(6);
    public void AddWhiteChocolateToPig() => AddChocolateBalance(chocolateStep);
    public void AddBlackChocolateToPig() => AddChocolateBalance(-chocolateStep);

    public void SetEmotionState(int emotionState)
    {
        animationController?.SetEmotionState(emotionState);
    }

    public void SetChocolateBalance(float chocolateBalance)
    {
        animationController?.SetChocolateBalance(chocolateBalance);
    }

    public void AddChocolateBalance(float delta)
    {
        animationController?.AddChocolateBalance(delta);
    }

    public bool RunInteraction(int interactionIndex)
    {
        InteractionDatabase database = ResolveDatabase();
        return database != null
            && database.TryGetInteraction(interactionIndex, out InteractionDefinition interaction)
            && RunInteraction(interaction);
    }

    public bool RunInteraction(string interactionName)
    {
        InteractionDatabase database = ResolveDatabase();
        return database != null
            && database.TryGetInteraction(interactionName, out InteractionDefinition interaction)
            && RunInteraction(interaction);
    }

    private bool RunInteraction(InteractionDefinition interaction)
    {
        if (interaction == null)
            return false;

        bool allowOverride = IsOverrideInteraction(interaction);
        if (activeInteractionCoroutine != null)
        {
            if (!allowOverride)
                return false;

            StopCoroutine(activeInteractionCoroutine);
            activeInteractionCoroutine = null;
        }

        if (!HasPlayableContent(interaction))
            return false;

        activeInteractionCoroutine = StartCoroutine(RunInteractionRoutine(interaction));
        return true;
    }

    private IEnumerator RunInteractionRoutine(InteractionDefinition interaction)
    {
        try
        {
            if (interaction.sequence != null && interaction.sequence.Length > 0)
            {
                for (int i = 0; i < interaction.sequence.Length; i++)
                {
                    DialogueSequenceEntry entry = interaction.sequence[i];
                    if (entry == null || entry.speakerSlot != localSpeakerId)
                        continue;

                    if (TryResolveLine(interaction, entry, out DialogueLine line))
                    {
                        yield return PlayLineRoutine(line);

                        if (entry.postLineDelayTicks > 0)
                            yield return WaitForTicks(entry.postLineDelayTicks);
                    }
                }
            }
            else if (interaction.lines != null)
            {
                for (int i = 0; i < interaction.lines.Length; i++)
                    yield return PlayLineRoutine(interaction.lines[i]);
            }
        }
        finally
        {
            activeInteractionCoroutine = null;

            if (isIdle)
                ScheduleNextIdleInteraction(Time.time);
        }
    }

    private bool HasPlayableContent(InteractionDefinition interaction)
    {
        if (interaction.sequence != null && interaction.sequence.Length > 0)
        {
            for (int i = 0; i < interaction.sequence.Length; i++)
            {
                DialogueSequenceEntry entry = interaction.sequence[i];
                if (entry == null || entry.speakerSlot != localSpeakerId)
                    continue;

                if (TryResolveLine(interaction, entry, out DialogueLine line) && line?.beats != null && line.beats.Length > 0)
                {
                    for (int beatIndex = 0; beatIndex < line.beats.Length; beatIndex++)
                    {
                        if (IsPlayableBeat(line.beats[beatIndex]))
                            return true;
                    }
                }
            }

            return false;
        }

        if (interaction.lines != null)
        {
            for (int i = 0; i < interaction.lines.Length; i++)
            {
                DialogueLine line = interaction.lines[i];
                if (line?.beats == null)
                    continue;

                for (int beatIndex = 0; beatIndex < line.beats.Length; beatIndex++)
                {
                    if (IsPlayableBeat(line.beats[beatIndex]))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool TryResolveLine(InteractionDefinition interaction, DialogueSequenceEntry entry, out DialogueLine line)
    {
        if (!string.IsNullOrWhiteSpace(entry.lineName) && interaction.TryGetLine(entry.lineName, out line))
            return true;

        return interaction.TryGetLine(entry.lineIndex, out line);
    }

    private IEnumerator PlayLineRoutine(DialogueLine line)
    {
        if (line?.beats == null)
            yield break;

        for (int i = 0; i < line.beats.Length; i++)
        {
            DialogueBeat beat = line.beats[i];
            yield return PlayBeatRoutine(beat);

            if (beat != null && beat.postBeatDelayTicks > 0)
                yield return WaitForTicks(beat.postBeatDelayTicks);
        }
    }

    private IEnumerator PlayBeatRoutine(DialogueBeat beat)
    {
        if (beat == null)
            yield break;

        string animationTrigger = beat.GetAnimationCueName();
        bool hasAnimationCue = beat.HasAnimationCue();

        ApplyBeatStateChanges(beat);

        if (hasAnimationCue)
        {
            yield return new WaitUntil(() => !isAnimationLocked);

            if (animationController == null)
            {
                Debug.LogWarning($"[PigInteractionController] Beat requested animation cue '{animationTrigger}' on '{name}', but no PigAnimationController is assigned. Visual cue presentation will not run for this beat.", this);
                yield break;
            }

            if (!animationController.RequestAnimationTrigger(animationTrigger))
            {
                Debug.LogWarning($"[PigInteractionController] Animation trigger '{animationTrigger}' was rejected on '{name}'. Visual cue presentation will not run for this beat.", this);
                yield break;
            }

            BeginAnimationLock(animationTrigger);
            PlayBeatPresentation(beat);

            yield return new WaitUntil(() => !isAnimationLocked);
            yield break;
        }

        PlayBeatPresentation(beat);
    }

    private static bool IsPlayableBeat(DialogueBeat beat)
    {
        if (beat == null)
            return false;

        if (beat.HasAnimationCue())
            return true;

        return beat.HasStateChange() || beat.HasAudioCue() || beat.GetVisualCue() != PigVisualCue.None;
    }

    private void PlayBeatPresentation(DialogueBeat beat)
    {
        if (beat == null)
            return;

        if (beat.HasAudioCue())
            speechController?.PlayBeat(beat);

        PigVisualCue visualCue = beat.GetVisualCue();
        if (visualCue != PigVisualCue.None)
        {
            if (cueEmitter == null)
            {
                Debug.LogWarning($"[PigInteractionController] Beat requested visual cue '{visualCue}' on '{name}', but no PigCueEmitter is assigned.", this);
                return;
            }

            if (!cueEmitter.Emit(visualCue))
                Debug.LogWarning($"[PigInteractionController] PigCueEmitter failed to emit visual cue '{visualCue}' on '{name}'.", this);
        }
    }

    private void ApplyBeatStateChanges(DialogueBeat beat)
    {
        if (beat == null)
            return;

        if (beat.changeEmotionState >= 0)
            SetEmotionState(beat.changeEmotionState);

        if (Mathf.Abs(beat.changeChocolateBalance) > 0.0001f)
            AddChocolateBalance(beat.changeChocolateBalance);
    }

    private void HandleIdleStateChanged(bool idle)
    {
        isIdle = idle;

        if (isIdle)
            ScheduleNextIdleInteraction(Time.time);
        else
            ResetIdleInteractionSchedule();
    }

    private void ScheduleNextIdleInteraction(float currentTime)
    {
        if (randomIdleInteractions == null || randomIdleInteractions.Length == 0)
        {
            nextIdleInteractionTime = float.PositiveInfinity;
            return;
        }

        float minDelay = Mathf.Min(idleInteractionDelayRange.x, idleInteractionDelayRange.y);
        float maxDelay = Mathf.Max(idleInteractionDelayRange.x, idleInteractionDelayRange.y);
        nextIdleInteractionTime = currentTime + UnityEngine.Random.Range(minDelay, maxDelay);
    }

    private void ResetIdleInteractionSchedule()
    {
        nextIdleInteractionTime = 0f;
    }

    private void TryRunRandomIdleInteraction(float currentTime)
    {
        if (!TryPickRandomIdleInteraction(out RandomIdleInteractionEntry selectedEntry)
            || string.IsNullOrWhiteSpace(selectedEntry.interactionName))
        {
            ScheduleNextIdleInteraction(currentTime);
            return;
        }

        string interactionName = selectedEntry.interactionName.Trim();
        nextIdleInteractionTime = float.PositiveInfinity;
        if (!RunInteraction(interactionName))
            ScheduleNextIdleInteraction(currentTime);
    }

    private bool TryPickRandomIdleInteraction(out RandomIdleInteractionEntry selectedEntry)
    {
        selectedEntry = default;

        if (randomIdleInteractions == null || randomIdleInteractions.Length == 0)
            return false;

        int emotionState = animationController != null ? animationController.CurrentEmotionState : 0;
        float totalChance = 0f;
        for (int i = 0; i < randomIdleInteractions.Length; i++)
        {
            RandomIdleInteractionEntry entry = randomIdleInteractions[i];
            if (!IsRandomIdleInteractionAllowed(entry.interactionName, emotionState))
                continue;

            totalChance += Mathf.Max(0f, entry.chance);
        }

        if (totalChance <= 0f)
            return false;

        float roll = UnityEngine.Random.value * totalChance;
        float cumulativeChance = 0f;
        RandomIdleInteractionEntry lastAllowedEntry = default;
        bool hasAllowedEntry = false;
        for (int i = 0; i < randomIdleInteractions.Length; i++)
        {
            RandomIdleInteractionEntry entry = randomIdleInteractions[i];
            if (!IsRandomIdleInteractionAllowed(entry.interactionName, emotionState))
                continue;

            float entryChance = Mathf.Max(0f, entry.chance);
            if (entryChance <= 0f)
                continue;

            lastAllowedEntry = entry;
            hasAllowedEntry = true;
            cumulativeChance += entryChance;
            if (roll <= cumulativeChance)
            {
                selectedEntry = entry;
                return true;
            }
        }

        if (!hasAllowedEntry)
            return false;

        selectedEntry = lastAllowedEntry;
        return true;
    }

    private bool IsRandomIdleInteractionAllowed(string interactionName, int emotionState)
    {
        if (string.IsNullOrWhiteSpace(interactionName))
            return false;

        if (!TryGetRandomIdleEmotionRule(emotionState, out RandomIdleEmotionRule rule))
            return true;

        if (rule.forbidAllInteractions)
            return false;

        if (rule.forbiddenInteractionNames == null || rule.forbiddenInteractionNames.Length == 0)
            return true;

        string trimmedInteractionName = interactionName.Trim();
        for (int i = 0; i < rule.forbiddenInteractionNames.Length; i++)
        {
            string forbiddenInteractionName = rule.forbiddenInteractionNames[i];
            if (string.IsNullOrWhiteSpace(forbiddenInteractionName))
                continue;

            if (string.Equals(trimmedInteractionName, forbiddenInteractionName.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private bool TryGetRandomIdleEmotionRule(int emotionState, out RandomIdleEmotionRule rule)
    {
        if (randomIdleEmotionRules != null)
        {
            for (int i = 0; i < randomIdleEmotionRules.Length; i++)
            {
                if (randomIdleEmotionRules[i].emotionState == emotionState)
                {
                    rule = randomIdleEmotionRules[i];
                    return true;
                }
            }
        }

        rule = default;
        return false;
    }

    private bool IsOverrideInteraction(InteractionDefinition interaction)
    {
        if (interaction == null || string.IsNullOrWhiteSpace(interaction.name))
            return false;

        return string.Equals(interaction.name, defaultOinkInteraction, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(interaction.name, defaultSnortInteraction, System.StringComparison.OrdinalIgnoreCase);
    }

    private WaitForSeconds WaitForTicks(int ticks)
    {
        float resolvedTicksPerSecond = ticksPerSecond > 0f ? ticksPerSecond : 60f;
        return new WaitForSeconds(ticks / resolvedTicksPerSecond);
    }

    private IEnumerator ReleaseAnimationAfterDelay()
    {
        yield return new WaitForSeconds(animationLockTimeoutSeconds);
        Debug.LogWarning($"[PigInteractionController] Animation interaction '{activeAnimationTrigger}' timed out after {animationLockTimeoutSeconds:0.##} seconds. The animation clip may be missing AnimationEvent_InteractionAnimationFinished.", this);
        ReleaseAnimationLock();
    }

    private void HandleInteractionAnimationFinished()
    {
        ReleaseAnimationLock();
    }

    private void BeginAnimationLock(string animationTrigger)
    {
        isAnimationLocked = true;
        activeAnimationTrigger = animationTrigger;

        if (animationLockCoroutine != null)
            StopCoroutine(animationLockCoroutine);

        animationLockCoroutine = StartCoroutine(ReleaseAnimationAfterDelay());
    }

    private void ReleaseAnimationLock()
    {
        isAnimationLocked = false;
        activeAnimationTrigger = null;

        if (animationLockCoroutine != null)
        {
            StopCoroutine(animationLockCoroutine);
            animationLockCoroutine = null;
        }
    }

    private InteractionDatabase ResolveDatabase()
    {
        if (interactionDatabase == null)
            interactionDatabase = InteractionDatabase.Instance;

        return interactionDatabase;
    }
}