using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(OutfitManager))]
public class OutfitManagerEditor : Editor
{
	public override void OnInspectorGUI()
	{
		serializedObject.Update();
		DrawDefaultInspector();
		serializedObject.ApplyModifiedProperties();

		OutfitManager outfitManager = (OutfitManager)target;

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("Outfit Testing", EditorStyles.boldLabel);

		using (new EditorGUILayout.HorizontalScope())
		{
			if (GUILayout.Button("Apply Serialized"))
				InvokeAction(outfitManager, "ContextApplyAllSelections");

			if (GUILayout.Button("Refresh Visuals"))
				InvokeAction(outfitManager, "ContextRefreshVisualLayering");
		}

		using (new EditorGUILayout.HorizontalScope())
		{
			if (GUILayout.Button("Select By Index"))
				InvokeAction(outfitManager, "ContextSelectClothingByIndex");

			if (GUILayout.Button("Clear By Index"))
				InvokeAction(outfitManager, "ContextSelectNoneByIndex");
		}

		EditorGUILayout.HelpBox(
			"These buttons call the same Outfit test methods as the component ContextMenu. Use the test index fields above to choose area/layer/clothing before selecting or clearing.",
			MessageType.Info);
	}

	private static void InvokeAction(OutfitManager outfitManager, string methodName)
	{
		if (outfitManager == null)
			return;

		Undo.RecordObject(outfitManager, methodName);
		outfitManager.SendMessage(methodName, SendMessageOptions.DontRequireReceiver);

		EditorUtility.SetDirty(outfitManager);
		if (!Application.isPlaying && outfitManager.gameObject.scene.IsValid())
			EditorSceneManager.MarkSceneDirty(outfitManager.gameObject.scene);
	}
}