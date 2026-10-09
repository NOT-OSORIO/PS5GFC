using System;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoPkgTool.Containers;

public static class ProsperoNapsLayoutBuilder
{
	private const long UBlock = 262144L;

	private const long Mod256K = 262143L;

	private const long ClenEvenCap = 131071L;

	public static NapsLayoutDocument BuildDocument(NapsGenerationRequest request)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		if (request.Blocks == null || request.Blocks.Count == 0)
		{
			throw new ArgumentException("A naps generation request needs at least one block.", "request");
		}
		if (request.FileLogicalOffsets == null || request.FileLogicalOffsets.Count == 0)
		{
			throw new ArgumentException("A naps generation request needs the fidx logical-offset table.", "request");
		}
		ValidateRequest(request);
		(List<NapsCblockInfoEntry> Entries, List<(int Index, long Logical)> StdLogical) tuple = WalkBlocks(request.Blocks);
		List<NapsCblockInfoEntry> item = tuple.Entries;
		List<(int Index, long Logical)> item2 = tuple.StdLogical;
		NapsLayoutCounts counts = new NapsLayoutCounts(request.FileLogicalOffsets.Count, request.CompressionType, request.NumKeys, request.ShufflePatterns?.Count ?? 0, request.NumUBlocks, request.NumOuterBlocks, item.Count);
		List<NapsFileOffsetEntry> fileOffsets = BuildFileOffsets(request);
		List<NapsU2cEntry> cblockInfoOffsetByUblock = BuildU2c(item2, counts.NumUBlocks, counts.NumCblockInfo);
		IReadOnlyList<byte[]> outerBlockDigests = request.OuterBlockDigests ?? (from _ in Enumerable.Range(0, request.NumOuterBlocks)
			select new byte[8]).ToList();
		IReadOnlyList<byte[]> shufflePatterns = request.ShufflePatterns ?? Array.Empty<byte[]>();
		return new NapsLayoutDocument
		{
			Counts = counts,
			Map = ProsperoNapsLayout.SectionMap(counts),
			OuterBlockDigests = outerBlockDigests,
			ShufflePatterns = shufflePatterns,
			FileOffsets = fileOffsets,
			CblockInfoOffsetByUblock = cblockInfoOffsetByUblock,
			CblockInfos = item
		};
	}

	public static byte[] Build(NapsGenerationRequest request, int alignment = 16)
	{
		return ProsperoNapsLayout.BuildLayout(BuildDocument(request), alignment);
	}

	private static void ValidateRequest(NapsGenerationRequest request)
	{
		if (request.NumUBlocks <= 0 || request.NumOuterBlocks < 0)
		{
			throw new ArgumentOutOfRangeException("request", "NAPS block counts must be non-negative and include at least one ublock.");
		}
		IReadOnlyList<byte[]> outerBlockDigests = request.OuterBlockDigests;
		if (outerBlockDigests != null)
		{
			int count = outerBlockDigests.Count;
			if (count != request.NumOuterBlocks)
			{
				throw new ArgumentException("Outer digest table length must equal NumOuterBlocks.", "request");
			}
		}
		outerBlockDigests = request.ShufflePatterns;
		if (outerBlockDigests != null && outerBlockDigests.Count > 15)
		{
			throw new ArgumentException("NAPS supports at most 15 shuffle patterns.", "request");
		}
		for (int i = 0; i < request.FileLogicalOffsets.Count; i++)
		{
			if (request.FileLogicalOffsets[i] < 0)
			{
				throw new ArgumentException($"fidx logical offset {i}/{request.FileLogicalOffsets.Count} is negative ({request.FileLogicalOffsets[i]}).", "request");
			}
			if (i > 0 && request.FileLogicalOffsets[i] < request.FileLogicalOffsets[i - 1])
			{
				throw new ArgumentException($"fidx logical offset {i}/{request.FileLogicalOffsets.Count} ({request.FileLogicalOffsets[i]}) is less than offset {i - 1} ({request.FileLogicalOffsets[i - 1]}).", "request");
			}
		}
		if (request.Blocks.Count((NapsCblockPlanEntry b) => b.Terminator) == 1)
		{
			IReadOnlyList<NapsCblockPlanEntry> blocks = request.Blocks;
			if (blocks[blocks.Count - 1].Terminator)
			{
				for (int num = 0; num < request.Blocks.Count; num++)
				{
					NapsCblockPlanEntry napsCblockPlanEntry = request.Blocks[num];
					if (napsCblockPlanEntry.OnDiskOffset < 0 || napsCblockPlanEntry.LogicalOffset < 0 || napsCblockPlanEntry.StreamLength < 0 || napsCblockPlanEntry.EvenChunkCompressedLength < 0)
					{
						throw new ArgumentException($"NAPS block {num} has a negative offset or length.", "request");
					}
					bool flag = !napsCblockPlanEntry.Terminator && napsCblockPlanEntry.StreamLength == 0L && napsCblockPlanEntry.EvenChunkCompressedLength == 0L && napsCblockPlanEntry.Even == 0 && napsCblockPlanEntry.Odd == 0 && napsCblockPlanEntry.KdePredictor == 0;
					if (!napsCblockPlanEntry.Terminator && !flag && !napsCblockPlanEntry.IsAlias && (napsCblockPlanEntry.StreamLength <= 0 || napsCblockPlanEntry.StreamLength > 262144 || napsCblockPlanEntry.EvenChunkCompressedLength <= 0 || napsCblockPlanEntry.EvenChunkCompressedLength > 131072))
					{
						throw new ArgumentException($"NAPS block {num} has an unsupported stored stream geometry.", "request");
					}
					if (!napsCblockPlanEntry.Terminator && napsCblockPlanEntry.LogicalOffset + 262144 < napsCblockPlanEntry.LogicalOffset)
					{
						throw new ArgumentException($"NAPS block {num} logical range overflows.", "request");
					}
				}
				return;
			}
		}
		throw new ArgumentException("A NAPS plan must end with exactly one terminator.", "request");
	}

	private static (List<NapsCblockInfoEntry> Entries, List<(int Index, long Logical)> StdLogical) WalkBlocks(IReadOnlyList<NapsCblockPlanEntry> blocks)
	{
		List<NapsCblockInfoEntry> list = new List<NapsCblockInfoEntry>(blocks.Count * 2);
		List<(int, long)> list2 = new List<(int, long)>(blocks.Count);
		long num = 0L;
		foreach (NapsCblockPlanEntry block in blocks)
		{
			if (block.StartRun)
			{
				uint coffsetEndMod256K = (uint)(num & 0x3FFFF);
				uint tweakIdxStart = (uint)((!block.Terminator) ? (block.OnDiskOffset >> 16) : 0u);
				uint coffsetStart256K = (uint)(block.OnDiskOffset >> 18);
				list.Add(new NapsCblockInfoEntry
				{
					Raw = new byte[9],
					IsRunBase = true,
					CoffsetEndMod256K = coffsetEndMod256K,
					TweakIdxStart = tweakIdxStart,
					KeyTableIdx = 0,
					CoffsetStart256K = coffsetStart256K
				});
				num = block.OnDiskOffset;
			}
			uint coffsetStartMod256K = (uint)(num & 0x3FFFF);
			byte[] array = new byte[9];
			uint uoffsetStart;
			uint clenEvenMinus;
			if (block.Terminator)
			{
				uoffsetStart = (uint)(block.LogicalOffset & 0x3FFFF);
				clenEvenMinus = 0u;
				array[2] |= 8;
			}
			else
			{
				uoffsetStart = (uint)(block.LogicalOffset & 0x3FFFF);
				clenEvenMinus = (uint)Math.Min(block.EvenChunkCompressedLength - 1, 131071L);
				if (block.StoredRaw)
				{
					array[8] |= 8;
				}
			}
			int count = list.Count;
			list.Add(new NapsCblockInfoEntry
			{
				Raw = array,
				IsRunBase = false,
				CoffsetStartMod256K = coffsetStartMod256K,
				UoffsetStart = uoffsetStart,
				ClenEvenMinus1 = clenEvenMinus,
				Even = block.Even,
				Odd = block.Odd,
				KdePredictor = block.KdePredictor,
				ShuffleIdx = block.ShuffleIndex
			});
			list2.Add((count, block.LogicalOffset));
			num += block.StreamLength;
		}
		return (Entries: list, StdLogical: list2);
	}

	private static List<NapsFileOffsetEntry> BuildFileOffsets(NapsGenerationRequest request)
	{
		int count = request.FileLogicalOffsets.Count;
		List<NapsFileOffsetEntry> list = new List<NapsFileOffsetEntry>(count);
		for (int i = 0; i < count; i++)
		{
			byte type = (byte)((i == count - 1) ? request.FinalFileOffsetType : 0);
			list.Add(new NapsFileOffsetEntry(type, (ulong)request.FileLogicalOffsets[i]));
		}
		return list;
	}

	private static List<NapsU2cEntry> BuildU2c(List<(int Index, long Logical)> stdLogical, int numUBlocks, int numCblockInfo)
	{
		(int, long)[] array = stdLogical.OrderBy(((int Index, long Logical) t) => t.Logical).ToArray();
		int[] array2 = new int[numUBlocks];
		int num = 0;
		for (int num2 = 0; num2 < numUBlocks; num2++)
		{
			for (long num3 = (long)num2 * 262144L; num < array.Length && array[num].Item2 < num3; num++)
			{
			}
			array2[num2] = ((num < array.Length) ? array[num].Item1 : (numCblockInfo - 1));
		}
		int num4 = (numUBlocks + 8) / 8;
		List<NapsU2cEntry> list = new List<NapsU2cEntry>(num4);
		for (int num5 = 0; num5 < num4; num5++)
		{
			int num6 = 8 * num5;
			int num7 = ((num6 < numUBlocks) ? array2[num6] : (numCblockInfo - 1));
			byte[] array3 = new byte[7];
			for (int num8 = 0; num8 < 7; num8++)
			{
				int num9 = num6 + 1 + num8;
				int num10 = ((num9 < numUBlocks) ? array2[num9] : (numCblockInfo - 1));
				array3[num8] = ToU2cByte(num10 - num7, "delta");
			}
			list.Add(new NapsU2cEntry((uint)num7, array3));
		}
		return list;
		static byte ToU2cByte(int value, string field)
		{
			if (value < 0 || value > 255)
			{
				throw new NotSupportedException($"NAPS u2c {field} value {value} exceeds the single-byte delta field; this layout size is unsupported.");
			}
			return (byte)value;
		}
	}
}
