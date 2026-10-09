using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProsperoPkgTool.Compression;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoInnerImageAssembler
{
	public sealed class InnerFile
	{
		public required string Path { get; init; }

		public byte[] Data { get; set; } = Array.Empty<byte>();

		public string? SourcePath { get; init; }

		public IProsperoFileSource? Source { get; init; }

		public long Length { get; init; }

		public byte PlayGoChunkId { get; init; }

		public long PayloadLength
		{
			get
			{
				if (Source == null)
				{
					if (SourcePath == null)
					{
						return Data.LongLength;
					}
					return Length;
				}
				return Source.Length;
			}
		}
	}

	public sealed class InnerImageResult
	{
		public required byte[] Image { get; init; }

		public string? ImagePath { get; init; }

		public long ImageLength { get; init; }

		public long ImageSize
		{
			get
			{
				if (ImageLength <= 0)
				{
					return Image.Length;
				}
				return ImageLength;
			}
		}

		public required byte[] MetadataPlaintext { get; init; }

		public required byte[] CompressedMetadata { get; init; }

		public required IReadOnlyList<ProsperoPfsv3Writer.CompressedBlock> MetadataBlocks { get; init; }

		public IReadOnlyList<ProsperoEncodedBlock> EncodedBlocks { get; init; } = Array.Empty<ProsperoEncodedBlock>();

		public ProsperoCompressionMetrics CompressionMetrics { get; init; }

		public required IReadOnlyList<MetaNodeInfo> Nodes { get; init; }

		public required long Ndblock { get; init; }

		public required long MetaBaseLogical { get; init; }

		public required long DataEndLogical { get; init; }

		public required long BlockInfoOnDiskOffset { get; init; }

		public required long MetadataOnDiskOffset { get; init; }

		public required IReadOnlyList<Placement> Placements { get; init; }

		public required IReadOnlyList<long> AfidLogicalOffsets { get; init; }

		public required int InodeCount { get; init; }

		public required int InnerContentInodes { get; init; }

		public required int AppFileCount { get; init; }

		public Stream OpenImage()
		{
			if (ImagePath == null)
			{
				return new MemoryStream(Image, writable: false);
			}
			return new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.RandomAccess);
		}
	}

	public readonly record struct Placement(uint Afid, long OnDiskOffset, long LogicalOffset, long OnDiskSize, long UncompressedSize, bool StoreRaw);

	public sealed class MetaNodeInfo
	{
		public string Name { get; init; } = "";

		public string FullPath { get; init; } = "";

		public uint Inode { get; init; }

		public bool IsDirectory { get; init; }

		public bool IsApr { get; init; }

		public uint Afid { get; init; }

		public ulong LogicalOffset { get; init; }

		public long Size { get; init; }

		public ushort Mode { get; init; }

		public ushort Nlink { get; init; } = 1;

		public uint Flags { get; init; }

		public int ParentInode { get; init; } = -1;

		public int DirentOffset { get; init; } = -1;
	}

	private sealed class Dir
	{
		public string Name = "";

		public string FullPath = "";

		public Dir? Parent;

		public readonly List<Dir> SubDirs = new List<Dir>();

		public readonly List<FileNode> Files = new List<FileNode>();

		public uint Inode;

		public int DirentOffsetInParent = -1;

		public readonly List<Dirent> Dirents = new List<Dirent>();
	}

	private sealed class FileNode
	{
		public string Name = "";

		public string FullPath = "";

		public byte[] Data = Array.Empty<byte>();

		public string? SourcePath;

		public IProsperoFileSource? Source;

		public long PayloadLength;

		public Dir Parent;

		public uint Inode;

		public uint Afid;

		public bool StoreRaw;

		public bool WholeBlockRaw;

		public byte PlayGoChunkId;

		public long LogicalOffset;

		public long OnDiskOffset;

		public long EncodedLength;

		public byte[]? OnDiskData;

		public int DirentOffsetInParent = -1;
	}

	private readonly record struct Dirent(uint Inode, int Type, string Name);

	private sealed class MetaNode
	{
		public string Name = "";

		public string FullPath = "";

		public uint Inode;

		public bool IsDirectory;

		public uint Afid;

		public ulong LogicalOffset;

		public long Size;

		public ushort Mode;

		public ushort Nlink = 1;

		public uint Flags;

		public int ParentInode = -1;

		public int DirentOffset = -1;
	}

	private readonly record struct FlatPathEntry(ulong Hash, ulong Packed);

	public const int BlockSize = 65536;

	private const string SceSysDir = "sce_sys";

	private const long MetadataReserveBlocks = 60L;

	private const int InodesPerBlock = 390;

	private readonly long _timeSec;

	private readonly uint _timeNsec;

	private const bool VerifyKrakenRoundTrip = false;

	private const int DirentTypeFile = 2;

	private const int DirentTypeDirectory = 3;

	private const int DirentTypeDot = 4;

	private const int DirentTypeDotDot = 5;

	private const ulong FltSeed0 = 10577419142525243217uL;

	private const ulong FltSeed1 = 701355796979237965uL;

	private const ulong FltRoundConst = 9223372039002292353uL;

	private const ulong FltFlagDir = 1073741824uL;

	private const ulong FltFlagSubtree = 2147483648uL;

	private const ulong FltDirValue = 16777215uL;

	private const uint BlockInfoVersion = 4194307u;

	private const uint BlockInfoTemplate = 16580391u;

	private const uint BlockInfoBase = 2621436u;

	public ProsperoInnerImageAssembler(long buildTimeSec, uint buildTimeNsec)
	{
		_timeSec = buildTimeSec;
		_timeNsec = buildTimeNsec;
	}

	public InnerImageResult Build(IReadOnlyList<InnerFile> files, ProsperoInnerCompressionMode compressionMode = ProsperoInnerCompressionMode.Stored, IProgress<ProsperoBuildProgress>? progress = null, int krakenLevel = 7, int krakenThreads = 0, CancellationToken cancellationToken = default(CancellationToken))
	{
		return BuildCore(files, compressionMode, null, progress, krakenLevel, krakenThreads, cancellationToken);
	}

	public InnerImageResult BuildToFile(IReadOnlyList<InnerFile> files, string outputPath, ProsperoInnerCompressionMode compressionMode = ProsperoInnerCompressionMode.Stored, IProgress<ProsperoBuildProgress>? progress = null, int krakenLevel = 7, int krakenThreads = 0, CancellationToken cancellationToken = default(CancellationToken))
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		string fullPath = Path.GetFullPath(outputPath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		return BuildCore(files, compressionMode, fullPath, progress, krakenLevel, krakenThreads, cancellationToken);
	}

	private InnerImageResult BuildCore(IReadOnlyList<InnerFile> files, ProsperoInnerCompressionMode compressionMode, string? outputPath, IProgress<ProsperoBuildProgress>? progress = null, int krakenLevel = 7, int krakenThreads = 0, CancellationToken cancellationToken = default(CancellationToken))
	{
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(files, "files");
		if (files.Count == 0)
		{
			throw new ArgumentException("At least one inner file is required.", "files");
		}
		KrakenTuning.ValidateLevel(krakenLevel);
		int krakenThreads2 = KrakenTuning.ResolveThreads(krakenThreads);
		Dir dir = BuildTree(files);
		List<Dir> list = new List<Dir>();
		CollectDirsPreOrder(dir, list);
		uint num = 0u;
		num++;
		uint inodeFltInode = num++;
		uint aprFltInode = num++;
		uint afidTableInode = num++;
		foreach (Dir item in list)
		{
			item.Inode = num++;
		}
		List<Dir> list2 = new List<Dir>();
		CollectDirsPostOrder(dir, list2);
		List<FileNode> list3 = new List<FileNode>();
		foreach (Dir item2 in list2)
		{
			foreach (FileNode item3 in item2.Files.OrderBy((FileNode fileNode) => fileNode.Name, StringComparer.Ordinal))
			{
				item3.Inode = num++;
				list3.Add(item3);
			}
		}
		List<FileNode> list4 = new List<FileNode>();
		Dir dir2 = dir.SubDirs.FirstOrDefault((Dir d) => d.Name == "sce_sys");
		if (dir2 != null)
		{
			List<FileNode> list5 = new List<FileNode>();
			CollectFilesPreOrder(dir2, list5);
			list4.AddRange(list5.OrderBy(SystemAfidRank).ThenBy((FileNode fileNode) => fileNode.FullPath, StringComparer.Ordinal));
		}
		CollectFilesEntryOrder(dir, dir2, list4);
		List<FileNode> list6 = list4.Where((FileNode fileNode) => fileNode.PayloadLength == 0).ToList();
		list4.RemoveAll((FileNode fileNode) => fileNode.PayloadLength == 0);
		List<FileNode> list7 = new List<FileNode>();
		List<long> list8 = new List<long>();
		long num2 = 0L;
		long num3 = -1L;
		int num4 = 0;
		foreach (FileNode item4 in list4)
		{
			long num5 = num2 / 262144;
			if (num5 != num3)
			{
				num3 = num5;
				num4 = 0;
			}
			if (num4 >= ((num3 == 0L) ? 28 : 30))
			{
				list8.Add(num2);
				list7.Add(null);
				num2 += 262144;
				num3 = num2 / 262144;
				num4 = 0;
			}
			item4.Afid = (uint)list7.Count;
			item4.LogicalOffset = num2;
			list7.Add(item4);
			list8.Add(num2);
			num2 += item4.PayloadLength;
			num4++;
		}
		uint afid = (uint)(list7.Count + list6.Count + 1);
		foreach (FileNode item5 in list6)
		{
			item5.Afid = afid;
		}
		byte? b = null;
		foreach (FileNode item6 in list4)
		{
			bool flag = b.HasValue && b.Value != item6.PlayGoChunkId;
			b = item6.PlayGoChunkId;
			item6.WholeBlockRaw = (IsKeystone(item6.FullPath) || IsExecutableModule(item6)) | flag;
		}
		List<ProsperoEncodedBlock> list9 = new List<ProsperoEncodedBlock>();
		int sourceBlockIndex = 0;
		long num6 = 0L;
		int num7 = 0;
		long num8 = 0L;
		foreach (FileNode item7 in list4)
		{
			num8 += item7.PayloadLength;
		}
		long num9 = 0L;
		using FileStream fileStream = ((outputPath != null) ? new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan) : null);
		List<(long, byte[])> list10 = ((fileStream == null) ? new List<(long, byte[])>() : null);
		foreach (FileNode f in list4)
		{
			cancellationToken.ThrowIfCancellationRequested();
			f.StoreRaw = compressionMode == ProsperoInnerCompressionMode.Stored || f.WholeBlockRaw;
			if (f.WholeBlockRaw || (f.StoreRaw && num6 % 65536 != 0L && f.PayloadLength > 65536 - num6 % 65536))
			{
				num6 = RoundUp(num6, 65536L);
			}
			f.OnDiskOffset = num6;
			int firstBlockOfFile = list9.Count;
			long num10;
			if (fileStream != null)
			{
				fileStream.Position = num6;
				num10 = RenderFilePayload(f, compressionMode, list9, ref sourceBlockIndex, retainEncodedBytes: false, fileStream, krakenThreads2, out byte[] _);
			}
			else
			{
				num10 = RenderFilePayload(f, compressionMode, list9, ref sourceBlockIndex, retainEncodedBytes: true, null, krakenThreads2, out byte[] payload2);
				list10.Add((num6, payload2));
				f.OnDiskData = payload2;
			}
			f.EncodedLength = num10;
			long num11 = num6;

			for (int blockIndex = firstBlockOfFile; blockIndex < list9.Count; blockIndex++)
			{
				ProsperoEncodedBlock item8 = list9[blockIndex];
				item8.PhysicalOffset = num11;
				num11 += item8.StoredLength;
			}
			num6 += num10;
			if (f.WholeBlockRaw)
			{
				num6 = RoundUp(num6, 65536L);
			}
			num9 += f.PayloadLength;
			progress?.Report(new ProsperoBuildProgress("Inner image", ++num7, list4.Count)
			{
				StageId = ProsperoBuildStage.InnerImage,
				BytesDone = num9,
				BytesTotal = num8,
				CurrentPath = f.FullPath
			});
		}
		long num12 = ((list4.Count > 0) ? list4.Max((FileNode fileNode) => fileNode.LogicalOffset + fileNode.PayloadLength) : 0);
		foreach (FileNode item9 in list6)
		{
			item9.LogicalOffset = num12;
		}
		long num13 = RoundUp(Math.Max(num12, 1L), 262144L) / 65536;
		BuildDirents(dir);
		List<byte[]> list11 = BuildMetadataContent(list, list3, list7, inodeFltInode, aprFltInode, afidTableInode, dir);
		int num14 = (4 + list.Count + list3.Count + 390 - 1) / 390;
		int num15 = 0;
		foreach (byte[] item10 in list11)
		{
			num15 += AllocatedBlocks(item10.Length);
		}
		int num16 = 1 + num14 + num15;
		if ((num16 & 1) != 0)
		{
			list11.Add(Array.Empty<byte>());
		}
		long num17 = num13 + 60;
		long num18 = num17 * 65536;
		long num19 = num17 + num16 + (num16 & 1);
		long[] array = new long[list11.Count];
		long num20 = num18 + (1 + num14) * 65536;
		for (int num21 = 0; num21 < list11.Count; num21++)
		{
			array[num21] = num20;
			num20 += (long)AllocatedBlocks(list11[num21].Length) * 65536L;
		}
		List<MetaNode> list12 = BuildNodes(dir, list, list3, list11, array);
		byte[] array2 = BuildMetadataPlaintext(list12, list11, num14, num17, num19);
		byte[] array3 = BuildBlockInfoTable(num12);
		num6 = RoundUp(num6, 65536L);
		long blockInfoOnDiskOffset = num6;
		if (fileStream != null)
		{
			fileStream.Position = num6;
			fileStream.Write(array3, 0, array3.Length);
		}
		else
		{
			list10.Add((num6, array3));
		}
		num6 += array3.Length;
		byte[] array4;
		IReadOnlyList<ProsperoPfsv3Writer.CompressedBlock> metadataBlocks;
		if (compressionMode == ProsperoInnerCompressionMode.Stored)
		{
			array4 = ProsperoPfsv3Writer.WriteStored(array2, 262144, krakenLevel);
			metadataBlocks = BuildStoredMetadataBlocks(array2);
		}
		else
		{
			cancellationToken.ThrowIfCancellationRequested();
			(array4, metadataBlocks) = ProsperoPfsv3Writer.WriteCompressed(array2, 262144, krakenLevel, cancellationToken);
		}
		num6 = RoundUp(num6, 65536L);
		long metadataOnDiskOffset = num6;
		if (fileStream != null)
		{
			fileStream.Position = num6;
			fileStream.Write(array4, 0, array4.Length);
		}
		else
		{
			list10.Add((num6, array4));
		}
		num6 += array4.Length;
		long num22 = num6;
		byte[] array5;
		if (fileStream != null)
		{
			fileStream.SetLength(num22);
			fileStream.Flush();
			array5 = Array.Empty<byte>();
		}
		else
		{
			array5 = new byte[num22];
			foreach (var (num23, array6) in list10)
			{
				array6.CopyTo(array5.AsSpan((int)num23, array6.Length));
			}
		}
		long metaBaseLogical = num19 * 65536 - array2.Length;
		int innerContentInodes = list12.Count((MetaNode n) => n.ParentInode >= 0);
		int appFileCount = list3.Count((FileNode fileNode) => !fileNode.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal));
		List<MetaNodeInfo> nodes = (from n in list12
			orderby n.Inode
			select new MetaNodeInfo
			{
				Name = n.Name,
				FullPath = n.FullPath,
				Inode = n.Inode,
				IsDirectory = n.IsDirectory,
				IsApr = (!n.IsDirectory && !n.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal)),
				Afid = n.Afid,
				LogicalOffset = n.LogicalOffset,
				Size = n.Size,
				Mode = n.Mode,
				Nlink = n.Nlink,
				Flags = n.Flags,
				ParentInode = n.ParentInode,
				DirentOffset = n.DirentOffset
			}).ToList();
		List<Placement> placements = list4.Select((FileNode fileNode) => new Placement(fileNode.Afid, fileNode.OnDiskOffset, fileNode.LogicalOffset, fileNode.EncodedLength, fileNode.PayloadLength, fileNode.StoreRaw)).ToList();
		return new InnerImageResult
		{
			Image = array5,
			ImagePath = outputPath,
			ImageLength = num22,
			MetadataPlaintext = array2,
			CompressedMetadata = array4,
			MetadataBlocks = metadataBlocks,
			EncodedBlocks = list9,
			CompressionMetrics = new ProsperoCompressionMetrics(((IEnumerable<ProsperoEncodedBlock>)list9).Sum((Func<ProsperoEncodedBlock, long>)((ProsperoEncodedBlock prosperoEncodedBlock) => prosperoEncodedBlock.UncompressedLength)), ((IEnumerable<ProsperoEncodedBlock>)list9).Sum((Func<ProsperoEncodedBlock, long>)((ProsperoEncodedBlock prosperoEncodedBlock) => prosperoEncodedBlock.StoredLength)), list9.Count((ProsperoEncodedBlock prosperoEncodedBlock) => !prosperoEncodedBlock.IsStored), list9.Count((ProsperoEncodedBlock prosperoEncodedBlock) => prosperoEncodedBlock.IsStored)),
			Nodes = nodes,
			Ndblock = num19,
			MetaBaseLogical = metaBaseLogical,
			DataEndLogical = num12,
			BlockInfoOnDiskOffset = blockInfoOnDiskOffset,
			MetadataOnDiskOffset = metadataOnDiskOffset,
			Placements = placements,
			AfidLogicalOffsets = list8,
			InodeCount = list12.Count,
			InnerContentInodes = innerContentInodes,
			AppFileCount = appFileCount
		};
	}

	private static bool VerifyKraken(ReadOnlySpan<byte> raw, KrakenEncoder.EncodedBlock encoded)
	{
		byte[] array = new byte[raw.Length];
		if (KrakenDecoder.DecodeBlock(encoded.Payload, encoded.Flags, encoded.FirstChunkCompressedLength, array) == KrakenDecoder.KrakenDecodeStatus.Success)
		{
			return ((ReadOnlySpan<byte>)array.AsSpan()).SequenceEqual(raw);
		}
		return false;
	}

	private static IReadOnlyList<ProsperoPfsv3Writer.CompressedBlock> BuildStoredMetadataBlocks(byte[] metaPlain)
	{
		List<ProsperoPfsv3Writer.CompressedBlock> list = new List<ProsperoPfsv3Writer.CompressedBlock>();
		for (int i = 0; i < metaPlain.Length; i += 262144)
		{
			int num = Math.Min(262144, metaPlain.Length - i);
			list.Add(new ProsperoPfsv3Writer.CompressedBlock(num, num, MultiChunk: false, 0, 0));
		}
		return list;
	}

	private static Dir BuildTree(IReadOnlyList<InnerFile> files)
	{
		Dir dir = new Dir
		{
			Name = "uroot",
			FullPath = ""
		};
		Dictionary<string, Dir> dirLookup = new Dictionary<string, Dir>(StringComparer.Ordinal) { [""] = dir };
		foreach (InnerFile item in files.OrderBy((InnerFile f) => f.Path, StringComparer.Ordinal))
		{
			string text = (item.Path.StartsWith('/') ? item.Path.Substring(1) : item.Path);
			int num = text.LastIndexOf('/');
			string fullPath = ((num < 0) ? "" : text.Substring(0, num));
			string name = ((num < 0) ? text : text.Substring(num + 1));
			Dir dir2 = GetDir(fullPath);
			dir2.Files.Add(new FileNode
			{
				Name = name,
				FullPath = "/" + text,
				Data = item.Data,
				SourcePath = item.SourcePath,
				Source = item.Source,
				PayloadLength = item.PayloadLength,
				Parent = dir2,
				PlayGoChunkId = item.PlayGoChunkId
			});
		}
		return dir;
		Dir GetDir(string text2)
		{
			if (dirLookup.TryGetValue(text2, out Dir value))
			{
				return value;
			}
			int num2 = text2.LastIndexOf('/');
			string fullPath2 = ((num2 <= 0) ? "" : text2.Substring(0, num2));
			string name2 = text2.Substring(num2 + 1);
			Dir dir3 = GetDir(fullPath2);
			Dir dir4 = new Dir
			{
				Name = name2,
				FullPath = text2,
				Parent = dir3
			};
			dir3.SubDirs.Add(dir4);
			dirLookup[text2] = dir4;
			return dir4;
		}
	}

	private static void CollectDirsPreOrder(Dir dir, List<Dir> outList)
	{
		outList.Add(dir);
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectDirsPreOrder(item, outList);
		}
	}

	private static void CollectDirsPostOrder(Dir dir, List<Dir> outList)
	{
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectDirsPostOrder(item, outList);
		}
		outList.Add(dir);
	}

	private static void CollectDirsPostOrderImpl(Dir dir, List<Dir> outList)
	{
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectDirsPostOrderImpl(item, outList);
		}
		outList.Add(dir);
	}

	private static void CollectFilesPreOrder(Dir dir, List<FileNode> outList)
	{
		foreach (FileNode item in dir.Files.OrderBy((FileNode f) => f.Name, StringComparer.Ordinal))
		{
			outList.Add(item);
		}
		foreach (Dir item2 in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			CollectFilesPreOrder(item2, outList);
		}
	}

	private static void CollectFilesEntryOrder(Dir directory, Dir? excludedSubtree, List<FileNode> outList)
	{
		foreach (var item in (from child in directory.SubDirs
			where child != excludedSubtree && !IsUnder(child, excludedSubtree)
			select ((string Name, Dir Dir, FileNode File))(Name: child.Name, Dir: child, File: null)).Concat(directory.Files.Select((FileNode file) => ((string Name, Dir Dir, FileNode File))(Name: file.Name, Dir: null, File: file))).OrderBy(((string Name, Dir Dir, FileNode File) e) => e.Name, StringComparer.Ordinal))
		{
			if (item.File != null)
			{
				outList.Add(item.File);
			}
			else if (item.Dir != null)
			{
				CollectFilesEntryOrder(item.Dir, excludedSubtree, outList);
			}
		}
	}

	private static int SystemAfidRank(FileNode f)
	{
		if (IsKeystone(f.FullPath))
		{
			return 0;
		}
		if (IsExecutableModule(f))
		{
			return 1;
		}
		if (f.FullPath.Equals("/sce_sys/pfs-version.dat", StringComparison.Ordinal))
		{
			return 3;
		}
		return 2;
	}

	private static bool IsUnder(Dir dir, Dir? ancestor)
	{
		if (ancestor == null)
		{
			return false;
		}
		for (Dir dir2 = dir; dir2 != null; dir2 = dir2.Parent)
		{
			if (dir2 == ancestor)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsKeystone(string fullPath)
	{
		return string.Equals(fullPath, "/sce_sys/keystone", StringComparison.Ordinal);
	}

	private static bool IsExecutableModule(ReadOnlySpan<byte> data)
	{
		if (data.Length < 4)
		{
			return false;
		}
		uint num = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
		if (num == 490542415 || num == 1179403647 || num == 4009038932u)
		{
			return true;
		}
		return false;
	}

	private static bool IsExecutableModule(FileNode f)
	{
		if (f.Data.Length >= 4 || (f.SourcePath == null && f.Source == null))
		{
			return IsExecutableModule(f.Data.AsSpan());
		}
		Span<byte> span = stackalloc byte[4];
		using Stream stream = OpenSource(f);
		int i;
		int num;
		for (i = 0; i < span.Length; i += num)
		{
			num = stream.Read(span.Slice(i));
			if (num <= 0)
			{
				break;
			}
		}
		return i == span.Length && IsExecutableModule(span);
	}

	private static void BuildDirents(Dir uroot)
	{
		BuildDirentsRecursive(uroot);
	}

	private static void BuildDirentsRecursive(Dir dir)
	{
		dir.Dirents.Clear();
		dir.Dirents.Add(new Dirent(dir.Inode, 4, "."));
		uint inode = dir.Parent?.Inode ?? dir.Inode;
		dir.Dirents.Add(new Dirent(inode, 5, ".."));
		foreach (Dir item in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			dir.Dirents.Add(new Dirent(item.Inode, 3, item.Name));
		}
		foreach (FileNode item2 in dir.Files.OrderBy((FileNode f) => f.Name, StringComparer.Ordinal))
		{
			dir.Dirents.Add(new Dirent(item2.Inode, 2, item2.Name));
		}
		int num = 0;
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (Dirent dirent in dir.Dirents)
		{
			dictionary[dirent.Name] = num;
			num += DirentSize(dirent.Name.Length);
		}
		foreach (Dir subDir in dir.SubDirs)
		{
			subDir.DirentOffsetInParent = dictionary[subDir.Name];
		}
		foreach (FileNode file in dir.Files)
		{
			file.DirentOffsetInParent = dictionary[file.Name];
		}
		foreach (Dir item3 in dir.SubDirs.OrderBy((Dir d) => d.Name, StringComparer.Ordinal))
		{
			BuildDirentsRecursive(item3);
		}
	}

	private static int DirentSize(int nameLen)
	{
		int num = nameLen + 17;
		if (num % 8 != 0)
		{
			num += 8 - num % 8;
		}
		return num;
	}

	private List<MetaNode> BuildNodes(Dir uroot, List<Dir> dirsPreOrder, List<FileNode> fileNodes, IReadOnlyList<byte[]> contentBlocks, IReadOnlyList<long> contentOffsets)
	{
		List<MetaNode> list = new List<MetaNode>();
		list.Add(new MetaNode
		{
			Name = "",
			Inode = 0u,
			IsDirectory = true,
			Mode = 16749,
			Nlink = 1,
			Flags = 131088u,
			Size = BlockSizeOf(0),
			LogicalOffset = (ulong)contentOffsets[0]
		});
		list.Add(new MetaNode
		{
			Name = "inode_flat_path_table",
			Inode = 1u,
			Mode = 33133,
			Nlink = 1,
			Flags = 131088u,
			Size = contentBlocks[1].LongLength,
			LogicalOffset = (ulong)contentOffsets[1]
		});
		list.Add(new MetaNode
		{
			Name = "apr_flat_path_table",
			Inode = 2u,
			Mode = 33133,
			Nlink = 1,
			Flags = 131088u,
			Size = contentBlocks[2].LongLength,
			LogicalOffset = (ulong)contentOffsets[2]
		});
		list.Add(new MetaNode
		{
			Name = "afid_to_ino_table",
			Inode = 3u,
			Mode = 33133,
			Nlink = 1,
			Flags = 131088u,
			Size = contentBlocks[3].LongLength,
			LogicalOffset = (ulong)contentOffsets[3]
		});
		for (int i = 0; i < dirsPreOrder.Count; i++)
		{
			Dir dir = dirsPreOrder[i];
			bool flag = dir == uroot;
			bool flag2 = !flag && (dir.FullPath.Equals("sce_sys", StringComparison.Ordinal) || dir.FullPath.StartsWith("sce_sys/", StringComparison.Ordinal));
			list.Add(new MetaNode
			{
				Name = dir.Name,
				Inode = dir.Inode,
				IsDirectory = true,
				Mode = (ushort)(flag2 ? 16744u : 16749u),
				Nlink = (ushort)((uint)(2 + dir.SubDirs.Count) + (flag ? 1u : 0u)),
				Flags = (flag2 ? 131088u : 16u),
				Size = BlockSizeOf(4 + i),
				LogicalOffset = (ulong)contentOffsets[4 + i],
				ParentInode = (flag ? (-1) : ((int)dir.Parent.Inode)),
				DirentOffset = (flag ? (-1) : dir.DirentOffsetInParent)
			});
		}
		foreach (FileNode fileNode in fileNodes)
		{
			bool flag3 = fileNode.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);
			bool flag4 = IsExecutableModule(fileNode);
			list.Add(new MetaNode
			{
				Name = fileNode.Name,
				FullPath = fileNode.FullPath,
				Inode = fileNode.Inode,
				IsDirectory = false,
				Mode = (ushort)(flag3 ? 33128u : 33133u),
				Nlink = 1,
				Flags = (uint)(0x10 | (flag4 ? 64 : 32) | (flag3 ? 131072 : 0)),
				Afid = fileNode.Afid,
				Size = fileNode.PayloadLength,
				LogicalOffset = (ulong)fileNode.LogicalOffset,
				ParentInode = (int)fileNode.Parent.Inode,
				DirentOffset = fileNode.DirentOffsetInParent
			});
		}
		return list.OrderBy((MetaNode n) => n.Inode).ToList();
		long BlockSizeOf(int index)
		{
			return (long)AllocatedBlocks(contentBlocks[index].Length) * 65536L;
		}
	}

	private List<byte[]> BuildMetadataContent(List<Dir> dirsPreOrder, List<FileNode> fileNodes, IReadOnlyList<FileNode?> afidSlots, uint inodeFltInode, uint aprFltInode, uint afidTableInode, Dir uroot)
	{
		List<FlatPathEntry> list = new List<FlatPathEntry>();
		List<FlatPathEntry> list2 = new List<FlatPathEntry>();
		foreach (Dir item3 in dirsPreOrder)
		{
			if (item3 != uroot)
			{
				bool isSubtree = item3.FullPath.Equals("sce_sys", StringComparison.Ordinal) || item3.FullPath.StartsWith("sce_sys/", StringComparison.Ordinal);
				list.Add(new FlatPathEntry(HashPath(item3.FullPath), PackInodeEntry(item3.Inode, isDir: true, isSubtree, 0u)));
			}
		}
		foreach (FileNode fileNode in fileNodes)
		{
			bool flag = !fileNode.FullPath.StartsWith("/sce_sys/", StringComparison.Ordinal);
			list.Add(new FlatPathEntry(HashPath(fileNode.FullPath), PackInodeEntry(fileNode.Inode, isDir: false, !flag, fileNode.Afid)));
			if (flag)
			{
				list2.Add(new FlatPathEntry(HashPath(fileNode.FullPath), PackAprEntry(fileNode.PayloadLength, fileNode.Afid)));
			}
		}
		byte[] item = SerializeFlatPathTable(list);
		byte[] item2 = SerializeFlatPathTable(list2);
		List<int> list3 = new List<int> { afidSlots.Count + 2 };
		foreach (FileNode afidSlot in afidSlots)
		{
			list3.Add(((int?)afidSlot?.Inode) ?? (-1));
		}
		list3.Add(-1);
		list3.Add(-1);
		byte[] array = new byte[list3.Count * 4];
		for (int i = 0; i < list3.Count; i++)
		{
			BinaryPrimitives.WriteInt32LittleEndian(array.AsSpan(i * 4), list3[i]);
		}
		List<byte[]> list4 = new List<byte[]>
		{
			SerializeDirents(SuperRootDirents(inodeFltInode, aprFltInode, afidTableInode, uroot.Inode)),
			item,
			item2,
			array
		};
		foreach (Dir item4 in dirsPreOrder)
		{
			list4.Add(SerializeDirents(item4.Dirents));
		}
		return list4;
	}

	private byte[] BuildMetadataPlaintext(List<MetaNode> nodes, List<byte[]> contentBlocks, int inodeBlockCount, long metaBaseBlock, long ndblock)
	{
		int num = 0;
		foreach (byte[] contentBlock in contentBlocks)
		{
			num += AllocatedBlocks(contentBlock.Length);
		}
		byte[] array = new byte[(1 + inodeBlockCount + num) * 65536];
		BuildSuperblock(array.AsSpan(0, 65536), nodes.Count, inodeBlockCount, ndblock, metaBaseBlock + 1);
		for (int i = 0; i < nodes.Count; i++)
		{
			int num2 = 1 + i / 390;
			int num3 = i % 390;
			WriteInode(array.AsSpan(num2 * 65536 + num3 * 168, 168), nodes[i]);
		}
		long num4 = (long)(1 + inodeBlockCount) * 65536L;
		foreach (byte[] contentBlock2 in contentBlocks)
		{
			contentBlock2.CopyTo(array.AsSpan((int)num4));
			num4 += (long)AllocatedBlocks(contentBlock2.Length) * 65536L;
		}
		return array;
	}

	private static int AllocatedBlocks(int length)
	{
		return Math.Max(1, (length + 65536 - 1) / 65536);
	}

	private static IEnumerable<Dirent> SuperRootDirents(uint inodeFlt, uint aprFlt, uint afid, uint uroot)
	{
		return new Dirent[4]
		{
			new Dirent(inodeFlt, 2, "inode_flat_path_table"),
			new Dirent(aprFlt, 2, "apr_flat_path_table"),
			new Dirent(afid, 2, "afid_to_ino_table"),
			new Dirent(uroot, 3, "uroot")
		};
	}

	private void BuildSuperblock(Span<byte> sb, int inodeCount, int inodeBlockCount, long ndblock, long inodeTableBlock)
	{
		sb.Clear();
		BinaryPrimitives.WriteInt64LittleEndian(sb, 2L);
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(8), 20130315L);
		sb[26] = 1;
		BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(28), 24);
		BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(32), 65536u);
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(40), 1L);
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(48), inodeCount);
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(56), ndblock);
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(64), inodeBlockCount);
		BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(80), 65536u);
		BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(84), 16u);
		BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(88), 65536u);
		BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(96), 65536u);
		for (int i = 0; i < 4; i++)
		{
			BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(104 + i * 8), _timeSec);
		}
		for (int j = 0; j < 4; j++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(136 + j * 4), _timeNsec);
		}
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(176), 1L);
		BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(216), inodeTableBlock);
		sb[872] = 1;
	}

	private void WriteInode(Span<byte> dst, MetaNode n)
	{
		dst.Clear();
		BinaryPrimitives.WriteUInt16LittleEndian(dst, n.Mode);
		BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(2), n.Nlink);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4), n.Flags);
		BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), n.Size);
		BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(16), n.Size);
		for (int i = 0; i < 4; i++)
		{
			BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(24 + i * 8), _timeSec);
		}
		for (int j = 0; j < 4; j++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(56 + j * 4), _timeNsec);
		}
		BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(96), n.LogicalOffset);
		int value;
		if (n.ParentInode < 0)
		{
			value = -1;
		}
		else
		{
			value = (n.IsDirectory ? (-1) : ((int)n.Afid));
		}
		BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(104), value);
		BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(108), n.ParentInode);
		BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(112), n.DirentOffset);
	}

	private static byte[] SerializeDirents(IEnumerable<Dirent> dirents)
	{
		using MemoryStream memoryStream = new MemoryStream();
		byte[] array = new byte[280];
		foreach (Dirent dirent in dirents)
		{
			byte[] bytes = Encoding.ASCII.GetBytes(dirent.Name);
			int num = DirentSize(bytes.Length);
			Array.Clear(array, 0, num);
			Span<byte> span = array.AsSpan(0, num);
			BinaryPrimitives.WriteUInt32LittleEndian(span, dirent.Inode);
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), dirent.Type);
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), bytes.Length);
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), num);
			bytes.CopyTo(span.Slice(16));
			memoryStream.Write(span);
		}
		return memoryStream.ToArray();
	}

	private static ulong Rotl(ulong x, int n)
	{
		return (x << n) | (x >> 64 - n);
	}

	private static ulong Rotr(ulong x, int n)
	{
		return (x >> n) | (x << 64 - n);
	}

	private static ulong HashPath(string path)
	{
		if (path.Length > 0 && path[0] == '/')
		{
			path = path.Substring(1);
		}
		return HashBytes(Encoding.ASCII.GetBytes(path.ToUpperInvariant()));
	}

	private static ulong HashBytes(ReadOnlySpan<byte> str)
	{
		int length = str.Length;
		ulong num = 10577419142525243217uL;
		ulong num2 = Rotl(10577419142525243217uL, 11);
		ulong num3 = Rotl(10577419142525243217uL, 23);
		ulong num4 = 0uL;
		if (length != 0)
		{
			int num5 = length - 1 >> 3;
			ulong num6 = num;
			ulong num7 = num2;
			ulong num8 = num3;
			int num9 = 0;
			for (int i = 0; i < num5; i++)
			{
				ulong num10 = BinaryPrimitives.ReadUInt64LittleEndian(str.Slice(num9, 8));
				num9 += 8;
				num6 ^= num10;
				ulong num11 = Rotr(Rotl(num8 ^ num7, 5) ^ num6, 11);
				ulong num12 = Rotl(Rotl(num8 ^ num6, 17) ^ num7, 11);
				num8 = Rotr(Rotl(num7 ^ num6, 1) ^ num8, 5);
				num6 = (~num12 & num8) ^ num11 ^ 0x8000000080008081uL;
				num7 = (~num8 & num11) ^ num12;
				num8 = (~num11 & num12) ^ num8;
			}
			num = num6;
			num2 = num7;
			num3 = num8;
			int num13 = ((length - 1) & 7) + 1;
			for (int j = 0; j < num13; j++)
			{
				num4 |= (ulong)str[num9 + j] << 8 * j;
			}
		}
		ulong num14 = num2;
		ulong num15 = num3;
		ulong num16 = num4 ^ num ^ 0x9BBB761A41BC44DL;
		ulong x = Rotl(num15 ^ num14, 5) ^ num16;
		ulong x2 = Rotl(num15 ^ num16, 17) ^ num14;
		num15 = Rotl(num14 ^ num16, 1) ^ num15;
		return (~Rotl(x2, 11) & Rotr(num15, 5)) ^ Rotr(x, 11) ^ 0x8000000080008081uL;
	}

	private static ulong PackInodeEntry(uint inode, bool isDir, bool isSubtree, uint afid)
	{
		ulong num = (ulong)inode & 0xFFFFFFuL;
		if (isDir)
		{
			num |= 0x40000000;
		}
		if (isSubtree)
		{
			num |= 0x80000000u;
		}
		return num | ((ulong)(isDir ? 16777215 : afid) << 40);
	}

	private static ulong PackAprEntry(long size, uint afid)
	{
		return (ulong)(size & 0xFFFFFFFFFFL) | ((ulong)afid << 40);
	}

	private static byte[] SerializeFlatPathTable(List<FlatPathEntry> entries)
	{
		entries.Sort((FlatPathEntry a, FlatPathEntry b) => a.Hash.CompareTo(b.Hash));
		using MemoryStream memoryStream = new MemoryStream();
		Span<byte> span = stackalloc byte[64];
		span.Clear();
		BinaryPrimitives.WriteUInt32LittleEndian(span, 1u);
		span[4] = 16;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), 64u);
		span[32] = 127;
		span[33] = 70;
		span[34] = 76;
		span[35] = 84;
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(44), (uint)entries.Count);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(48), 10577419142525243217uL);
		BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(56), 701355796979237965uL);
		memoryStream.Write(span);
		Span<byte> span2 = stackalloc byte[16];
		foreach (FlatPathEntry entry in entries)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(span2, entry.Hash);
			BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(8), entry.Packed);
			memoryStream.Write(span2);
		}
		return memoryStream.ToArray();
	}

	private static long RoundUp(long value, long granularity)
	{
		return (value + granularity - 1) / granularity * granularity;
	}

	private static byte[] BuildBlockInfoTable(long afidDataEnd)
	{
		uint num = (uint)((afidDataEnd & 0xFFFF) * 4);
		uint num2 = (2621436 - num) & 0xFFFFFF;
		uint val = ((num2 & 0xFF) << 16) | (num2 & 0xFF00) | ((num2 >> 16) & 0xFF);
		byte[] array = new byte[32];
		WriteBlockInfoEntry(array, 0, 16580391u);
		WriteBlockInfoEntry(array, 8, 16580391u);
		WriteBlockInfoEntry(array, 16, 16580391u);
		WriteBlockInfoEntry(array, 24, val);
		return array;
		static void WriteBlockInfoEntry(byte[] buf, int off, uint value)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off), value);
			BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off + 4), 4194307u);
		}
	}

	private static long RenderFilePayload(FileNode f, ProsperoInnerCompressionMode compressionMode, List<ProsperoEncodedBlock> encodedBlocks, ref int sourceBlockIndex, bool retainEncodedBytes, Stream? sink, int krakenThreads, out byte[]? payload)
	{
		payload = null;
		if (f.PayloadLength == 0L)
		{
			encodedBlocks.Add(new ProsperoEncodedBlock
			{
				SourceBlockIndex = sourceBlockIndex++,
				LogicalOffset = f.LogicalOffset,
				UncompressedLength = 0,
				EncodedBytes = Array.Empty<byte>(),
				EncodedLength = 0,
				IsStored = true
			});
			payload = Array.Empty<byte>();
			return 0L;
		}
		List<byte> filePayload = ((sink == null) ? new List<byte>() : null);
		long total;
		using (Stream stream = OpenSource(f))
		{
			long num = f.PayloadLength;
			long num2 = 0L;
			total = 0L;
			int num3 = ((compressionMode == ProsperoInnerCompressionMode.Stored) ? 1 : Math.Max(krakenThreads, 1));

			if (num3 <= 1 || f.PayloadLength <= 524288)
			{
				byte[] array = new byte[(int)Math.Min(131072L, f.PayloadLength)];
				while (num > 0)
				{
					int num4 = (int)Math.Min(131072L, num);
					ReadExactly(stream, array, num4);
					ReadOnlySpan<byte> input = array.AsSpan(0, num4);
					bool flag = num2 + num4 >= f.PayloadLength;
					KrakenEncoder.EncodedBlock encodedBlock = (((compressionMode == ProsperoInnerCompressionMode.Stored || f.StoreRaw) | flag) ? null : KrakenEncoder.TryEncodeNewLz(input));
					if (encodedBlock != null)
					{
						EmitKraken(encodedBlock, num4, num2, ref sourceBlockIndex);
					}
					else
					{
						EmitStored(input.ToArray(), num4, num2, ref sourceBlockIndex);
					}
					num -= num4;
					num2 += num4;
				}
			}
			else
			{

				int chunk = 131072;
				int window = Math.Max(num3 * 2, 4);
				Queue<(Task<(KrakenEncoder.EncodedBlock? Kraken, byte[]? Stored)> Work, int Length, long Offset)> inFlight = new Queue<(Task<(KrakenEncoder.EncodedBlock? Kraken, byte[]? Stored)>, int, long)>();
				while (num > 0 || inFlight.Count > 0)
				{
					while (num > 0 && inFlight.Count < window)
					{
						int length = (int)Math.Min(chunk, num);
						byte[] raw = new byte[length];
						ReadExactly(stream, raw, length);
						bool lastBlock = num2 + length >= f.PayloadLength;
						bool storeRaw = f.StoreRaw;
						Task<(KrakenEncoder.EncodedBlock? Kraken, byte[]? Stored)> work = Task.Run<(KrakenEncoder.EncodedBlock? Kraken, byte[]? Stored)>(() =>
						{
							KrakenEncoder.EncodedBlock? encoded = ((storeRaw || lastBlock) ? null : KrakenEncoder.TryEncodeNewLz(raw));
							return (encoded != null) ? (encoded, null) : (null, raw);
						});
						inFlight.Enqueue((work, length, num2));
						num -= length;
						num2 += length;
					}
					(Task<(KrakenEncoder.EncodedBlock? Kraken, byte[]? Stored)> Work, int Length, long Offset) next = inFlight.Dequeue();
					(KrakenEncoder.EncodedBlock? Kraken, byte[]? Stored) result = next.Work.GetAwaiter().GetResult();
					if (result.Kraken != null)
					{
						EmitKraken(result.Kraken, next.Length, next.Offset, ref sourceBlockIndex);
					}
					else
					{
						EmitStored(result.Stored, next.Length, next.Offset, ref sourceBlockIndex);
					}
				}
			}
			payload = filePayload?.ToArray();
			return total;
		}
		void EmitKraken(KrakenEncoder.EncodedBlock kraken, int rawLength, long num9, ref int sourceIndex)
		{
			sink?.Write(kraken.Payload, 0, kraken.Payload.Length);
			filePayload?.AddRange(kraken.Payload);
			total += kraken.Payload.Length;
			encodedBlocks.Add(new ProsperoEncodedBlock
			{
				SourceBlockIndex = sourceIndex++,
				LogicalOffset = f.LogicalOffset + num9,
				UncompressedLength = rawLength,
				EncodedBytes = (retainEncodedBytes ? kraken.Payload : Array.Empty<byte>()),
				EncodedLength = kraken.Payload.Length,
				IsStored = false,
				KrakenMode = kraken.Mode,
				Flags = kraken.Flags,
				FirstChunkCompressedLength = kraken.FirstChunkCompressedLength
			});
		}
		void EmitStored(byte[] stored, int rawLength, long num9, ref int sourceIndex)
		{
			sink?.Write(stored, 0, stored.Length);
			filePayload?.AddRange(stored);
			total += stored.Length;
			encodedBlocks.Add(new ProsperoEncodedBlock
			{
				SourceBlockIndex = sourceIndex++,
				LogicalOffset = f.LogicalOffset + num9,
				UncompressedLength = rawLength,
				EncodedBytes = (retainEncodedBytes ? stored : Array.Empty<byte>()),
				EncodedLength = stored.Length,
				IsStored = true
			});
		}
	}

	private static Stream OpenSource(FileNode f)
	{
		if (f.Source == null)
		{
			if (f.SourcePath != null)
			{

				return new FileStream(f.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, (int)Math.Clamp(f.PayloadLength, 4096L, 1048576L), FileOptions.SequentialScan);
			}
			return new MemoryStream(f.Data, writable: false);
		}
		return f.Source.Open();
	}

	private static void ReadExactly(Stream stream, byte[] buffer, int length)
	{
		int num;
		for (int i = 0; i < length; i += num)
		{
			num = stream.Read(buffer, i, length - i);
			if (num <= 0)
			{
				throw new EndOfStreamException("A source file ended before its declared length.");
			}
		}
	}
}
