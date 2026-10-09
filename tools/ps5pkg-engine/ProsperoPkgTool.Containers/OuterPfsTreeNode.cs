using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsTreeNode
{
	public string Name { get; init; } = "";

	public bool IsDirectory { get; init; }

	public uint Inode { get; init; }

	public long StoredSize { get; init; }

	public long PlainSize { get; init; }

	public uint Flags { get; init; }

	public ushort Mode { get; init; }

	public ushort Nlink { get; init; } = 1;

	public int StartBlock { get; init; }

	public bool Internal { get; init; }

	public List<OuterPfsTreeNode> Children { get; } = new List<OuterPfsTreeNode>();
}
