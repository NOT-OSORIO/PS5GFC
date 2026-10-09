using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ProsperoPkgTool.Containers;

public static class ProsperoNapsPlanGenerator
{
	public sealed class PlanResult
	{
		public required IReadOnlyList<NapsCblockPlanEntry> Blocks { get; init; }

		public required IReadOnlyList<long> FileLogicalOffsets { get; init; }

		public required int NumUBlocks { get; init; }

		public required int NumOuterBlocks { get; init; }

		public required long MountSize { get; init; }

		public NapsGenerationRequest ToRequest(byte compressionType = 0, byte finalFileOffsetType = 64)
		{
			return new NapsGenerationRequest
			{
				CompressionType = compressionType,
				NumUBlocks = NumUBlocks,
				NumOuterBlocks = NumOuterBlocks,
				FileLogicalOffsets = FileLogicalOffsets,
				FinalFileOffsetType = finalFileOffsetType,
				Blocks = Blocks
			};
		}
	}

	private sealed class BlockEmitter(IReadOnlyList<NapsCblockPlanEntry> sink)
	{
		private readonly List<NapsCblockPlanEntry> _sink = (List<NapsCblockPlanEntry>)sink;

		private long _prevDataEndOnDisk = -1L;

		private bool _first = true;

		public void Emit(NapsCblockPlanEntry entry)
		{
			_sink.Add(entry);
			if (!entry.Terminator)
			{
				_prevDataEndOnDisk = entry.OnDiskOffset + StreamOf(entry);
			}
		}

		public void EmitStoredFull(long onDisk, long logical)
		{
			Emit(new NapsCblockPlanEntry
			{
				StartRun = NeedsRun(onDisk),
				OnDiskOffset = onDisk,
				LogicalOffset = logical,
				EvenChunkCompressedLength = 131072L,
				StreamLength = 262144L,
				Even = 1,
				Odd = 1,
				KdePredictor = 0,
				StoredRaw = true
			});
		}

		public void EmitStoredTail(long onDisk, long logical, long size, long? streamLength = null)
		{
			if (size <= 0 || size > 262144)
			{
				throw new ArgumentOutOfRangeException("size", "Stored NAPS tail must fit one 256KiB ublock.");
			}
			Emit(new NapsCblockPlanEntry
			{
				StartRun = NeedsRun(onDisk),
				OnDiskOffset = onDisk,
				LogicalOffset = logical,
				EvenChunkCompressedLength = Math.Min(size, 131072L),
				StreamLength = (streamLength ?? size),
				Even = 1,
				Odd = ((size > 131072) ? ((byte)1) : ((byte)0)),
				StoredRaw = true
			});
		}

		public void EmitEncoded(ProsperoEncodedBlock block)
		{
			if (block.UncompressedLength <= 0)
			{
				return;
			}
			if ((long)block.UncompressedLength > 262144L)
			{
				throw new ArgumentOutOfRangeException("block", "Encoded NAPS block exceeds 256 KiB.");
			}
			if (block.IsStored)
			{
				if ((long)block.UncompressedLength == 262144 && (long)block.StoredLength == 262144)
				{
					EmitStoredFull(block.PhysicalOffset, block.LogicalOffset);
				}
				else
				{
					EmitStoredTail(block.PhysicalOffset, block.LogicalOffset, block.UncompressedLength, block.StoredLength);
				}
				return;
			}
			if (block.StoredLength <= 0 || block.StoredLength >= block.UncompressedLength)
			{
				throw new InvalidDataException("Kraken block must be strictly smaller than its raw block.");
			}
			var (even, odd) = CodecModes(block.Flags, block.UncompressedLength > 131072);
			Emit(new NapsCblockPlanEntry
			{
				StartRun = NeedsRun(block.PhysicalOffset),
				OnDiskOffset = block.PhysicalOffset,
				LogicalOffset = block.LogicalOffset,
				EvenChunkCompressedLength = ((block.UncompressedLength > 131072) ? block.FirstChunkCompressedLength : block.StoredLength),
				StreamLength = block.StoredLength,
				Even = even,
				Odd = odd,
				KdePredictor = 0
			});
		}

		public void EmitEncodedMetadata(long onDisk, long logical, ProsperoPfsv3Writer.CompressedBlock block)
		{
			if (block.UncompressedSize > 0)
			{
				var (even, odd) = CodecModes(block.MultiChunk ? 34 : 2, block.MultiChunk);
				Emit(new NapsCblockPlanEntry
				{
					StartRun = NeedsRun(onDisk),
					OnDiskOffset = onDisk,
					LogicalOffset = logical,
					EvenChunkCompressedLength = (block.MultiChunk ? block.FirstChunkCompressedSize : block.CompressedSize),
					StreamLength = block.CompressedSize,
					Even = even,
					Odd = odd,
					KdePredictor = 0
				});
			}
		}

		private static (byte Even, byte Odd) CodecModes(int flags, bool multiChunk)
		{
			byte item = (byte)(1 | (((flags & 2) != 0) ? 4 : 0) | (((flags & 1) != 0) ? 2 : 0));
			byte item2 = (byte)(multiChunk ? ((byte)((((flags & 0x20) == 0) ? 1 : 4) | (((flags & 0x10) != 0) ? 2 : 0))) : 0);
			return (Even: item, Odd: item2);
		}

