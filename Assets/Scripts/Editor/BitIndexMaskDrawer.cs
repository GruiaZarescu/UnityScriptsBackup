using System.Text;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(BitIndexMask))]
public class BitIndexMaskDrawer : PropertyDrawer
{
	private const float ButtonWidth = 56f;
	private const float RemoveButtonWidth = 24f;
	private const float Spacing = 2f;
	private static readonly GUIContent AddBitLabel = new GUIContent("Add Bit");
	private static readonly GUIContent[] BitOptions = CreateBitOptions();
	private static readonly int[] BitValues = CreateBitValues();

	public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
	{
		float lineHeight = EditorGUIUtility.singleLineHeight;
		if (!property.isExpanded)
			return lineHeight;

		SerializedProperty bitIndices = property.FindPropertyRelative("_bitIndices");
		int lineCount = 3 + bitIndices.arraySize;
		return lineCount * lineHeight + (lineCount - 1) * EditorGUIUtility.standardVerticalSpacing;
	}

	public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
	{
		EditorGUI.BeginProperty(position, label, property);

		SerializedProperty bitIndices = property.FindPropertyRelative("_bitIndices");
		float lineHeight = EditorGUIUtility.singleLineHeight;
		float verticalSpacing = EditorGUIUtility.standardVerticalSpacing;
		Rect lineRect = new Rect(position.x, position.y, position.width, lineHeight);

		property.isExpanded = EditorGUI.Foldout(lineRect, property.isExpanded, BuildHeaderLabel(bitIndices, label), true);
		if (!property.isExpanded)
		{
			EditorGUI.EndProperty();
			return;
		}

		lineRect.y += lineHeight + verticalSpacing;
		EditorGUI.LabelField(lineRect, "Enabled bit indices", EditorStyles.miniLabel);

		for (int i = 0; i < bitIndices.arraySize; i++)
		{
			lineRect.y += lineHeight + verticalSpacing;
			DrawBitIndexRow(lineRect, bitIndices, i);
		}

		lineRect.y += lineHeight + verticalSpacing;
		Rect addButtonRect = new Rect(lineRect.x, lineRect.y, ButtonWidth, lineHeight);
		if (GUI.Button(addButtonRect, AddBitLabel))
		{
			int newIndex = bitIndices.arraySize;
			bitIndices.InsertArrayElementAtIndex(newIndex);
			bitIndices.GetArrayElementAtIndex(newIndex).intValue = 0;
		}

		Rect previewRect = new Rect(addButtonRect.xMax + 6f, lineRect.y, lineRect.width - ButtonWidth - 6f, lineHeight);
		EditorGUI.LabelField(previewRect, $"Mask Value: {BitIndexMask.BuildMask(ReadBitIndices(bitIndices))}");

		EditorGUI.EndProperty();
	}

	private static void DrawBitIndexRow(Rect rect, SerializedProperty bitIndices, int index)
	{
		SerializedProperty bitIndexProperty = bitIndices.GetArrayElementAtIndex(index);
		Rect popupRect = new Rect(rect.x, rect.y, rect.width - RemoveButtonWidth - Spacing, rect.height);
		Rect removeRect = new Rect(popupRect.xMax + Spacing, rect.y, RemoveButtonWidth, rect.height);
		Rect valueRect = EditorGUI.PrefixLabel(popupRect, new GUIContent($"Bit {index}"));

		bitIndexProperty.intValue = EditorGUI.IntPopup(valueRect, Mathf.Clamp(bitIndexProperty.intValue, 0, 31), BitOptions, BitValues);
		if (GUI.Button(removeRect, "-"))
			bitIndices.DeleteArrayElementAtIndex(index);
	}

	private static GUIContent BuildHeaderLabel(SerializedProperty bitIndices, GUIContent label)
	{
		uint maskValue = BitIndexMask.BuildMask(ReadBitIndices(bitIndices));
		string summary = BuildSummary(bitIndices);
		string suffix = string.IsNullOrEmpty(summary) ? $"= {maskValue}" : $"= {maskValue} [{summary}]";
		return new GUIContent($"{label.text} {suffix}", label.tooltip);
	}

	private static string BuildSummary(SerializedProperty bitIndices)
	{
		if (bitIndices.arraySize == 0)
			return string.Empty;

		StringBuilder builder = new StringBuilder();
		for (int i = 0; i < bitIndices.arraySize; i++)
		{
			if (i > 0)
				builder.Append(',');

			builder.Append(Mathf.Clamp(bitIndices.GetArrayElementAtIndex(i).intValue, 0, 31));
		}

		return builder.ToString();
	}

	private static int[] ReadBitIndices(SerializedProperty bitIndices)
	{
		int[] values = new int[bitIndices.arraySize];
		for (int i = 0; i < bitIndices.arraySize; i++)
			values[i] = bitIndices.GetArrayElementAtIndex(i).intValue;

		return values;
	}

	private static GUIContent[] CreateBitOptions()
	{
		GUIContent[] options = new GUIContent[32];
		for (int i = 0; i < options.Length; i++)
			options[i] = new GUIContent($"Bit {i:00}");

		return options;
	}

	private static int[] CreateBitValues()
	{
		int[] values = new int[32];
		for (int i = 0; i < values.Length; i++)
			values[i] = i;

		return values;
	}
}