using TMPro;
using UnityEngine;

public class FPSCounter : MonoBehaviour
{
    public TextMeshProUGUI fpsText;
    private float deltaTime = 0.0f;

    void Update()
    {
        // Altitude is owned by ChunkManager (uses settings.sphereRadius). The previously
        // hardcoded 8162f offset here was wrong (settings.sphereRadius = 7162).
        float altitude = ChunkManager.Instance != null ? ChunkManager.Instance.PlayerAltitude : 0f;
        deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
        float fps = 1.0f / deltaTime;

        fpsText.text = $"FPS: {Mathf.Ceil(fps)}\nAltitude: {altitude:F1} m";
    }
}
