namespace ProsperoPkgTool.Containers;

public readonly record struct ProsperoCompressionMetrics(long RawBytes, long EncodedBytes, int KrakenBlocks, int StoredBlocks)
{
	public double Ratio
	{
		get
		{
			if (RawBytes != 0L)
			{
				return (double)EncodedBytes / (double)RawBytes;
			}
			return 1.0;
		}
	}
}
