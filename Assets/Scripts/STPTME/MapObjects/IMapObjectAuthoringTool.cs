using UnityEditor;
using UnityEngine;

/// <summary>
/// Contract for a single placement-mode tool plugged into MapObjectAuthoringWindow.
/// Each tool owns its own scene-view interaction; the dashboard only owns which tool
/// is active and the shared references (database, registry).
/// </summary>
public interface IMapObjectAuthoringTool
{
    string DisplayName { get; }

    /// <summary>Called every SceneView.duringSceneGui tick while this tool is the active one.</summary>
    void OnSceneGUI(SceneView view, MapObjectDatabase database, MapObjectPrototypeRegistry registry);

    /// <summary>Optional: draw tool-specific controls in the dashboard window body (e.g. prototype picker).</summary>
    void OnDashboardGUI(MapObjectDatabase database, MapObjectPrototypeRegistry registry);

    /// <summary>
    /// Called by the dashboard whenever this tool stops being the active one — either because
    /// another tool was selected, or the dashboard window itself is closing. Only the active
    /// tool's OnSceneGUI runs, so without this hook a tool left mid-session (mode still "on",
    /// a ghost preview still instantiated) has no way to clean up: it just sits frozen in the
    /// scene, silently, since nothing calls it again until you switch back.
    /// </summary>
    void OnToolDeactivated();
}