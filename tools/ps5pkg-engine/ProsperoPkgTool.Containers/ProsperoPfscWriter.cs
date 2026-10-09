using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPfscWriter
{
	public const int BlockSize = 65536;

	public const uint Magic = 1129530960u;

	public const int Unk8 = 6;

	public const long PointerTableOffset = 1024L;

	private const long PointerTableLimit = 64512L;

	public static long ComputeHeaderSize(long payloadSize)
	{
		long num = ComputeBlockCount(payloadSize);
		long num2 = (8 + num * 8 - 64512 + 65535) / 65536;
		if (num2 < 0)
		{
			num2 = 0L;
		}
		return 65536 + num2 * 65536;
	}

	public static long ComputeBlockCount(long size)
	{
		return (size + 65536 - 1) / 65536;
	}

	public static void Write(ReadOnlySpan<byte> payload, Stream output)
	{
		ArgumentNullException.ThrowIfNull(output, "output");
		long num = payload.Length;
		long num2 = ComputeBlockCount(num);
		long headerSize = ComputeHeaderSize(num);
		WriteHeader(output, headerSize, num2, 65536, 65536L, num2 * 65536);
		WritePointerTable(output, headerSize, num2, (long _) => 65536L);
		output.Write(payload);
		long num3 = num2 * 65536;
		WriteZeroes(output, num3 - num);
	}

	public static byte[] Write(ReadOnlySpan<byte> payload)
	{
		using MemoryStream memoryStream = new MemoryStream(checked((int)(ComputeHeaderSize(payload.Length) + ComputeBlockCount(payload.Length) * 65536)));
		Write(payload, memoryStream);
		return memoryStream.ToArray();
	}

	public static void WriteCompressed(ReadOnlySpan<byte> payload, Stream output, CompressionLevel level = CompressionLevel.SmallestSize)
	{
		ArgumentNullException.ThrowIfNull(output, "output");
		long num = payload.Length;
		long num2 = ComputeBlockCount(num);
		long[] blockSizes = new long[num2];
		long num3 = 0L;
		long num4 = 0L;
		byte[] array = new byte[65536];
		for (long num5 = 0L; num5 < num2; num5++)
		{
			int num6 = (int)Math.Min(65536L, num - num5 * 65536);
			payload.Slice((int)(num5 * 65536), num6).CopyTo(array);
			if (num6 < 65536)
			{
				Array.Clear(array, num6, 65536 - num6);
			}
			byte[] array2 = ZlibCompress(array, level);
			if (array2.Length < 65536)
			{
				blockSizes[num5] = array2.Length;
				num3++;
			}
			else
			{
				blockSizes[num5] = 65536L;
			}
			num4 += blockSizes[num5];
		}
		if (num3 == 0L)
		{
			Write(payload, output);
			return;
		}
		long headerSize = ComputeHeaderSize(num);
		WriteHeader(output, headerSize, num2, 65536, 65536L, num2 * 65536);
		WritePointerTable(output, headerSize, num2, (long i) => blockSizes[i]);
		for (long num7 = 0L; num7 < num2; num7++)
		{
			int num8 = (int)Math.Min(65536L, num - num7 * 65536);
			payload.Slice((int)(num7 * 65536), num8).CopyTo(array);
			if (num8 < 65536)
			{
				Array.Clear(array, num8, 65536 - num8);
			}
			if (blockSizes[num7] < 65536)
			{
				byte[] array3 = ZlibCompress(array, level);
				output.Write(array3);
			}
			else
			{
				output.Write(array);
			}
		}
	}

	public static byte[] WriteCompressed(ReadOnlySpan<byte> payload, CompressionLevel level = CompressionLevel.SmallestSize)
	{
		using MemoryStream memoryStream = new MemoryStream();
		WriteCompressed(payload, memoryStream, level);
		return memoryStream.ToArray();
	}

	private static void WriteHeader(Stream output, long headerSize, long numBlocks, int blockSize32, long blockSize64, long dataLength)
	{
		Span<byte> span = stackalloc byte[48];
		span.Clear();
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0), 1129530960u);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), 6);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), blockSize32);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), blockSize64);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(24), 1024L);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), (ulong)headerSize);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(40), dataLength);
		output.Write(span);
		WriteZeroes(output, 976L);
	}

	private static void WritePointerTable(Stream output, long headerSize, long numBlocks, Func<long, long> blockSizeAt)
	{
		byte[] array = new byte[8];
		long num = headerSize;
		for (long num2 = 0L; num2 <= numBlocks; num2++)
		{
			BinaryPrimitives.WriteInt64LittleEndian(array, num);
			output.Write(array);
			if (num2 < numBlocks)
			{
				num += blockSizeAt(num2);
			}
		}
		long num3 = 1024 + (numBlocks + 1) * 8;
		WriteZeroes(output, headerSize - num3);
	}

	private static byte[] ZlibCompress(byte[] data, CompressionLevel level)
	{
		using MemoryStream memoryStream = new MemoryStream();
		using (ZLibStream zLibStream = new ZLibStream(memoryStream, level, leaveOpen: true))
		{
			zLibStream.Write(data, 0, data.Length);
		}
		return memoryStream.ToArray();
	}

	private static void WriteZeroes(Stream output, long count)
	{
		if (count > 0)
		{
			byte[] array = new byte[65536];
			while (count > 0)
			{
				int num = (int)Math.Min(count, array.Length);
				output.Write(array, 0, num);
				count -= num;
			}
		}
	}
}
