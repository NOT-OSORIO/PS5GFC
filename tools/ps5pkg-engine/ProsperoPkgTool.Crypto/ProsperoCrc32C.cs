using System;
using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;

namespace ProsperoPkgTool.Crypto;

public static class ProsperoCrc32C
{
	public const uint ReflectedPolynomial = 2197175160u;

	private static readonly uint[][] Tables = ReflectedCrc32.BuildTables(2197175160u);

	public static uint Update(uint crc, ReadOnlySpan<byte> data)
	{
		if (Sse42.IsSupported)
		{
			return UpdateHardware(crc, data);
		}
		return ReflectedCrc32.Update(crc, data, Tables);
	}

	private static uint UpdateHardware(uint crc, ReadOnlySpan<byte> data)
	{
		uint num = crc;
		int i;
		for (i = 0; i + 8 <= data.Length; i += 8)
		{
			num = (uint)Sse42.X64.Crc32(num, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i)));
		}
		if (i + 4 <= data.Length)
		{
			num = Sse42.Crc32(num, BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)));
			i += 4;
		}
		if (i + 2 <= data.Length)
		{
			num = Sse42.Crc32(num, BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i)));
			i += 2;
		}
		if (i < data.Length)
		{
			num = Sse42.Crc32(num, data[i]);
		}
		return num;
	}

	public static uint Compute(ReadOnlySpan<byte> data)
	{
		return ~Update(uint.MaxValue, data);
	}
}
