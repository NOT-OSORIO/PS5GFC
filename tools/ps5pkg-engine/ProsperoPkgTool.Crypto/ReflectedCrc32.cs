using System;
using System.Buffers.Binary;

namespace ProsperoPkgTool.Crypto;

internal static class ReflectedCrc32
{
	public static uint[][] BuildTables(uint polynomial)
	{
		uint[][] array = new uint[8][]
		{
			new uint[256],
			null,
			null,
			null,
			null,
			null,
			null,
			null
		};
		for (uint num = 0u; num < 256; num++)
		{
			uint num2 = num;
			for (int i = 0; i < 8; i++)
			{
				num2 = (((num2 & 1) != 0) ? (polynomial ^ (num2 >> 1)) : (num2 >> 1));
			}
			array[0][num] = num2;
		}
		for (int j = 1; j < 8; j++)
		{
			array[j] = new uint[256];
			for (uint num3 = 0u; num3 < 256; num3++)
			{
				array[j][num3] = (array[j - 1][num3] >> 8) ^ array[0][array[j - 1][num3] & 0xFF];
			}
		}
		return array;
	}

	public static uint Update(uint crc, ReadOnlySpan<byte> data, uint[][] tables)
	{
		uint num = crc;
		uint[] array = tables[0];
		int i;
		for (i = 0; i + 8 <= data.Length; i += 8)
		{
			uint num2 = num ^ BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));
			uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
			num = tables[7][(byte)num2] ^ tables[6][(byte)(num2 >> 8)] ^ tables[5][(byte)(num2 >> 16)] ^ tables[4][(byte)(num2 >> 24)] ^ tables[3][(byte)num3] ^ tables[2][(byte)(num3 >> 8)] ^ tables[1][(byte)(num3 >> 16)] ^ array[(byte)(num3 >> 24)];
		}
		for (; i < data.Length; i++)
		{
			num = array[(num ^ data[i]) & 0xFF] ^ (num >> 8);
		}
		return num;
	}
}
