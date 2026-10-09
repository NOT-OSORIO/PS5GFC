using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed class NapsLayoutDocument
{
	public required NapsLayoutCounts Counts { get; init; }

	public required NapsSectionMap Map { get; init; }

	public required IReadOnlyList<byte[]> OuterBlockDigests { get; init; }

	public required IReadOnlyList<byte[]> ShufflePatterns { get; init; }

	public required IReadOnlyList<NapsFileOffsetEntry> FileOffsets { get; init; }

	public required IReadOnlyList<NapsU2cEntry> CblockInfoOffsetByUblock { get; init; }

	public required IReadOnlyList<NapsCblockInfoEntry> CblockInfos { get; init; }

	public int TrailingZeroBytes { get; init; } = -1;
}
