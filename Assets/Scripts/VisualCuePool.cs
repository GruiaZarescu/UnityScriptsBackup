using System.Collections.Generic;
using UnityEngine;

public class VisualCuePool : MonoBehaviour
{
    public static VisualCuePool Instance { get; private set; }

    [Header("Prefabs")]
    [SerializeField] private VisualCueInstance snoutOinkCuePrefab;
    [SerializeField] private VisualCueInstance snoutSnortCuePrefab;

    [Header("Pool")]
    [SerializeField] private Transform poolRoot;
    [SerializeField, Min(0)] private int prewarmOinkCount = 8;
    [SerializeField, Min(1)] private int maxInstances = 128;

    private readonly Dictionary<VisualCueInstance, Stack<VisualCueInstance>> inactiveByPrefab = new Dictionary<VisualCueInstance, Stack<VisualCueInstance>>();
    private int totalCreated;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[VisualCuePool] Multiple instances found. Keeping the first instance.", this);
            return;
        }

        Instance = this;

        if (poolRoot == null)
            poolRoot = transform;

        Prewarm(snoutOinkCuePrefab, prewarmOinkCount);
    }

    public bool Emit(PigVisualCue cue, VisualCueRequest request)
    {
        VisualCueInstance prefab = GetPrefab(cue);
        if (prefab == null)
        {
            Debug.LogWarning($"[VisualCuePool] No prefab assigned for visual cue '{cue}' on pool '{name}'.", this);
            return false;
        }

        VisualCueInstance instance = GetInstance(prefab);
        if (instance == null)
        {
            Debug.LogWarning($"[VisualCuePool] Could not get an instance for visual cue '{cue}' on pool '{name}'. Total created: {totalCreated}, max instances: {maxInstances}.", this);
            return false;
        }

        instance.Play(this, prefab, request);
        return true;
    }

    public void Release(VisualCueInstance instance, VisualCueInstance prefabKey)
    {
        if (instance == null || prefabKey == null)
            return;

        instance.transform.SetParent(poolRoot, false);
        instance.gameObject.SetActive(false);

        if (!inactiveByPrefab.TryGetValue(prefabKey, out Stack<VisualCueInstance> stack))
        {
            stack = new Stack<VisualCueInstance>();
            inactiveByPrefab.Add(prefabKey, stack);
        }

        stack.Push(instance);
    }

    private VisualCueInstance GetPrefab(PigVisualCue cue)
    {
        switch (cue)
        {
            case PigVisualCue.SnoutOinkSpeech:
                return snoutOinkCuePrefab;
            case PigVisualCue.SnoutSnortSpeech:
                return snoutSnortCuePrefab;
            default:
                return null;
        }
    }

    private VisualCueInstance GetInstance(VisualCueInstance prefab)
    {
        if (!inactiveByPrefab.TryGetValue(prefab, out Stack<VisualCueInstance> stack))
        {
            stack = new Stack<VisualCueInstance>();
            inactiveByPrefab.Add(prefab, stack);
        }

        if (stack.Count > 0)
            return stack.Pop();

        if (totalCreated >= maxInstances)
        {
            Debug.LogWarning($"[VisualCuePool] Pool '{name}' reached maxInstances ({maxInstances}) for prefab '{prefab.name}'.", this);
            return null;
        }

        VisualCueInstance instance = Instantiate(prefab, poolRoot);
        instance.gameObject.SetActive(false);
        totalCreated++;
        return instance;
    }

    private void Prewarm(VisualCueInstance prefab, int count)
    {
        if (prefab == null || count <= 0)
            return;

        for (int i = 0; i < count && totalCreated < maxInstances; i++)
        {
            VisualCueInstance instance = Instantiate(prefab, poolRoot);
            totalCreated++;
            Release(instance, prefab);
        }
    }
}