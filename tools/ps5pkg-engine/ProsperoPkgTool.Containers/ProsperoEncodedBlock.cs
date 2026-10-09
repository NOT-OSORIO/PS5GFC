using ProsperoPkgTool.Compression;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoEncodedBlock
{
	public required int SourceBlockIndex { get; init; }

	public required long LogicalOffset { get; init; }

	public required int UncompressedLength { get; init; }

	public required byte[] EncodedBytes { get; init; }

	public required bool IsStored { get; init; }

	public KrakenEncoder.LiteralMode? KrakenMode { get; init; }

	public int Flags { get; init; }

	public int FirstChunkCompressedLength { get; init; }

	public int EncodedLength { get; init; } = -1;

	public long PhysicalOffset { get; internal set; }

	public int StoredLength
	{
		get
		{
			if (EncodedLength < 0)
			{
				return EncodedBytes.Length;
			}
			return EncodedLength;
		}
	}
}
