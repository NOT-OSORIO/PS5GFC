using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPackageReader
{
	private const int FihRegionSize = 65536;

	private const int CntHeaderSize = 1440;

	private const int SegmentAlignment = 65536;

	private static ReadOnlySpan<byte> FihMagic => "\u007fFIH"u8;

	private static ReadOnlySpan<byte> LihMagic => "\u007fLIH"u8;

	private static ReadOnlySpan<byte> CntMagic => "\u007fCNT"u8;

	public static ProsperoPackageInspection Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		string fullPath = Path.GetFullPath(path);
		using FileStream fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (fileStream.Length < 4)
		{
			throw new InvalidDataException("File is too small to contain a PS5 package header.");
		}
		Span<byte> span = stackalloc byte[4];
		ReadExactlyAt(fileStream, 0L, span);
		ProsperoPackageInspection result;
		if (!((ReadOnlySpan<byte>)span).SequenceEqual(FihMagic))
		{
			if (!((ReadOnlySpan<byte>)span).SequenceEqual(LihMagic))
			{
				if (!((ReadOnlySpan<byte>)span).SequenceEqual(CntMagic))
				{
					throw new InvalidDataException("Unknown package magic; expected FIH, LIH, or CNT.");
				}
				result = ReadMetadata(fileStream, fullPath);
			}
			else
			{
				result = ReadPatch(fileStream, fullPath);
			}
		}
		else
		{
			result = ReadFinalized(fileStream, fullPath);
		}
		return result;
	}

	private static ProsperoPackageInspection ReadPatch(FileStream stream, string path)
	{
		if (stream.Length < 65536)
		{
			throw new InvalidDataException("Patch image is smaller than the 0x10000-byte LIH region.");
		}
		byte[] array = new byte[80];
		ReadExactlyAt(stream, 0L, array);
		ProsperoLihHeader prosperoLihHeader = new ProsperoLihHeader(BinaryPrimitives.ReadUInt16LittleEndian(array.AsSpan(6, 2)), BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(20, 4)), BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(16, 4)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(24, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(32, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(40, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(48, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(56, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(64, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(72, 8)));
		List<VerificationIssue> list = new List<VerificationIssue>();
		if (prosperoLihHeader.FormatVersion != 1)
		{
			list.Add(Error("LIH_VERSION", $"LIH format version is {prosperoLihHeader.FormatVersion}; expected 1."));
		}
		if (prosperoLihHeader.LihSize != 65536 || prosperoLihHeader.FihOffset != prosperoLihHeader.LihSize)
		{
			list.Add(Error("LIH_FIH_OFFSET", "LIH does not place the FIH immediately after its 0x10000-byte region."));
		}
		if (!TryRange(prosperoLihHeader.FihOffset, prosperoLihHeader.FihSize, stream.Length, out var resultOffset, out var resultSize))
		{
			throw new InvalidDataException("LIH FIH segment lies outside the package.");
		}
		if (prosperoLihHeader.PackageSize != (ulong)stream.Length)
		{
			list.Add(Error("LIH_PACKAGE_SIZE", "LIH package size does not match the physical file length."));
		}
		if (!TryRange(prosperoLihHeader.CntOffset, prosperoLihHeader.CntSize, stream.Length, out var resultOffset2, out var resultSize2))
		{
			throw new InvalidDataException("LIH SC/CNT segment lies outside the package.");
		}
		if (!TryRange(prosperoLihHeader.SiOffset, prosperoLihHeader.SiSize, stream.Length, out var resultOffset3, out var resultSize3))
		{
			throw new InvalidDataException("LIH SI segment lies outside the package.");
		}
		long num;
		long num2;
		checked
		{
			num = resultOffset + resultSize;
			num2 = resultOffset2 - num;
			if (num2 < 0)
			{
				throw new InvalidDataException("LIH SC/CNT begins before the end of FIH.");
			}
		}
		if (resultOffset3 != resultOffset2 + resultSize2)
		{
			list.Add(Error("LIH_SEGMENT_GAP", "LIH SI offset does not immediately follow SC/CNT."));
		}
		if (resultOffset3 + resultSize3 != stream.Length)
		{
			list.Add(Error("LIH_SI_SIZE", "LIH SI segment does not end at the physical package length."));
		}
		Span<byte> buffer = stackalloc byte[8];
		ReadExactlyAt(stream, resultOffset, buffer);
		if (!((ReadOnlySpan<byte>)buffer.Slice(0, 4)).SequenceEqual(FihMagic))
		{
			throw new InvalidDataException("LIH FIH segment does not begin with FIH magic.");
		}
		byte b = buffer[5];
		if (b != 0)
		{
			throw ProsperoErrorInfo.Unsupported($"Unsupported patch FIH signed byte 0x{b:X2}; only observed debug patches are supported structurally.");
		}
		ProsperoFihHeader fih = new ProsperoFihHeader(b, BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6)), (ulong)num, (ulong)num2, (ulong)resultOffset2);
		ProsperoCntHeader cnt = ReadCntHeader(stream, resultOffset2);
		IReadOnlyList<ProsperoCntEntry> entries = ReadCntEntries(stream, resultOffset2, cnt, list, out var logicalSize);
		if (logicalSize > resultSize2)
		{
			list.Add(Error("CNT_RANGE", "CNT entry payloads extend beyond the LIH SC/CNT segment."));
		}
		if (resultSize3 >= 4)
		{
			Span<byte> buffer2 = stackalloc byte[4];
			ReadExactlyAt(stream, resultOffset3, buffer2);
			if (buffer2[0] != 80 || buffer2[1] != 75 || buffer2[2] != 3 || buffer2[3] != 4)
			{
				list.Add(new VerificationIssue("SI_MAGIC", "LIH SI segment does not begin with a ZIP local-file header.", IsError: false));
			}
		}
		IReadOnlyList<ProsperoPackageSegment> segments = new _003C_003Ez__ReadOnlyArray<ProsperoPackageSegment>(new ProsperoPackageSegment[5]
		{
			new ProsperoPackageSegment("LIH", 0L, checked((long)prosperoLihHeader.LihSize)),
			new ProsperoPackageSegment("FIH", resultOffset, resultSize),
			new ProsperoPackageSegment("PFS", num, num2),
			new ProsperoPackageSegment("SC", resultOffset2, resultSize2),
			new ProsperoPackageSegment("SI", resultOffset3, resultSize3)
		});
		return new ProsperoPackageInspection(path, stream.Length, ProsperoPackageKind.FinalizedPatchDebug, fih, cnt, entries, segments, new VerificationResult(list.All((VerificationIssue issue) => !issue.IsError), list), prosperoLihHeader);
	}

	private static ProsperoPackageInspection ReadFinalized(FileStream stream, string path)
	{
		if (stream.Length < 65536)
		{
			throw new InvalidDataException("Finalized image is smaller than the 0x10000-byte FIH region.");
		}
		byte[] array = new byte[256];
		ReadExactlyAt(stream, 0L, array);
		byte b = array[5];
		ProsperoPackageKind kind = b switch
		{
			0 => ProsperoPackageKind.FinalizedDebug,
			128 => ProsperoPackageKind.FinalizedRetail,
			_ => throw ProsperoErrorInfo.Unsupported($"Unsupported FIH signed byte 0x{b:X2}."),
		};
		ProsperoFihHeader prosperoFihHeader = new ProsperoFihHeader(b, BinaryPrimitives.ReadUInt16LittleEndian(array.AsSpan(6, 2)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(16, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(24, 8)), BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(88, 8)));
		List<VerificationIssue> list = new List<VerificationIssue>();
		if (prosperoFihHeader.FormatVersion != 3)
		{
			list.Add(Error("FIH_VERSION", $"FIH format version is {prosperoFihHeader.FormatVersion}; expected 3."));
		}
		if (prosperoFihHeader.PfsOffset != 65536)
		{
			list.Add(Error("PFS_OFFSET", $"PFS starts at 0x{prosperoFihHeader.PfsOffset:X}; expected 0x{65536:X}."));
		}
		if (!TryRange(prosperoFihHeader.PfsOffset, prosperoFihHeader.PfsSize, stream.Length, out var resultOffset, out var resultSize))
		{
			list.Add(Error("PFS_RANGE", "PFS segment lies outside the package."));
		}
		if (prosperoFihHeader.CntOffset > (ulong)Math.Max(0L, stream.Length - 1440))
		{
			throw new InvalidDataException("Embedded CNT header lies outside the package.");
		}
		if (TryRange(prosperoFihHeader.PfsOffset, prosperoFihHeader.PfsSize, stream.Length, out resultOffset, out resultSize) && resultOffset + resultSize != (long)prosperoFihHeader.CntOffset)
		{
			list.Add(Error("SEGMENT_GAP", "PFS end does not equal the embedded CNT offset."));
		}
		long num = checked((long)prosperoFihHeader.CntOffset);
		ProsperoCntHeader cnt = ReadCntHeader(stream, num);
		IReadOnlyList<ProsperoCntEntry> entries = ReadCntEntries(stream, num, cnt, list, out var logicalSize);
		long num2 = AlignUp(logicalSize, 65536);
		if (!IsRangeWithin(num, num2, stream.Length))
		{
			list.Add(Error("CNT_RANGE", "Aligned embedded CNT segment lies outside the package."));
			num2 = Math.Max(0L, stream.Length - num);
		}
		long num3 = checked(num + num2);
		long num4 = Math.Max(0L, stream.Length - num3);
		if (num4 >= 4)
		{
			Span<byte> buffer = stackalloc byte[4];
			ReadExactlyAt(stream, num3, buffer);
			if (buffer[0] != 80 || buffer[1] != 75 || buffer[2] != 3 || buffer[3] != 4)
			{
				list.Add(new VerificationIssue("SI_MAGIC", "Trailing SI segment does not begin with a ZIP local-file header.", IsError: false));
			}
		}
		else if (num4 > 0)
		{
			list.Add(Error("SI_RANGE", "Trailing SI segment is too small to contain a ZIP header."));
		}
		else
		{
			list.Add(new VerificationIssue("SI_MISSING", "Finalized image has no trailing SI segment.", IsError: false));
		}
		List<ProsperoPackageSegment> segments = new List<ProsperoPackageSegment>
		{
			new ProsperoPackageSegment("FIH", 0L, 65536L),
			new ProsperoPackageSegment("PFS", resultOffset, resultSize),
			new ProsperoPackageSegment("SC", num, num2),
			new ProsperoPackageSegment("SI", num3, num4)
		};
		return new ProsperoPackageInspection(path, stream.Length, kind, prosperoFihHeader, cnt, entries, segments, new VerificationResult(list.All((VerificationIssue issue) => !issue.IsError), list));
	}

	private static ProsperoPackageInspection ReadMetadata(FileStream stream, string path)
	{
		ProsperoCntHeader prosperoCntHeader = ReadCntHeader(stream, 0L);
		List<VerificationIssue> list = new List<VerificationIssue>();
		if (prosperoCntHeader.EntryTableOffset < 1440)
		{
			list.Add(Error("ENTRY_TABLE_OFFSET", "CNT entry table overlaps the fixed header."));
		}
		IReadOnlyList<ProsperoCntEntry> entries = ReadCntEntries(stream, 0L, prosperoCntHeader, list, out var logicalSize);
		if (logicalSize > stream.Length)
		{
			list.Add(Error("CNT_RANGE", "CNT body lies outside the file."));
		}
		return new ProsperoPackageInspection(path, stream.Length, ProsperoPackageKind.MetadataContainer, null, prosperoCntHeader, entries, new _003C_003Ez__ReadOnlySingleElementList<ProsperoPackageSegment>(new ProsperoPackageSegment("CNT", 0L, stream.Length)), new VerificationResult(list.All((VerificationIssue issue) => !issue.IsError), list));
	}

	private static ProsperoCntHeader ReadCntHeader(FileStream stream, long offset)
	{
		if (!IsRangeWithin(offset, 1440L, stream.Length))
		{
			throw new InvalidDataException("CNT header lies outside the package.");
		}
		byte[] array = new byte[1440];
		ReadExactlyAt(stream, offset, array);
		if (!((ReadOnlySpan<byte>)array.AsSpan(0, 4)).SequenceEqual(CntMagic))
		{
			throw new InvalidDataException("Embedded container does not have CNT magic.");
		}
		return new ProsperoCntHeader(BinaryPrimitives.ReadUInt16BigEndian(array.AsSpan(4, 2)), BinaryPrimitives.ReadUInt16BigEndian(array.AsSpan(6, 2)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(12, 4)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(16, 4)), BinaryPrimitives.ReadUInt16BigEndian(array.AsSpan(20, 2)), BinaryPrimitives.ReadUInt16BigEndian(array.AsSpan(22, 2)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(24, 4)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(28, 4)), BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(32, 8)), BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(40, 8)), ReadAscii(array.AsSpan(64, 36)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(112, 4)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(116, 4)), BinaryPrimitives.ReadUInt32BigEndian(array.AsSpan(120, 4)));
	}

	private static IReadOnlyList<ProsperoCntEntry> ReadCntEntries(FileStream stream, long cntOffset, ProsperoCntHeader cnt, ICollection<VerificationIssue> issues, out long logicalSize)
	{
		long num = Math.Max(0L, stream.Length - cntOffset);
		if (cnt.EntryCount > 65536)
		{
			issues.Add(Error("ENTRY_COUNT", $"CNT entry count {cnt.EntryCount} exceeds the safety limit."));
			logicalSize = Math.Min(num, 1440L);
			return Array.Empty<ProsperoCntEntry>();
		}
		List<ProsperoCntEntry> list;
		byte[] array2;
		checked
		{
			long num2 = unchecked((long)cnt.EntryCount) * 32L;
			if (!IsRangeWithin(cnt.EntryTableOffset, num2, num) || !IsRangeWithin(cntOffset + cnt.EntryTableOffset, num2, stream.Length))
			{
				issues.Add(Error("ENTRY_TABLE_RANGE", "CNT entry table lies outside the container."));
				logicalSize = Math.Min(num, 1440L);
				return Array.Empty<ProsperoCntEntry>();
			}
			list = new List<ProsperoCntEntry>(unchecked((int)cnt.EntryCount));
			byte[] array = new byte[num2];
			ReadExactlyAt(stream, cntOffset + cnt.EntryTableOffset, array);
			long num3 = 1440L;
			for (int i = 0; i < cnt.EntryCount; i = unchecked(i + 1))
			{
				ReadOnlySpan<byte> readOnlySpan = array.AsSpan(unchecked(i * 32), 32);
				ProsperoCntEntry prosperoCntEntry = new ProsperoCntEntry(BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(0)), BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(4)), BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(8)), BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(12)), BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(16)), BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(20)), null);
				if (!IsRangeWithin(prosperoCntEntry.DataOffset, prosperoCntEntry.DataSize, num))
				{
					issues.Add(Error("ENTRY_DATA_RANGE", $"CNT entry 0x{prosperoCntEntry.Id:X8} data lies outside the container."));
				}
				else
				{
					long num4 = unchecked((long)prosperoCntEntry.DataOffset) + unchecked((long)prosperoCntEntry.DataSize);
					if (num4 > num3)
					{
						num3 = num4;
					}
				}
				list.Add(prosperoCntEntry);
			}
			logicalSize = num3;
			int num5 = list.FindIndex((ProsperoCntEntry entry) => entry.Id == 512);
			if (num5 < 0)
			{
				return list;
			}
			ProsperoCntEntry prosperoCntEntry2 = list[num5];
			if (prosperoCntEntry2.DataSize == 0 || prosperoCntEntry2.DataSize > 16777216 || !IsRangeWithin(prosperoCntEntry2.DataOffset, prosperoCntEntry2.DataSize, num))
			{
				return list;
			}
			array2 = new byte[prosperoCntEntry2.DataSize];
			ReadExactlyAt(stream, cntOffset + prosperoCntEntry2.DataOffset, array2);
		}
		for (int num6 = 0; num6 < list.Count; num6++)
		{
			ProsperoCntEntry prosperoCntEntry3 = list[num6];
			if (prosperoCntEntry3.NameOffset != uint.MaxValue && prosperoCntEntry3.NameOffset < array2.Length)
			{
				ReadOnlySpan<byte> span = array2.AsSpan((int)prosperoCntEntry3.NameOffset);
				int num7 = span.IndexOf((byte)0);
				if (num7 < 0)
				{
					issues.Add(Error("ENTRY_NAME", $"CNT entry 0x{prosperoCntEntry3.Id:X8} name is not NUL-terminated."));
				}
				else
				{
					string text = Encoding.UTF8.GetString(span.Slice(0, num7));
					list[num6] = prosperoCntEntry3 with
					{
						Name = ((text.Length == 0) ? null : text)
					};
				}
			}
		}
		return list;
	}

	private static bool TryRange(ulong offset, ulong size, long fileSize, out long resultOffset, out long resultSize)
	{
		resultOffset = (long)((offset <= long.MaxValue) ? offset : 0);
		resultSize = (long)((size <= long.MaxValue) ? size : 0);
		if (offset <= long.MaxValue && size <= long.MaxValue)
		{
			return IsRangeWithin(resultOffset, resultSize, fileSize);
		}
		return false;
	}

	private static bool IsRangeWithin(long offset, long size, long fileSize)
	{
		if (offset >= 0 && size >= 0 && offset <= fileSize)
		{
			return size <= fileSize - offset;
		}
		return false;
	}

	private static long AlignUp(long value, int alignment)
	{
		long num = value % alignment;
		if (num != 0L)
		{
			return checked(value + alignment - num);
		}
		return value;
	}

	private static string ReadAscii(ReadOnlySpan<byte> bytes)
	{
		int num = bytes.IndexOf((byte)0);
		if (num < 0)
		{
			num = bytes.Length;
		}
		return Encoding.ASCII.GetString(bytes.Slice(0, num));
	}

	private static void ReadExactlyAt(FileStream stream, long offset, Span<byte> buffer)
	{
		stream.Position = offset;
		stream.ReadExactly(buffer);
	}

	private static VerificationIssue Error(string code, string message)
	{
		return new VerificationIssue(code, message, IsError: true);
	}
}
