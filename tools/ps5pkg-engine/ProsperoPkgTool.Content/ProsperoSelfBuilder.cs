using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ProsperoPkgTool.Content;

public static class ProsperoSelfBuilder
{
	private readonly record struct SelectedSegment(int ProgramHeaderIndex, int FileOffset, int FileSize);

	public const uint Magic = 4009038932u;

	public const uint LegacyOrbisMagic = 490542415u;

	private const int ContainerHeaderSize = 32;

	private const int SegmentEntrySize = 32;

	private const int ExtInfoSize = 64;

	private const int ControlRegionSize = 48;

	private const int MetaBlockSize = 80;

	private const int MetaSignatureSize = 544;

	private const int DigestSize = 32;

	private const int SignedBlockSize = 16384;

	private const int FooterMarkerOffset = 48;

	private const uint DefaultProgramType = 268435713u;

	private const int ElfHeaderSize = 64;

	private const int ElfProgramHeaderSize = 56;

	private const int ExInfoByteOffset = 16128;

	private const int SectionHeaderSize = 64;

	private const uint PtLoad = 1u;

	private const uint PtModuleData = 1627389952u;

	private const uint PtRelro = 1627389968u;

	private const uint PtComment = 1879047936u;

	public static bool IsSelf(ReadOnlySpan<byte> data)
	{
		if (data.Length < 32)
		{
			return false;
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(data);
		if (num == 490542415 || num == 4009038932u)
		{
			return true;
		}
		return false;
	}

	public static bool IsElf(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 64 && data[0] == 127 && data[1] == 69 && data[2] == 76)
		{
			return data[3] == 70;
		}
		return false;
	}

