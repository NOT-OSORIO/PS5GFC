namespace ProsperoPkgTool.Containers;

public sealed record DebugPackageBuildResult(string OutputPath, long PackageLength, string ContentId, int InnerFileCount, bool FileBacked);
