using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProsperoPkgTool.Gp5;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPlayGoChunkBuilder
{
	private readonly record struct PhysicalLeaf(long OnDiskOffset, long OnDiskSize, byte ChunkId);

	public const int HeaderSize = 256;

	private const int RecordSize = 32;

	private const uint Magic = 2020043888u;

	private const uint DebugRecordFlags = 196736u;

	private const uint DebugRecordUse = 17u;

	private const uint DebugHeaderFlags = 133u;

	private const uint DebugHeaderUse = 4u;

	private const uint DebugHeaderUnknown = 17u;

	private const int OuterBlockSize = 65536;

	public static PlayGoProject CreateDefaultProject(string contentId)
	{
		return new PlayGoProject
		{
			VersionMajor = 4096,
			VersionMinor = 0,
			ContentId = contentId,
			DefaultScenarioId = 0,
			Chunks = new _003C_003Ez__ReadOnlySingleElementList<PlayGoChunk>(new PlayGoChunk(0, "Chunk #0", ulong.MaxValue)),
			Scenarios = new _003C_003Ez__ReadOnlySingleElementList<PlayGoScenario>(new PlayGoScenario(0, "Scenario #0", 1, new _003C_003Ez__ReadOnlySingleElementList<int>(0)))
		};
	}

	public static PlayGoProject CreateProject(string contentId, int chunkCount)
	{
		if (chunkCount <= 1)
		{
			return CreateDefaultProject(contentId);
		}
		if (chunkCount > 255)
		{
			throw new ArgumentOutOfRangeException("chunkCount", chunkCount, "PlayGo chunk count must be in the range 1..255.");
		}
		List<PlayGoChunk> list = new List<PlayGoChunk>(chunkCount);
		List<int> list2 = new List<int>(chunkCount);
		for (int i = 0; i < chunkCount; i++)
		{
			list.Add(new PlayGoChunk(i, $"Chunk #{i}", ulong.MaxValue));
			list2.Add(i);
		}
		return new PlayGoProject
		{
			VersionMajor = 4096,
			VersionMinor = 0,
			ContentId = contentId,
			DefaultScenarioId = 0,
			Chunks = list,
			Scenarios = new _003C_003Ez__ReadOnlySingleElementList<PlayGoScenario>(new PlayGoScenario(0, "Scenario #0", 1, list2))
		};
	}

	public static ProsperoPlayGoChunkBuildResult Build(string contentId, PlayGoProject project, ProsperoInnerImageAssembler.InnerImageResult inner, ulong fihPfsOffset, ulong fihCntOffset, IReadOnlyDictionary<string, byte>? chunkAssignments = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(contentId, "contentId");
		ArgumentNullException.ThrowIfNull(project, "project");
		ArgumentNullException.ThrowIfNull(inner, "inner");
		ValidateProject(contentId, project);
		if (fihPfsOffset % 65536 != 0L || fihCntOffset <= fihPfsOffset)
		{
			throw new ArgumentException("PLGX requires ordered 64-KiB-aligned FIH PFS/CNT offsets.");
		}
		Dictionary<string, byte> assignments = NormalizeAssignments(inner, chunkAssignments ?? project.FileChunkAssignments, project.Chunks.Count);
		List<ProsperoPlayGoExtent> list = DeriveExtents(BuildPhysicalLeaves(inner, assignments), inner.MetadataOnDiskOffset, fihPfsOffset, fihCntOffset);
		List<uint>[] array = (from _ in Enumerable.Range(0, project.Chunks.Count)
			select new List<uint>()).ToArray();
		for (int num = 0; num < list.Count; num++)
		{
			array[list[num].ChunkId].Add((uint)num);
		}
		if (project.Chunks.Count == 1 && project.Scenarios.Count == 1)
		{
			long imageSize = inner.ImageSize;
			long num2 = ((imageSize <= 0) ? 0 : ((imageSize + 65536 - 1) / 65536 * 65536));
			ulong num3 = ((num2 > 0 && (ulong)num2 < fihCntOffset) ? ((ulong)num2) : (fihCntOffset - 65536));
			ulong mchunk = fihCntOffset - num3;
			return new ProsperoPlayGoChunkBuildResult
			{
				Data = BuildSingleChunkProfile(contentId, project.Chunks[0].LanguageMask, num3, mchunk),
				Extents = list,
				ChunkExtentIds = array
			};
		}
		byte[] array2 = EncodeLabels(project.Chunks.Select((PlayGoChunk c) => c.Label));
		byte[] array3 = EncodeLabels(project.Scenarios.Select((PlayGoScenario s) => s.Label));
		int num4;
		int num5;
		int num6;
		int num7;
		int num8;
		int num9;
		int num10;
		int num11;
		byte[] array4;
		checked
		{
			num4 = list.Count * 4;
			num5 = 256 + project.Chunks.Count * 32 + num4;
			num6 = num5 + array2.Length;
			num7 = num6 + list.Count * 16;
			num8 = num7 + project.Scenarios.Count * 32;
			num9 = project.Scenarios.Sum((PlayGoScenario s) => s.Chunks.Count);
			num10 = num8 + num9 * 2;
			num11 = num10 + array3.Length;
			array4 = new byte[num11];
		}
		WriteHeader(array4, contentId, project, num5, num6, num7, num8, num10, num11, num4, array2.Length, list.Count * 16, num9 * 2, array3.Length);
		int num12 = 256 + project.Chunks.Count * 32;
		int num13 = 0;
		for (int num14 = 0; num14 < project.Chunks.Count; num14++)
		{
			int num15 = 256 + num14 * 32;
			PlayGoChunk playGoChunk = project.Chunks[num14];
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num15), 196736u);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num15 + 4), (uint)array[num14].Count);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num15 + 8), 17u);
			BinaryPrimitives.WriteUInt64LittleEndian(array4.AsSpan(num15 + 16), playGoChunk.LanguageMask);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num15 + 24), (uint)num13);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num15 + 28), (uint)LabelOffset(project.Chunks, num14));
			foreach (uint item in array[num14])
			{
				BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num12 + num13), item);
				num13 += 4;
			}
		}
		array2.CopyTo(array4.AsSpan(num5));
		for (int num16 = 0; num16 < list.Count; num16++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(array4.AsSpan(num6 + num16 * 16), list[num16].Start);
			BinaryPrimitives.WriteUInt64LittleEndian(array4.AsSpan(num6 + num16 * 16 + 8), list[num16].Length);
		}
		int num17 = 0;
		for (int num18 = 0; num18 < project.Scenarios.Count; num18++)
		{
			PlayGoScenario playGoScenario = project.Scenarios[num18];
			int num19 = num7 + num18 * 32;
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num19), 33u);
			BinaryPrimitives.WriteUInt16LittleEndian(array4.AsSpan(num19 + 20), (ushort)playGoScenario.InitialChunkCount);
			BinaryPrimitives.WriteUInt16LittleEndian(array4.AsSpan(num19 + 22), (ushort)playGoScenario.Chunks.Count);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num19 + 24), (uint)num17);
			BinaryPrimitives.WriteUInt32LittleEndian(array4.AsSpan(num19 + 28), (uint)LabelOffset(project.Scenarios, num18));
			foreach (int chunk in playGoScenario.Chunks)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(array4.AsSpan(num8 + num17), (ushort)chunk);
				num17 += 2;
			}
		}
		array3.CopyTo(array4.AsSpan(num10));
		return new ProsperoPlayGoChunkBuildResult
		{
			Data = array4,
			Extents = list,
			ChunkExtentIds = array
		};
	}

	private static byte[] BuildSingleChunkProfile(string contentId, ulong languageMask, ulong mchunk0, ulong mchunk1)
	{
		byte[] array = new byte[416];
		BinaryPrimitives.WriteUInt32LittleEndian(array, 2020043888u);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(4), 4096);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(10), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(14), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(16), 416u);
		BinaryPrimitives.WriteUInt16LittleEndian(array.AsSpan(22), 1);
		array[30] = 133;
		array[32] = 2;
		array[36] = 1;
		array[48] = 17;
		array.AsSpan(56, 8).Fill(byte.MaxValue);
		Encoding.ASCII.GetBytes(contentId).CopyTo(array.AsSpan(64));
		WriteSection(array, 192, 256, 32);
		WriteSection(array, 200, 288, 8);
		WriteSection(array, 208, 304, 9);
		WriteSection(array, 216, 320, 32);
		WriteSection(array, 224, 352, 32);
		WriteSection(array, 232, 384, 2);
		WriteSection(array, 240, 400, 12);
		array[256] = 128;
		array[258] = 3;
		array[260] = 2;
		array[264] = 17;
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(272), languageMask);
		BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(292), 1u);
		Encoding.ASCII.GetBytes("Chunk #0\0").CopyTo(array.AsSpan(304));
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(320), 0uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(328), mchunk0);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(336), mchunk0);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(344), mchunk1);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(352), 33uL);
		array[372] = 1;
		array[374] = 1;
		Encoding.ASCII.GetBytes("Scenario #0\0").CopyTo(array.AsSpan(400));
		return array;
	}

	private static List<ProsperoPlayGoExtent> DeriveExtents(IReadOnlyList<PhysicalLeaf> leaves, long metadataOnDiskOffset, ulong pfsOffset, ulong cntOffset)
	{
		if (metadataOnDiskOffset < 0)
		{
			throw new InvalidDataException("Inner metadata on-disk offset is invalid.");
		}
		ulong num = checked(pfsOffset + (ulong)metadataOnDiskOffset);
		if (num > cntOffset || num % 65536 != 0L)
		{
			throw new InvalidDataException("PLGX metadata boundary is outside FIH PFS/CNT geometry or not 64-KiB aligned.");
		}
		ulong start = 0uL;
		int num2 = 0;
		List<ProsperoPlayGoExtent> list = new List<ProsperoPlayGoExtent>();
		foreach (PhysicalLeaf item in from l in leaves
			where l.OnDiskSize > 0
			orderby l.OnDiskOffset
			select l)
		{
			if (item.ChunkId != num2)
			{
				ulong num3 = checked(pfsOffset + (ulong)item.OnDiskOffset);
				if (num3 % 65536 != 0L)
				{
					throw new InvalidDataException("A non-zero PlayGo chunk transition must begin at a 64-KiB physical inner boundary.");
				}
				AddExtent(list, start, num3, num2);
				start = num3;
				num2 = item.ChunkId;
			}
		}
		AddExtent(list, start, num, num2);
		AddExtent(list, num, cntOffset, 0);
		return list;
	}

	private static void AddExtent(List<ProsperoPlayGoExtent> extents, ulong start, ulong end, int owner)
	{
		if (end <= start || (end - start) % 65536 != 0L)
		{
			throw new InvalidDataException("PLGX ownership extent is not a positive 64-KiB range.");
		}
		extents.Add(new ProsperoPlayGoExtent(start, end - start, owner));
	}

	private static List<PhysicalLeaf> BuildPhysicalLeaves(ProsperoInnerImageAssembler.InnerImageResult inner, IReadOnlyDictionary<string, byte> assignments)
	{
		Dictionary<uint, ProsperoInnerImageAssembler.Placement> byAfid = inner.Placements.ToDictionary((ProsperoInnerImageAssembler.Placement p) => p.Afid);
		return inner.Nodes.Where((ProsperoInnerImageAssembler.MetaNodeInfo n) => !n.IsDirectory && n.FullPath.Length != 0 && n.Size > 0).Select((ProsperoInnerImageAssembler.MetaNodeInfo n) =>
		{
			ProsperoInnerImageAssembler.Placement placement = byAfid[n.Afid];
			byte value;
			return new PhysicalLeaf(placement.OnDiskOffset, placement.OnDiskSize, (byte)(assignments.TryGetValue(ProsperoPlayGoRecordBuilder.NormalizePath(n.FullPath), out value) ? value : 0));
		}).ToList();
	}

	private static Dictionary<string, byte> NormalizeAssignments(ProsperoInnerImageAssembler.InnerImageResult inner, IReadOnlyDictionary<string, byte>? assignments, int chunkCount)
	{
		Dictionary<string, byte> dictionary = new Dictionary<string, byte>(StringComparer.Ordinal);
		if (assignments == null)
		{
			return dictionary;
		}
		HashSet<string> hashSet = (from n in inner.Nodes
			where !n.IsDirectory && n.FullPath.Length != 0
			select ProsperoPlayGoRecordBuilder.NormalizePath(n.FullPath)).ToHashSet(StringComparer.Ordinal);
		foreach (var (text2, b2) in assignments)
		{
			string text3 = ProsperoPlayGoRecordBuilder.NormalizePath(text2);
			bool flag = ((text3 == "SCE_SYS/KEYSTONE" || text3 == "SCE_SYS/ABOUT/RIGHT.SPRX") ? true : false);
			if (flag && b2 != 0)
			{
				throw new InvalidDataException("Generated PlayGo leaf '" + text2 + "' is fixed to chunk zero.");
			}
			if (b2 >= chunkCount || !hashSet.Contains(text3))
			{
				throw new InvalidDataException($"Invalid PlayGo assignment '{text2}' -> {b2}.");
			}
			dictionary.Add(text3, b2);
		}
		return dictionary;
	}

	private static void ValidateProject(string contentId, PlayGoProject project)
	{
		int count = project.Chunks.Count;
		bool flag = ((count < 1 || count > 255) ? true : false);
		bool flag2 = flag;
		if (!flag2)
		{
			int count2 = project.Scenarios.Count;
			bool flag3 = ((count2 < 1 || count2 > 65535) ? true : false);
			flag2 = flag3;
		}
		if (flag2 || project.DefaultScenarioId < 0 || project.DefaultScenarioId >= project.Scenarios.Count)
		{
			throw ProsperoErrorInfo.Unsupported("Unsupported debug/nwonly PlayGo project counts or default scenario.");
		}
		RequireAscii(contentId, 128, "content ID");
		for (int i = 0; i < project.Chunks.Count; i++)
		{
			if (project.Chunks[i].Id != i)
			{
				throw new InvalidDataException("PlayGo chunk IDs must be contiguous from zero.");
			}
			RequireAscii(project.Chunks[i].Label, int.MaxValue, "chunk label");
		}
		for (int j = 0; j < project.Scenarios.Count; j++)
		{
			PlayGoScenario playGoScenario = project.Scenarios[j];
			if (playGoScenario.Id != j || playGoScenario.Chunks.Count == 0 || playGoScenario.InitialChunkCount < 0 || playGoScenario.InitialChunkCount > playGoScenario.Chunks.Count || playGoScenario.Chunks.Any((int c) => c < 0 || c >= project.Chunks.Count) || playGoScenario.Chunks.Distinct().Count() != playGoScenario.Chunks.Count)
			{
				throw new InvalidDataException("Invalid debug/nwonly PlayGo scenario.");
			}
			RequireAscii(playGoScenario.Label, int.MaxValue, "scenario label");
		}
	}

	private static void WriteHeader(byte[] data, string contentId, PlayGoProject project, int d0, int d8, int e0, int e8, int f0, int f8, int c8Size, int chunkLabelSize, int d8Size, int e8Size, int scenarioLabelSize)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(data, 2020043888u);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 4096);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 1);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)project.Chunks.Count);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)project.Scenarios.Count);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)data.Length);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), (ushort)project.DefaultScenarioId);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 133u);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 4u);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), 17u);
		BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(56), ulong.MaxValue);
		Encoding.ASCII.GetBytes(contentId).CopyTo(data.AsSpan(64));
		WriteSection(data, 192, 256, project.Chunks.Count * 32);
		WriteSection(data, 200, 256 + project.Chunks.Count * 32, c8Size);
		WriteSection(data, 208, d0, chunkLabelSize);
		WriteSection(data, 216, d8, d8Size);
		WriteSection(data, 224, e0, project.Scenarios.Count * 32);
		WriteSection(data, 232, e8, e8Size);
		WriteSection(data, 240, f0, scenarioLabelSize);
		WriteSection(data, 248, f8, 0);
	}

	private static void WriteSection(byte[] data, int offset, int start, int size)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)start);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), (uint)size);
	}

	private static byte[] EncodeLabels(IEnumerable<string> labels)
	{
		return Encoding.ASCII.GetBytes(string.Concat(labels.Select((string l) => l + "\0")));
	}

	private static int LabelOffset<T>(IReadOnlyList<T> values, int index) where T : notnull
	{
		return values.Take(index).Sum((T v) =>
		{
			Encoding aSCII = Encoding.ASCII;
			string label;
			if (!(v is PlayGoChunk playGoChunk))
			{
				if (!(v is PlayGoScenario playGoScenario))
				{
					throw new InvalidOperationException();
				}
				label = playGoScenario.Label;
			}
			else
			{
				label = playGoChunk.Label;
			}
			return aSCII.GetByteCount(label) + 1;
		});
	}

	private static void RequireAscii(string value, int maxLength, string name)
	{
		if (string.IsNullOrEmpty(value) || value.Length > maxLength || value.Any((char c) => (c < ' ' || c > '~') ? true : false))
		{
			throw new InvalidDataException("PlayGo " + name + " must be non-empty printable ASCII.");
		}
	}
}
