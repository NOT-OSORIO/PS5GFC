using System.Collections.Generic;
using System.Threading;
using ProsperoPkgTool.Gp5;

namespace ProsperoPkgTool.Containers;

public sealed class DebugPackageBuildOptions
{
	public required string OutputPath { get; init; }

	public CancellationToken CancellationToken { get; init; }

	public required string ContentId { get; init; }

	public required string Passcode { get; init; }

	public required byte[] ParamJson { get; init; }

	public ProsperoInnerCompressionMode Compression { get; init; } = ProsperoInnerCompressionMode.Auto;

	public int KrakenLevel { get; init; } = 7;

	public int KrakenThreads { get; init; }

	public long TimestampSeconds { get; init; } = 1781638585L;

	public uint TimestampNanoseconds { get; init; } = 350000000u;

	public byte[]? OuterSeed { get; init; }

	public bool DeterministicEntryKeys { get; init; }

	public string? TempDirectory { get; init; }

	public bool SweepStaleTempWorkspaces { get; init; } = true;

	public int StaleWorkspaceHours { get; init; } = 12;

	public bool SkipFreeSpaceCheck { get; init; }

	public long FileBackedInnerImageThreshold { get; init; } = 1073741824L;

	public PlayGoProject? PlayGo { get; init; }

	public int PlayGoChunkCount { get; init; } = 1;

	public IReadOnlyDictionary<string, byte[]>? SceSysFiles { get; init; }

	public bool FakeSignModules { get; init; } = true;

	public bool InjectPfsVersionDat { get; init; } = true;

	public bool InjectKeystone { get; init; } = true;

	public bool InjectRightSprx { get; init; } = true;

	public ulong? SdkVersionOverride { get; init; }

	public string? DrmTypeOverride { get; init; }

	public string? TitleOverride { get; init; }

	public string? TitleIdOverride { get; init; }

	public DebugPackageBuildLog? Log { get; init; }
}
