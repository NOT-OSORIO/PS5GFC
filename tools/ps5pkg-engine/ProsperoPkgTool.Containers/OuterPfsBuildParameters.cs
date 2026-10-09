namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsBuildParameters
{
	public long TimestampSeconds { get; init; } = 1781638585L;

	public uint TimestampNanoseconds { get; init; } = 350000000u;

	public byte[]? Seed { get; init; }

	public int BlockParallelism { get; init; }
}
