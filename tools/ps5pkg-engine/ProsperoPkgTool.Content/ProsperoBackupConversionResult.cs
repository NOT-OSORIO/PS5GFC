using System.Collections.Generic;

namespace ProsperoPkgTool.Content;

public sealed class ProsperoBackupConversionResult
{
	public required string OutputPath { get; init; }

	public required ProsperoDebugLicense DebugLicense { get; init; }

	public required IReadOnlyList<string> SubstitutedModules { get; init; }

	public required IReadOnlyList<string> PlaintextModules { get; init; }

	public required IReadOnlyList<string> UnresolvedModules { get; init; }

	public required IReadOnlyList<string> Warnings { get; init; }

	public required ProsperoLaunchReadinessReport LaunchReadiness { get; init; }

	public required string StagingFolder { get; init; }
}
