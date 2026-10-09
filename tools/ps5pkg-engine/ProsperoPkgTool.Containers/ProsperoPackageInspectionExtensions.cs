using System;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPackageInspectionExtensions
{
	public static ProsperoPackageCategoryInfo Category(this ProsperoPackageInspection inspection)
	{
		ArgumentNullException.ThrowIfNull(inspection, "inspection");
		return ProsperoPackageCategories.Classify(inspection.Cnt.ContentType);
	}
}