		public void EmitHole(long onDisk, long logical, long stream, bool full, bool startRun)
		{
			Emit(new NapsCblockPlanEntry
			{
				StartRun = (startRun || NeedsRun(onDisk)),
				OnDiskOffset = onDisk,
				LogicalOffset = logical,
				EvenChunkCompressedLength = 8L,
				StreamLength = stream,
				Even = 1,
				Odd = (full ? ((byte)1) : ((byte)0)),
				KdePredictor = 0
			});
		}

		private bool NeedsRun(long onDisk)
		{
			bool result = _first || onDisk != _prevDataEndOnDisk;
			_first = false;
			return result;
		}

		private static long StreamOf(NapsCblockPlanEntry e)
		{
			if (!e.Terminator)
			{
				return e.StreamLength;
			}
			return 0L;
		}
	}

	private const long UBlock = 262144L;

	private const int Block64K = 65536;

	private const int Pfsv3DataOffsetField = 170;

	public static PlanResult Generate(ProsperoInnerImageAssembler.InnerImageResult inner)
	{
		ArgumentNullException.ThrowIfNull(inner, "inner");
		List<NapsCblockPlanEntry> list = new List<NapsCblockPlanEntry>();
		BlockEmitter blockEmitter = new BlockEmitter(list);
		if (inner.EncodedBlocks.Count != 0)
		{
			foreach (ProsperoEncodedBlock item in inner.EncodedBlocks.OrderBy((ProsperoEncodedBlock b) => b.SourceBlockIndex))
			{
				blockEmitter.EmitEncoded(item);
			}
		}
		else
		{
			foreach (ProsperoInnerImageAssembler.Placement placement in inner.Placements)
			{
				long uncompressedSize = placement.UncompressedSize;
				long num = uncompressedSize / 262144;
				long num2 = uncompressedSize - num * 262144;
				for (long num3 = 0L; num3 < num; num3++)
				{
					blockEmitter.EmitStoredFull(placement.OnDiskOffset + num3 * 262144, placement.LogicalOffset + num3 * 262144);
				}
				if (num2 > 0 || num == 0L)
				{
					blockEmitter.EmitStoredTail(placement.OnDiskOffset + num * 262144, placement.LogicalOffset + num * 262144, num2);
				}
			}
		}
		long num4 = inner.MetaBaseLogical - inner.DataEndLogical;
		if (num4 > 0)
		{
			int num5 = (int)((num4 + 262144 - 1) / 262144);
			long blockInfoOnDiskOffset = inner.BlockInfoOnDiskOffset;
			long num6 = blockInfoOnDiskOffset;
			for (int num7 = 0; num7 < num5; num7++)
			{
				bool flag = num7 == num5 - 1;
				long num8 = (flag ? num6 : blockInfoOnDiskOffset);
				bool flag2 = Math.Min(262144L, num4 - (long)num7 * 262144L) > 131072;
				long num9 = (flag2 ? 16 : 8);
				blockEmitter.EmitHole(num8, inner.DataEndLogical + (long)num7 * 262144L, num9, flag2, !flag);
				num6 = num8 + num9;
			}
		}
		long num10 = inner.MetaBaseLogical;
		long num11 = inner.MetadataOnDiskOffset + ReadDataSectionOffset(inner.CompressedMetadata);
		foreach (ProsperoPfsv3Writer.CompressedBlock metadataBlock in inner.MetadataBlocks)
		{
			if (metadataBlock.Flags != 0 && metadataBlock.CompressedSize < metadataBlock.UncompressedSize)
			{
				blockEmitter.EmitEncodedMetadata(num11, num10, metadataBlock);
			}
			else if ((long)metadataBlock.CompressedSize >= 262144L)
			{
				blockEmitter.EmitStoredFull(num11, num10);
			}
			else
			{
				blockEmitter.EmitStoredTail(num11, num10, metadataBlock.UncompressedSize, metadataBlock.CompressedSize);
			}
			num10 += metadataBlock.UncompressedSize;
			num11 += metadataBlock.CompressedSize;
		}
		long num12 = inner.Ndblock * 65536;
		list.Add(new NapsCblockPlanEntry
		{
			StartRun = true,
			OnDiskOffset = num11,
			LogicalOffset = num12,
			Terminator = true
		});
		List<long> list2 = new List<long>(inner.Placements.Count + 3);
		foreach (ProsperoInnerImageAssembler.Placement placement2 in inner.Placements)
		{
			if (placement2.UncompressedSize > 0)
			{
				list2.Add(placement2.LogicalOffset);
			}
		}
		list2.Add(inner.DataEndLogical);
		list2.Add(inner.MetaBaseLogical);
		list2.Add(num12);
		return new PlanResult
		{
			Blocks = list,
			FileLogicalOffsets = list2,
			NumUBlocks = (int)((num12 + 262144 - 1) / 262144),
			NumOuterBlocks = (int)((inner.ImageSize + 65536 - 1) / 65536),
			MountSize = num12
		};
	}

	private static long ReadDataSectionOffset(byte[] container)
	{
		if (container.Length < 174)
		{
			return 0L;
		}
		return BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(170, 4));
	}
}
