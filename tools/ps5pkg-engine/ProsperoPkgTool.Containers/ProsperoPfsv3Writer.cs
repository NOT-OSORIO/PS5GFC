using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using ProsperoPkgTool.Compression;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPfsv3Writer
{
	public readonly record struct CompressedBlock(int CompressedSize, int UncompressedSize, bool MultiChunk, int FirstChunkCompressedSize, int Flags);

	public const int DefaultBlockSize = 262144;

	private const int HeaderSize = 72;

	private const int DirectoryEntrySize = 16;

	private const int SectionCount = 7;

	private const int SectionAlignment = 8;

	private const int DataAlignment = 1024;

	private const uint Magic = 1129530960u;

	private const uint EncodeParam0C = 2050u;

	private const int KrakenAlgorithm = 2;

	private const int DefaultLevel = 7;

	private const int WindowBits = 18;

	private const ulong StoredFlagBase = 12uL;

	private const ulong StoredFlagLargeHalf = 192uL;

	private const ulong BoundaryFlagShift = 48uL;

	private const ulong SizeHintShift = 44uL;

	private const ulong SizeHintMax = 131071uL;

	private static readonly byte[] GitHash = new byte[20]
	{
		35, 152, 125, 22, 201, 32, 154, 199, 40, 55,
		25, 50, 126, 15, 80, 107, 188, 244, 89, 244
	};

	private static readonly byte[] ShuffleTable = new byte[64]
	{
		4, 4, 0, 0, 0, 0, 0, 0, 2, 2,
		4, 0, 0, 0, 0, 0, 1, 1, 6, 0,
		0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
		1, 1, 8, 2, 2, 4, 0, 0, 0, 0,
		1, 1, 6, 2, 2, 4, 0, 0, 1, 1,
		6, 1, 1, 6, 0, 0, 4, 4, 4, 4,
		0, 0, 0, 0
	};

	public static byte[] WriteStored(ReadOnlySpan<byte> payload, int blockSize = 262144, int level = 7)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, "blockSize");
		KrakenTuning.ValidateLevel(level);
		int num = ((payload.Length == 0) ? 1 : ((payload.Length + blockSize - 1) / blockSize));
		int num2 = GitHash.Length;
		int num3 = ShuffleTable.Length;
		int num4 = (num + 1) * 16;
		int num5 = num * 32;
		int num6 = num * 16;
		int num7 = 184;
		int num8 = Align(num7 + num2, 8);
		int num9 = Align(num8 + num3, 8);
		int num10 = Align(num9 + num4, 8);
		int num11 = Align(num10 + num5, 8);
		int num12 = Align(num11 + num6, 8);
		int num13 = Align(num12, 1024);
		long num14 = (long)num13 + (long)payload.Length;
		byte[] array = new byte[checked((int)num14)];
		Span<byte> span = array;
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1129530960u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), 3);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 7);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)blockSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 2050u);
		ulong value = 2 | ((ulong)(byte)level << 8) | 0x120000;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), (ulong)payload.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), (ulong)num14);
		WriteDirectoryEntry(span, 0, 1, num7, num2);
		WriteDirectoryEntry(span, 1, 2, num8, num3);
		WriteDirectoryEntry(span, 2, 3, num9, num4);
		WriteDirectoryEntry(span, 3, 4, num10, num5);
		WriteDirectoryEntry(span, 4, 5, num11, num6);
		WriteDirectoryEntry(span, 5, 6, num12, 0);
		WriteDirectoryEntry(span, 6, 7, num13, payload.Length);
		GitHash.CopyTo(span.Slice(num7));
		ShuffleTable.CopyTo(span.Slice(num8));
		int num15 = blockSize / 2;
		long num16 = 0L;
		for (int i = 0; i < num; i++)
		{
			int num17 = (int)((payload.Length != 0) ? Math.Min(blockSize, payload.Length - num16) : 0);
			ulong num18 = 0xCuL | (ulong)((num17 > num15) ? 192 : 0);
			ulong num19 = (ulong)Math.Min(Math.Max(num17 - 1, 0), 131071L);
			int num20 = num9 + i * 16;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num20), (ulong)num16 | (num18 << 48));
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num20 + 8), (ulong)num16 | (num19 << 44));
			ReadOnlySpan<byte> data = ((num17 == 0) ? default(ReadOnlySpan<byte>) : payload.Slice((int)num16, num17));
			Sha3.Sha3_256(data).CopyTo(span.Slice(num10 + i * 32, 32));
			if (num17 > 0)
			{
				data.CopyTo(span.Slice(num13 + (int)num16));
			}
			num16 += num17;
		}
		int num21 = num9 + num * 16;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num21), (ulong)payload.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num21 + 8), (ulong)payload.Length);
		ComputeFileDigest(span.Slice(8, 24), span.Slice(num8, num3), span.Slice(num9, num4), span.Slice(num10, num5)).CopyTo(span.Slice(40));
		return array;
	}

	public static (byte[] Container, IReadOnlyList<CompressedBlock> Blocks) WriteCompressed(ReadOnlySpan<byte> payload, int blockSize = 262144, int level = 7, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize, "blockSize");
		KrakenTuning.ValidateLevel(level);
		int num = ((payload.Length == 0) ? 1 : ((payload.Length + blockSize - 1) / blockSize));
		byte[][] array = new byte[num][];
		CompressedBlock[] array2 = new CompressedBlock[num];
		bool[] array3 = new bool[num];
		long num2 = 0L;
		for (int i = 0; i < num; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int num3 = (int)((payload.Length != 0) ? Math.Min(blockSize, payload.Length - num2) : 0);
			ReadOnlySpan<byte> input = ((num3 == 0) ? default(ReadOnlySpan<byte>) : payload.Slice((int)num2, num3));
			KrakenEncoder.EncodedBlock encodedBlock = ((num3 > 0) ? KrakenEncoder.TryEncodeNewLz(input) : null);
			if (encodedBlock != null && encodedBlock.Payload.Length < num3)
			{
				bool multiChunk = encodedBlock.Flags == 34;
				array[i] = encodedBlock.Payload;
				array3[i] = true;
				array2[i] = new CompressedBlock(encodedBlock.Payload.Length, num3, multiChunk, encodedBlock.FirstChunkCompressedLength, encodedBlock.Flags);
			}
			else
			{
				array[i] = input.ToArray();
				array2[i] = new CompressedBlock(num3, num3, MultiChunk: false, 0, 0);
			}
			num2 += num3;
		}
		long num4 = 0L;
		for (int j = 0; j < num; j++)
		{
			num4 += array[j].Length;
		}
		int num5 = GitHash.Length;
		int num6 = ShuffleTable.Length;
		int num7 = (num + 1) * 16;
		int num8 = num * 32;
		int num9 = num * 16;
		int num10 = 184;
		int num11 = Align(num10 + num5, 8);
		int num12 = Align(num11 + num6, 8);
		int num13 = Align(num12 + num7, 8);
		int num14 = Align(num13 + num8, 8);
		int num15 = Align(num14 + num9, 8);
		int num16 = Align(num15, 1024);
		long num17 = num16 + num4;
		byte[] array4 = new byte[checked((int)num17)];
		Span<byte> span = array4;
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1129530960u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), 3);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), 7);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), (uint)blockSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), 2050u);
		ulong value = 2 | ((ulong)(byte)level << 8) | 0x120000;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), (ulong)payload.Length);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), (ulong)num17);
		WriteDirectoryEntry(span, 0, 1, num10, num5);
		WriteDirectoryEntry(span, 1, 2, num11, num6);
		WriteDirectoryEntry(span, 2, 3, num12, num7);
		WriteDirectoryEntry(span, 3, 4, num13, num8);
		WriteDirectoryEntry(span, 4, 5, num14, num9);
		WriteDirectoryEntry(span, 5, 6, num15, 0);
		WriteDirectoryEntry(span, 6, 7, num16, (int)num4);
		GitHash.CopyTo(span.Slice(num10));
		ShuffleTable.CopyTo(span.Slice(num11));
		int num18 = blockSize / 2;
		long num19 = 0L;
		long num20 = 0L;
		for (int k = 0; k < num; k++)
		{
			CompressedBlock compressedBlock = array2[k];
			int num21 = array[k].Length;
			ulong num22;
			ulong num23;
			if (array3[k])
			{
				num22 = (ulong)compressedBlock.Flags;
				num23 = (ulong)Math.Clamp((compressedBlock.MultiChunk ? compressedBlock.FirstChunkCompressedSize : num21) - 1, 0, 131071);
			}
			else
			{
				num22 = 0xCuL | (ulong)((compressedBlock.UncompressedSize > num18) ? 192 : 0);
				num23 = (ulong)Math.Clamp(compressedBlock.UncompressedSize - 1, 0, 131071);
			}
			int num24 = num12 + k * 16;
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num24), (ulong)num19 | (num22 << 48));
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num24 + 8), (ulong)num20 | (num23 << 44));
			Sha3.Sha3_256((compressedBlock.UncompressedSize == 0) ? default(ReadOnlySpan<byte>) : payload.Slice((int)num20, compressedBlock.UncompressedSize)).CopyTo(span.Slice(num13 + k * 32, 32));
			if (num21 > 0)
			{
				array[k].CopyTo(span.Slice(num16 + (int)num19));
			}
			num19 += num21;
			num20 += compressedBlock.UncompressedSize;
		}
		int num25 = num12 + num * 16;
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num25), (ulong)num4);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num25 + 8), (ulong)payload.Length);
		ComputeFileDigest(span.Slice(8, 24), span.Slice(num11, num6), span.Slice(num12, num7), span.Slice(num13, num8)).CopyTo(span.Slice(40));
		return (Container: array4, Blocks: array2);
	}

	private static byte[] ComputeFileDigest(ReadOnlySpan<byte> headerParams, ReadOnlySpan<byte> shuffleSection, ReadOnlySpan<byte> boundarySection, ReadOnlySpan<byte> blockHashSection)
	{
		Span<byte> destination = stackalloc byte[32];
		destination.Clear();
		BinaryPrimitives.WriteUInt32LittleEndian(destination, 1u);
		headerParams.Slice(0, 8).CopyTo(destination.Slice(4));
		headerParams.Slice(8).CopyTo(destination.Slice(16));
		byte[] array = new byte[destination.Length + shuffleSection.Length + boundarySection.Length + blockHashSection.Length];
		destination.CopyTo(array);
		shuffleSection.CopyTo(array.AsSpan(destination.Length));
		boundarySection.CopyTo(array.AsSpan(destination.Length + shuffleSection.Length));
		blockHashSection.CopyTo(array.AsSpan(destination.Length + shuffleSection.Length + boundarySection.Length));
		return Sha3.Sha3_256(array);
	}

	private static void WriteDirectoryEntry(Span<byte> span, int index, ushort id, int offset, int size)
	{
		int num = 72 + index * 16;
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(num), id);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num + 2), (uint)offset);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(num + 10), (uint)size);
	}

	private static int Align(int value, int alignment)
	{
		return (value + alignment - 1) & ~(alignment - 1);
	}
}
