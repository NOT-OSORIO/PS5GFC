using System.Collections.Generic;

namespace ProsperoPkgTool;

public static class ProsperoBuildStages
{
	public static IReadOnlyList<ProsperoBuildStage> Build { get; } = new _003C_003Ez__ReadOnlyArray<ProsperoBuildStage>(new ProsperoBuildStage[5]
	{
		ProsperoBuildStage.InnerImage,
		ProsperoBuildStage.Naps,
		ProsperoBuildStage.OuterPfs,
		ProsperoBuildStage.Cnt,
		ProsperoBuildStage.Finalize
	});

	public static string Name(ProsperoBuildStage stage)
	{
		return stage switch
		{
			ProsperoBuildStage.Staging => "Staging",
			ProsperoBuildStage.InnerImage => "Inner image",
			ProsperoBuildStage.Naps => "NAPS",
			ProsperoBuildStage.OuterPfs => "Outer PFS",
			ProsperoBuildStage.Cnt => "CNT",
			ProsperoBuildStage.Finalize => "Finalize",
			_ => stage.ToString(),
		};
	}

	public static int IndexOf(ProsperoBuildStage stage)
	{
		for (int i = 0; i < Build.Count; i++)
		{
			if (Build[i] == stage)
			{
				return i;
			}
		}
		return -1;
	}
}
