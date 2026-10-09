namespace ProsperoPkgTool.Containers;

public sealed class NapsCblockPlanEntry
{
	public bool IsAlias { get; init; }

	public bool StoredRaw { get; init; }

	public bool StartRun { get; init; }

	public long OnDiskOffset { get; init; }

	public long LogicalOffset { get; init; }

	public long EvenChunkCompressedLength { get; init; }

	public long StreamLength { get; init; }

	public byte Even { get; init; }

	public byte Odd { get; init; }

	public byte KdePredictor { get; init; }

	public byte ShuffleIndex { get; init; }

	public bool Terminator { get; init; }
}
