using System.Collections.Generic;

namespace ProsperoPkgTool.Content;

public sealed class ProsperoLaunchReadinessReport
{
	public required string AppRoot { get; init; }

	public required IReadOnlyList<ModuleLaunchReadiness> Modules { get; init; }

	public required bool HasEboot { get; init; }

	public required bool HasParamJson { get; init; }

	public required bool HasParamSfo { get; init; }

	public required bool RequiresDebugConsole { get; init; }

	public required IReadOnlyList<string> Issues { get; init; }

	public bool IsLaunchReady => Issues.Count == 0;
}
