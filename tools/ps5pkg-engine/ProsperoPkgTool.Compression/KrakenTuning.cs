using System;

namespace ProsperoPkgTool.Compression;

public static class KrakenTuning
{
	public const int DefaultLevel = 7;

	public const int MinLevel = -4;

	public const int MaxLevel = 9;

	public const int AutoThreads = 0;

	public const int MaxThreads = 256;

	public static int ValidateLevel(int level)
	{
		if (level < -4 || level > 9)
		{
			throw new ArgumentOutOfRangeException("level", level, $"Kraken level must be in the range {-4}..{9}.");
		}
		return level;
	}

	public static int ResolveThreads(int threads)
	{
		if (threads >= 0)
		{
			if (threads == 0)
			{
				return Math.Clamp(Environment.ProcessorCount, 1, 256);
			}
			return Math.Clamp(threads, 1, 256);
		}
		throw new ArgumentOutOfRangeException("threads", threads, "Kraken threads must be 0 (auto) or a positive count.");
	}
}
