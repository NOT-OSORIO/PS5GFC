namespace ProsperoPkgTool;

public readonly record struct ProsperoBuildProgress(string Stage, long Done, long Total)
{
	public ProsperoBuildStage? StageId { get; init; } = null;

	public long BytesDone { get; init; } = 0L;

	public long BytesTotal { get; init; } = 0L;

	public string? CurrentPath { get; init; } = null;

	public double Fraction
	{
		get
		{
			if (Total <= 0)
			{
				return 0.0;
			}
			return (double)Done / (double)Total;
		}
	}

	public double ByteFraction
	{
		get
		{
			if (BytesTotal <= 0)
			{
				return 0.0;
			}
			return (double)BytesDone / (double)BytesTotal;
		}
	}
}
