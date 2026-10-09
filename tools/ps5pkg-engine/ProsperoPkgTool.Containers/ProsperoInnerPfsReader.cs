using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProsperoPkgTool.Containers;

public static class ProsperoInnerPfsReader
{
	public sealed record Entry(string Path, bool IsDirectory, long LogicalOffset, long Size, uint Inode);

	private readonly record struct Inode(bool IsDirectory, long Size, ulong Offset)
	{
		public long LogicalOffset
		{
			get
			{
				if (Offset <= long.MaxValue)
				{
					return (long)Offset;
				}
				throw new InvalidDataException("Inner PFS offset is invalid.");
			}
		}
	}

	public const int BlockSize = 65536;

	private const int InodeSize = 168;

	private const int InodesPerBlock = 390;

	private const long SuperblockMagic = 20130315L;

	public static IReadOnlyList<Entry> Enumerate(ReadOnlySpan<byte> mount, bool repairTruncatedOffsets = false)
	{
		bool repaired;
		return Enumerate(mount, repairTruncatedOffsets, out repaired);
	}

	public static IReadOnlyList<Entry> Enumerate(ReadOnlySpan<byte> mount, bool repairTruncatedOffsets, out bool repaired)
	{
		int num = FindSuperblock(mount);
		int num2 = CheckedInt(BinaryPrimitives.ReadInt64LittleEndian(mount.Slice(num + 48, 8)), "inode count");
		long num3 = num + 65536 + (long)InodeBlockCount(num2) * 65536L;
		bool flag = ((num2 < 1 || num2 > 65536) ? true : false);
		if (flag || num3 > mount.Length)
		{
			throw new InvalidDataException("Inner PFS inode table is outside the reconstructed mount.");
		}
		Inode[] array = new Inode[num2];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = ReadInode(mount.Slice(InodeOffset(num, i), 168));
		}
		repaired = RepairOrThrowTruncated(array, num, InodeBlockCount(num2), mount.Length, repairTruncatedOffsets);
		List<Entry> result = new List<Entry>();
		WalkDirectory(mount, array, 0u, string.Empty, result, new HashSet<uint>());
		return result;
	}

	public static byte[] ReadFile(ReadOnlySpan<byte> mount, Entry entry)
	{
		if (entry.IsDirectory)
		{
			throw new InvalidDataException("'" + entry.Path + "' is a directory.");
		}
		if (entry.LogicalOffset < 0 || entry.Size < 0 || entry.LogicalOffset > mount.Length || entry.Size > mount.Length - entry.LogicalOffset)
		{
			throw new InvalidDataException("Inner PFS file '" + entry.Path + "' lies outside the reconstructed mount.");
		}
		return checked(mount.Slice((int)entry.LogicalOffset, (int)entry.Size)).ToArray();
	}

	public static IReadOnlyList<Entry> Enumerate(Stream mount, long superblockHint = -1L, bool repairTruncatedOffsets = false)
	{
		bool repaired;
		return Enumerate(mount, superblockHint, repairTruncatedOffsets, out repaired);
	}

	public static IReadOnlyList<Entry> Enumerate(Stream mount, long superblockHint, bool repairTruncatedOffsets, out bool repaired)
	{
		ArgumentNullException.ThrowIfNull(mount, "mount");
		if (!mount.CanSeek)
		{
			throw new ArgumentException("The inner mount stream must be seekable.", "mount");
		}
		long length = mount.Length;
		long num = FindSuperblock(mount, length, superblockHint);
		long num2 = ReadInt64(mount, num + 48);
		if ((num2 < 1 || num2 > 65536) ? true : false)
		{
			throw new InvalidDataException("Inner PFS inode table is outside the reconstructed mount.");
		}
		int num3 = (int)num2;
		long num4 = num + 65536;
		if (num4 + (long)InodeBlockCount(num3) * 65536L > length)
		{
			throw new InvalidDataException("Inner PFS inode table is outside the reconstructed mount.");
		}
		Inode[] array = new Inode[num3];
		Span<byte> span = stackalloc byte[168];
		for (int i = 0; i < num3; i++)
		{
			mount.Position = num4 + (long)(i / 390) * 65536L + (long)(i % 390) * 168L;
			mount.ReadExactly(span);
			array[i] = ReadInode(span);
		}
		repaired = RepairOrThrowTruncated(array, num, InodeBlockCount(num3), length, repairTruncatedOffsets);
		List<Entry> result = new List<Entry>();
		WalkDirectory(mount, length, array, 0u, string.Empty, result, new HashSet<uint>());
		return result;
	}

	public static void ExtractFile(Stream mount, Entry entry, Stream destination)
	{
		ArgumentNullException.ThrowIfNull(mount, "mount");
		ArgumentNullException.ThrowIfNull(destination, "destination");
		if (entry.IsDirectory)
		{
			throw new InvalidDataException("'" + entry.Path + "' is a directory.");
		}
		if (entry.LogicalOffset < 0 || entry.Size < 0 || entry.LogicalOffset > mount.Length || entry.Size > mount.Length - entry.LogicalOffset)
		{
			throw new InvalidDataException("Inner PFS file '" + entry.Path + "' lies outside the reconstructed mount.");
		}
		mount.Position = entry.LogicalOffset;
		byte[] array = new byte[1048576];
		long num = entry.Size;
		while (num > 0)
		{
			int count = (int)Math.Min(array.Length, num);
			int num2 = mount.Read(array, 0, count);
			if (num2 <= 0)
			{
				throw new InvalidDataException("Inner PFS file '" + entry.Path + "' ended before its declared size.");
			}
			destination.Write(array, 0, num2);
			num -= num2;
		}
	}

	private static long FindSuperblock(Stream mount, long length, long hint)
	{
		Span<byte> probe = stackalloc byte[40];
		if (hint >= 0 && hint + 65536 <= length && ProbeIsSuperblock(mount, hint, probe))
		{
			return hint;
		}
		for (long num = 0L; num + 65536 <= length; num += 65536)
		{
			if (ProbeIsSuperblock(mount, num, probe))
			{
				return num;
			}
		}
		throw new InvalidDataException("No supported inner PFSv2 superblock was found in the reconstructed mount.");
	}

	private static bool ProbeIsSuperblock(Stream mount, long offset, Span<byte> probe)
	{
		mount.Position = offset;
		int num;
		for (int i = 0; i < probe.Length; i += num)
		{
			num = mount.Read(probe.Slice(i));
			if (num <= 0)
			{
				return false;
			}
		}
		if (BinaryPrimitives.ReadInt64LittleEndian(probe.Slice(0, 8)) == 2 && BinaryPrimitives.ReadInt64LittleEndian(probe.Slice(8, 8)) == 20130315)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(probe.Slice(32, 4)) == 65536;
		}
		return false;
	}

	private static void WalkDirectory(Stream mount, long mountLength, Inode[] inodes, uint inodeIndex, string prefix, List<Entry> result, HashSet<uint> active)
	{
		if (inodeIndex >= inodes.Length)
		{
			throw new InvalidDataException("Inner PFS directory references an invalid inode.");
		}
		if (!active.Add(inodeIndex))
		{
			throw new InvalidDataException("Inner PFS directory graph contains a cycle.");
		}
		Inode inode = inodes[inodeIndex];
		if (!inode.IsDirectory)
		{
			throw new InvalidDataException("Inner PFS directory entry points to a regular inode.");
		}
		long logicalOffset = inode.LogicalOffset;
		if (logicalOffset < 0 || inode.Size < 0 || logicalOffset > mountLength || inode.Size > mountLength - logicalOffset)
		{
			throw new InvalidDataException("Inner PFS directory bytes lie outside the reconstructed mount.");
		}
		if (inode.Size > int.MaxValue)
		{
			throw new InvalidDataException("Inner PFS directory is too large to enumerate.");
		}
		byte[] array = new byte[(int)inode.Size];
		mount.Position = logicalOffset;
		mount.ReadExactly(array);
		List<(uint, string)> list = new List<(uint, string)>();
		int num = 0;
		while (num + 16 <= array.Length)
		{
			uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(num, 4));
			int num3 = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num + 4, 4));
			int num4 = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num + 8, 4));
			int num5 = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(num + 12, 4));
			if (num5 == 0)
			{
				break;
			}
			if (num5 < 16 || num5 > array.Length - num || num4 < 0 || num4 > num5 - 16)
			{
				throw new InvalidDataException("Inner PFS directory record is malformed.");
			}
			string text = Encoding.ASCII.GetString(array.AsSpan(num + 16, num4));
			num += num5;
			if ((!(text == ".") && !(text == "..")) || 1 == 0)
			{
				if (text.Length == 0 || text.Contains('/') || num2 >= inodes.Length)
				{
					throw new InvalidDataException("Inner PFS directory contains an invalid name or inode.");
				}
				string text2 = prefix + "/" + text;
				Inode inode2 = inodes[num2];
				bool flag = num3 == 3;
				if (flag != inode2.IsDirectory)
				{
					throw new InvalidDataException("Inner PFS dirent type disagrees with its inode.");
				}
				if (flag)
				{
					list.Add((num2, text2));
				}
				else
				{
					result.Add(new Entry(text2, IsDirectory: false, inode2.LogicalOffset, inode2.Size, num2));
				}
			}
		}
		foreach (var item3 in list)
		{
			uint item = item3.Item1;
			string item2 = item3.Item2;
			Inode inode3 = inodes[item];
			result.Add(new Entry(item2, IsDirectory: true, inode3.LogicalOffset, inode3.Size, item));
			WalkDirectory(mount, mountLength, inodes, item, item2, result, active);
		}
		active.Remove(inodeIndex);
	}

	private static long ReadInt64(Stream stream, long offset)
	{
		Span<byte> span = stackalloc byte[8];
		stream.Position = offset;
		stream.ReadExactly(span);
		return BinaryPrimitives.ReadInt64LittleEndian(span);
	}

	private static int FindSuperblock(ReadOnlySpan<byte> mount)
	{
		for (int i = 0; i + 65536 <= mount.Length; i += 65536)
		{
			if (BinaryPrimitives.ReadInt64LittleEndian(mount.Slice(i, 8)) == 2 && BinaryPrimitives.ReadInt64LittleEndian(mount.Slice(i + 8, 8)) == 20130315 && BinaryPrimitives.ReadUInt32LittleEndian(mount.Slice(i + 32, 4)) == 65536)
			{
				return i;
			}
		}
		throw new InvalidDataException("No supported inner PFSv2 superblock was found in the reconstructed mount.");
	}

	private static void WalkDirectory(ReadOnlySpan<byte> mount, Inode[] inodes, uint inodeIndex, string prefix, List<Entry> result, HashSet<uint> active)
	{
		if (inodeIndex >= inodes.Length)
		{
			throw new InvalidDataException("Inner PFS directory references an invalid inode.");
		}
		if (!active.Add(inodeIndex))
		{
			throw new InvalidDataException("Inner PFS directory graph contains a cycle.");
		}
		Inode inode = inodes[inodeIndex];
		if (!inode.IsDirectory)
		{
			throw new InvalidDataException("Inner PFS directory entry points to a regular inode.");
		}
		if (inode.LogicalOffset > mount.Length || inode.Size > mount.Length - inode.LogicalOffset)
		{
			throw new InvalidDataException("Inner PFS directory bytes lie outside the reconstructed mount.");
		}
		ReadOnlySpan<byte> readOnlySpan = checked(mount.Slice((int)inode.LogicalOffset, (int)inode.Size));
		List<(uint, string)> list = new List<(uint, string)>();
		int num = 0;
		while (num + 16 <= readOnlySpan.Length)
		{
			uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(readOnlySpan.Slice(num, 4));
			int num3 = BinaryPrimitives.ReadInt32LittleEndian(readOnlySpan.Slice(num + 4, 4));
			int num4 = BinaryPrimitives.ReadInt32LittleEndian(readOnlySpan.Slice(num + 8, 4));
			int num5 = BinaryPrimitives.ReadInt32LittleEndian(readOnlySpan.Slice(num + 12, 4));
			if (num5 == 0)
			{
				break;
			}
			if (num5 < 16 || num5 > readOnlySpan.Length - num || num4 < 0 || num4 > num5 - 16)
			{
				throw new InvalidDataException("Inner PFS directory record is malformed.");
			}
			string text = Encoding.ASCII.GetString(readOnlySpan.Slice(num + 16, num4));
			num += num5;
			if ((!(text == ".") && !(text == "..")) || 1 == 0)
			{
				if (text.Length == 0 || text.Contains('/') || num2 >= inodes.Length)
				{
					throw new InvalidDataException("Inner PFS directory contains an invalid name or inode.");
				}
				string text2 = prefix + "/" + text;
				Inode inode2 = inodes[num2];
				bool flag = num3 == 3;
				if (flag != inode2.IsDirectory)
				{
					throw new InvalidDataException("Inner PFS dirent type disagrees with its inode.");
				}
				if (flag)
				{
					list.Add((num2, text2));
				}
				else
				{
					result.Add(new Entry(text2, IsDirectory: false, inode2.LogicalOffset, inode2.Size, num2));
				}
			}
		}
		foreach (var item3 in list)
		{
			uint item = item3.Item1;
			string item2 = item3.Item2;
			Inode inode3 = inodes[item];
			result.Add(new Entry(item2, IsDirectory: true, inode3.LogicalOffset, inode3.Size, item));
			WalkDirectory(mount, inodes, item, item2, result, active);
		}
		active.Remove(inodeIndex);
	}

	private static Inode ReadInode(ReadOnlySpan<byte> source)
	{
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(source);
		bool isDirectory = ((num == 16744 || num == 16749) ? true : false);
		return new Inode(isDirectory, BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8, 8)), BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(96, 8)));
	}

	private static int InodeBlockCount(int inodeCount)
	{
		return (inodeCount + 390 - 1) / 390;
	}

	private static int InodeOffset(int superblock, int index)
	{
		return superblock + 65536 + index / 390 * 65536 + index % 390 * 168;
	}

	private static bool RepairOrThrowTruncated(Inode[] inodes, long superblock, int inodeBlockCount, long mountLength, bool repair)
	{
		long num = superblock + (long)(1 + inodeBlockCount) * 65536L;
		if (num <= uint.MaxValue)
		{
			return false;
		}
		ulong num2 = (ulong)(num & -4294967296L);
		bool result = false;
		for (int i = 0; i < inodes.Length; i++)
		{
			if (!inodes[i].IsDirectory)
			{
				continue;
			}
			ulong offset = inodes[i].Offset;
			if (offset < (ulong)num)
			{
				if (!repair)
				{
					throw new InvalidDataException($"Inner PFS inode {i} DataOffset 0x{offset:X} lies below the inner metadata region 0x{num:X}: the inode's 64-bit offset was truncated to 32 bits (offsets " + ">= 4 GiB are lost). This package was produced by a generator with a 64-bit inode offset-write bug. Re-run img_file_list with --repair-offsets to reconstruct the directory offsets for listing.");
				}
				ulong num3 = num2 | (offset & 0xFFFFFFFFu);
				if (num3 < (ulong)num || num3 >= (ulong)mountLength)
				{
					throw new InvalidDataException($"Inner PFS inode {i} DataOffset 0x{offset:X} cannot be repaired: the reconstructed offset 0x{num3:X} lies outside the inner metadata region.");
				}
				inodes[i] = inodes[i]with
				{
					Offset = num3
				};
				result = true;
			}
		}
		return result;
	}

	private static int CheckedInt(long value, string label)
	{
		if ((value >= 0 && value <= int.MaxValue) || 1 == 0)
		{
			return (int)value;
		}
		throw new InvalidDataException("Inner PFS " + label + " is invalid.");
	}
}
