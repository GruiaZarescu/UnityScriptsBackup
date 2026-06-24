using System.Text;
using UnityEditor;
using UnityEngine;

public static class OutfitBakePlannerEditor
{
	[MenuItem("CONTEXT/OutfitManager/Outfit/Print Bake Planning Report")]
	private static void PrintBakePlanningReport(MenuCommand command)
	{
		OutfitManager outfitManager = command.context as OutfitManager;
		if (outfitManager == null)
			return;

		OutfitArea[] areas = outfitManager.Areas;
		if (areas == null || areas.Length == 0)
		{
			Debug.LogWarning("Outfit bake planning report: no outfit areas configured.", outfitManager);
			return;
		}

		StringBuilder report = new StringBuilder(2048);
		report.AppendLine("Outfit Bake Planning Report");
		report.AppendLine("===========================");
		report.AppendLine($"Target: {outfitManager.name}");
		report.AppendLine();

		int totalAdjacentPairs = 0;
		int totalDirectLowerPairs = 0;
		int totalUpperItemsForBodyLikeLayer0 = 0;

		for (int areaIndex = 0; areaIndex < areas.Length; areaIndex++)
		{
			OutfitArea area = areas[areaIndex];
			if (area?.Layers == null)
				continue;

			int[] itemsPerLayer = new int[area.Layers.Length];
			for (int layerIndex = 0; layerIndex < area.Layers.Length; layerIndex++)
			{
				OutfitLayer layer = area.Layers[layerIndex];
				itemsPerLayer[layerIndex] = layer?.Clothes != null ? layer.Clothes.Length : 0;
			}

			int adjacentPairs = 0;
			for (int layerIndex = 0; layerIndex < itemsPerLayer.Length - 1; layerIndex++)
				adjacentPairs += itemsPerLayer[layerIndex] * itemsPerLayer[layerIndex + 1];

			int directLowerPairs = 0;
			for (int lowerLayer = 0; lowerLayer < itemsPerLayer.Length; lowerLayer++)
			{
				for (int upperLayer = lowerLayer + 1; upperLayer < itemsPerLayer.Length; upperLayer++)
					directLowerPairs += itemsPerLayer[lowerLayer] * itemsPerLayer[upperLayer];
			}

			int upperItemsForLayer0 = 0;
			for (int upperLayer = 1; upperLayer < itemsPerLayer.Length; upperLayer++)
				upperItemsForLayer0 += itemsPerLayer[upperLayer];

			totalAdjacentPairs += adjacentPairs;
			totalDirectLowerPairs += directLowerPairs;
			totalUpperItemsForBodyLikeLayer0 += upperItemsForLayer0;

			report.AppendLine($"Area {areaIndex}: {area.Name}");
			for (int layerIndex = 0; layerIndex < area.Layers.Length; layerIndex++)
			{
				OutfitLayer layer = area.Layers[layerIndex];
				string layerName = layer != null ? layer.Name : $"Layer {layerIndex}";
				report.AppendLine($"  Layer {layerIndex} ({layerName}): {itemsPerLayer[layerIndex]} item(s)");
			}

			report.AppendLine($"  Adjacent-pair surface estimate: {adjacentPairs}");
			report.AppendLine($"  All lower-layer direct pair estimate: {directLowerPairs}");
			report.AppendLine($"  Layer 0 upper-item bit demand estimate: {upperItemsForLayer0}");
			report.AppendLine();
		}

		report.AppendLine("Totals");
		report.AppendLine("------");
		report.AppendLine($"Adjacent-pair estimate: {totalAdjacentPairs}");
		report.AppendLine($"All lower-layer direct pair estimate: {totalDirectLowerPairs}");
		report.AppendLine($"Layer 0 upper-item bit demand estimate: {totalUpperItemsForBodyLikeLayer0}");
		report.AppendLine();
		report.AppendLine("Interpretation");
		report.AppendLine("- Adjacent-pair estimate is the smallest likely bake surface if only neighboring layers interact.");
		report.AppendLine("- All lower-layer direct pair estimate is the upper-bound style count when upper items may directly hide any lower layer in the same area.");
		report.AppendLine("- Layer 0 upper-item bit demand estimate is the rough per-area body-bit pressure if the lowest mesh needs direct masks for every upper item.");

		Debug.Log(report.ToString(), outfitManager);
	}
}
