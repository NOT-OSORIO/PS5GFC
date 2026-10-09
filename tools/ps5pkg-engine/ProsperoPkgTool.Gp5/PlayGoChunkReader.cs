using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProsperoPkgTool.Gp5;

public static class PlayGoChunkReader
{
	private readonly record struct Section(int Offset, int Size);

	private const uint PlgxMagic = 2020043888u;

	private const int HeaderSize = 256;

	private const int AttributeSize = 32;

	private const int ExtentSize = 16;

	private const int HashTableHeaderSize = 56;

	private const int HashTableCountOffset = 36;

	private const int FicmHeaderSize = 16;

	public static PlayGoProject Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		return Read(File.ReadAllBytes(path));
	}

	public static PlayGoProject Read(ReadOnlySpan<byte> data)
	{
		if (data.Length < 256)
		{
			throw new InvalidDataException("playgo-chunk.dat is shorter than its 0x100-byte header.");
		}
		if (U32(data, 0) != 2020043888)
		{
			throw new InvalidDataException("playgo-chunk.dat does not contain PS5 'plgx' magic.");
		}
		ushort versionMajor = U16(data, 4);
		ushort versionMinor = U16(data, 6);
		int num = U16(data, 8);
		int num2 = U16(data, 10);
		int num3 = U16(data, 14);
		uint num4 = U32(data, 16);
		int num5 = U16(data, 20);
		uint headerFlags = U32(data, 28);
		if (num4 < 256 || num4 > data.Length)
		{
			throw new InvalidDataException("playgo-chunk.dat declared file_size is outside the file.");
		}
		data = data.Slice(0, checked((int)num4));
		if ((num < 1 || num > 2) ? true : false)
		{
			throw new InvalidDataException($"Implausible PlayGo image count {num}.");
		}
		if ((num2 < 1 || num2 > 1000) ? true : false)
		{
			throw new InvalidDataException($"Implausible PlayGo chunk count {num2}.");
		}
		if ((num3 < 1 || num3 > 32) ? true : false)
		{
			throw new InvalidDataException($"Implausible PlayGo scenario count {num3}.");
		}
		if (num5 >= num3)
		{
			throw new InvalidDataException("PlayGo default scenario ID is outside the scenario table.");
		}
		Section section = ReadSection(data, 192, "chunk attributes");
		Section section2 = ReadSection(data, 200, "chunk extent ids");
		Section section3 = ReadSection(data, 208, "chunk labels");
		Section section4 = ReadSection(data, 216, "extents");
		Section section5 = ReadSection(data, 224, "scenario attributes");
		Section section6 = ReadSection(data, 232, "scenario chunks");
		Section section7 = ReadSection(data, 240, "scenario labels");
		RequireCapacity(section, num2, 32, "chunk attributes");
		RequireCapacity(section5, num3, 32, "scenario attributes");
		if (section4.Size % 16 != 0)
		{
			throw new InvalidDataException("PlayGo extent section is not a multiple of 16 bytes.");
		}
		if (section2.Size % 4 != 0)
		{
			throw new InvalidDataException("PlayGo chunk extent-id list is not a multiple of 4 bytes.");
		}
		int num6 = section4.Size / 16;
		List<PlayGoChunk> list = new List<PlayGoChunk>(num2);
		List<PlayGoExtent> list2 = new List<PlayGoExtent>();
		for (int i = 0; i < num2; i++)
		{
			int num7 = checked(section.Offset + i * 32);
			ulong languageMask = U64(data, num7 + 16);
			uint relativeOffset = U32(data, num7 + 28);
			string label = ReadLabel(data, section3, relativeOffset, $"chunk {i}");
			list.Add(new PlayGoChunk(i, label, languageMask));
			int num8;
			int num9;
			checked
			{
				num8 = (int)U32(data, num7 + 4);
				num9 = (int)U32(data, num7 + 24);
			}
			if (num8 < 0 || num9 < 0 || num9 + (long)num8 * 4L > section2.Size)
			{
				throw new InvalidDataException($"PlayGo chunk {i} extent-id list is outside its section.");
			}
			for (int j = 0; j < num8; j++)
			{
				int num11;
				checked
				{
					int num10 = (int)U32(data, section2.Offset + num9 + j * 4);
					if (num10 < 0 || num10 >= num6)
					{
						throw new InvalidDataException($"PlayGo chunk {i} references undefined extent {num10}.");
					}
					num11 = section4.Offset + num10 * 16;
				}
				list2.Add(new PlayGoExtent(i, U64(data, num11), U64(data, num11 + 8)));
			}
		}
		List<PlayGoScenario> list3 = new List<PlayGoScenario>(num3);
		for (int k = 0; k < num3; k++)
		{
			int num12 = checked(section5.Offset + k * 32);
			int num13 = U16(data, num12 + 20);
			int num14 = U16(data, num12 + 22);
			uint num15 = U32(data, num12 + 24);
			uint relativeOffset2 = U32(data, num12 + 28);
			if (num13 > num14)
			{
				throw new InvalidDataException($"PlayGo scenario {k} initial chunk count exceeds its sequence.");
			}
			if ((num15 & 1) != 0 || num15 > section6.Size || num15 + (long)num14 * 2L > section6.Size)
			{
				throw new InvalidDataException($"PlayGo scenario {k} chunk sequence is outside its section.");
			}
			List<int> list4 = new List<int>(num14);
			HashSet<int> hashSet = new HashSet<int>();
			for (int l = 0; l < num14; l++)
			{
				int num16 = U16(data, checked(section6.Offset + (int)num15 + l * 2));
				if (num16 >= num2)
				{
					throw new InvalidDataException($"PlayGo scenario {k} references undefined chunk {num16}.");
				}
				if (!hashSet.Add(num16))
				{
					throw new InvalidDataException($"PlayGo scenario {k} contains duplicate chunk {num16}.");
				}
				list4.Add(num16);
			}
			if (list4.Count == 0)
			{
				throw new InvalidDataException($"PlayGo scenario {k} has an empty chunk sequence.");
			}
			string label2 = ReadLabel(data, section7, relativeOffset2, $"scenario {k}");
			list3.Add(new PlayGoScenario(k, label2, num13, list4));
		}
		return new PlayGoProject
		{
			VersionMajor = versionMajor,
			VersionMinor = versionMinor,
			ContentId = ReadFixedAscii(data.Slice(64, 128)),
			DefaultScenarioId = num5,
			HeaderFlags = headerFlags,
			Chunks = list,
			Scenarios = list3,
			Extents = list2
		};
	}

	public static PlayGoProject Read(ReadOnlySpan<byte> data, ReadOnlySpan<byte> hashTable, ReadOnlySpan<byte> ficm, IReadOnlyDictionary<ulong, string>? pathByHash = null)
	{
		PlayGoProject playGoProject = Read(data);
		IReadOnlyList<PlayGoAssignment> assignments = ReadAssignments(hashTable, ficm, pathByHash);
		return new PlayGoProject
		{
			VersionMajor = playGoProject.VersionMajor,
			VersionMinor = playGoProject.VersionMinor,
			ContentId = playGoProject.ContentId,
			DefaultScenarioId = playGoProject.DefaultScenarioId,
			HeaderFlags = playGoProject.HeaderFlags,
			Chunks = playGoProject.Chunks,
			Scenarios = playGoProject.Scenarios,
			Extents = playGoProject.Extents,
			Assignments = assignments,
			FileChunkAssignments = playGoProject.FileChunkAssignments
		};
	}

	public static IReadOnlyList<PlayGoAssignment> ReadAssignments(ReadOnlySpan<byte> hashTable, ReadOnlySpan<byte> ficm, IReadOnlyDictionary<ulong, string>? pathByHash = null)
	{
		if (hashTable.Length < 56)
		{
			throw new InvalidDataException("playgo-hash-table.dat is shorter than its 0x38-byte header.");
		}
		int num = checked((int)U32(hashTable, 36));
		if (num < 0 || 56 + (long)num * 8L > hashTable.Length)
		{
			throw new InvalidDataException("PlayGo hash table is shorter than its declared count.");
		}
		if (ficm.Length < 16)
		{
			throw new InvalidDataException("playgo-ficm.dat is shorter than its 0x10-byte header.");
		}
		uint num2 = U32(ficm, 12);
		if (num2 != 0 && (num2 % 2 != 0 || num2 / 2 != (uint)num))
		{
			throw new InvalidDataException("PlayGo FICM count disagrees with the hash table.");
		}
		if (16 + (long)num * 2L > ficm.Length)
		{
			throw new InvalidDataException("PlayGo FICM is shorter than the hash-table count.");
		}
		List<PlayGoAssignment> list = new List<PlayGoAssignment>(num);
		for (int i = 0; i < num; i++)
		{
			ulong num3 = U64(hashTable, 56 + i * 8);
			int chunkId = U16(ficm, 16 + i * 2);
			string path = ((pathByHash != null && pathByHash.TryGetValue(num3, out string value)) ? value : null);
			list.Add(new PlayGoAssignment(num3, chunkId, path));
		}
		return list;
	}

	private static Section ReadSection(ReadOnlySpan<byte> data, int pointerOffset, string name)
	{
		uint num = U32(data, pointerOffset);
		uint num2 = U32(data, pointerOffset + 4);
		if (num > data.Length || (long)num + (long)num2 > data.Length)
		{
			throw new InvalidDataException("PlayGo " + name + " section is outside the file.");
		}
		return checked(new Section((int)num, (int)num2));
	}

	private static void RequireCapacity(Section section, int count, int stride, string name)
	{
		if ((long)count * (long)stride > section.Size)
		{
			throw new InvalidDataException("PlayGo " + name + " section is shorter than its declared count.");
		}
	}

	private static string ReadLabel(ReadOnlySpan<byte> data, Section section, uint relativeOffset, string owner)
	{
		if (relativeOffset >= section.Size)
		{
			throw new InvalidDataException("PlayGo " + owner + " label offset is outside the label section.");
		}
		ReadOnlySpan<byte> span = data.Slice(checked(section.Offset + (int)relativeOffset), section.Size - checked((int)relativeOffset));
		int num = span.IndexOf((byte)0);
		if (num < 0)
		{
			throw new InvalidDataException("PlayGo " + owner + " label is not NUL terminated.");
		}
		if (span.Slice(0, num).ContainsAnyExceptInRange((byte)32, (byte)126))
		{
			throw new InvalidDataException("PlayGo " + owner + " label contains non-ASCII control data.");
		}
		return Encoding.ASCII.GetString(span.Slice(0, num));
	}

	private static string ReadFixedAscii(ReadOnlySpan<byte> value)
	{
		int num = value.IndexOf((byte)0);
		if (num >= 0)
		{
			value = value.Slice(0, num);
		}
		return Encoding.ASCII.GetString(value).TrimEnd();
	}

	private static ushort U16(ReadOnlySpan<byte> data, int offset)
	{
		return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
	}

	private static uint U32(ReadOnlySpan<byte> data, int offset)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
	}

	private static ulong U64(ReadOnlySpan<byte> data, int offset)
	{
		return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
	}
}
