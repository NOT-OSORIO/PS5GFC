using System;

namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsBuildResult
{
	public required byte[] Plaintext { get; init; }

	public required OuterBlockKind[] BlockKinds { get; init; }

	public required int SuperblockIndex { get; init; }

	public int[] FileFirstBlock { get; init; } = Array.Empty<int>();

	public int[] FileBlockCount { get; init; } = Array.Empty<int>();

	public int InodeTableIndex { get; init; }

	public int SuperRootDirentIndex { get; init; }

	public int FltIndex { get; init; }

	public int UrootDirentIndex { get; init; }

	public int BlockCount => BlockKinds.Length;
}
