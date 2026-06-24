using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class PigSpeechController : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip[] oinkClips;
    [SerializeField, Range(0f, 1f)] private float defaultVolume = 1f;

    private void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
    }

    public bool PlayBeat(DialogueBeat beat)
    {
        if (beat == null)
            return false;

        return PlayOink(beat.text, beat.loudness, beat.clipIndex);
    }

    public bool PlayOink(string text = null, byte loudness = 128, int clipIndex = -1)
    {
        if (audioSource == null || oinkClips == null || oinkClips.Length == 0)
            return false;

        AudioClip clip = oinkClips[ResolveClipIndex(text, clipIndex, oinkClips.Length)];
        if (clip == null)
            return false;

        float volumeScale = defaultVolume * Mathf.Clamp01(loudness / 255f);
        audioSource.PlayOneShot(clip, volumeScale);
        return true;
    }

    private static int ResolveClipIndex(string text, int clipIndex, int clipCount)
    {
        if (clipCount <= 1)
            return 0;

        if (clipIndex >= 0)
            return clipIndex % clipCount;

        return GetTextClipIndex(text, clipCount);
    }

    private static int GetTextClipIndex(string text, int clipCount)
    {
        if (clipCount <= 1 || string.IsNullOrEmpty(text))
            return 0;

        int sum = 0;
        for (int i = 0; i < text.Length; i++)
            sum += char.ToLowerInvariant(text[i]);

        return Mathf.Abs(sum) % clipCount;
    }
}