	public static byte[] MakeFself(byte[] elf, FselfOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(elf, "elf");
		if (options == null)
		{
			options = new FselfOptions();
		}
		if (!IsElf(elf))
		{
			throw new ArgumentException("Input is not an ELF file.", "elf");
		}
		if (elf[4] != 2)
		{
			throw new ArgumentException("Only 64-bit ELF modules are supported.", "elf");
		}
		ushort eType = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(16));
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(32));
		ushort num2 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(54));
		ushort num3 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(56));
		if (num != 64)
		{
			throw new ArgumentException($"The ELF program-header table must follow the ELF header at 0x{64:X} (e_phoff is 0x{num:X}).", "elf");
		}
		if (num2 != 56)
		{
			throw new ArgumentException($"Unexpected ELF program-header size {num2}.", "elf");
		}
		if (64 + (long)num3 * 56L > elf.Length)
		{
			throw new ArgumentException("ELF program headers overrun the file.", "elf");
		}
		List<SelectedSegment> list = SelectSegments(elf, 64, num3);
		if (list.Count == 0)
		{
			throw new ArgumentException("The ELF has no loadable segment content.", "elf");
		}
		int num4 = list.Count * 2;
		int num5 = 32 + num4 * 32;
		int num6 = 64 + num3 * 56;
		int num7 = AlignUp(num5 + num6, 16);
		int num8 = num7 + 64 + 48;
		int num9 = num4 * 80 + 80 + 544;
		int num10 = num8 + num9;
		if (num8 > 65535 || num9 > 65535)
		{
			throw new ArgumentException($"The ELF has too many segments to fake-sign (header 0x{num8:X}, meta 0x{num9:X} exceed the 16-bit container fields).", "elf");
		}
		int[] array = new int[list.Count];
		int[] array2 = new int[num4];
		int num11 = num10;
		for (int i = 0; i < list.Count; i++)
		{
			array[i] = (list[i].FileSize + 16384 - 1) / 16384 * 32;
			array2[i * 2] = num11;
			num11 = (array2[i * 2 + 1] = num11 + array[i]) + list[i].FileSize;
			if (i + 1 < list.Count)
			{
				num11 = AlignUp(num11, 16);
			}
		}
		int num12 = num11;
		ReadOnlySpan<byte> readOnlySpan = FindElfSection(elf, ".sceversion");
		byte[] array3 = null;
		byte[] array4 = null;
		ulong? sdkVersionOverride = options.SdkVersionOverride;
		if (sdkVersionOverride.HasValue)
		{
			ulong valueOrDefault = sdkVersionOverride.GetValueOrDefault();
			if (!readOnlySpan.IsEmpty)
			{
				byte[] array5 = readOnlySpan.ToArray();
				if (TryOverrideSceVersionRecords(array5, valueOrDefault))
				{
					array4 = array5;
				}
			}
			else if (!string.IsNullOrWhiteSpace(options.SceVersionName))
			{
				array3 = BuildSceVersionRecord(options.SceVersionName, BuildSdkVersionTuple(valueOrDefault));
			}
		}
		if (array3 == null && array4 == null && readOnlySpan.IsEmpty && !string.IsNullOrWhiteSpace(options.SceVersionName))
		{
			byte[] sceVersionRecord = options.SceVersionRecord;
			if (sceVersionRecord != null && sceVersionRecord.Length == 8)
			{
				array3 = BuildSceVersionRecord(options.SceVersionName, options.SceVersionRecord);
			}
		}
		ReadOnlySpan<byte> readOnlySpan2;
		if (array4 != null)
		{
			readOnlySpan2 = array4;
		}
		else
		{
			readOnlySpan2 = ((array3 != null) ? ((ReadOnlySpan<byte>)array3) : readOnlySpan);
		}
		byte[] array6 = new byte[num12 + readOnlySpan2.Length];
		Span<byte> span = array6;
		BinaryPrimitives.WriteUInt32LittleEndian(span, 4009038932u);
		span[4] = 16;
		span[5] = 1;
		span[6] = 1;
		span[7] = 18;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 268435713u);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12), (ushort)num8);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14), (ushort)num9);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(16), (ulong)num12);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(24), (ushort)num4);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(26), 50);
		for (int j = 0; j < list.Count; j++)
		{
			int entry = 32 + j * 2 * 32;
			int entry2 = 32 + (j * 2 + 1) * 32;
			ulong flags = (ulong)(((long)(j * 2 + 1) << 20) | 0x10004);
			WriteSegment(span, entry, flags, array2[j * 2], array[j], array[j]);
			ulong flags2 = (ulong)(((long)list[j].ProgramHeaderIndex << 20) | 0x2804);
			WriteSegment(span, entry2, flags2, array2[j * 2 + 1], list[j].FileSize, list[j].FileSize);
		}
		elf.AsSpan(0, num6).CopyTo(span.Slice(num5));
		ulong value = options.AuthorityId ?? DeriveAuthorityId(elf, eType);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num7), value);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num7 + 8), 1uL);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num7 + 16), options.AppVersion);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num7 + 24), options.FirmwareVersion);
		SHA256.HashData(elf).CopyTo(span.Slice(num7 + 32));
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(num7 + 64), 3uL);
		int start = num8 + num4 * 80 + 48;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(start), 65536u);
		for (int k = 0; k < list.Count; k++)
		{
			elf.AsSpan(list[k].FileOffset, list[k].FileSize).CopyTo(span.Slice(array2[k * 2 + 1]));
		}
		readOnlySpan2.CopyTo(span.Slice(num12));
		return array6;
	}

	public static bool TryGetSceVersionRecord(byte[] image, out byte[] record)
	{
		ArgumentNullException.ThrowIfNull(image, "image");
		ReadOnlySpan<byte> sceVersionRecords = GetSceVersionRecords(image);
		if (sceVersionRecords.Length >= 4)
		{
			int num = BinaryPrimitives.ReadUInt16LittleEndian(sceVersionRecords.Slice(2));
			if (num >= 17 && num + 4 <= sceVersionRecords.Length)
			{
				int start = num - 12;
				record = sceVersionRecords.Slice(start, 8).ToArray();
				return true;
			}
		}
		record = Array.Empty<byte>();
		return false;
	}

	private static ReadOnlySpan<byte> GetSceVersionRecords(byte[] image)
	{
		if (IsElf(image))
		{
			return FindElfSection(image, ".sceversion");
		}
		if (IsSelf(image))
		{
			ulong num = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(16));
			if (num <= (ulong)image.LongLength)
			{
				return image.AsSpan((int)num);
			}
		}
		return ReadOnlySpan<byte>.Empty;
	}

	private static byte[] BuildSceVersionRecord(string moduleName, ReadOnlySpan<byte> version)
	{
		string s = (moduleName.EndsWith(':') ? moduleName : (moduleName + ":"));
		byte[] bytes = Encoding.ASCII.GetBytes(s);
		int num = 1 + bytes.Length + 16;
		if (num > 65535)
		{
			throw new ArgumentException("The synthesized .sceversion module name is too long.", "moduleName");
		}
		byte[] array = new byte[num + 4];
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(2), (ushort)num);
		array[4] = 8;
		bytes.CopyTo(array, 5);
		version.CopyTo(array.AsSpan(5 + bytes.Length, 8));
		version.CopyTo(array.AsSpan(13 + bytes.Length, 8));
		return array;
	}

	public static byte[] BuildSdkVersionTuple(ulong sdkVersion)
	{
		return ProsperoSdkVersions.BuildTuple(sdkVersion);
	}

	public static bool TryOverrideSceVersionRecords(Span<byte> records, ulong sdkVersion)
	{
		if (records.Length < 4)
		{
			return false;
		}
		Span<byte> destination = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(destination, sdkVersion);
		bool result = false;
		int num2;
		for (int i = 0; i + 4 <= records.Length; i += num2)
		{
			int num = BinaryPrimitives.ReadUInt16LittleEndian(records.Slice(i + 2));
			num2 = num + 4;
			if (num < 17 || num2 > records.Length - i)
			{
				break;
			}
			int num3 = i + num - 12;
			if (num3 < 0 || num3 + 8 > records.Length)
			{
				break;
			}
			destination.CopyTo(records.Slice(num3));
			result = true;
		}
		return result;
	}

	private static ReadOnlySpan<byte> FindElfSection(byte[] elf, string sectionName)
	{
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(40));
		int num2 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(58));
		int num3 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(60));
		int num4 = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(62));
		if (num == 0L || num3 == 0 || num2 < 64 || num4 >= num3 || num > int.MaxValue)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		int num5 = (int)num;
		if (num5 + (long)num3 * (long)num2 > elf.Length)
		{
			throw new ArgumentException("ELF section headers overrun the file.", "elf");
		}
		ReadOnlySpan<byte> readOnlySpan = SectionPayload(elf, num5 + num4 * num2);
		for (int i = 0; i < num3; i++)
		{
			int num6 = num5 + i * num2;
			uint num7 = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(num6));
			if (num7 < readOnlySpan.Length)
			{
				ReadOnlySpan<byte> span = readOnlySpan.Slice((int)num7);
				int num8 = span.IndexOf((byte)0);
				if (num8 >= 0 && span.Slice(0, num8).SequenceEqual(Encoding.ASCII.GetBytes(sectionName)))
				{
					return SectionPayload(elf, num6);
				}
			}
		}
		return ReadOnlySpan<byte>.Empty;
	}

	private static ReadOnlySpan<byte> SectionPayload(byte[] source, int headerOffset)
	{
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(headerOffset + 24));
		ulong num2 = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(headerOffset + 32));
		if (num > int.MaxValue || num2 > int.MaxValue || num + num2 > (ulong)source.LongLength)
		{
			throw new ArgumentException("ELF section payload overruns the file.");
		}
		return source.AsSpan((int)num, (int)num2);
	}

	private static ulong DeriveAuthorityId(byte[] elf, ushort eType)
	{
		bool flag = ((eType == 2 || eType == 65024 || eType == 65040) ? true : false);
		bool flag2 = flag;
		return (byte)((elf.Length > 16128) ? elf[16128] : 0) switch
		{
			64 => flag2 ? 3530822107858473217uL : 3530822107858473218uL,
			128 => flag2 ? 3530822107858472961uL : 3530822107858472962uL,
			_ => flag2 ? 3530822107858468865uL : 3530822107858468866uL,
		};
	}

	private static List<SelectedSegment> SelectSegments(byte[] elf, int programHeaderOffset, int programHeaderCount)
	{
		List<SelectedSegment> list = new List<SelectedSegment>(programHeaderCount);
		for (int i = 0; i < programHeaderCount; i++)
		{
			int num = programHeaderOffset + i * 56;
			uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(num));
			long num3 = (long)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(num + 8));
			long num4 = (long)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(num + 32));
			if (num4 > 0 && num3 + num4 <= elf.Length)
			{
				bool flag;
				switch (num2)
				{
				case 1u:
				case 1627389952u:
				case 1627389968u:
				case 1879047936u:
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				if (flag)
				{
					list.Add(new SelectedSegment(i, (int)num3, (int)num4));
				}
			}
		}
		return list;
	}

	private static void WriteSegment(Span<byte> span, int entry, ulong flags, int offset, long fileSize, long memSize)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry), flags);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry + 8), (ulong)offset);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry + 16), (ulong)fileSize);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(entry + 24), (ulong)memSize);
	}

	private static int AlignUp(int value, int alignment)
	{
		return (value + alignment - 1) & ~(alignment - 1);
	}
}
