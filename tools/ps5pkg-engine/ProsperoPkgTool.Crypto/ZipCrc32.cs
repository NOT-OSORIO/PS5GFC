using System;

namespace ProsperoPkgTool.Crypto;

public static class ZipCrc32
{
	public const uint ReflectedPolynomial = 3988292384u;

	private static readonly uint[][] Tables = ReflectedCrc32.BuildTables(3988292384u);

	public static uint Update(uint crc, ReadOnlySpan<byte> data)
	{
		return ReflectedCrc32.Update(crc, data, Tables);
	}

	public static uint Compute(ReadOnlySpan<byte> data)
	{
		return ~Update(uint.MaxValue, data);
	}
}
