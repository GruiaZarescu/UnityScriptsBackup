using System;
using System.Collections.Generic;
using UnityEngine;

public enum PigVisualCue : byte
{
    None = 0,
    SnoutOinkSpeech = 1,
    SnoutSnortSpeech = 2,
    HeadEmoji = 3,
}

[Serializable]
public class DialogueBeat
{
    public string text;
    public byte durationTicks = 5;
    public int postBeatDelayTicks;
    public byte loudness = 128;
    public int clipIndex = -1;
    public int changeEmotionState = -1;
    public float changeChocolateBalance;
    public string visualCue = nameof(PigVisualCue.None);
    public string animationCue;

    public string GetAnimationCueName()
    {
        return string.IsNullOrWhiteSpace(animationCue) ? string.Empty : animationCue.Trim();
    }

    public bool HasAnimationCue()
    {
        return !string.IsNullOrWhiteSpace(GetAnimationCueName());
    }

    public bool HasAudioCue()
    {
        return clipIndex >= 0 || !string.IsNullOrWhiteSpace(text);
    }

    public PigVisualCue GetVisualCue()
    {
        if (string.IsNullOrWhiteSpace(visualCue))
            return PigVisualCue.None;

        return Enum.TryParse(visualCue, true, out PigVisualCue cue) ? cue : PigVisualCue.None;
    }

    public bool HasStateChange()
    {
        return changeEmotionState >= 0 || Math.Abs(changeChocolateBalance) > 0.0001f;
    }
}

[Serializable]
public class DialogueLine
{
    public string name;
    public DialogueBeat[] beats;
}

[Serializable]
public class InteractionParticipant
{
    public byte slot;
    public string name;
}

[Serializable]
public class DialogueSequenceEntry
{
    public byte speakerSlot;
    public int lineIndex = -1;
    public string lineName;
    public int postLineDelayTicks;
}

[Serializable]
public class InteractionDefinition
{
    public string name;
    public InteractionParticipant[] participants;
    public DialogueLine[] lines;
    public DialogueSequenceEntry[] sequence;

    public bool TryGetLine(int index, out DialogueLine line)
    {
        if (lines != null && index >= 0 && index < lines.Length)
        {
            line = lines[index];
            return line != null;
        }

        line = null;
        return false;
    }

    public bool TryGetLine(string lineName, out DialogueLine line)
    {
        if (!string.IsNullOrWhiteSpace(lineName) && lines != null)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                DialogueLine candidate = lines[i];
                if (candidate != null && string.Equals(candidate.name, lineName, StringComparison.OrdinalIgnoreCase))
                {
                    line = candidate;
                    return true;
                }
            }
        }

        line = null;
        return false;
    }
}

[Serializable]
public class InteractionCatalog
{
    public InteractionDefinition[] interactions;
}

public class InteractionDatabase : MonoBehaviour
{
    public static InteractionDatabase Instance { get; private set; }

    [SerializeField] private TextAsset interactionsJson;
    [SerializeField] private string resourcesFallbackPath = "Interactions/InteractionDatabase";

    private InteractionDefinition[] interactions = Array.Empty<InteractionDefinition>();
    private readonly Dictionary<string, int> interactionIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int Count => interactions.Length;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[InteractionDatabase] Multiple instances found. Keeping the first instance.", this);
            return;
        }

        Instance = this;
        Load();
    }

    public void Load()
    {
        TextAsset source = interactionsJson;
        if (source == null && !string.IsNullOrWhiteSpace(resourcesFallbackPath))
            source = Resources.Load<TextAsset>(resourcesFallbackPath);

        if (source == null)
        {
            interactions = Array.Empty<InteractionDefinition>();
            interactionIndexByName.Clear();
            Debug.LogWarning("[InteractionDatabase] No interaction JSON assigned or found in Resources.", this);
            return;
        }

        InteractionCatalog catalog;
        try
        {
            catalog = JsonUtility.FromJson<InteractionCatalog>(source.text);
        }
        catch (Exception ex)
        {
            interactions = Array.Empty<InteractionDefinition>();
            interactionIndexByName.Clear();
            Debug.LogError($"[InteractionDatabase] Failed to parse interaction JSON: {ex.Message}", this);
            return;
        }

        interactions = catalog?.interactions ?? Array.Empty<InteractionDefinition>();
        BuildLookup();
    }

    private void BuildLookup()
    {
        interactionIndexByName.Clear();

        for (int i = 0; i < interactions.Length; i++)
        {
            InteractionDefinition interaction = interactions[i];
            if (interaction == null || string.IsNullOrWhiteSpace(interaction.name))
                continue;

            if (interactionIndexByName.ContainsKey(interaction.name))
            {
                Debug.LogWarning($"[InteractionDatabase] Duplicate interaction name '{interaction.name}' at index {i}. First entry remains active.", this);
                continue;
            }

            interactionIndexByName.Add(interaction.name, i);
        }
    }

    public bool TryGetInteraction(int index, out InteractionDefinition interaction)
    {
        if (index >= 0 && index < interactions.Length)
        {
            interaction = interactions[index];
            return interaction != null;
        }

        interaction = null;
        return false;
    }

    public bool TryGetInteraction(string interactionName, out InteractionDefinition interaction)
    {
        if (!string.IsNullOrWhiteSpace(interactionName) && interactionIndexByName.TryGetValue(interactionName, out int index))
            return TryGetInteraction(index, out interaction);

        interaction = null;
        return false;
    }
}