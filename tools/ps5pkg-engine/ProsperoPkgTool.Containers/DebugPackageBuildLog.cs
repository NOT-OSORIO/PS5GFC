using System;

namespace ProsperoPkgTool.Containers;

public sealed class DebugPackageBuildLog
{
	public IProgress<ProsperoBuildProgress>? Progress { get; init; }

	public Action<string> Log { get; init; } = (string _) =>
	{
	};

	public Action Finish { get; init; } = () =>
	{
	};
}
