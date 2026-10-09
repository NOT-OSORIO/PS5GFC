using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ProsperoPkgTool.Containers;

public static class ProsperoNapsLayout
{
	public const string FileName = "naps_pkg_layout.dat";

	public const int HeaderSize = 16;

	public const int OuterBlockDigestStride = 8;

	public const int ShufflePatternStride = 8;

	public const int FileOffsetStride = 6;

	public const int U2cStride = 10;

	public const int CblockInfoStride = 9;

	public const int DefaultAlignment = 16;

	public static long ComputeCblockInfoBase(ReadOnlySpan<byte> header)
	{
		if (header.Length < 16)
		{
			throw new ArgumentException($"NAPS header needs {16} bytes.", "header");
		}
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(header);
		ulong num2 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8));
		uint num3 = (uint)(num & 0xFFFFFF);
		uint num4 = (uint)((num >> 28) & 0xF);
		uint num5 = (uint)((num >> 32) & 0xFFFFFF);
		uint num6 = (uint)(num2 & 0xFFFFFF);
		return ((long)(num4 + num6) * 8L + 10L * (long)(num5 + 8 >> 3) + 6L * (long)num3 + 29) & -8;
	}

	public static long ComputeCblockInfoBase(NapsLayoutCounts counts)
	{
		return ((long)(counts.NumShufflePatterns + counts.NumOuterBlocks) * 8L + 10L * (long)(counts.NumUBlocks + 8 >> 3) + 6L * (long)(counts.NumFiles - 1) + 29) & -8;
	}

	public static long ComputeU2cOffset(NapsLayoutCounts counts)
	{
		return (long)(counts.NumShufflePatterns + counts.NumOuterBlocks) * 8L + 6L * (long)(counts.NumFiles - 1) + 22;
	}

	public static NapsLayoutCounts DecodeHeader(ReadOnlySpan<byte> header)
	{
		if (header.Length < 16)
		{
			throw new ArgumentException($"NAPS header needs {16} bytes.", "header");
		}
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(header);
		ulong num2 = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8));
		int numFiles = (int)(num & 0xFFFFFF) + 1;
		byte compressionType = (byte)((num >> 24) & 3);
		int numKeys = (int)((num >> 26) & 3) + 1;
		int numShufflePatterns = (int)((num >> 28) & 0xF);
		int numUBlocks = (int)((num >> 32) & 0xFFFFFF);
		int numOuterBlocks = (int)(num2 & 0xFFFFFF);
		int numCblockInfo = (int)((num2 >> 24) & 0xFFFFFF) + 2;
		return new NapsLayoutCounts(numFiles, compressionType, numKeys, numShufflePatterns, numUBlocks, numOuterBlocks, numCblockInfo);
	}

	public static byte[] EncodeHeader(NapsLayoutCounts counts)
	{
		ulong value = ((ulong)(uint)(counts.NumFiles - 1) & 0xFFFFFFuL) | (ulong)((long)(counts.CompressionType & 3) << 24) | ((ulong)(uint)((counts.NumKeys - 1) & 3) << 26) | ((ulong)(uint)(counts.NumShufflePatterns & 0xF) << 28) | ((ulong)(uint)(counts.NumUBlocks & 0xFFFFFF) << 32);
		ulong value2 = (uint)(counts.NumOuterBlocks & 0xFFFFFF) | ((ulong)(uint)((counts.NumCblockInfo - 2) & 0xFFFFFF) << 24);
		byte[] array = new byte[16];
		BinaryPrimitives.WriteUInt64LittleEndian(array, value);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(8), value2);
		return array;
	}

	public static NapsSectionMap SectionMap(NapsLayoutCounts counts)
	{
		long num = 0L;
		NapsSection header = new NapsSection(num, 16L, 16, 1);
		num += header.Size;
		NapsSection outerBlockDigest = new NapsSection(num, (long)counts.NumOuterBlocks * 8L, 8, counts.NumOuterBlocks);
		num += outerBlockDigest.Size;
		NapsSection shufflePattern = new NapsSection(num, (long)counts.NumShufflePatterns * 8L, 8, counts.NumShufflePatterns);
		num += shufflePattern.Size;
		NapsSection uncompressedOffsetStartByFileIdx = new NapsSection(num, (long)counts.NumFiles * 6L, 6, counts.NumFiles);
		num += uncompressedOffsetStartByFileIdx.Size;
		int numU2cEntries = counts.NumU2cEntries;
		NapsSection cblockInfoOffsetByUblockIdxCompressed = new NapsSection(num, (long)numU2cEntries * 10L, 10, numU2cEntries);
		num += cblockInfoOffsetByUblockIdxCompressed.Size;
		long num2 = ComputeCblockInfoBase(counts);
		long cblockInfoPad = num2 - num;
		NapsSection cblockInfo = new NapsSection(num2, (long)counts.NumCblockInfo * 9L, 9, counts.NumCblockInfo);
		num = num2 + cblockInfo.Size;
		return new NapsSectionMap(header, outerBlockDigest, shufflePattern, uncompressedOffsetStartByFileIdx, cblockInfoOffsetByUblockIdxCompressed, cblockInfo, cblockInfoPad, num);
	}

	public static int DeriveFidxEntryCount(NapsLayoutCounts counts, long blobLength)
	{
		return counts.NumFiles;
	}

	public static NapsFileOffsetEntry DecodeFileOffsetEntry(ReadOnlySpan<byte> entry)
	{
		if (entry.Length < 6)
		{
			throw new ArgumentException($"fidx entry needs {6} bytes.", "entry");
		}
		ulong num = 0uL;
		for (int i = 0; i < 5; i++)
		{
			num |= (ulong)entry[i] << 8 * i;
		}
		return new NapsFileOffsetEntry(entry[5], num);
	}

	public static byte[] EncodeFileOffsetEntry(NapsFileOffsetEntry entry)
	{
		byte[] array = new byte[6];
		ulong uncompressedOffsetStart = entry.UncompressedOffsetStart;
		for (int i = 0; i < 5; i++)
		{
			array[i] = (byte)(uncompressedOffsetStart >> 8 * i);
		}
		array[5] = entry.Type;
		return array;
	}

	public static NapsU2cEntry DecodeU2cEntry(ReadOnlySpan<byte> entry)
	{
		if (entry.Length < 10)
		{
			throw new ArgumentException($"u2c entry needs {10} bytes.", "entry");
		}
		int infoOffset9BBase = entry[0] | (entry[1] << 8) | (entry[2] << 16);
		byte[] array = new byte[7];
		entry.Slice(3, 7).CopyTo(array);
		return new NapsU2cEntry((uint)infoOffset9BBase, array);
	}

	public static byte[] EncodeU2cEntry(NapsU2cEntry entry)
	{
		byte[] array = new byte[10]
		{
			(byte)entry.InfoOffset9BBase,
			(byte)(entry.InfoOffset9BBase >> 8),
			(byte)(entry.InfoOffset9BBase >> 16),
			0,
			0,
			0,
			0,
			0,
			0,
			0
		};
		for (int i = 0; i < 7 && i < entry.DeltaFromBase.Length; i++)
		{
			array[3 + i] = entry.DeltaFromBase[i];
		}
		return array;
	}

	public static NapsCblockInfoEntry DecodeCblockInfoEntry(ReadOnlySpan<byte> entry)
	{
		if (entry.Length < 9)
		{
			throw new ArgumentException($"cblockinfo entry needs {9} bytes.", "entry");
		}
		byte[] array = new byte[9];
		entry.Slice(0, 9).CopyTo(array);
		ulong num = 0uL;
		for (int i = 0; i < 8; i++)
		{
			num |= (ulong)array[i] << 8 * i;
		}
		ulong num2 = array[8];
		bool flag = ((num >> 18) & 1) != 0;
		uint num3 = (uint)(num & 0x3FFFF);
		if (!flag)
		{
			return new NapsCblockInfoEntry
			{
				Raw = array,
				IsRunBase = false,
				CoffsetStartMod256K = num3,
				UoffsetStart = (uint)((num >> 20) & 0x3FFFF),
				ClenEvenMinus1 = (uint)((num >> 38) & 0x1FFFF),
				Even = (byte)((num >> 55) & 7),
				Odd = (byte)((num >> 58) & 7),
				KdePredictor = (byte)(((num2 & 7) << 3) | ((num >> 61) & 7)),
				ShuffleIdx = (byte)((num2 >> 4) & 0xF)
			};
		}
		return new NapsCblockInfoEntry
		{
			Raw = array,
			IsRunBase = true,
			CoffsetEndMod256K = num3,
			TweakIdxStart = (uint)((num >> 20) & 0xFFFFFFF),
			KeyTableIdx = (byte)((num >> 48) & 3),
			CoffsetStart256K = (uint)(((num2 & 0xFF) << 14) | ((num >> 50) & 0x3FFF))
		};
	}

	public static byte[] EncodeCblockInfoEntry(NapsCblockInfoEntry entry)
	{
		ulong num = (entry.IsRunBase ? entry.CoffsetEndMod256K : entry.CoffsetStartMod256K) & 0x3FFFF;
		ulong num2;
		if (!entry.IsRunBase)
		{
			num |= (ulong)(entry.UoffsetStart & 0x3FFFF) << 20;
			num |= (ulong)(entry.ClenEvenMinus1 & 0x1FFFF) << 38;
			num |= (ulong)((long)(entry.Even & 7) << 55);
			num |= (ulong)((long)(entry.Odd & 7) << 58);
			num |= (ulong)((long)(entry.KdePredictor & 7) << 61);
			num2 = ((ulong)(entry.KdePredictor >> 3) & 7uL) | (ulong)((long)(entry.ShuffleIdx & 0xF) << 4);
		}
		else
		{
			num |= 0x40000;
			num |= (ulong)(entry.TweakIdxStart & 0xFFFFFFF) << 20;
			num |= (ulong)((long)(entry.KeyTableIdx & 3) << 48);
			num |= (ulong)(entry.CoffsetStart256K & 0x3FFF) << 50;
			num2 = (entry.CoffsetStart256K >> 14) & 0xFF;
		}
		byte[] raw = entry.Raw;
		if (raw != null && raw.Length == 9)
		{
			num |= (ulong)(uint)(entry.Raw[2] & 8) << 16;
			if (!entry.IsRunBase)
			{
				num2 |= (uint)(entry.Raw[8] & 8);
			}
		}
		byte[] array = new byte[9];
		for (int i = 0; i < 8; i++)
		{
			array[i] = (byte)(num >> 8 * i);
		}
		array[8] = (byte)num2;
		return array;
	}

	public static NapsLayoutDocument Parse(ReadOnlySpan<byte> blob)
	{
		NapsLayoutCounts counts = DecodeHeader(blob);
		return Parse(blob, counts);
	}

	public static NapsLayoutDocument Parse(ReadOnlySpan<byte> blob, NapsLayoutCounts counts)
	{
		NapsSectionMap map = SectionMap(counts);
		if (blob.Length < map.TotalSize)
		{
			throw new ArgumentException($"NAPS blob is {blob.Length} bytes but the counts require {map.TotalSize}.", "blob");
		}
		long num = (map.TotalSize + 7) & -8;
		int trailingZeroBytes = blob.Length - (int)num;
		for (int i = (int)num; i < blob.Length; i++)
		{
			if (blob[i] != 0)
			{
				throw new InvalidDataException("NAPS trailing footer contains non-zero bytes.");
			}
		}
		List<byte[]> outerBlockDigests = SliceRaw(blob, map.OuterBlockDigest);
		List<byte[]> shufflePatterns = SliceRaw(blob, map.ShufflePattern);
		List<NapsFileOffsetEntry> list = new List<NapsFileOffsetEntry>(map.UncompressedOffsetStartByFileIdx.Count);
		for (int j = 0; j < map.UncompressedOffsetStartByFileIdx.Count; j++)
		{
			list.Add(DecodeFileOffsetEntry(EntrySpan(blob, map.UncompressedOffsetStartByFileIdx, j)));
		}
		List<NapsU2cEntry> list2 = new List<NapsU2cEntry>(map.CblockInfoOffsetByUblockIdxCompressed.Count);
		for (int k = 0; k < map.CblockInfoOffsetByUblockIdxCompressed.Count; k++)
		{
			list2.Add(DecodeU2cEntry(EntrySpan(blob, map.CblockInfoOffsetByUblockIdxCompressed, k)));
		}
		List<NapsCblockInfoEntry> list3 = new List<NapsCblockInfoEntry>(map.CblockInfo.Count);
		for (int l = 0; l < map.CblockInfo.Count; l++)
		{
			list3.Add(DecodeCblockInfoEntry(EntrySpan(blob, map.CblockInfo, l)));
		}
		return new NapsLayoutDocument
		{
			Counts = counts,
			Map = map,
			OuterBlockDigests = outerBlockDigests,
			ShufflePatterns = shufflePatterns,
			FileOffsets = list,
			CblockInfoOffsetByUblock = list2,
			CblockInfos = list3,
			TrailingZeroBytes = trailingZeroBytes
		};
	}

	public static byte[] BuildLayout(NapsLayoutDocument document, int alignment = 16)
	{
		ArgumentNullException.ThrowIfNull(document, "document");
		NapsLayoutCounts counts = document.Counts;
		if (document.OuterBlockDigests.Count != counts.NumOuterBlocks)
		{
			throw new ArgumentException($"OuterBlockDigests count {document.OuterBlockDigests.Count} != NumOuterBlocks {counts.NumOuterBlocks}.", "document");
		}
		if (document.ShufflePatterns.Count != counts.NumShufflePatterns)
		{
			throw new ArgumentException($"ShufflePatterns count {document.ShufflePatterns.Count} != NumShufflePatterns {counts.NumShufflePatterns}.", "document");
		}
		if (document.FileOffsets.Count != counts.NumFiles)
		{
			throw new ArgumentException($"FileOffsets count {document.FileOffsets.Count} != NumFiles {counts.NumFiles}.", "document");
		}
		if (document.CblockInfoOffsetByUblock.Count != counts.NumU2cEntries)
		{
			throw new ArgumentException($"u2c count {document.CblockInfoOffsetByUblock.Count} != NumU2cEntries {counts.NumU2cEntries}.", "document");
		}
		if (document.CblockInfos.Count != counts.NumCblockInfo)
		{
			throw new ArgumentException($"CblockInfos count {document.CblockInfos.Count} != NumCblockInfo {counts.NumCblockInfo}.", "document");
		}
		NapsSectionMap napsSectionMap = SectionMap(counts);
		long totalSize = napsSectionMap.TotalSize;
		byte[] array = new byte[(document.TrailingZeroBytes >= 0) ? (((totalSize + 7) & -8) + document.TrailingZeroBytes) : ((alignment > 1) ? ((totalSize + alignment - 1) / alignment * alignment) : totalSize)];
		Span<byte> destination = array.AsSpan();
		EncodeHeader(counts).CopyTo(destination);
		for (int i = 0; i < document.OuterBlockDigests.Count; i++)
		{
			byte[] array2 = document.OuterBlockDigests[i];
			if (array2.Length != 8)
			{
				throw new ArgumentException($"outer-block digest must be {8} bytes.", "document");
			}
			array2.CopyTo(destination.Slice((int)napsSectionMap.OuterBlockDigest.Offset + i * 8, 8));
		}
		for (int j = 0; j < document.ShufflePatterns.Count; j++)
		{
			byte[] array3 = document.ShufflePatterns[j];
			if (array3.Length != 8)
			{
				throw new ArgumentException($"shuffle pattern must be {8} bytes.", "document");
			}
			array3.CopyTo(destination.Slice((int)napsSectionMap.ShufflePattern.Offset + j * 8, 8));
		}
		int num = 0;
		foreach (NapsFileOffsetEntry fileOffset in document.FileOffsets)
		{
			EncodeFileOffsetEntry(fileOffset).CopyTo(destination.Slice((int)napsSectionMap.UncompressedOffsetStartByFileIdx.Offset + num * 6, 6));
			num++;
		}
		num = 0;
		foreach (NapsU2cEntry item in document.CblockInfoOffsetByUblock)
		{
			EncodeU2cEntry(item).CopyTo(destination.Slice((int)napsSectionMap.CblockInfoOffsetByUblockIdxCompressed.Offset + num * 10, 10));
			num++;
		}
		num = 0;
		foreach (NapsCblockInfoEntry cblockInfo in document.CblockInfos)
		{
			EncodeCblockInfoEntry(cblockInfo).CopyTo(destination.Slice((int)napsSectionMap.CblockInfo.Offset + num * 9, 9));
			num++;
		}
		return array;
	}

	private static ReadOnlySpan<byte> EntrySpan(ReadOnlySpan<byte> blob, NapsSection section, int index)
	{
		return blob.Slice((int)section.Offset + index * section.Stride, section.Stride);
	}

	private static List<byte[]> SliceRaw(ReadOnlySpan<byte> blob, NapsSection section)
	{
		List<byte[]> list = new List<byte[]>(section.Count);
		for (int i = 0; i < section.Count; i++)
		{
			byte[] array = new byte[section.Stride];
			blob.Slice((int)section.Offset + i * section.Stride, section.Stride).CopyTo(array);
			list.Add(array);
		}
		return list;
	}
}
