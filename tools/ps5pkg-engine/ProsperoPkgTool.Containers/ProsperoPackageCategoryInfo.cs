namespace ProsperoPkgTool.Containers;

public sealed record ProsperoPackageCategoryInfo(ProsperoPackageCategory Category, uint RawContentType, PackageCategoryEvidence Evidence, string Description);
