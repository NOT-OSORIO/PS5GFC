using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed class NapsGenerationRequest
{
	public byte CompressionType { get; init; }

	public required int NumUBlocks { get; init; }

	public required int NumOuterBlocks { get; init; }

	public int NumKeys { get; init; } = 1;

	public required IReadOnlyList<long> FileLogicalOffsets { get; init; }

	public byte FinalFileOffsetType { get; init; } = 64;

	public required IReadOnlyList<NapsCblockPlanEntry> Blocks { get; init; }

	public IReadOnlyList<byte[]>? OuterBlockDigests { get; init; }

	public IReadOnlyList<byte[]>? ShufflePatterns { get; init; }
}
