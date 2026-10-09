using System.Collections.Generic;

namespace ProsperoPkgTool.Content;

public sealed class ProsperoBackupStagingResult
{
	public required string StagingFolder { get; init; }

	public required string ContentId { get; init; }

	public required string Passcode { get; init; }

	public required string Version { get; init; }

	public required ProsperoDebugLicense DebugLicense { get; init; }

	public required IReadOnlyList<string> SubstitutedModules { get; init; }

	public required IReadOnlyList<string> PlaintextModules { get; init; }

	public required IReadOnlyList<string> UnresolvedModules { get; init; }

	public required IReadOnlyList<string> Warnings { get; init; }

	public required ProsperoLaunchReadinessReport LaunchReadiness { get; init; }
}
