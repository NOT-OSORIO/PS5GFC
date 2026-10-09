namespace ProsperoPkgTool.Containers;

public sealed class OuterFile
{
	public string Name { get; }

	public long Size { get; }

	public long SizeCompressed { get; }

	public int[] BlockIndexes { get; }

	public bool Signed { get; }

	public OuterFile(string name, long size, long sizeCompressed, int[] blockIndexes, bool signed)
	{
		Name = name;
		Size = size;
		SizeCompressed = sizeCompressed;
		BlockIndexes = blockIndexes;
		Signed = signed;
	}
}
