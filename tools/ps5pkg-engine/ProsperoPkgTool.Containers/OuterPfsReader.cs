using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public sealed class OuterPfsReader
{
	private sealed class S32Inode
	{
		public ushort Mode;

		public uint Flags;

		public long Size;

		public long SizeCompressed;

		public uint Blocks;

		public int[] DirectBlocks = Array.Empty<int>();

		public int[] IndirectBlocks = Array.Empty<int>();
	}

	private readonly record struct OuterDirent(uint Inode, uint Type, string Name, uint EntSize);

	public const long BlockSize = 65536L;

	public const int SuperblockSeedOffset = 880;

	public const int SuperblockIcvOffset = 896;

	public const int SuperblockIcvLength = 32;

	public const int SuperblockIcvRegionLength = 1440;

	private const long SuperblockMagic = 20130315L;

	private const ulong SignedSectorFlag = 140737488355328uL;

	private static readonly byte[] PlainNoAuthSeed = "PPRPLAIN-NOAUTH!"u8.ToArray();

	private const int S32InodeSize = 712;

	private const int S32DirectBlockOffset = 100;

	private const int S32IndirectBlockOffset = 532;

	private const int BlockSig32Stride = 36;

	private const int BlockIndexOffsetInSig = 32;

	private const int InodeSigSize = 784;

	private const int InodeSigIndirectOffset = 580;

	private readonly Stream _stream;

	private readonly long _pfsOffset;

	private readonly long _pfsSize;

	public int SuperblockIndex { get; }

	public int BlockCount { get; }

	public byte[] Seed { get; }

	public byte[] Superblock { get; }

	public bool IsPlainNoAuth { get; }

	public long InnerImageLength => (long)Math.Max(0, SuperblockIndex - 1) * 65536L;

	private OuterPfsReader(Stream stream, long pfsOffset, long pfsSize, int superblockIndex, byte[] superblock, byte[] seed)
	{
		_stream = stream;
		_pfsOffset = pfsOffset;
		_pfsSize = pfsSize;
		SuperblockIndex = superblockIndex;
		BlockCount = (int)(pfsSize / 65536);
		Superblock = superblock;
		Seed = seed;
		IsPlainNoAuth = ((ReadOnlySpan<byte>)seed.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)PlainNoAuthSeed);
	}

	public static OuterPfsReader Open(Stream stream, long pfsOffset, long pfsSize, int superblockIndexHint = -1)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		if (pfsOffset < 0 || pfsSize < 65536)
		{
			throw new ArgumentOutOfRangeException("pfsSize", "PFS region is too small to contain a superblock.");
		}
		int num = (int)(pfsSize / 65536);
		byte[] array = new byte[65536];
		if (superblockIndexHint >= 0 && superblockIndexHint < num)
		{
			ReadBlockRaw(stream, pfsOffset, superblockIndexHint, array);
			if (BinaryPrimitives.ReadInt64LittleEndian(array.AsSpan(8, 8)) == 20130315)
			{
				byte[] seed = array.AsSpan(880, 16).ToArray();
				return new OuterPfsReader(stream, pfsOffset, pfsSize, superblockIndexHint, (byte[])array.Clone(), seed);
			}
		}
		for (int i = 0; i < num; i++)
		{
			ReadBlockRaw(stream, pfsOffset, i, array);
			if (BinaryPrimitives.ReadInt64LittleEndian(array.AsSpan(8, 8)) == 20130315)
			{
				byte[] seed2 = array.AsSpan(880, 16).ToArray();
				return new OuterPfsReader(stream, pfsOffset, pfsSize, i, (byte[])array.Clone(), seed2);
			}
		}
		throw new InvalidDataException("No plaintext PFS superblock found in the outer image.");
	}

	public byte[] ReadBlock(AesXts xts, int blockIndex)
	{
		return ReadBlock(xts, blockIndex, signed: true);
	}

	public byte[] ReadBlock(AesXts xts, int blockIndex, bool signed)
	{
		ArgumentNullException.ThrowIfNull(xts, "xts");
		if (blockIndex < 0 || blockIndex >= BlockCount)
		{
			throw new ArgumentOutOfRangeException("blockIndex");
		}
		byte[] array = new byte[65536];
		ReadBlockRaw(_stream, _pfsOffset, blockIndex, array);
		if (blockIndex == SuperblockIndex)
		{
			return array;
		}
		if (IsPlainNoAuth)
		{
			return array;
		}
		ulong sector = (signed ? ((ulong)(uint)blockIndex | 0x800000000000uL) : ((uint)blockIndex));
		return xts.Decrypt(array, sector);
	}

	public byte[] ReadInnerImage(AesXts xts)
	{
		int num = Math.Max(0, SuperblockIndex - 1);
		long num2 = (long)num * 65536L;
		if (num2 > int.MaxValue)
		{
			throw new InvalidDataException($"pfs_image.dat ({num2} bytes) exceeds the in-memory reader limit; use the file-backed reader.");
		}
		using MemoryStream memoryStream = new MemoryStream((int)num2);
		for (int i = 0; i < num; i++)
		{
			byte[] array = ReadBlock(xts, i, signed: false);
			memoryStream.Write(array, 0, array.Length);
		}
		return memoryStream.ToArray();
	}

	public byte[] ReadInnerImageRegion(AesXts xts, long offset, int length)
	{
		ArgumentNullException.ThrowIfNull(xts, "xts");
		if (offset < 0 || length < 0 || offset + length > InnerImageLength)
		{
			throw new InvalidDataException($"Inner-image region 0x{offset:X}+0x{length:X} is outside pfs_image.dat.");
		}
		if (length == 0)
		{
			return Array.Empty<byte>();
		}
		int num;
		int num2;
		byte[] array;
		int num3;
		checked
		{
			num = (int)unchecked(offset / 65536);
			num2 = (int)unchecked(checked(offset + length - 1) / 65536);
			array = new byte[length];
			num3 = 0;
		}
		for (int i = num; i <= num2; i++)
		{
			byte[] sourceArray = ReadBlock(xts, i, signed: false);
			int num4 = (int)((i == num) ? (offset - (long)i * 65536L) : 0);
			int num5 = (int)Math.Min(65536L - (long)num4, length - num3);
			Array.Copy(sourceArray, num4, array, num3, num5);
			num3 += num5;
		}
		return array;
	}

	public byte[] ReadInnerImageBlock(AesXts xts, int blockIndex)
	{
		if (blockIndex < 0 || blockIndex >= Math.Max(0, SuperblockIndex - 1))
		{
			throw new ArgumentOutOfRangeException("blockIndex", $"Block index {blockIndex} is outside the pfs_image.dat data region (0 .. {Math.Max(0, SuperblockIndex - 1) - 1}).");
		}
		return ReadBlock(xts, blockIndex, signed: false);
	}

	public bool VerifySuperblockIcv()
	{
		if (IsPlainNoAuth)
		{
			return true;
		}
		byte[] array = Superblock.AsSpan(0, 1440).ToArray();
		Array.Clear(array, 896, 32);
		byte[] array2 = Sha3.Sha3_256(array);
		return ((ReadOnlySpan<byte>)Superblock.AsSpan(896, 32)).SequenceEqual((ReadOnlySpan<byte>)array2);
	}

	public IReadOnlyDictionary<string, OuterFile> EnumerateOuterFiles(AesXts xts)
	{
		S32Inode[] array = ReadInodeTable(xts);
		List<OuterDirent> list = ReadDirectory(xts, array[2]);
		Dictionary<string, OuterFile> dictionary = new Dictionary<string, OuterFile>(StringComparer.OrdinalIgnoreCase);
		foreach (OuterDirent item in list)
		{
			if (!(item.Name == ".") && !(item.Name == ".."))
			{
				S32Inode s32Inode = array[item.Inode];
				bool signed = !string.Equals(item.Name, "pfs_image.dat", StringComparison.OrdinalIgnoreCase);
				dictionary[item.Name] = new OuterFile(item.Name, s32Inode.Size, s32Inode.SizeCompressed, ResolveBlocks(xts, s32Inode), signed);
			}
		}
		return dictionary;
	}

	public byte[] ReadNaps(AesXts xts)
	{
		if (!EnumerateOuterFiles(xts).TryGetValue("naps_pkg_layout.dat", out OuterFile value))
		{
			throw new InvalidDataException("Outer PFS uroot is missing naps_pkg_layout.dat.");
		}
		return ReadFileBytes(xts, value);
	}

	private S32Inode[] ReadInodeTable(AesXts xts)
	{
		ReadOnlySpan<byte> readOnlySpan = Superblock.AsSpan(80, 784);
		int num = 0;
		for (int i = 0; i < 5; i++)
		{
			if (BinaryPrimitives.ReadInt64LittleEndian(readOnlySpan.Slice(580 + i * 40 + 8, 8)) > 0)
			{
				num++;
			}
		}
		int num2;
		int num3;
		checked
		{
			num2 = (int)BinaryPrimitives.ReadInt64LittleEndian(Superblock.AsSpan(48, 8));
			num3 = (int)BinaryPrimitives.ReadInt64LittleEndian(Superblock.AsSpan(64, 8));
		}
		int num4 = SuperblockIndex + 1 + num;
		byte[] array = new byte[num3 * 65536];
		for (int j = 0; j < num3; j++)
		{
			ReadBlock(xts, num4 + j, signed: true).CopyTo(array, j * 65536);
		}
		S32Inode[] array2 = new S32Inode[num2];
		for (int k = 0; k < num2; k++)
		{
			ReadOnlySpan<byte> source = array.AsSpan(k * 712, 712);
			array2[k] = new S32Inode
			{
				Mode = BinaryPrimitives.ReadUInt16LittleEndian(source),
				Flags = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4)),
				Size = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8)),
				SizeCompressed = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16)),
				Blocks = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(96)),
				DirectBlocks = ReadBlockSigs32(source.Slice(100, 432), 12),
				IndirectBlocks = ReadBlockSigs32(source.Slice(532, 180), 5)
			};
		}
		if (array2.Length <= 2)
		{
			throw new InvalidDataException("The outer PFS inode table is missing its uroot inode.");
		}
		S32Inode s32Inode = array2[2];
		if ((s32Inode.Mode & 0xF000) != 16384 || s32Inode.Size <= 0 || s32Inode.Size > (long)BlockCount * 65536L || s32Inode.Blocks == 0 || s32Inode.Blocks > (uint)BlockCount)
		{
			throw new InvalidDataException("The outer PFS inode table did not yield a valid uroot directory: the passcode is wrong, the outer-PFS layout is unsupported, or the package is corrupt.");
		}
		return array2;
	}

	private List<OuterDirent> ReadDirectory(AesXts xts, S32Inode inode)
	{
		byte[] array = ReadFileBytes(xts, inode, signed: true);
		int num = (int)Math.Min(inode.Size, array.Length);
		List<OuterDirent> list = new List<OuterDirent>();
		uint num2;
		for (int i = 0; i + 16 <= num; i += (int)num2)
		{
			ReadOnlySpan<byte> source = array.AsSpan(i, 16);
			uint inode2 = BinaryPrimitives.ReadUInt32LittleEndian(source);
			uint type = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4));
			uint count = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8));
			num2 = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(12));
			if (num2 < 16 || i + (int)num2 > num)
			{
				break;
			}
			string name = Encoding.ASCII.GetString(array, i + 16, (int)count);
			list.Add(new OuterDirent(inode2, type, name, num2));
		}
		return list;
	}

	private int[] ResolveBlocks(AesXts xts, S32Inode inode)
	{
		if (inode.Blocks > (uint)Math.Max(0, BlockCount))
		{
			throw new InvalidDataException($"Outer inode declares {inode.Blocks} blocks, more than the image's {BlockCount} blocks: " + "the passcode is wrong, the outer-PFS layout is unsupported, or the package is corrupt.");
		}
		int blocks = (int)inode.Blocks;
		if (blocks < 0)
		{
			throw new InvalidDataException("Inode block count is negative.");
		}
		List<int> list = new List<int>(blocks);
		for (int i = 0; i < 12; i++)
		{
			if (list.Count >= blocks)
			{
				break;
			}
			list.Add(inode.DirectBlocks[i]);
		}
		for (int j = 0; j < 5; j++)
		{
			if (list.Count >= blocks)
			{
				break;
			}
			int num = inode.IndirectBlocks[j];
			if (num <= 0)
			{
				continue;
			}
			ProsperoOuterPfsBuilder.CollectIndirectTree(num, j + 1, blocks, (long blockIndex) =>
			{
				if (blockIndex < 0 || blockIndex >= BlockCount)
				{
					throw new InvalidDataException("An outer inode references an out-of-range metadata block: the passcode is wrong, the outer-PFS layout is unsupported, or the package is corrupt.");
				}
				return ReadBlock(xts, (int)blockIndex, signed: true);
			}, list);
		}
		if (list.Count < blocks)
		{
			throw new InvalidDataException($"Outer inode resolves {list.Count} blocks but declares {blocks}; the indirect tree is incomplete.");
		}
		return list.ToArray();
	}

	private byte[] ReadFileBytes(AesXts xts, OuterFile file)
	{
		int[] blockIndexes = file.BlockIndexes;
		long num = Math.Min(file.Size, (long)blockIndexes.Length * 65536L);
		if (num > int.MaxValue)
		{
			throw new InvalidDataException($"File '{file.Name}' is too large to read into memory ({num} bytes).");
		}
		byte[] array = new byte[(int)num];
		int num2 = 0;
		int[] array2 = blockIndexes;
		foreach (int blockIndex in array2)
		{
			byte[] array3 = ReadBlock(xts, blockIndex, file.Signed);
			int num3 = Math.Min(array3.Length, (int)(num - num2));
			if (num3 <= 0)
			{
				break;
			}
			Array.Copy(array3, 0, array, num2, num3);
			num2 += num3;
		}
		return array;
	}

	private byte[] ReadFileBytes(AesXts xts, S32Inode inode, bool signed)
	{
		int[] blockIndexes = ResolveBlocks(xts, inode);
		return ReadFileBytes(xts, new OuterFile("", inode.Size, inode.SizeCompressed, blockIndexes, signed));
	}

	private static int[] ReadBlockSigs32(ReadOnlySpan<byte> region, int count)
	{
		int[] array = new int[count];
		for (int i = 0; i < count; i++)
		{
			array[i] = BinaryPrimitives.ReadInt32LittleEndian(region.Slice(i * 36 + 32, 4));
		}
		return array;
	}

	private static void ReadBlockRaw(Stream stream, long pfsOffset, int blockIndex, byte[] buffer)
	{
		stream.Position = pfsOffset + (long)blockIndex * 65536L;
		stream.ReadExactly(buffer, 0, 65536);
	}
}
