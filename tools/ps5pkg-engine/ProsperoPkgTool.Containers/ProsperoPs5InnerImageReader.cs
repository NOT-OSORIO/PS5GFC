using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProsperoPkgTool.Compression;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPs5InnerImageReader
{
	public readonly record struct InnerBlock(long CompressedOffset, int CompressedLength, int EvenCompressedLength, long UncompressedOffset, int UncompressedLength, bool IsKraken, bool IsZeroHole = false, bool IsAlias = false, int SourceBlockIndex = -1, int RunIndex = -1, int KrakenFlags = 0);

	private const int Chunk128K = 131072;

	private const int Ublock256K = 262144;

	private const int OuterBlock64K = 65536;

	private const uint Mod256K = 262143u;

	public static IReadOnlyList<InnerBlock> WalkBlocks(ReadOnlySpan<byte> naps, long innerImageLength)
	{
		return NapsDialectRegistry.Select(naps, innerImageLength, null);
	}

	internal static IReadOnlyList<InnerBlock> WalkBlocksCore(ReadOnlySpan<byte> naps, long innerImageLength, bool nativeSpans)
	{
		IReadOnlyList<NapsCblockInfoEntry> readOnlyList = DecodeCblockInfos(naps);
		if (nativeSpans)
		{
			if (!readOnlyList.Any((NapsCblockInfoEntry e) => !e.IsRunBase && ((e.Raw[8] & 8) != 0 || e.KdePredictor != 0)) && readOnlyList.Count >= 3 && readOnlyList[0].IsRunBase)
			{
				if (!readOnlyList[readOnlyList.Count - 1].IsRunBase)
				{
					if (readOnlyList[readOnlyList.Count - 1].Even == 0)
					{
						if (readOnlyList[readOnlyList.Count - 1].Odd == 0)
						{
							goto IL_00a5;
						}
					}
				}
			}
			throw new InvalidDataException("Native span layout requires unmarked KDE=0 records, an initial RUN and a terminator.");
		}
		goto IL_00a5;
		IL_00a5:
		if (nativeSpans && readOnlyList.Zip(readOnlyList.Skip(1)).Any(((NapsCblockInfoEntry First, NapsCblockInfoEntry Second) pair) => pair.First.IsRunBase && pair.Second.IsRunBase))
		{
			throw new InvalidDataException("Consecutive NAPS RUN records have no STD span.");
		}
		List<(NapsCblockInfoEntry, NapsCblockInfoEntry?, int, int, uint)> list = new List<(NapsCblockInfoEntry, NapsCblockInfoEntry?, int, int, uint)>();
		NapsCblockInfoEntry? item = null;
		int item2 = -1;
		for (int num = 0; num < readOnlyList.Count; num++)
		{
			if (readOnlyList[num].IsRunBase)
			{
				item = readOnlyList[num];
				item2 = num;
			}
			else
			{
				uint item3;
				if (num + 1 >= readOnlyList.Count)
				{
					item3 = readOnlyList[num].CoffsetStartMod256K;
				}
				else
				{
					item3 = (readOnlyList[num + 1].IsRunBase ? readOnlyList[num + 1].CoffsetEndMod256K : readOnlyList[num + 1].CoffsetStartMod256K);
				}
				list.Add((readOnlyList[num], item, item2, num, item3));
			}
		}
		if (list.Count < 2)
		{
			throw new InvalidDataException("NAPS CblockInfo does not contain a STD block and terminator.");
		}
		bool flag = !nativeSpans && ProsperoNapsLayout.DecodeHeader(naps).CompressionType != 0 && !list.Any(((NapsCblockInfoEntry Entry, NapsCblockInfoEntry? Run, int RunIndex, int EntryIndex, uint EndCoffMod) s) => (s.Entry.Raw[8] & 8) != 0) && !list.Any(((NapsCblockInfoEntry Entry, NapsCblockInfoEntry? Run, int RunIndex, int EntryIndex, uint EndCoffMod) s) =>
		{
			NapsCblockInfoEntry? item7 = s.Run;
			return item7.HasValue && item7.GetValueOrDefault().CoffsetStart256K != 0;
		});
		List<InnerBlock> list2 = new List<InnerBlock>(list.Count - 1);
		long num2 = 0L;
		long num3 = 0L;
		int num4 = -1;
		Dictionary<long, int> dictionary = new Dictionary<long, int>();
		for (int num5 = 0; num5 + 1 < list.Count; num5++)
		{
			NapsCblockInfoEntry item4 = list[num5].Item1;
			NapsCblockInfoEntry item5 = list[num5 + 1].Item1;
			if (flag)
			{
				num3 = item4.CoffsetStartMod256K;
			}
			else
			{
				NapsCblockInfoEntry? item6 = list[num5].Item2;
				if (item6.HasValue)
				{
					NapsCblockInfoEntry valueOrDefault = item6.GetValueOrDefault();
					if (list[num5].Item3 != num4)
					{
						num3 = (long)(((ulong)valueOrDefault.CoffsetStart256K << 18) | item4.CoffsetStartMod256K);
						num4 = list[num5].Item3;
					}
				}
			}
			int num6 = (int)(((item5.UoffsetStart - item4.UoffsetStart - 1) & 0x3FFFF) + 1);
			int num7 = (int)(flag ? ((item5.CoffsetStartMod256K - item4.CoffsetStartMod256K) & 0x3FFFF) : ((list[num5].Item5 - item4.CoffsetStartMod256K) & 0x3FFFF));
			int num8 = (int)(item4.ClenEvenMinus1 + 1);
			bool flag2 = (item4.Even == 0 && item4.Odd == 0 && item4.KdePredictor == 0) || (item4.Even == 1 && item4.KdePredictor == 0 && item4.ClenEvenMinus1 == 7 && ((item4.Odd == 1 && num7 == 16 && num6 > 16) || (item4.Odd == 0 && num7 == 8 && num6 > 8)));
			bool flag3 = (item4.Raw[8] & 8) != 0 && !flag2;
			if (nativeSpans && !flag2 && item4.Even == 1 && (uint)item4.Odd == ((num6 > 131072) ? 1u : 0u) && num8 == Math.Min(num6, 131072) && (ulong)num7 == ((ulong)num6 & 0x3FFFFuL))
			{
				flag3 = true;
			}
			if (flag3)
			{
				num7 = num6;
			}
			bool flag4 = ((!flag) ? (!flag3 && !flag2 && num7 == 0) : (!flag2 && (num7 == 0 || (item4.KdePredictor == 0 && item4.Even == 1 && item4.Odd == 1 && num7 < num6))));
			bool flag5 = !flag3 && !flag4 && !flag2 && num7 < num6;
			if (nativeSpans)
			{
				if ((num3 & 0x3FFFF) != item4.CoffsetStartMod256K)
				{
					throw new InvalidDataException($"NAPS source cursor disagrees with STD record {list[num5].Item4}.");
				}
				NapsCblockInfoEntry? item6 = list[num5].Item2;
				if (!item6.HasValue || (list[num5].Item4 > 0 && readOnlyList[list[num5].Item4 - 1].IsRunBase && readOnlyList[list[num5].Item4 - 1].KeyTableIdx != 0))
				{
					throw ProsperoErrorInfo.Unsupported($"Unsupported NAPS RUN at record {list[num5].Item4}.");
				}
				if (!flag4 && (num3 < 0 || num3 > innerImageLength || num7 > innerImageLength - num3))
				{
					throw new InvalidDataException($"NAPS source outside image at record {list[num5].Item4}.");
				}
				if (!flag2 && !flag4 && !flag3 && !flag5)
				{
					throw new InvalidDataException($"Invalid native stored span at record {list[num5].Item4}.");
				}
				if (flag5 && (num7 <= 0 || num8 > num7 || (num6 > 131072 && num8 == num7)))
				{
					throw new InvalidDataException($"Invalid compressed range at record {list[num5].Item4}.");
				}
			}
			int value = -1;
			if (flag4)
			{
				if (!dictionary.TryGetValue(num3, out value))
				{
					value = -1;
				}
				if (!flag && (value < 0 || value >= list2.Count || list2[value].CompressedOffset != num3))
				{
					throw new InvalidDataException($"NAPS dedup alias at logical 0x{num2:X} references compressed offset 0x{num3:X} with no matching source block.");
				}
				if (nativeSpans && list2[value].UncompressedLength < num6)
				{
					throw new InvalidDataException($"NAPS alias at logical 0x{num2:X} exceeds its source block.");
				}
			}
			int krakenFlags = (flag5 ? DeriveKrakenFlags(item4.Even, item4.Odd, num6 > 131072) : 0);
			list2.Add(new InnerBlock(num3, num7, num8, num2, num6, flag5, flag2, flag4, value, num4, krakenFlags));
			if (!flag2 && !flag4)
			{
				dictionary[num3] = list2.Count - 1;
			}
			num2 += num6;
			if ((!flag2 | nativeSpans) && !flag4)
			{
				num3 += num7;
			}
		}
		if (nativeSpans && (num2 <= ((long)ProsperoNapsLayout.DecodeHeader(naps).NumUBlocks - 1L) * 262144 || num2 > (long)ProsperoNapsLayout.DecodeHeader(naps).NumUBlocks * 262144L))
		{
			throw new InvalidDataException("Native span logical coverage disagrees with the NAPS ublock count.");
		}
		return list2;
	}

	private static IReadOnlyList<NapsCblockInfoEntry> DecodeCblockInfos(ReadOnlySpan<byte> naps)
	{
		NapsLayoutCounts napsLayoutCounts = ProsperoNapsLayout.DecodeHeader(naps);
		long num = ProsperoNapsLayout.ComputeCblockInfoBase(naps);
		int numCblockInfo = napsLayoutCounts.NumCblockInfo;
		if (num < 0 || num > naps.Length || (long)numCblockInfo * 9L > naps.Length - num)
		{
			throw new InvalidDataException($"Truncated NAPS CblockInfo table: declared {numCblockInfo} records at 0x{num:X}.");
		}
		List<NapsCblockInfoEntry> list = new List<NapsCblockInfoEntry>(numCblockInfo);
		for (int i = 0; i < numCblockInfo; i++)
		{
			long num2 = num + (long)i * 72L / 8;
			if (num2 + 9 > naps.Length)
			{
				throw new InvalidDataException($"Truncated NAPS CblockInfo record {i} of {numCblockInfo}.");
			}
			list.Add(ProsperoNapsLayout.DecodeCblockInfoEntry(naps.Slice((int)num2, 9)));
		}
		return list;
	}

	public static byte[] ReconstructMount(byte[] innerImage, byte[] naps)
	{
		ArgumentNullException.ThrowIfNull(innerImage, "innerImage");
		ArgumentNullException.ThrowIfNull(naps, "naps");
		IReadOnlyList<InnerBlock> blocks = NapsDialectRegistry.Select(naps, innerImage.Length, (IReadOnlyList<InnerBlock> candidate) =>
		{
			NapsDialectRegistry.ProbePfs(candidate, innerImage.Length, (long offset, int length) => innerImage.AsSpan(checked((int)offset), length).ToArray(), -1L);
		});
		return ReconstructMount(innerImage, blocks);
	}

	internal static byte[] ReconstructMount(byte[] innerImage, IReadOnlyList<InnerBlock> blocks)
	{
		long num = 0L;
		foreach (InnerBlock block in blocks)
		{
			num = Math.Max(num, block.UncompressedOffset + block.UncompressedLength);
		}
		num = (num + 65535) & -65536;
		if (num <= 0 || num > int.MaxValue)
		{
			throw new InvalidDataException($"Implausible inner mount size {num}.");
		}
		byte[] array = new byte[num];
		foreach (InnerBlock block2 in blocks)
		{
			int num2 = (int)Math.Min(block2.UncompressedLength, array.Length - block2.UncompressedOffset);
			if (num2 <= 0 || block2.IsZeroHole)
			{
				continue;
			}
			if (block2.IsAlias)
			{
				if (block2.SourceBlockIndex >= 0)
				{
					if (block2.SourceBlockIndex >= blocks.Count)
					{
						throw new InvalidDataException($"Invalid NAPS alias source {block2.SourceBlockIndex}.");
					}
					InnerBlock innerBlock = blocks[block2.SourceBlockIndex];
					if (innerBlock.UncompressedLength >= num2)
					{
						Array.Copy(array, innerBlock.UncompressedOffset, array, block2.UncompressedOffset, num2);
					}
				}
			}
			else if (!block2.IsKraken)
			{
				if (block2.CompressedOffset < 0 || block2.CompressedLength < num2 || block2.CompressedOffset + num2 > innerImage.Length)
				{
					throw new InvalidDataException($"Stored NAPS block at logical 0x{block2.UncompressedOffset:X} reads outside pfs_image.dat (source 0x{block2.CompressedOffset:X}, length 0x{num2:X}).");
				}
				Array.Copy(innerImage, block2.CompressedOffset, array, block2.UncompressedOffset, num2);
			}
			else
			{
				DecodeKrakenBlock(innerImage, block2, array);
			}
		}
		return array;
	}

	public static long MountSize(IReadOnlyList<InnerBlock> blocks)
	{
		long num = 0L;
		foreach (InnerBlock block in blocks)
		{
			num = Math.Max(num, block.UncompressedOffset + block.UncompressedLength);
		}
		return (num + 65535) & -65536;
	}

	public static byte[] DecodeBlock(IReadOnlyList<InnerBlock> blocks, int index, Func<long, int, byte[]> readInnerImage, long innerImageLength)
	{
		ArgumentNullException.ThrowIfNull(blocks, "blocks");
		ArgumentNullException.ThrowIfNull(readInnerImage, "readInnerImage");
		return DecodeBlock(blocks, index, readInnerImage, innerImageLength, 0);
	}

	private static byte[] DecodeBlock(IReadOnlyList<InnerBlock> blocks, int index, Func<long, int, byte[]> readInnerImage, long innerImageLength, int depth)
	{
		if (index < 0 || index >= blocks.Count)
		{
			throw new ArgumentOutOfRangeException("index");
		}
		if (depth > 32)
		{
			throw new InvalidDataException("NAPS dedup alias chain is too deep to resolve.");
		}
		InnerBlock b = blocks[index];
		int uncompressedLength = b.UncompressedLength;
		byte[] array = new byte[uncompressedLength];
		if (uncompressedLength == 0 || b.IsZeroHole)
		{
			return array;
		}
		if (b.IsAlias)
		{
			if (b.SourceBlockIndex < 0 || b.SourceBlockIndex >= blocks.Count)
			{
				return array;
			}
			int num = Math.Min(uncompressedLength, blocks[b.SourceBlockIndex].UncompressedLength);
			if (num > 0)
			{
				Array.Copy(DecodeBlock(blocks, b.SourceBlockIndex, readInnerImage, innerImageLength, depth + 1), 0, array, 0, num);
			}
			return array;
		}
		if (!b.IsKraken)
		{
			if (b.CompressedOffset < 0 || b.CompressedLength < uncompressedLength || b.CompressedOffset + uncompressedLength > innerImageLength)
			{
				throw new InvalidDataException($"Stored NAPS block at logical 0x{b.UncompressedOffset:X} reads outside pfs_image.dat (source 0x{b.CompressedOffset:X}, length 0x{uncompressedLength:X}).");
			}
			byte[] array2 = readInnerImage(b.CompressedOffset, uncompressedLength);
			if (array2.Length < uncompressedLength)
			{
				throw new InvalidDataException($"Stored NAPS block at logical 0x{b.UncompressedOffset:X} returned only 0x{array2.Length:X} of 0x{uncompressedLength:X} bytes.");
			}
			if (array2.Length != uncompressedLength)
			{
				return array2[..uncompressedLength];
			}
			return array2;
		}
		if (b.CompressedOffset < 0 || b.CompressedLength <= 0 || b.CompressedOffset + b.CompressedLength > innerImageLength)
		{
			throw new InvalidDataException($"Kraken NAPS block at logical 0x{b.UncompressedOffset:X} has invalid source range 0x{b.CompressedOffset:X}+0x{b.CompressedLength:X}.");
		}
		DecodeKrakenDeterministic(readInnerImage(b.CompressedOffset, b.CompressedLength), b, array);
		return array;
	}

	private static void DecodeKrakenDeterministic(ReadOnlySpan<byte> compressed, InnerBlock b, Span<byte> destination)
	{
		int firstChunkComp = ((b.UncompressedLength > 131072) ? b.EvenCompressedLength : 0);
		KrakenDecoder.KrakenDecodeStatus krakenDecodeStatus;
		try
		{
			krakenDecodeStatus = KrakenDecoder.DecodeBlock(compressed, b.KrakenFlags, firstChunkComp, destination);
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException) ? true : false)
		{
			throw new InvalidDataException(KrakenFailure(b, $"threw {ex.GetType().Name}, flags 0x{b.KrakenFlags:X2}"), ex);
		}
		if (krakenDecodeStatus != KrakenDecoder.KrakenDecodeStatus.Success)
		{
			throw new InvalidDataException(KrakenFailure(b, $"status {krakenDecodeStatus}, flags 0x{b.KrakenFlags:X2}"));
		}
	}

	private static string KrakenFailure(InnerBlock b, string detail)
	{
		return $"Kraken decode failed for NAPS block at logical 0x{b.UncompressedOffset:X}, source 0x{b.CompressedOffset:X}+0x{b.CompressedLength:X} ({detail}).";
	}

	public static void DecodeAllBlocks(IReadOnlyList<InnerBlock> blocks, Func<long, int, byte[]> readInnerImage, long innerImageLength)
	{
		ArgumentNullException.ThrowIfNull(blocks, "blocks");
		ArgumentNullException.ThrowIfNull(readInnerImage, "readInnerImage");
		for (int i = 0; i < blocks.Count; i++)
		{
			DecodeBlock(blocks, i, readInnerImage, innerImageLength, 0);
		}
	}

	public static void ReconstructMountToFile(IReadOnlyList<InnerBlock> blocks, long innerImageLength, Func<long, int, byte[]> readInnerImage, string outputPath)
	{
		ArgumentNullException.ThrowIfNull(blocks, "blocks");
		ArgumentNullException.ThrowIfNull(readInnerImage, "readInnerImage");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		long num = MountSize(blocks);
		if (num <= 0)
		{
			throw new InvalidDataException($"Implausible inner mount size {num}.");
		}
		string fullPath = Path.GetFullPath(outputPath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		using FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.RandomAccess);
		fileStream.SetLength(num);
		byte[] buffer = new byte[262144];
		for (int i = 0; i < blocks.Count; i++)
		{
			InnerBlock b = blocks[i];
			int num2 = (int)Math.Min(b.UncompressedLength, num - b.UncompressedOffset);
			if (num2 <= 0 || b.IsZeroHole)
			{
				continue;
			}
			if (b.IsAlias)
			{
				if (b.SourceBlockIndex >= 0 && b.SourceBlockIndex < blocks.Count)
				{
					InnerBlock innerBlock = blocks[b.SourceBlockIndex];
					if (innerBlock.UncompressedLength >= num2)
					{
						fileStream.Position = innerBlock.UncompressedOffset;
						ReadFully(fileStream, buffer, num2);
						fileStream.Position = b.UncompressedOffset;
						fileStream.Write(buffer, 0, num2);
					}
				}
				continue;
			}
			if (!b.IsKraken)
			{
				if (b.CompressedOffset < 0 || b.CompressedLength < num2 || b.CompressedOffset + num2 > innerImageLength)
				{
					throw new InvalidDataException($"Stored NAPS block at logical 0x{b.UncompressedOffset:X} reads outside pfs_image.dat (source 0x{b.CompressedOffset:X}, length 0x{num2:X}).");
				}
				byte[] buffer2 = readInnerImage(b.CompressedOffset, num2);
				fileStream.Position = b.UncompressedOffset;
				fileStream.Write(buffer2, 0, num2);
				continue;
			}
			if (b.CompressedOffset < 0 || b.CompressedLength <= 0 || b.CompressedOffset + b.CompressedLength > innerImageLength)
			{
				throw new InvalidDataException($"Kraken NAPS block at logical 0x{b.UncompressedOffset:X} has invalid source range 0x{b.CompressedOffset:X}+0x{b.CompressedLength:X}.");
			}
			byte[] array = readInnerImage(b.CompressedOffset, b.CompressedLength);
			byte[] array2 = new byte[b.UncompressedLength];
			DecodeKrakenDeterministic(array, b, array2);
			fileStream.Position = b.UncompressedOffset;
			fileStream.Write(array2, 0, num2);
		}
		fileStream.Flush();
	}

	private static void ReadFully(Stream stream, byte[] buffer, int length)
	{
		int num;
		for (int i = 0; i < length; i += num)
		{
			num = stream.Read(buffer, i, length - i);
			if (num <= 0)
			{
				throw new EndOfStreamException("The reconstructed mount ended before its declared size.");
			}
		}
	}

	private static void DecodeKrakenBlock(byte[] innerImage, InnerBlock b, byte[] mount)
	{
		if (b.CompressedOffset < 0 || b.CompressedOffset + b.CompressedLength > innerImage.Length || b.CompressedLength <= 0)
		{
			throw new InvalidDataException($"Kraken NAPS block at logical 0x{b.UncompressedOffset:X} has invalid source range 0x{b.CompressedOffset:X}+0x{b.CompressedLength:X}.");
		}
		DecodeKrakenDeterministic(innerImage.AsSpan((int)b.CompressedOffset, b.CompressedLength), destination: mount.AsSpan((int)b.UncompressedOffset, b.UncompressedLength), b: b);
	}

	private static int DeriveKrakenFlags(byte even, byte odd, bool multiChunk)
	{
		int num = 0;
		if ((even & 4) != 0)
		{
			num |= 2;
		}
		if ((even & 2) != 0)
		{
			num |= 1;
		}
		if (multiChunk)
		{
			if ((odd & 4) != 0)
			{
				num |= 0x20;
			}
			if ((odd & 2) != 0)
			{
				num |= 0x10;
			}
		}
		return num;
	}

	public static byte[] ReconstructRunEncryptedMount(byte[] encryptedVirtualImage, byte[] naps, AesXts xts)
	{
		ArgumentNullException.ThrowIfNull(encryptedVirtualImage, "encryptedVirtualImage");
		ArgumentNullException.ThrowIfNull(naps, "naps");
		ArgumentNullException.ThrowIfNull(xts, "xts");
		NapsLayoutDocument napsLayoutDocument = ProsperoNapsLayout.Parse(naps);
		List<byte[]> list = new List<byte[]>();
		NapsCblockInfoEntry? napsCblockInfoEntry = null;
		int runIndex = -1;
		int num = -1;
		long num2 = 0L;
		for (int i = 0; i + 1 < napsLayoutDocument.CblockInfos.Count; i++)
		{
			NapsCblockInfoEntry napsCblockInfoEntry2 = napsLayoutDocument.CblockInfos[i];
			if (napsCblockInfoEntry2.IsRunBase)
			{
				napsCblockInfoEntry = napsCblockInfoEntry2;
				runIndex = i;
				num = i + 1;
			}
			else
			{
				if (napsCblockInfoEntry2.Even == 0)
				{
					continue;
				}
				if (napsCblockInfoEntry.HasValue)
				{
					NapsCblockInfoEntry valueOrDefault = napsCblockInfoEntry.GetValueOrDefault();
					if (valueOrDefault.KeyTableIdx == 0 && num >= 0 && num < napsLayoutDocument.CblockInfos.Count)
					{
						NapsCblockInfoEntry napsCblockInfoEntry3 = napsLayoutDocument.CblockInfos[i + 1];
						int num3 = (int)(((napsCblockInfoEntry3.IsRunBase ? napsCblockInfoEntry3.CoffsetEndMod256K : napsCblockInfoEntry3.CoffsetStartMod256K) - napsCblockInfoEntry2.CoffsetStartMod256K) & 0x3FFFF);
						int num4 = (napsCblockInfoEntry3.IsRunBase ? (i + 2) : (i + 1));
						if (num4 >= napsLayoutDocument.CblockInfos.Count || napsLayoutDocument.CblockInfos[num4].IsRunBase)
						{
							throw new InvalidDataException($"NAPS record {i} has no following STD boundary.");
						}
						int num5 = (int)(((napsLayoutDocument.CblockInfos[num4].UoffsetStart - napsCblockInfoEntry2.UoffsetStart - 1) & 0x3FFFF) + 1);
						long sourceOffset = (long)(((ulong)valueOrDefault.CoffsetStart256K << 18) | napsCblockInfoEntry2.CoffsetStartMod256K);
						NapsCblockInfoEntry napsCblockInfoEntry4 = napsLayoutDocument.CblockInfos[num];
						checked
						{
							int runStartBlock = (int)unchecked((long)(((ulong)valueOrDefault.CoffsetStart256K << 18) | napsCblockInfoEntry4.CoffsetStartMod256K) / 65536L);
							byte[] array = DecryptRunRange(encryptedVirtualImage, xts, sourceOffset, num3, valueOrDefault.TweakIdxStart, runStartBlock, runIndex);
							byte[] array2;
							if (num3 == num5)
							{
								array2 = array;
							}
							else
							{
								if (num3 <= 0)
								{
									throw new InvalidDataException($"Compressed NAPS record {i} has an empty source range.");
								}
								array2 = new byte[num5];
								bool flag = num5 > 131072;
								int num6 = DecodeSonyCodecFlags(napsCblockInfoEntry2, flag);
								int firstChunkComp = (flag ? ((int)napsCblockInfoEntry2.ClenEvenMinus1 + 1) : 0);
								KrakenDecoder.KrakenDecodeStatus krakenDecodeStatus = KrakenDecoder.DecodeBlock(array, num6, firstChunkComp, array2);
								if (krakenDecodeStatus != KrakenDecoder.KrakenDecodeStatus.Success)
								{
									throw new InvalidDataException($"Kraken decode failed for NAPS record {i} ({krakenDecodeStatus}, flags 0x{num6:X2}).");
								}
							}
							num2 += array2.Length;
							if (num2 > int.MaxValue)
							{
								throw new InvalidDataException("Reconstructed patch mount exceeds the supported in-memory size.");
							}
							list.Add(array2);
							continue;
						}
					}
				}
				throw ProsperoErrorInfo.Unsupported($"Unsupported or missing NAPS RUN before CblockInfo record {i}.");
			}
		}
		byte[] array3 = new byte[checked((int)num2)];
		int num7 = 0;
		foreach (byte[] item in list)
		{
			item.CopyTo(array3, num7);
			num7 += item.Length;
		}
		return array3;
	}

	private static byte[] DecryptRunRange(byte[] encrypted, AesXts xts, long sourceOffset, int length, uint tweakStart, int runStartBlock, int runIndex)
	{
		if (sourceOffset < 0 || length < 0 || sourceOffset > encrypted.Length || length > encrypted.Length - sourceOffset)
		{
			throw new InvalidDataException($"NAPS RUN {runIndex} source range 0x{sourceOffset:X}+0x{length:X} is outside the virtual image.");
		}
		if (length == 0)
		{
			return Array.Empty<byte>();
		}
		int num;
		int num2;
		byte[] array;
		checked
		{
			num = (int)unchecked(sourceOffset / 65536);
			num2 = (int)unchecked(checked(sourceOffset + length - 1) / 65536);
			if (num < runStartBlock)
			{
				throw new InvalidDataException($"NAPS RUN {runIndex} source precedes its tweak origin.");
			}
			array = new byte[(num2 - num + 1) * 65536];
		}
		for (int i = num; i <= num2; i++)
		{
			byte[] array2 = encrypted.AsSpan(i * 65536, 65536).ToArray();
			xts.Decrypt(array2, checked(tweakStart + (ulong)(i - runStartBlock))).CopyTo(array, (i - num) * 65536);
		}
		checked
		{
			int start = (int)(sourceOffset - unchecked((long)num) * 65536L);
			return array.AsSpan(start, length).ToArray();
		}
	}

	private static int DecodeSonyCodecFlags(NapsCblockInfoEntry item, bool multiChunk)
	{
		int num;
		switch (item.Even)
		{
		case 1:
			num = 0;
			break;
		case 4:
		case 5:
			num = 2;
			break;
		case 6:
		case 7:
			num = 3;
			break;
		default:
			throw ProsperoErrorInfo.Unsupported($"Unsupported Sony NAPS even codec selector {item.Even}.");
		}
		int num2 = num;
		if (!multiChunk)
		{
			return num2;
		}
		switch (item.Odd)
		{
		case 1:
			num = 0;
			break;
		case 4:
		case 5:
			num = 32;
			break;
		case 6:
		case 7:
			num = 48;
			break;
		default:
			throw ProsperoErrorInfo.Unsupported($"Unsupported Sony NAPS odd codec selector {item.Odd}.");
		}
		int num3 = num;
		return num2 | num3;
	}
}
