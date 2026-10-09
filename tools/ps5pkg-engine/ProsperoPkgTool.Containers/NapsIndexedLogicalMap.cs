using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProsperoPkgTool.Containers;

internal static class NapsIndexedLogicalMap
{
	private const int PageSize = 262144;

	internal static long? ReadMountEnd(ReadOnlySpan<byte> naps)
	{
		NapsLayoutCounts counts = ProsperoNapsLayout.DecodeHeader(naps);
		long num = ProsperoNapsLayout.SectionMap(counts).UncompressedOffsetStartByFileIdx.Offset + ((long)counts.NumFiles - 1L) * 6;
		if (num < 0 || num > naps.Length - 6)
		{
			throw new InvalidDataException("NAPS fidx mount sentinel lies outside the layout.");
		}
		NapsFileOffsetEntry napsFileOffsetEntry = ProsperoNapsLayout.DecodeFileOffsetEntry(naps.Slice((int)num, 6));
		if (napsFileOffsetEntry.Type != 64)
		{
			return null;
		}
		return checked((long)napsFileOffsetEntry.UncompressedOffsetStart);
	}

	internal static IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Restore(ReadOnlySpan<byte> naps, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks)
	{
		long num = blocks[blocks.Count - 1].UncompressedOffset + blocks[blocks.Count - 1].UncompressedLength;
		long valueOrDefault = ReadMountEnd(naps).GetValueOrDefault();
		if (valueOrDefault <= num || (valueOrDefault - num) % 262144 != 0L)
		{
			throw new InvalidDataException("Indexed NAPS map requires an exact fidx mount sentinel and missing whole logical pages.");
		}
		NapsLayoutDocument napsLayoutDocument = ProsperoNapsLayout.Parse(naps);
		(NapsCblockInfoEntry, int)[] array = (from item in napsLayoutDocument.CblockInfos.Select((NapsCblockInfoEntry entry, int index) => (Entry: entry, Index: index))
			where !item.Entry.IsRunBase
			select item).ToArray();
		if (array.Length != blocks.Count + 1)
		{
			throw new InvalidDataException("Indexed NAPS map requires one STD per payload block and a terminator.");
		}
		if (valueOrDefault <= ((long)napsLayoutDocument.Counts.NumUBlocks - 1L) * 262144 || valueOrDefault > (long)napsLayoutDocument.Counts.NumUBlocks * 262144L)
		{
			throw new InvalidDataException("Indexed NAPS fidx mount end disagrees with the header ublock count.");
		}
		Dictionary<int, long> dictionary = new Dictionary<int, long>();
		int[] array2 = new int[napsLayoutDocument.Counts.NumUBlocks];
		int num2 = -1;
		for (int num3 = 0; num3 < array2.Length; num3++)
		{
			NapsU2cEntry napsU2cEntry = napsLayoutDocument.CblockInfoOffsetByUblock[num3 / 8];
			long num4 = napsU2cEntry.InfoOffset9BBase + ((num3 % 8 == 0) ? 0 : napsU2cEntry.DeltaFromBase[num3 % 8 - 1]);
			if (num4 < num2 || num4 >= napsLayoutDocument.CblockInfos.Count || napsLayoutDocument.CblockInfos[(int)num4].IsRunBase)
			{
				throw new InvalidDataException($"Invalid NAPS u2c STD anchor for logical page {num3}.");
			}
			num2 = (array2[num3] = (int)num4);
			dictionary[(int)num4] = (long)num3 * 262144L;
		}
		List<ProsperoPs5InnerImageReader.InnerBlock> list = new List<ProsperoPs5InnerImageReader.InnerBlock>(blocks.Count);
		long[] array3 = new long[array.Length];
		int[] array4 = new int[blocks.Count];
		long num5 = 0L;
		for (int num6 = 0; num6 < array.Length; num6++)
		{
			long num7 = checked(((num6 < blocks.Count) ? blocks[num6].UncompressedOffset : num) + num5);
			if (dictionary.TryGetValue(array[num6].Item2, out var value) && num7 < value)
			{
				long num8 = (value - num7 + 262144 - 1) / 262144 * 262144;
				if (num8 > valueOrDefault - num - num5)
				{
					throw new InvalidDataException("NAPS u2c anchors demand more sparse pages than the fidx mount allows.");
				}
				for (long num9 = 0L; num9 < num8; num9 += 262144)
				{
					list.Add(new ProsperoPs5InnerImageReader.InnerBlock(0L, 0, 0, num7 + num9, 262144, IsKraken: false, IsZeroHole: true));
				}
				num5 += num8;
				num7 += num8;
			}
			if ((num7 & 0x3FFFF) != array[num6].Item1.UoffsetStart)
			{
				throw new InvalidDataException($"NAPS indexed logical offset disagrees with STD {array[num6].Item2}.");
			}
			array3[num6] = num7;
			if (num6 == blocks.Count)
			{
				break;
			}
			ProsperoPs5InnerImageReader.InnerBlock innerBlock = blocks[num6];
			int num10 = innerBlock.SourceBlockIndex;
			if (innerBlock.IsAlias && num10 >= 0)
			{
				if (num10 >= num6)
				{
					throw new InvalidDataException("Indexed NAPS alias must reference an earlier payload block.");
				}
				num10 = array4[num10];
			}
			array4[num6] = list.Count;
			list.Add(innerBlock with
			{
				UncompressedOffset = num7,
				SourceBlockIndex = num10
			});
		}
		if (array3[^1] != valueOrDefault)
		{
			throw new InvalidDataException("NAPS indexed logical coverage does not reach the exact fidx mount end.");
		}
		int num11 = 0;
		for (int num12 = 0; num12 < array2.Length; num12++)
		{
			for (; num11 + 1 < array3.Length && array3[num11] < (long)num12 * 262144L; num11++)
			{
			}
			if (array[num11].Item2 != array2[num12])
			{
				throw new InvalidDataException($"NAPS u2c page {num12} disagrees with the restored STD boundaries.");
			}
		}
		HashSet<long> hashSet = array3.ToHashSet();
		ulong num13 = 0uL;
		foreach (NapsFileOffsetEntry fileOffset in napsLayoutDocument.FileOffsets)
		{
			if (fileOffset.UncompressedOffsetStart < num13 || fileOffset.UncompressedOffsetStart > (ulong)valueOrDefault || !hashSet.Contains((long)fileOffset.UncompressedOffsetStart))
			{
				throw new InvalidDataException("NAPS fidx entry disagrees with the restored logical block boundaries.");
			}
			num13 = fileOffset.UncompressedOffsetStart;
		}
		return list;
	}
}
