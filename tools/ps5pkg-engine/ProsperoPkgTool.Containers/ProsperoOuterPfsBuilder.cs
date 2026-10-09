using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoOuterPfsBuilder
{
	private sealed class IndirectTreeNode
	{
		public int BlockIndex;

		public int Depth;

		public int FirstDataOffset;

		public int DataBlockCount;

		public readonly List<IndirectTreeNode> Children = new List<IndirectTreeNode>();
	}

	private readonly record struct IndirectPlan(int InodeLevel, IndirectTreeNode Root);

	public const int BlockSize = 65536;

	private const string FlatPathTableName = "inode_flat_path_table";

	private const string UrootName = "uroot";

	private const int MetadataInodeCount = 3;

	private const ushort ModeDir = 16749;

	private const ushort ModeFile = 33133;

	private const uint FlagsInternalMeta = 131084u;

	private const uint FlagsDir = 12u;

	private const uint FlagsFile = 13u;

	private const long SuperblockVersion = 2L;

	private const long SuperblockMagic = 20130315L;

	private const int SuperblockInodeSigOffset = 80;

	private const int SuperblockUnknownIndexOffset = 876;

	private const int SuperblockSeedOffset = 880;

	private const int SuperblockIcvOffset = 896;

	private const int SuperblockIcvLength = 32;

	private const int SuperblockIcvCoverage = 1440;

	private const ulong SignedSectorFlag = 140737488355328uL;

	private const int S32InodeSize = 712;

	private const int DirectBlockSlots = 12;

	private const int IndirectLevelCount = 5;

	private const int BlockSig32Stride = 36;

	private const int IndirectEntriesPerBlock = 1820;

	public const int TreeEntrySize = 36;

	public const int TreeEntriesPerBlock = 1820;

	public const int TreeLevelCount = 5;

	private const int S32DirectBlockOffset = 100;

	private const int S32IndirectBlockOffset = 532;

	private const int S64InodeSize = 784;

	private const int S64DirectBlockOffset = 104;

	private const int BlockSig64Stride = 40;

	private const uint FltVersion = 1u;

	private const uint FltHeaderSize = 16u;

	private const uint FltDataOffset = 64u;

	private static readonly byte[] FltMagic = new byte[4] { 127, 70, 76, 84 };

	private const ulong FltRoundConstant = 9223372039002292353uL;

	private const ulong FltSeed0 = 10577419142525243217uL;

	private const ulong FltSeed1 = 701355796979237965uL;

	private const int DirentHeaderSize = 16;

	private const int DirentMaxSize = 280;

	private const int DirentTypeFile = 2;

	private const int DirentTypeDirectory = 3;

	private const int DirentTypeDot = 4;

	private const int DirentTypeDotDot = 5;

	public static OuterPfsBuildResult BuildPlaintext(IReadOnlyList<OuterPfsFile> files, OuterPfsBuildParameters parameters, IProgress<ProsperoBuildProgress>? progress = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(files, "files");
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one outer file is required.", "files");
		}
		int num = 0;
		int[] array = new int[files.Count];
		int[] array2 = new int[files.Count];
		for (int i = 0; i < files.Count; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			OuterPfsFile outerPfsFile = files[i];
			ArgumentNullException.ThrowIfNull(outerPfsFile, "f");
			int num2 = Math.Max(1, (outerPfsFile.Data.Length + 65536 - 1) / 65536);
			array[i] = num;
			array2[i] = num2;
			num += num2;
		}
		int num3 = num;
		int num4 = num + 1;
		int num5 = num + 2;
		int num6 = num + 3;
		List<IndirectPlan>[] array3 = new List<IndirectPlan>[files.Count];
		int nextBlock = num6 + 1;
		int indirectBlocksAllocated = 0;
		for (int j = 0; j < files.Count; j++)
		{
			array3[j] = PlanIndirectTree(files[j].Name, array2[j], ref nextBlock, ref indirectBlocksAllocated);
		}
		int num7 = nextBlock;
		int num8 = num7 + 1;
		if ((long)num8 * 65536L > Array.MaxLength)
		{
			throw new NotSupportedException($"The outer image ({(long)num8 * 65536L} bytes) exceeds the in-memory array limit.");
		}
		byte[] array4 = new byte[(long)num8 * 65536L];
		for (int k = 0; k < files.Count; k++)
		{
			byte[] data = files[k].Data;
			Buffer.BlockCopy(data, 0, array4, array[k] * 65536, data.Length);
			progress?.Report(new ProsperoBuildProgress("Outer PFS", k + 1, files.Count)
			{
				StageId = ProsperoBuildStage.OuterPfs
			});
		}
		BuildSuperRootDirents(array4.AsSpan(num5 * 65536, 65536));
		BuildFlatPathTable(array4.AsSpan(num6 * 65536, 65536), files);
		BuildUrootDirents(array4.AsSpan(num7 * 65536, 65536), files);
		BuildInodeTable(array4, num4, parameters, files, array, array2, array3, num5, num6, num7);
		byte[] inodeTableHash = Sha3.Sha3_256(array4.AsSpan(num4 * 65536, 65536));
		BuildSuperblock(array4.AsSpan(num3 * 65536, 65536), parameters, files.Count, num8, num4, inodeTableHash);
		OuterBlockKind[] array5 = new OuterBlockKind[num8];
		for (int l = 0; l < files.Count; l++)
		{
			OuterBlockKind outerBlockKind = (files[l].Signed ? OuterBlockKind.Signed : OuterBlockKind.Data);
			for (int m = 0; m < array2[l]; m++)
			{
				array5[array[l] + m] = outerBlockKind;
			}
		}
		array5[num3] = OuterBlockKind.Plaintext;
		array5[num4] = OuterBlockKind.Signed;
		array5[num5] = OuterBlockKind.Signed;
		array5[num6] = OuterBlockKind.Signed;
		List<IndirectPlan>[] array6 = array3;
		for (int n = 0; n < array6.Length; n++)
		{
			foreach (IndirectPlan item in array6[n])
			{
				MarkTreeSigned(item.Root, array5);
			}
		}
		array5[num7] = OuterBlockKind.Signed;
		return new OuterPfsBuildResult
		{
			Plaintext = array4,
			BlockKinds = array5,
			SuperblockIndex = num3,
			FileFirstBlock = array,
			FileBlockCount = array2,
			InodeTableIndex = num4,
			SuperRootDirentIndex = num5,
			FltIndex = num6,
			UrootDirentIndex = num7
		};
	}

	public static void Encrypt(OuterPfsBuildResult build, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, bool encrypt = true, IProgress<ProsperoBuildProgress>? progress = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(build, "build");
		using AesXts aesXts = new AesXts(dataKey, tweakKey);
		for (int i = 0; i < build.BlockCount; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			OuterBlockKind outerBlockKind = build.BlockKinds[i];
			if (outerBlockKind == OuterBlockKind.Plaintext)
			{
				progress?.Report(new ProsperoBuildProgress("Outer PFS encrypt", i + 1, build.BlockCount)
				{
					StageId = ProsperoBuildStage.OuterPfs
				});
				continue;
			}
			int num = i * 65536;
			ulong sector = ((outerBlockKind == OuterBlockKind.Signed) ? ((ulong)(uint)i | 0x800000000000uL) : ((uint)i));
			byte[] array = build.Plaintext.AsSpan(num, 65536).ToArray();
			(encrypt ? aesXts.Encrypt(array, sector) : aesXts.Decrypt(array, sector)).CopyTo(build.Plaintext, num);
			progress?.Report(new ProsperoBuildProgress("Outer PFS encrypt", i + 1, build.BlockCount)
			{
				StageId = ProsperoBuildStage.OuterPfs
			});
		}
	}

	public static byte[] BuildEncrypted(IReadOnlyList<OuterPfsFile> files, OuterPfsBuildParameters parameters, string contentId, string passcode)
	{
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		byte[] seed = parameters.Seed;
		if (seed == null || seed.Length != 16)
		{
			throw new ArgumentException("A 16-byte build seed is required to encrypt the image.", "parameters");
		}
		OuterPfsBuildResult outerPfsBuildResult = BuildPlaintext(files, parameters);
		(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ProsperoKeys.ComputeEkpfs(contentId, passcode), parameters.Seed);
		var (array, _) = tuple;
		Encrypt(dataKey: tuple.DataKey, build: outerPfsBuildResult, tweakKey: array);
		return outerPfsBuildResult.Plaintext;
	}

	public static OuterPfsFileBuildResult BuildForPackageToFile(IReadOnlyList<OuterPfsFileSource> files, OuterPfsBuildParameters parameters, string outputPath, bool encryptOutput, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, IProgress<ProsperoBuildProgress>? progress = null, long outputOffset = 0L, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(files, "files");
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one outer file is required.", "files");
		}
		if (outputOffset < 0 || outputOffset % 65536 != 0L)
		{
			throw new ArgumentOutOfRangeException("outputOffset", "The outer-PFS output offset must be block-aligned.");
		}
		string fullPath = Path.GetFullPath(outputPath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		long[] array = new long[files.Count];
		int num = 0;
		int[] array2 = new int[files.Count];
		int[] array3 = new int[files.Count];
		for (int i = 0; i < files.Count; i++)
		{
			OuterPfsFileSource outerPfsFileSource = files[i];
			ArgumentNullException.ThrowIfNull(outerPfsFileSource, "f");
			long num2 = Math.Max(1L, ((array[i] = new FileInfo(outerPfsFileSource.Path).Length) + 65536 - 1) / 65536);
			if (num2 > int.MaxValue)
			{
				throw new NotSupportedException($"Outer file '{outerPfsFileSource.Name}' spans {num2} blocks.");
			}
			array2[i] = num;
			array3[i] = (int)num2;
			num += (int)num2;
		}
		int num3 = num;
		int num4 = num + 1;
		int num5 = num + 2;
		int num6 = num + 3;
		List<IndirectPlan>[] array4 = new List<IndirectPlan>[files.Count];
		int indirectRegionStart = num6 + 1;
		int nextBlock = indirectRegionStart;
		int indirectBlocksAllocated = 0;
		for (int j = 0; j < files.Count; j++)
		{
			array4[j] = PlanIndirectTree(files[j].Name, array3[j], ref nextBlock, ref indirectBlocksAllocated);
		}
		int num7 = nextBlock;
		int num8 = num7 + 1;
		long num9 = (long)num8 * 65536L;
		if (num8 > Array.MaxLength / 32)
		{
			throw new NotSupportedException($"The outer image digest table for {num8} blocks is too large.");
		}
		byte[] imageDigests = new byte[num8 * 32];
		int parallelism = ((parameters.BlockParallelism <= 0) ? Environment.ProcessorCount : Math.Clamp(parameters.BlockParallelism, 1, 256));
		byte[] array9;
		byte[] superblockIcv;
		OuterBlockKind[] array10;
		using (FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan))
		{
			using AesXts xts = (encryptOutput ? new AesXts(dataKey, tweakKey) : null);
			fileStream.SetLength(checked(outputOffset + num9));
			WriteDataBlocks(fileStream, files, array2, array3, num, encryptOutput, tweakKey, dataKey, parallelism, imageDigests, outputOffset, progress, cancellationToken);
			byte[] array5 = new byte[65536];
			BuildSuperRootDirents(array5);
			Sha3.Sha3_256(array5).CopyTo(imageDigests, num5 * 32);
			byte[] array6 = new byte[65536];
			BuildFlatPathTableFromNames(array6, files.Select((OuterPfsFileSource f) => f.Name).ToArray());
			Sha3.Sha3_256(array6).CopyTo(imageDigests, num6 * 32);
			byte[] array7 = new byte[65536];
			BuildUrootDirentsFromNames(array7, files.Select((OuterPfsFileSource f) => f.Name).ToArray());
			Sha3.Sha3_256(array7).CopyTo(imageDigests, num7 * 32);
			byte[] indirectRegion = ((indirectBlocksAllocated > 0) ? new byte[checked(indirectBlocksAllocated * 65536)] : null);
			if (indirectRegion != null)
			{
				for (int num10 = 0; num10 < files.Count; num10++)
				{
					int fileFirstBlock = array2[num10];
					foreach (IndirectPlan item in array4[num10])
					{
						SerializeIndirectNode(item.Root, fileFirstBlock, (int dataBlock) => imageDigests.AsSpan(dataBlock * 32, 32).ToArray(), (int blockIndex, byte[] bytes) =>
						{
							bytes.CopyTo(indirectRegion.AsSpan((blockIndex - indirectRegionStart) * 65536));
						}, (int blockIndex, byte[] hash) =>
						{
							hash.CopyTo(imageDigests, blockIndex * 32);
						});
					}
				}
			}
			byte[] array8 = new byte[65536];
			BuildInodeTableFromHashes(array8, parameters, files, array, imageDigests, array2, array3, array4, num5, num6, num7);
			Sha3.Sha3_256(array8).CopyTo(imageDigests, num4 * 32);
			array9 = new byte[65536];
			BuildSuperblock(array9, parameters, files.Count, num8, num4, imageDigests.AsSpan(num4 * 32, 32).ToArray());
			Sha3.Sha3_256(array9).CopyTo(imageDigests, num3 * 32);
			superblockIcv = ComputeSuperblockIcv(array9);
			array10 = new OuterBlockKind[num8];
			for (int num11 = 0; num11 < files.Count; num11++)
			{
				OuterBlockKind outerBlockKind = (files[num11].Signed ? OuterBlockKind.Signed : OuterBlockKind.Data);
				for (int num12 = 0; num12 < array3[num11]; num12++)
				{
					array10[array2[num11] + num12] = outerBlockKind;
				}
			}
			array10[num3] = OuterBlockKind.Plaintext;
			array10[num4] = OuterBlockKind.Signed;
			array10[num5] = OuterBlockKind.Signed;
			array10[num6] = OuterBlockKind.Signed;
			for (int num13 = 0; num13 < indirectBlocksAllocated; num13++)
			{
				array10[indirectRegionStart + num13] = OuterBlockKind.Signed;
			}
			array10[num7] = OuterBlockKind.Signed;
			WriteBlock(fileStream, num3, array9, OuterBlockKind.Plaintext, encryptOutput, xts, outputOffset);
			WriteBlock(fileStream, num4, array8, OuterBlockKind.Signed, encryptOutput, xts, outputOffset);
			WriteBlock(fileStream, num5, array5, OuterBlockKind.Signed, encryptOutput, xts, outputOffset);
			WriteBlock(fileStream, num6, array6, OuterBlockKind.Signed, encryptOutput, xts, outputOffset);
			for (int num14 = 0; num14 < indirectBlocksAllocated; num14++)
			{
				byte[] plaintext = indirectRegion.AsSpan(num14 * 65536, 65536).ToArray();
				WriteBlock(fileStream, indirectRegionStart + num14, plaintext, OuterBlockKind.Signed, encryptOutput, xts, outputOffset);
			}
			WriteBlock(fileStream, num7, array7, OuterBlockKind.Signed, encryptOutput, xts, outputOffset);
			fileStream.Flush();
		}
		return new OuterPfsFileBuildResult
		{
			ImagePath = fullPath,
			ImageLength = num9,
			BlockKinds = array10,
			SuperblockIndex = num3,
			FileFirstBlock = array2,
			FileBlockCount = array3,
			InodeTableIndex = num4,
			SuperRootDirentIndex = num5,
			FltIndex = num6,
			UrootDirentIndex = num7,
			ImageDigests = imageDigests,
			SuperblockIcv = superblockIcv,
			SuperblockPlaintext = array9
		};
	}

	private static void WriteDataBlocks(FileStream outFs, IReadOnlyList<OuterPfsFileSource> files, int[] fileFirstBlock, int[] fileBlockCount, int dataBlockTotal, bool encryptOutput, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> dataKey, int parallelism, byte[] imageDigests, long outputOffset, IProgress<ProsperoBuildProgress>? progress, CancellationToken cancellationToken = default(CancellationToken))
	{
		int num = 0;
		if (parallelism <= 1 || !encryptOutput)
		{
			byte[] array = new byte[65536];
			for (int i = 0; i < files.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				OuterBlockKind kind = (files[i].Signed ? OuterBlockKind.Signed : OuterBlockKind.Data);
				outFs.Position = outputOffset + (long)fileFirstBlock[i] * 65536L;
				using FileStream stream = new FileStream(files[i].Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
				using AesXts xts = (encryptOutput ? new AesXts(dataKey, tweakKey) : null);
				for (int j = 0; j < fileBlockCount[i]; j++)
				{
					int num2 = fileFirstBlock[i] + j;
					int num3 = ReadBlock(stream, array);
					if (num3 < 65536)
					{
						array.AsSpan(num3).Clear();
					}
					Sha3.Sha3_256(array).CopyTo(imageDigests, num2 * 32);
					if (encryptOutput)
					{
						array = ApplyXts(xts, array, kind, num2);
					}
					outFs.Write(array, 0, 65536);
					progress?.Report(new ProsperoBuildProgress("Outer PFS", ++num, dataBlockTotal)
					{
						StageId = ProsperoBuildStage.OuterPfs,
						BytesDone = (long)num * 65536L,
						BytesTotal = (long)dataBlockTotal * 65536L
					});
				}
			}
			return;
		}
		byte[] tweak = tweakKey.ToArray();
		byte[] data = dataKey.ToArray();

		const int BatchBlocks = 128;
		ParallelOptions parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = parallelism
		};
		using CancellationTokenSource pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		CancellationToken pipelineToken = pipelineCts.Token;
		using BlockingCollection<OuterBatch> toCompute = new BlockingCollection<OuterBatch>(3);
		using BlockingCollection<OuterBatch> toWrite = new BlockingCollection<OuterBatch>(3);
		Task readerTask = Task.Run(delegate
		{
			try
			{
				for (int l = 0; l < files.Count; l++)
				{
					pipelineToken.ThrowIfCancellationRequested();
					OuterBlockKind fileKind = (files[l].Signed ? OuterBlockKind.Signed : OuterBlockKind.Data);
					using FileStream stream2 = new FileStream(files[l].Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
					for (int m = 0; m < fileBlockCount[l]; m += BatchBlocks)
					{
						pipelineToken.ThrowIfCancellationRequested();
						int count = Math.Min(BatchBlocks, fileBlockCount[l] - m);
						OuterBatch batch = new OuterBatch(fileKind, fileFirstBlock[l] + m, count);
						for (int n = 0; n < count; n++)
						{
							byte[] block = new byte[65536];
							int got = ReadBlock(stream2, block);
							if (got < 65536)
							{
								block.AsSpan(got).Clear();
							}
							batch.Blocks[n] = block;
						}
						toCompute.Add(batch, pipelineToken);
					}
				}
			}
			finally
			{
				toCompute.CompleteAdding();
			}
		});
		int blocksWritten = 0;
		Task writerTask = Task.Run(delegate
		{
			foreach (OuterBatch done in toWrite.GetConsumingEnumerable(pipelineToken))
			{
				outerFsWrite(done);
			}
		});
		void outerFsWrite(OuterBatch done)
		{
			outFs.Position = outputOffset + (long)done.BaseIndex * 65536L;
			for (int k = 0; k < done.Count; k++)
			{
				outFs.Write(done.Blocks[k], 0, 65536);
			}
			blocksWritten += done.Count;
			progress?.Report(new ProsperoBuildProgress("Outer PFS", blocksWritten, dataBlockTotal)
			{
				StageId = ProsperoBuildStage.OuterPfs,
				BytesDone = (long)blocksWritten * 65536L,
				BytesTotal = (long)dataBlockTotal * 65536L
			});
		}
		Exception failure = null;
		try
		{
			foreach (OuterBatch work in toCompute.GetConsumingEnumerable(pipelineToken))
			{
				OuterBatch current = work;
				Parallel.For(0, current.Count, parallelOptions, () => new AesXts(data, tweak), (int index, ParallelLoopState _, AesXts local) =>
				{
					Sha3.Sha3_256(current.Blocks[index]).CopyTo(imageDigests, (current.BaseIndex + index) * 32);
					current.Blocks[index] = ApplyXts(local, current.Blocks[index], current.Kind, current.BaseIndex + index);
					return local;
				}, (AesXts local) =>
				{
					local.Dispose();
				});
				toWrite.Add(current, pipelineToken);
			}
		}
		catch (Exception ex)
		{
			failure = ex;
			pipelineCts.Cancel();
		}
		finally
		{
			toWrite.CompleteAdding();
		}
		try
		{
			Task.WaitAll(readerTask, writerTask);
		}
		catch (AggregateException ae)
		{
			if (failure == null)
			{
				failure = ae.InnerExceptions.FirstOrDefault((Exception e) => e is not OperationCanceledException) ?? ae.InnerExceptions[0];
			}
		}
		if (failure != null)
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
		}
	}

	private sealed class OuterBatch
	{
		public readonly OuterBlockKind Kind;

		public readonly int BaseIndex;

		public readonly int Count;

		public readonly byte[][] Blocks;

		public OuterBatch(OuterBlockKind kind, int baseIndex, int count)
		{
			Kind = kind;
			BaseIndex = baseIndex;
			Count = count;
			Blocks = new byte[count][];
		}
	}

	private static byte[] ApplyXts(AesXts xts, byte[] plaintext, OuterBlockKind kind, int blockIndex)
	{
		ulong sector = ((kind == OuterBlockKind.Signed) ? ((ulong)(uint)blockIndex | 0x800000000000uL) : ((uint)blockIndex));
		return xts.Encrypt(plaintext, sector);
	}

	private static void WriteBlock(FileStream fs, int blockIndex, byte[] plaintext, OuterBlockKind kind, bool encryptOutput, AesXts? xts, long outputOffset)
	{
		fs.Position = outputOffset + (long)blockIndex * 65536L;
		if (encryptOutput && kind != OuterBlockKind.Plaintext)
		{
			byte[] array = ((kind == OuterBlockKind.Signed) ? ApplyXts(xts, plaintext, kind, blockIndex) : xts.Encrypt(plaintext, (uint)blockIndex));
			fs.Write(array, 0, array.Length);
		}
		else
		{
			fs.Write(plaintext, 0, plaintext.Length);
		}
	}

	private static int ReadBlock(Stream stream, byte[] buffer)
	{
		int i;
		int num;
		for (i = 0; i < buffer.Length; i += num)
		{
			num = stream.Read(buffer, i, buffer.Length - i);
			if (num <= 0)
			{
				break;
			}
		}
		return i;
	}

	private static void BuildFlatPathTableFromNames(Span<byte> block, IReadOnlyList<string> names)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4), 16u);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(8), 64u);
		FltMagic.CopyTo(block.Slice(32));
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(44), (uint)names.Count);
		BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(48), 10577419142525243217uL);
		BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(56), 701355796979237965uL);
		int num = 64;
		for (int i = 0; i < names.Count; i++)
		{
			ulong value = (uint)(3 + i) | ((ulong)(uint)i << 40);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(num), FltPathHash(names[i]));
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(num + 8), value);
			num += 16;
		}
	}

	private static void BuildUrootDirentsFromNames(Span<byte> block, IReadOnlyList<string> names)
	{
		int pos = 0;
		pos = WriteDirent(block, pos, 2u, 4, ".");
		pos = WriteDirent(block, pos, 2u, 5, "..");
		for (int i = 0; i < names.Count; i++)
		{
			pos = WriteDirent(block, pos, (uint)(3 + i), 2, names[i]);
		}
	}

	private static void BuildInodeTableFromHashes(byte[] inodeTable, OuterPfsBuildParameters p, IReadOnlyList<OuterPfsFileSource> files, long[] fileLengths, byte[] imageDigests, int[] fileFirstBlock, int[] fileBlockCount, List<IndirectPlan>[] filePlans, int superRootDirentIndex, int fltIndex, int urootDirentIndex)
	{
		Span<byte> span = stackalloc byte[712];
		WriteHashInode(span, inodeTable, imageDigests, p, 0, 16749, 1, 131084u, 65536L, 65536L, new int[1] { superRootDirentIndex });
		long num = 64 + (long)files.Count * 16L;
		WriteHashInode(span, inodeTable, imageDigests, p, 1, 33133, 1, 131084u, num, num, new int[1] { fltIndex });
		WriteHashInode(span, inodeTable, imageDigests, p, 2, 16749, 3, 12u, 65536L, 65536L, new int[1] { urootDirentIndex });
		for (int i = 0; i < files.Count; i++)
		{
			int num2 = fileFirstBlock[i];
			int num3 = fileBlockCount[i];
			span.Clear();
			BinaryPrimitives.WriteUInt16LittleEndian(span, 33133);
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 13u);
			BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), fileLengths[i]);
			BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), files[i].SizeCompressed ?? fileLengths[i]);
			StampInodeTimes(span, p);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(96), (uint)num3);
			int num4 = Math.Min(num3, 12);
			for (int j = 0; j < num4; j++)
			{
				imageDigests.AsSpan((num2 + j) * 32, 32).CopyTo(span.Slice(100 + j * 36));
				BinaryPrimitives.WriteInt32LittleEndian(span.Slice(100 + j * 36 + 32), num2 + j);
			}
			foreach (IndirectPlan item in filePlans[i])
			{
				imageDigests.AsSpan(item.Root.BlockIndex * 32, 32).CopyTo(span.Slice(532 + item.InodeLevel * 36));
				BinaryPrimitives.WriteInt32LittleEndian(span.Slice(532 + item.InodeLevel * 36 + 32), item.Root.BlockIndex);
			}
			span.CopyTo(inodeTable.AsSpan((3 + i) * 712));
		}
	}

	private static List<IndirectPlan> PlanIndirectTree(string fileName, int fileBlockCount, ref int nextBlock, ref int indirectBlocksAllocated)
	{
		List<IndirectPlan> list = new List<IndirectPlan>();
		int num = Math.Max(0, fileBlockCount - 12);
		int num2 = 12;
		for (int i = 0; i < 5; i++)
		{
			if (num <= 0)
			{
				break;
			}
			int depth = i + 1;
			int num3 = (int)Math.Min(num, Capacity(depth));
			IndirectTreeNode root = AllocateIndirectNode(depth, num2, num3, ref nextBlock, ref indirectBlocksAllocated);
			list.Add(new IndirectPlan(i, root));
			num2 += num3;
			num -= num3;
		}
		if (num > 0)
		{
			long num4 = 12 + TotalTreeCapacity();
			throw new NotSupportedException($"Outer file '{fileName}' spans {fileBlockCount} blocks; one inode addresses at most {num4} blocks (~{num4 * 65536 / 1048576} MiB).");
		}
		return list;
	}

	private static IndirectTreeNode AllocateIndirectNode(int depth, int firstDataOffset, int dataBlockCount, ref int nextBlock, ref int indirectBlocksAllocated)
	{
		IndirectTreeNode indirectTreeNode = new IndirectTreeNode
		{
			BlockIndex = nextBlock++,
			Depth = depth,
			FirstDataOffset = firstDataOffset,
			DataBlockCount = dataBlockCount
		};
		indirectBlocksAllocated++;
		if (depth > 1)
		{
			long val = Capacity(depth - 1);
			int num = dataBlockCount;
			int num2 = firstDataOffset;
			while (num > 0)
			{
				int num3 = (int)Math.Min(num, val);
				indirectTreeNode.Children.Add(AllocateIndirectNode(depth - 1, num2, num3, ref nextBlock, ref indirectBlocksAllocated));
				num2 += num3;
				num -= num3;
			}
		}
		return indirectTreeNode;
	}

	private static long Capacity(int depth)
	{
		long num = 1L;
		for (int i = 0; i < depth; i++)
		{
			if (num > 5067786833436690L)
			{
				return long.MaxValue;
			}
			num *= 1820;
		}
		return num;
	}

	private static long TotalTreeCapacity()
	{
		long num = 0L;
		for (int i = 1; i <= 5; i++)
		{
			long num2 = Capacity(i);
			if (num > long.MaxValue - num2)
			{
				return long.MaxValue;
			}
			num += num2;
		}
		return num;
	}

	private static byte[] SerializeIndirectNode(IndirectTreeNode node, int fileFirstBlock, Func<int, byte[]> dataBlockDigest, Action<int, byte[]> store, Action<int, byte[]>? recordDigest)
	{
		byte[] array = new byte[65536];
		if (node.Depth == 1)
		{
			for (int i = 0; i < node.DataBlockCount; i++)
			{
				int num = fileFirstBlock + node.FirstDataOffset + i;
				dataBlockDigest(num).CopyTo(array.AsSpan(i * 36));
				BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(i * 36 + 32), num);
			}
		}
		else
		{
			for (int j = 0; j < node.Children.Count; j++)
			{
				IndirectTreeNode indirectTreeNode = node.Children[j];
				SerializeIndirectNode(indirectTreeNode, fileFirstBlock, dataBlockDigest, store, recordDigest).CopyTo(array.AsSpan(j * 36));
				BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(j * 36 + 32), indirectTreeNode.BlockIndex);
			}
		}
		byte[] array2 = Sha3.Sha3_256(array);
		recordDigest?.Invoke(node.BlockIndex, array2);
		store(node.BlockIndex, array);
		return array2;
	}

	private static void MarkTreeSigned(IndirectTreeNode node, OuterBlockKind[] kinds)
	{
		kinds[node.BlockIndex] = OuterBlockKind.Signed;
		foreach (IndirectTreeNode child in node.Children)
		{
			MarkTreeSigned(child, kinds);
		}
	}

	public static void CollectIndirectTree(long rootBlock, int depth, int maxEntries, Func<long, byte[]> readBlock, List<int> output)
	{
		ArgumentNullException.ThrowIfNull(readBlock, "readBlock");
		ArgumentNullException.ThrowIfNull(output, "output");
		if (depth < 1 || depth > 5)
		{
			throw new InvalidDataException($"Outer indirect tree depth {depth} is out of range.");
		}
		if (output.Count >= maxEntries)
		{
			return;
		}
		byte[] array = readBlock(rootBlock);
		if (array.Length < 65520)
		{
			throw new InvalidDataException($"Outer indirect block {rootBlock} is truncated.");
		}
		for (int i = 0; i < 1820; i++)
		{
			if (output.Count >= maxEntries)
			{
				break;
			}
			int num = BinaryPrimitives.ReadInt32LittleEndian(array.AsSpan(i * 36 + 32, 4));
			if (depth == 1)
			{
				output.Add(num);
				continue;
			}
			if (num > 0)
			{
				CollectIndirectTree(num, depth - 1, maxEntries, readBlock, output);
				continue;
			}
			throw new InvalidDataException($"Outer indirect block {rootBlock} entry {i} has no child.");
		}
	}

	private static void WriteHashInode(Span<byte> inode, byte[] inodeTable, byte[] imageDigests, OuterPfsBuildParameters p, int inodeNumber, ushort mode, ushort nlink, uint flags, long size, long sizeCompressed, int[] ownedBlocks)
	{
		inode.Clear();
		BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
		BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(2), nlink);
		BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(4), flags);
		BinaryPrimitives.WriteInt64LittleEndian(inode.Slice(8), size);
		BinaryPrimitives.WriteInt64LittleEndian(inode.Slice(16), sizeCompressed);
		StampInodeTimes(inode, p);
		BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(96), (uint)ownedBlocks.Length);
		for (int i = 0; i < ownedBlocks.Length; i++)
		{
			imageDigests.AsSpan(ownedBlocks[i] * 32, 32).CopyTo(inode.Slice(100 + i * 36));
			BinaryPrimitives.WriteInt32LittleEndian(inode.Slice(100 + i * 36 + 32), ownedBlocks[i]);
		}
		inode.CopyTo(inodeTable.AsSpan(inodeNumber * 712));
	}

	public static (byte[] ImageDigests, byte[] SuperblockIcv) ComputeImageDigests(OuterPfsBuildResult build)
	{
		ArgumentNullException.ThrowIfNull(build, "build");
		byte[] array = new byte[build.BlockCount * 32];
		for (int i = 0; i < build.BlockCount; i++)
		{
			Sha3.Sha3_256(build.Plaintext.AsSpan(i * 65536, 65536)).CopyTo(array, i * 32);
		}
		byte[] item = ComputeSuperblockIcv(build.Plaintext.AsSpan(build.SuperblockIndex * 65536, 65536));
		return (ImageDigests: array, SuperblockIcv: item);
	}

	private static void BuildSuperRootDirents(Span<byte> block)
	{
		int pos = 0;
		pos = WriteDirent(block, pos, 1u, 2, "inode_flat_path_table");
		pos = WriteDirent(block, pos, 2u, 3, "uroot");
	}

	private static void BuildUrootDirents(Span<byte> block, IReadOnlyList<OuterPfsFile> files)
	{
		int pos = 0;
		pos = WriteDirent(block, pos, 2u, 4, ".");
		pos = WriteDirent(block, pos, 2u, 5, "..");
		for (int i = 0; i < files.Count; i++)
		{
			pos = WriteDirent(block, pos, (uint)(3 + i), 2, files[i].Name);
		}
	}

	private static int WriteDirent(Span<byte> block, int pos, uint inode, int type, string name)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(name);
		int num = bytes.Length + 17;
		if (num % 8 != 0)
		{
			num += 8 - num % 8;
		}
		if (num > 280)
		{
			throw new InvalidOperationException("Dirent name '" + name + "' is too long.");
		}
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(pos), inode);
		BinaryPrimitives.WriteInt32LittleEndian(block.Slice(pos + 4), type);
		BinaryPrimitives.WriteInt32LittleEndian(block.Slice(pos + 8), bytes.Length);
		BinaryPrimitives.WriteInt32LittleEndian(block.Slice(pos + 12), num);
		bytes.CopyTo(block.Slice(pos + 16));
		return pos + num;
	}

	private static void BuildFlatPathTable(Span<byte> block, IReadOnlyList<OuterPfsFile> files)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(0), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4), 16u);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(8), 64u);
		FltMagic.CopyTo(block.Slice(32));
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(44), (uint)files.Count);
		BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(48), 10577419142525243217uL);
		BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(56), 701355796979237965uL);
		int num = 64;
		for (int i = 0; i < files.Count; i++)
		{
			ulong value = (uint)(3 + i) | ((ulong)(uint)i << 40);
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(num), FltPathHash(files[i].Name));
			BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(num + 8), value);
			num += 16;
		}
	}

	private static void BuildInodeTable(byte[] image, int inodeTableIndex, OuterPfsBuildParameters p, IReadOnlyList<OuterPfsFile> files, int[] fileFirstBlock, int[] fileBlockCount, List<IndirectPlan>[] filePlans, int superRootDirentIndex, int fltIndex, int urootDirentIndex)
	{
		int num = 3 + files.Count;
		long num2 = (long)num * 712L;
		if (num2 > 65536)
		{
			throw new NotSupportedException($"{num} outer inodes ({num2} bytes) exceed one {65536}-byte inode block.");
		}
		Span<byte> span = stackalloc byte[712];
		WriteS32Inode(image, inodeTableIndex, 0, p, 16749, 1, 131084u, 65536L, 65536L, 1, image, new int[1] { superRootDirentIndex });
		long num3 = 64 + (long)files.Count * 16L;
		WriteS32Inode(image, inodeTableIndex, 1, p, 33133, 1, 131084u, num3, num3, 1, image, new int[1] { fltIndex });
		WriteS32Inode(image, inodeTableIndex, 2, p, 16749, 3, 12u, 65536L, 65536L, 1, image, new int[1] { urootDirentIndex });
		for (int i = 0; i < files.Count; i++)
		{
			OuterPfsFile outerPfsFile = files[i];
			int num4 = fileFirstBlock[i];
			int num5 = fileBlockCount[i];
			span.Clear();
			BinaryPrimitives.WriteUInt16LittleEndian(span, 33133);
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), 1);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 13u);
			BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), outerPfsFile.Data.Length);
			BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), outerPfsFile.SizeCompressed ?? outerPfsFile.Data.Length);
			StampInodeTimes(span, p);
			BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(96), (uint)num5);
			int num6 = Math.Min(num5, 12);
			for (int j = 0; j < num6; j++)
			{
				WriteBlockSig32(span, 100 + j * 36, Sha3.Sha3_256(image.AsSpan((num4 + j) * 65536, 65536)), num4 + j);
			}
			foreach (IndirectPlan item in filePlans[i])
			{
				byte[] sig = SerializeIndirectNode(item.Root, num4, (int dataBlock) => Sha3.Sha3_256(image.AsSpan(dataBlock * 65536, 65536)), (int blockIndex, byte[] bytes) =>
				{
					bytes.CopyTo(image.AsSpan(blockIndex * 65536, 65536));
				}, null);
				WriteBlockSig32(span, 532 + item.InodeLevel * 36, sig, item.Root.BlockIndex);
			}
			span.CopyTo(image.AsSpan(inodeTableIndex * 65536 + (3 + i) * 712, 712));
		}
	}

	private static void WriteS32Inode(byte[] image, int inodeTableIndex, int inodeNumber, OuterPfsBuildParameters p, ushort mode, ushort nlink, uint flags, long size, long sizeCompressed, int blocks, byte[] srcImage, int[] ownedBlocks)
	{
		Span<byte> span = stackalloc byte[712];
		span.Clear();
		BinaryPrimitives.WriteUInt16LittleEndian(span, mode);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), nlink);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), flags);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), size);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), sizeCompressed);
		StampInodeTimes(span, p);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(96), (uint)blocks);
		for (int i = 0; i < ownedBlocks.Length; i++)
		{
			WriteBlockSig32(span, 100 + i * 36, Sha3.Sha3_256(srcImage.AsSpan(ownedBlocks[i] * 65536, 65536)), ownedBlocks[i]);
		}
		span.CopyTo(image.AsSpan(inodeTableIndex * 65536 + inodeNumber * 712, 712));
	}

	private static void StampInodeTimes(Span<byte> inode, OuterPfsBuildParameters p)
	{
		BinaryPrimitives.WriteInt64LittleEndian(inode.Slice(24), p.TimestampSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(inode.Slice(32), p.TimestampSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(inode.Slice(40), p.TimestampSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(inode.Slice(48), p.TimestampSeconds);
		BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(56), p.TimestampNanoseconds);
		BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(60), p.TimestampNanoseconds);
		BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(64), p.TimestampNanoseconds);
		BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(68), p.TimestampNanoseconds);
	}

	private static void WriteBlockSig32(Span<byte> inode, int offset, byte[] sig, int block)
	{
		sig.CopyTo(inode.Slice(offset));
		BinaryPrimitives.WriteInt32LittleEndian(inode.Slice(offset + 32), block);
	}

	private static void BuildSuperblock(Span<byte> block, OuterPfsBuildParameters p, int fileCount, int totalBlocks, int inodeTableIndex, byte[] inodeTableHash)
	{
		int num = 3 + fileCount;
		block.Clear();
		BinaryPrimitives.WriteInt64LittleEndian(block.Slice(0), 2L);
		BinaryPrimitives.WriteInt64LittleEndian(block.Slice(8), 20130315L);
		block[26] = 1;
		BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(28), 13);
		BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(32), 65536u);
		BinaryPrimitives.WriteInt64LittleEndian(block.Slice(40), 1L);
		BinaryPrimitives.WriteInt64LittleEndian(block.Slice(48), num);
		BinaryPrimitives.WriteInt64LittleEndian(block.Slice(56), totalBlocks);
		BinaryPrimitives.WriteInt64LittleEndian(block.Slice(64), 1L);
		Span<byte> span = block.Slice(80, 784);
		BinaryPrimitives.WriteUInt16LittleEndian(span, 0);
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), 1);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), 65536L);
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), 65536L);
		StampS64Times(span, p);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(96), 1u);
		inodeTableHash.CopyTo(span.Slice(104));
		BinaryPrimitives.WriteInt64LittleEndian(span.Slice(136), inodeTableIndex);
		BinaryPrimitives.WriteInt32LittleEndian(block.Slice(876), 1);
		(p.Seed ?? new byte[16]).CopyTo(block.Slice(880));
		ComputeSuperblockIcv(block).CopyTo(block.Slice(896));
	}

	private static void StampS64Times(Span<byte> sig, OuterPfsBuildParameters p)
	{
		BinaryPrimitives.WriteInt64LittleEndian(sig.Slice(24), p.TimestampSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(sig.Slice(32), p.TimestampSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(sig.Slice(40), p.TimestampSeconds);
		BinaryPrimitives.WriteInt64LittleEndian(sig.Slice(48), p.TimestampSeconds);
		BinaryPrimitives.WriteUInt32LittleEndian(sig.Slice(56), p.TimestampNanoseconds);
		BinaryPrimitives.WriteUInt32LittleEndian(sig.Slice(60), p.TimestampNanoseconds);
		BinaryPrimitives.WriteUInt32LittleEndian(sig.Slice(64), p.TimestampNanoseconds);
		BinaryPrimitives.WriteUInt32LittleEndian(sig.Slice(68), p.TimestampNanoseconds);
	}

	private static ulong Rotl(ulong x, int n)
	{
		return (x << n) | (x >> 64 - n);
	}

	private static ulong Rotr(ulong x, int n)
	{
		return (x >> n) | (x << 64 - n);
	}

	public static ulong FltPathHash(string name)
	{
		byte[] array = new byte[name.Length];
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			if (c >= 'a' && c <= 'z')
			{
				c = (char)(c - 32);
			}
			array[i] = (byte)c;
		}
		int num = array.Length;
		ulong num2 = 10577419142525243217uL;
		ulong num3 = Rotl(10577419142525243217uL, 11);
		ulong num4 = Rotl(10577419142525243217uL, 23);
		ulong num5 = 0uL;
		if (num != 0)
		{
			int num6 = num - 1 >> 3;
			int num7 = 0;
			for (int j = 0; j < num6; j++)
			{
				ulong num8 = BinaryPrimitives.ReadUInt64LittleEndian(array.AsSpan(num7, 8));
				num2 ^= num8;
				ulong num9 = Rotr(Rotl(num4 ^ num3, 5) ^ num2, 11);
				ulong num10 = Rotl(Rotl(num4 ^ num2, 17) ^ num3, 11);
				ulong num11 = Rotr(Rotl(num3 ^ num2, 1) ^ num4, 5);
				num2 = (~num10 & num11) ^ num9 ^ 0x8000000080008081uL;
				num3 = (~num11 & num9) ^ num10;
				num4 = (~num9 & num10) ^ num11;
				num7 += 8;
			}
			int length = ((num - 1) & 7) + 1;
			Span<byte> span = stackalloc byte[8];
			span.Clear();
			array.AsSpan(num7, length).CopyTo(span);
			num5 = BinaryPrimitives.ReadUInt64LittleEndian(span);
		}
		ulong num12 = num3;
		ulong num13 = num4;
		ulong num14 = num5 ^ num2 ^ 0x9BBB761A41BC44DL;
		ulong x = Rotl(num13 ^ num12, 5) ^ num14;
		ulong x2 = Rotl(num13 ^ num14, 17) ^ num12;
		ulong x3 = Rotl(num12 ^ num14, 1) ^ num13;
		return (~Rotl(x2, 11) & Rotr(x3, 5)) ^ Rotr(x, 11) ^ 0x8000000080008081uL;
	}

	public static byte[] ComputeSuperblockIcv(ReadOnlySpan<byte> superblock)
	{
		if (superblock.Length < 1440)
		{
			throw new ArgumentException("Superblock must be at least 0x5A0 bytes.", "superblock");
		}
		Span<byte> span = stackalloc byte[1440];
		superblock.Slice(0, 1440).CopyTo(span);
		span.Slice(896, 32).Clear();
		return Sha3.Sha3_256(span);
	}
}
