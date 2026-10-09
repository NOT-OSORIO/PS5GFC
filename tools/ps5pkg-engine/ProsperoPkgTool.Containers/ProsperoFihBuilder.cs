using System;
using System.Buffers.Binary;
using System.IO;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoFihBuilder
{
	public const int HeaderRegionSize = 65536;

	private const int SignedByteOffset = 5;

	private const int PfsImageOffsetField = 16;

	private const int PfsImageSizeField = 24;

	private const int EmbeddedCntOffsetField = 88;

	private const int DataRegionBlockCountField = 80;

	private const int InnerImageBlockCountField = 144;

	private const int MetaBlockCountField = 148;

	private const int MetaBlockCountMirrorField = 152;

	private const int ContentVersionField = 156;

	private const int InnerImageSizeField = 160;

	private const int InnerImageLogicalSizeField = 168;

	private const int OuterFileCountField = 240;

	private const int SparseAfidHoleCountField = 244;

	private const int FlatPathTableBlockCountField = 248;

	private const int EmptyFileCountField = 252;

	private const uint OuterFileCountNwonly = 1u;

	private const uint FlatPathTableBlockCountNwonly = 2u;

	public static byte[] BuildHeaderBlock(FihVariant variant, ulong pfsImageSize, ulong embeddedCntOffset, ReadOnlySpan<byte> image, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint contentVersionHi = 0u, int innerContentInodes = 0, int appFileCount = 0, int metaBlockCount = 0, int metaBlockCountMirror = 0, int emptyFileCount = 0, int sparseAfidHoleCount = 0)
	{
		byte[] array = new byte[65536];
		array[0] = 127;
		array[1] = 70;
		array[2] = 73;
		array[3] = 72;
		array[4] = 1;
		array[5] = (byte)((variant == FihVariant.Official) ? 128 : 0);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(6), 3);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(8), 1u);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(16), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(24), pfsImageSize);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(88), embeddedCntOffset);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(96), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(104), 140737488355328uL);
		if (nestedMetaBaseBlocks > 0)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(80), (ulong)nestedMetaBaseBlocks);
		}
		int num = LocateSuperblock(image);
		if (num >= 0)
		{
			byte[] source = Sha3.Sha3_256(image.Slice(num, 65536));
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(32), (ulong)(65536 + num));
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
			source.CopyTo(array.AsSpan(48, 32));
			source.CopyTo(array.AsSpan(112, 32));
			source.CopyTo(array.AsSpan(208, 32));
			long num2 = (long)num / 65536L;
			long num3 = (long)pfsImageSize / 65536L;
			if (num2 >= 1 && num % 65536 == 0 && (long)pfsImageSize % 65536L == 0L && num3 > num2)
			{
				bool flag = innerContentInodes > 0;
				long num4 = ((nestedImageSize > 0) ? ((nestedImageSize + 65536 - 1) / 65536) : 1);
				uint num5 = (uint)(num2 - num4);
				uint num6 = (uint)(flag ? innerContentInodes : (num3 - num5));
				uint value = ((metaBlockCount > 0) ? ((uint)metaBlockCount) : num6);
				uint value2 = ((metaBlockCountMirror > 0) ? ((uint)metaBlockCountMirror) : num6);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(144), num5);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(148), value);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(152), value2);
				BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(160), (ulong)num5 * 65536uL);
				if (contentVersionHi != 0)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(156), contentVersionHi);
				}
				if (nestedImageSize > 0)
				{
					BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(168), (ulong)nestedImageSize);
				}
				uint value3 = ((!flag || appFileCount <= 0) ? 1u : ((uint)appFileCount));
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(240), value3);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(244), (uint)sparseAfidHoleCount);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(248), 2u);
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(252), (uint)emptyFileCount);
			}
		}
		else
		{
			byte[] source2 = Sha3.Sha3_256(image);
			source2.CopyTo(array.AsSpan(48, 32));
			source2.CopyTo(array.AsSpan(112, 32));
			source2.CopyTo(array.AsSpan(208, 32));
		}
		((nestedImageDigest != null && nestedImageDigest.Length == 32) ? nestedImageDigest : Sha3.Sha3_256(image)).CopyTo(array.AsSpan(176, 32));
		return array;
	}

	public static byte[] BuildHeaderBlockFromSuperblock(FihVariant variant, ulong pfsImageSize, ulong embeddedCntOffset, ReadOnlySpan<byte> superblockBlock, long superblockOffset, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint contentVersionHi = 0u, int innerContentInodes = 0, int appFileCount = 0, int metaBlockCount = 0, int metaBlockCountMirror = 0, int emptyFileCount = 0, int sparseAfidHoleCount = 0)
	{
		byte[] array = new byte[65536];
		array[0] = 127;
		array[1] = 70;
		array[2] = 73;
		array[3] = 72;
		array[4] = 1;
		array[5] = (byte)((variant == FihVariant.Official) ? 128 : 0);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(6), 3);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(8), 1u);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(16), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(24), pfsImageSize);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(88), embeddedCntOffset);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(96), 65536uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(104), 140737488355328uL);
		if (nestedMetaBaseBlocks > 0)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(80), (ulong)nestedMetaBaseBlocks);
		}
		byte[] array2 = Sha3.Sha3_256(superblockBlock);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(32), (ulong)(65536 + superblockOffset));
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
		array2.CopyTo(array.AsSpan(48, 32));
		array2.CopyTo(array.AsSpan(112, 32));
		array2.CopyTo(array.AsSpan(208, 32));
		long num = superblockOffset / 65536;
		long num2 = (long)pfsImageSize / 65536L;
		if (num >= 1 && superblockOffset % 65536 == 0L && (long)pfsImageSize % 65536L == 0L && num2 > num)
		{
			bool flag = innerContentInodes > 0;
			long num3 = ((nestedImageSize > 0) ? ((nestedImageSize + 65536 - 1) / 65536) : 1);
			uint num4 = (uint)(num - num3);
			uint num5 = (uint)(flag ? innerContentInodes : (num2 - num4));
			uint value = ((metaBlockCount > 0) ? ((uint)metaBlockCount) : num5);
			uint value2 = ((metaBlockCountMirror > 0) ? ((uint)metaBlockCountMirror) : num5);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(144), num4);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(148), value);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(152), value2);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(160), (ulong)num4 * 65536uL);
			if (contentVersionHi != 0)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(156), contentVersionHi);
			}
			if (nestedImageSize > 0)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(168), (ulong)nestedImageSize);
			}
			uint value3 = ((!flag || appFileCount <= 0) ? 1u : ((uint)appFileCount));
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(240), value3);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(244), (uint)sparseAfidHoleCount);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(248), 2u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(252), (uint)emptyFileCount);
		}
		((nestedImageDigest != null && nestedImageDigest.Length == 32) ? nestedImageDigest : array2).CopyTo(array.AsSpan(176, 32));
		return array;
	}

	public static byte[] BuildFromCnt(byte[] cnt, FihVariant variant = FihVariant.Debug, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, long nestedMetaBaseBlocks = 0L, uint contentVersionHi = 0u, int innerContentInodes = 0, int appFileCount = 0, int metaBlockCount = 0, int metaBlockCountMirror = 0, int emptyFileCount = 0, int sparseAfidHoleCount = 0, byte[]? siSegment = null)
	{
		ArgumentNullException.ThrowIfNull(cnt, "cnt");
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(32));
		ulong num2 = BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(40));
		ulong num3 = BinaryPrimitives.ReadUInt64BigEndian(cnt.AsSpan(1048));
		ulong num4 = num + num2;
		if (num == 0L || num3 == 0L || num4 + num3 > (ulong)cnt.Length)
		{
			throw new InvalidDataException("CNT container has no embedded PFS image to finalize.");
		}
		int num5 = (int)num4;
		byte[] array = cnt.AsSpan(0, num5).ToArray();
		byte[] array2 = cnt.AsSpan(num5, (int)num3).ToArray();
		BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(1040), 65536uL);
		ulong embeddedCntOffset = 65536 + num3;
		byte[] array3 = BuildHeaderBlock(variant, num3, embeddedCntOffset, array2, nestedImageDigest, nestedImageSize, nestedMetaBaseBlocks, contentVersionHi, innerContentInodes, appFileCount, metaBlockCount, metaBlockCountMirror, emptyFileCount, sparseAfidHoleCount);
		using MemoryStream memoryStream = new MemoryStream(checked((int)(65536 + (long)num3 + num5 + (siSegment?.Length ?? 0))));
		memoryStream.Write(array3);
		memoryStream.Write(array2);
		memoryStream.Write(array);
		if (siSegment != null && siSegment.Length > 0)
		{
			memoryStream.Write(siSegment);
		}
		return memoryStream.ToArray();
	}

	public static void BuildFromCntToFile(string cntPath, string outputPath, FihVariant variant, int outerSuperblockIndex, byte[]? nestedImageDigest, long nestedImageSize, long nestedMetaBaseBlocks, uint contentVersionHi, int innerContentInodes, int appFileCount, int metaBlockCount, int metaBlockCountMirror, int emptyFileCount, int sparseAfidHoleCount, Func<Stream, long, byte[]>? siFactory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cntPath, "cntPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		using FileStream fileStream = new FileStream(Path.GetFullPath(cntPath), FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
		byte[] array = new byte[4096];
		if (!ReadFullyAt(fileStream, 0L, array))
		{
			throw new InvalidDataException("CNT container is too small.");
		}
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(32));
		ulong num2 = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(40));
		ulong num3 = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(1048));
		if (num == 0L || num3 == 0L)
		{
			throw new InvalidDataException("CNT container has no embedded PFS image to finalize.");
		}
		long num4 = checked((long)num + (long)num2);
		if (num4 > int.MaxValue)
		{
			throw new NotSupportedException($"The CNT metadata region ({num4} bytes) exceeds the in-memory limit.");
		}
		if (num4 + (long)num3 > fileStream.Length)
		{
			throw new InvalidDataException("CNT container has no embedded PFS image to finalize.");
		}
		byte[] array2 = new byte[num4];
		if (!ReadFullyAt(fileStream, 0L, array2))
		{
			throw new InvalidDataException("Failed to read the CNT metadata region.");
		}
		BinaryPrimitives.WriteUInt64BigEndian(array2.AsSpan(1040), 65536uL);
		long num5 = ((outerSuperblockIndex >= 0) ? ((long)outerSuperblockIndex * 65536L) : LocateSuperblock(fileStream, num4, (long)num3));
		if (num5 < 0 || num5 + 65536 > (long)num3)
		{
			throw new InvalidDataException("Plaintext outer superblock not found in the embedded PFS image.");
		}
		byte[] array3 = new byte[65536];
		if (!ReadFullyAt(fileStream, num4 + num5, array3))
		{
			throw new InvalidDataException("Failed to read the outer superblock block.");
		}
		ulong embeddedCntOffset = 65536 + num3;
		byte[] array4 = BuildHeaderBlockFromSuperblock(variant, num3, embeddedCntOffset, array3, num5, nestedImageDigest, nestedImageSize, nestedMetaBaseBlocks, contentVersionHi, innerContentInodes, appFileCount, metaBlockCount, metaBlockCountMirror, emptyFileCount, sparseAfidHoleCount);
		string fullPath = Path.GetFullPath(outputPath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		using FileStream fileStream2 = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.None);
		fileStream2.Write(array4, 0, array4.Length);
		fileStream.Position = num4;
		CopyBytes(fileStream, fileStream2, (long)num3);
		fileStream2.Write(array2, 0, array2.Length);
		fileStream2.Flush();
		long length = fileStream2.Length;
		if (siFactory != null)
		{
			byte[] array5 = siFactory(fileStream2, length);
			if (array5 != null && array5.Length > 0)
			{
				fileStream2.Position = length;
				fileStream2.Write(array5, 0, array5.Length);
			}
		}
		fileStream2.Flush();
	}

	public static void FinalizePrepositionedToFile(byte[] cntMetadata, string outputPath, long pfsImageSize, FihVariant variant, int outerSuperblockIndex, byte[] superblockBlock, byte[]? nestedImageDigest, long nestedImageSize, long nestedMetaBaseBlocks, uint contentVersionHi, int innerContentInodes, int appFileCount, int metaBlockCount, int metaBlockCountMirror, int emptyFileCount, int sparseAfidHoleCount, Func<Stream, long, byte[]>? siFactory)
	{
		ArgumentNullException.ThrowIfNull(cntMetadata, "cntMetadata");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		ArgumentNullException.ThrowIfNull(superblockBlock, "superblockBlock");
		if (superblockBlock.Length < 65536)
		{
			throw new ArgumentException("The outer superblock block is too small.", "superblockBlock");
		}
		BinaryPrimitives.WriteUInt64BigEndian(cntMetadata.AsSpan(1040), 65536uL);
		long superblockOffset = (long)outerSuperblockIndex * 65536L;
		ulong embeddedCntOffset = (ulong)(65536 + pfsImageSize);
		byte[] array = BuildHeaderBlockFromSuperblock(variant, (ulong)pfsImageSize, embeddedCntOffset, superblockBlock, superblockOffset, nestedImageDigest, nestedImageSize, nestedMetaBaseBlocks, contentVersionHi, innerContentInodes, appFileCount, metaBlockCount, metaBlockCountMirror, emptyFileCount, sparseAfidHoleCount);
		using FileStream fileStream = new FileStream(Path.GetFullPath(outputPath), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.None);
		fileStream.Position = 0L;
		fileStream.Write(array, 0, array.Length);
		fileStream.Position = 65536 + pfsImageSize;
		fileStream.Write(cntMetadata, 0, cntMetadata.Length);
		fileStream.Flush();
		long length = fileStream.Length;
		if (siFactory != null)
		{
			byte[] array2 = siFactory(fileStream, length);
			if (array2 != null && array2.Length > 0)
			{
				fileStream.Position = length;
				fileStream.Write(array2, 0, array2.Length);
			}
		}
		fileStream.Flush();
	}

	public static int LocateSuperblock(Stream stream, long start, long length, int blockSize = 65536)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		if (blockSize <= 16 || length < blockSize)
		{
			return -1;
		}
		byte[] array = new byte[blockSize];
		long num = start + length;
		for (long num2 = start; num2 + blockSize <= num && ReadFullyAt(stream, num2, array); num2 += blockSize)
		{
			if (BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(0, 8)) == 2 && ((ReadOnlySpan<byte>)array.AsSpan(8, 4)).SequenceEqual((ReadOnlySpan<byte>)new byte[4] { 11, 42, 51, 1 }))
			{
				return (int)(num2 - start);
			}
		}
		return -1;
	}

	private static void CopyBytes(Stream source, Stream destination, long count)
	{
		byte[] array = new byte[1048576];
		long num = count;
		while (num > 0)
		{
			int count2 = (int)Math.Min(array.Length, num);
			int num2 = source.Read(array, 0, count2);
			if (num2 <= 0)
			{
				throw new EndOfStreamException("Unexpected end of the source while copying the embedded PFS image.");
			}
			destination.Write(array, 0, num2);
			num -= num2;
		}
	}

	private static bool ReadFullyAt(Stream stream, long offset, byte[] buffer)
	{
		stream.Position = offset;
		int num;
		for (int i = 0; i < buffer.Length; i += num)
		{
			num = stream.Read(buffer, i, buffer.Length - i);
			if (num <= 0)
			{
				return false;
			}
		}
		return true;
	}

	public static int LocateSuperblock(ReadOnlySpan<byte> image, int blockSize = 65536)
	{
		if (blockSize <= 16)
		{
			return -1;
		}
		for (int i = 0; i + blockSize <= image.Length; i += blockSize)
		{
			if (BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(i, 8)) == 2 && image.Slice(i + 8, 4).SequenceEqual(new byte[4] { 11, 42, 51, 1 }))
			{
				return i;
			}
		}
		return -1;
	}
}
