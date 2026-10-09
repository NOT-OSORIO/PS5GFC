namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsFile
{
	public required string Name { get; init; }

	public required byte[] Data { get; init; }

	public long? SizeCompressed { get; init; }

	public bool Signed { get; init; }
}
