using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPfscReader
{
	public static byte[] Read(ReadOnlySpan<byte> container)
	{
		if (container.Length < 48)
		{
			throw new InvalidDataException("PFSC container is too small.");
		}
		if (BinaryPrimitives.ReadUInt32LittleEndian(container.Slice(0)) != 1129530960)
		{
			throw new InvalidDataException("Missing 'PFSC' magic.");
		}
		if (BinaryPrimitives.ReadInt32LittleEndian(container.Slice(4)) != 0)
		{
			throw new InvalidDataException("PFSC unk4 field is not zero.");
		}
		int num = BinaryPrimitives.ReadInt32LittleEndian(container.Slice(12));
		long num2 = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(16));
		long num3 = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(24));
		long num4 = (long)BinaryPrimitives.ReadUInt64LittleEndian(container.Slice(32));
		long num5 = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(40));
		if (num != 65536)
		{
			throw ProsperoErrorInfo.Unsupported($"Unsupported PFSC block size {num}.");
		}
		if (num2 != num)
		{
			throw new InvalidDataException("PFSC block-size fields disagree.");
		}
		if (num3 <= 0 || num3 >= container.Length)
		{
			throw new InvalidDataException("PFSC pointer-table offset is out of range.");
		}
		if (num4 < 0 || num4 > container.Length)
		{
			throw new InvalidDataException("PFSC data start is out of range.");
		}
		if (num5 < 0 || num5 % num != 0L)
		{
			throw new InvalidDataException("PFSC data length is not a whole number of blocks.");
		}
		long num6 = num5 / num;
		if (num3 + (num6 + 1) * 8 > container.Length)
		{
			throw new InvalidDataException("PFSC pointer table extends past the container.");
		}
		byte[] array = new byte[num5];
		for (long num7 = 0L; num7 < num6; num7++)
		{
			int num8 = (int)(num3 + num7 * 8);
			long num9 = BinaryPrimitives.ReadInt64LittleEndian(container.Slice(num8));
			int num10 = (int)(BinaryPrimitives.ReadInt64LittleEndian(container.Slice(num8 + 8)) - num9);
			if (num10 <= 0 || num10 > num)
			{
				throw new InvalidDataException($"PFSC block {num7} has an invalid size {num10}.");
			}
			Span<byte> span = array.AsSpan((int)(num7 * num), num);
			if (num10 == num)
			{
				container.Slice((int)num9, num).CopyTo(span);
				continue;
			}
			using MemoryStream stream = new MemoryStream(container.Slice((int)num9 + 2, num10 - 2).ToArray(), writable: false);
			using DeflateStream deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
			deflateStream.ReadExactly(span);
		}
		return array;
	}
}
