namespace ProsperoPkgTool.Containers;

public static class ProsperoPackageCategories
{
	public static ProsperoPackageCategoryInfo Classify(uint contentType)
	{
		return contentType switch
		{
			32u => new ProsperoPackageCategoryInfo(ProsperoPackageCategory.BaseGame, contentType, PackageCategoryEvidence.PubCmdConfirmed, "PS5 game/application data"),
			33u => new ProsperoPackageCategoryInfo(ProsperoPackageCategory.Dlc, contentType, PackageCategoryEvidence.Hypothesis, "PS5 additional content (DLC)"),
			_ => new ProsperoPackageCategoryInfo(ProsperoPackageCategory.Unknown, contentType, PackageCategoryEvidence.Unknown, "No clean-room category mapping is verified"),
		};
	}
}
