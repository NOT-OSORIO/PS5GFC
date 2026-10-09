namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsTreeInfo
{
	public int BlockSize { get; init; } = 65536;

	public long ImageBlocks { get; init; }

	public int InodeCount { get; init; }

	public int DinodeBlockCount { get; init; } = 1;

	public int DinodeBlock { get; init; }

	public long DinodeSize { get; init; } = 65536L;

	public uint DinodeFlags { get; init; } = 131084u;

	public uint RootInode { get; init; }

	public byte[]? Seed { get; init; }

	public byte[]? SuperblockIcv { get; init; }

	public bool Signed { get; init; }

	public bool Encrypted { get; init; }

	public OuterPfsTreeNode? Root { get; init; }
}
