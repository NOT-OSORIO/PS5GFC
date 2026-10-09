namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsFileBuildResult
{
	public required string ImagePath { get; init; }

	public required long ImageLength { get; init; }

	public required OuterBlockKind[] BlockKinds { get; init; }

	public required int SuperblockIndex { get; init; }

	public required int[] FileFirstBlock { get; init; }

	public required int[] FileBlockCount { get; init; }

	public required int InodeTableIndex { get; init; }

	public required int SuperRootDirentIndex { get; init; }

	public required int FltIndex { get; init; }

	public required int UrootDirentIndex { get; init; }

	public required byte[] ImageDigests { get; init; }

	public required byte[] SuperblockIcv { get; init; }

	public required byte[] SuperblockPlaintext { get; init; }

	public int BlockCount => BlockKinds.Length;
}
