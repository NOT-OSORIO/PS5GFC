namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsFileSource
{
	public required string Name { get; init; }

	public required string Path { get; init; }

	public long? SizeCompressed { get; init; }

	public bool Signed { get; init; }
}
