using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProsperoPkgTool.Containers;

internal static class NapsDialectRegistry
{
	private sealed class CoffsetDialect(bool nativeSpans) : INapsDialect
	{
		public string Id
		{
			get
			{
				if (!nativeSpans)
				{
					return "compatible-coffset-v1";
				}
				return "native-span-v1";
			}
		}

		public IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Decode(ReadOnlySpan<byte> naps, long sourceLength)
		{
			return ProsperoPs5InnerImageReader.WalkBlocksCore(naps, sourceLength, nativeSpans);
		}
	}

	private sealed class IndexedCoffsetDialect : INapsDialect
	{
		public string Id => "indexed-coffset-v1";

		public IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Decode(ReadOnlySpan<byte> naps, long sourceLength)
		{
			return NapsIndexedLogicalMap.Restore(naps, ProsperoPs5InnerImageReader.WalkBlocksCore(naps, sourceLength, nativeSpans: false));
		}
	}

	private static readonly INapsDialect[] Dialects = new INapsDialect[3]
	{
		new CoffsetDialect(nativeSpans: false),
		new CoffsetDialect(nativeSpans: true),
		new IndexedCoffsetDialect()
	};

	internal static NapsBlockPlan Select(ReadOnlySpan<byte> naps, long sourceLength, Action<IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock>>? probe)
	{
		List<string> list = new List<string>();
		List<(string Id, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Blocks)> candidates = new List<(string, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock>)>();
		long? num = NapsIndexedLogicalMap.ReadMountEnd(naps);
		INapsDialect[] dialects = Dialects;
		foreach (INapsDialect napsDialect in dialects)
		{
			try
			{
				IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> readOnlyList = napsDialect.Decode(naps, sourceLength);
				long num2 = readOnlyList[readOnlyList.Count - 1].UncompressedOffset + readOnlyList[readOnlyList.Count - 1].UncompressedLength;
				if (num.HasValue)
				{
					long valueOrDefault = num.GetValueOrDefault();
					if (num2 != valueOrDefault)
					{
						throw new InvalidDataException($"Logical coverage {num2} disagrees with the exact NAPS fidx mount end {valueOrDefault}.");
					}
				}
				candidates.Add((napsDialect.Id, readOnlyList));
			}
			catch (InvalidDataException ex)
			{
				list.Add(napsDialect.Id + ": " + ex.Message);
			}
		}
		if (candidates.Skip(1).Any(((string Id, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Blocks) candidate) => !Equivalent(candidates[0].Blocks, candidate.Blocks)))
		{
			if (probe == null)
			{
				throw new InvalidDataException("Ambiguous NAPS interpretations require a PFS metadata probe.");
			}
			for (int num3 = candidates.Count - 1; num3 >= 0; num3--)
			{
				try
				{
					probe(candidates[num3].Blocks);
				}
				catch (Exception ex2) when ((ex2 is IOException || ex2 is InvalidDataException || ex2 is ArgumentException || ex2 is OverflowException) ? true : false)
				{
					list.Add(candidates[num3].Id + ": PFS probe: " + ex2.Message);
					candidates.RemoveAt(num3);
				}
			}
			if (candidates.Skip(1).Any(((string Id, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Blocks) candidate) => !Equivalent(candidates[0].Blocks, candidate.Blocks)))
			{
				throw new InvalidDataException("Ambiguous NAPS interpretations: incompatible maps both passed PFS metadata validation.");
			}
		}
		if (candidates.Count == 0)
		{
			throw new InvalidDataException("No supported NAPS interpretation. " + string.Join(" | ", list));
		}
		foreach (var item in candidates.Skip(1))
		{
			list.Add(item.Id + ": equivalent logical map.");
		}
		return new NapsBlockPlan(candidates[0].Id, candidates[0].Blocks, list);
	}

	private static bool Equivalent(IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> left, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> right)
	{
		if (left.Count != right.Count)
		{
			return false;
		}
		for (int i = 0; i < left.Count; i++)
		{
			ProsperoPs5InnerImageReader.InnerBlock innerBlock = Normalize(left, i);
			ProsperoPs5InnerImageReader.InnerBlock innerBlock2 = Normalize(right, i);
			if (innerBlock != innerBlock2)
			{
				return false;
			}
		}
		return true;
	}

	private static ProsperoPs5InnerImageReader.InnerBlock Normalize(IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, int index)
	{
		ProsperoPs5InnerImageReader.InnerBlock innerBlock = blocks[index];
		if (innerBlock.IsAlias && (innerBlock.SourceBlockIndex < 0 || innerBlock.SourceBlockIndex >= index || blocks[innerBlock.SourceBlockIndex].UncompressedLength < innerBlock.UncompressedLength))
		{
			innerBlock = innerBlock with
			{
				IsZeroHole = true
			};
		}
		if (innerBlock.IsZeroHole)
		{
			return new ProsperoPs5InnerImageReader.InnerBlock(0L, 0, 0, innerBlock.UncompressedOffset, innerBlock.UncompressedLength, IsKraken: false, IsZeroHole: true);
		}
		return innerBlock with
		{
			RunIndex = -1,
			EvenCompressedLength = (innerBlock.IsKraken ? innerBlock.EvenCompressedLength : 0),
			CompressedLength = ((!innerBlock.IsKraken && !innerBlock.IsAlias) ? innerBlock.UncompressedLength : innerBlock.CompressedLength)
		};
	}

	internal static void ProbePfs(IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, long sourceLength, Func<long, int, byte[]> read, long superblockHint = -1L)
	{
		long remaining = 33554432L;
		using (ProsperoInnerMountStream mount = new ProsperoInnerMountStream(blocks, sourceLength, ReadProbe))
		{
			if (!ProsperoInnerPfsReader.Enumerate(mount, superblockHint).Any((ProsperoInnerPfsReader.Entry e) => e.IsDirectory && e.Path == "/uroot"))
			{
				throw ProsperoErrorInfo.Unsupported("NAPS metadata probe found no /uroot directory.");
			}
		}
		byte[] ReadProbe(long offset, int length)
		{
			if (length > remaining)
			{
				throw new NotSupportedException("NAPS interpretation remains ambiguous: metadata probe exceeded its 32 MiB source-read budget.");
			}
			remaining -= length;
			return read(offset, length);
		}
	}
}
