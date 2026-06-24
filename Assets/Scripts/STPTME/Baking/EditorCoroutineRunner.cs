#if UNITY_EDITOR
using UnityEditor;
using System.Collections;

public static class EditorCoroutineRunner
{
    public static void StartEditorCoroutine(IEnumerator routine)
    {
        EditorApplication.update += () => Tick(routine);
    }

    private static void Tick(IEnumerator routine)
    {
        try
        {
            if (!routine.MoveNext())
            {
                // Stop when done
                EditorApplication.update -= () => Tick(routine);
            }
            else
            {
                // If the yielded object is another IEnumerator, handle it recursively
                if (routine.Current is IEnumerator nested)
                {
                    StartEditorCoroutine(nested);
                }
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"Editor coroutine error: {ex}");
            EditorApplication.update -= () => Tick(routine);
        }
    }
}
#endif
