using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPatchRelocation
{
	public sealed record Tables(uint[] Origin, uint[] Gathered, uint[] Target, uint[] Update)
	{
		public int Count => Origin.Length;
	}

	private const int HeaderSize = 4096;

	private const int BlockSize = 65536;

	private const uint Missing = uint.MaxValue;

	public static Tables Read(string patchPath, ProsperoPackageInspection inspection)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchPath, "patchPath");
		ArgumentNullException.ThrowIfNull(inspection, "inspection");
		if (inspection.Kind != ProsperoPackageKind.FinalizedPatchDebug || (object)inspection.Lih == null)
		{
			throw new InvalidDataException("Relocation tables require a debug LIH patch package.");
		}
		using FileStream fileStream = File.OpenRead(Path.GetFullPath(patchPath));
		ProsperoPackageSegment prosperoPackageSegment = inspection.Segments.Single((ProsperoPackageSegment segment) => segment.Name == "SI");
		if (prosperoPackageSegment.Size <= 0 || prosperoPackageSegment.Size > int.MaxValue)
		{
			throw ProsperoErrorInfo.Unsupported("Patch SI size is unsupported.");
		}
		byte[] array = new byte[checked((int)prosperoPackageSegment.Size)];
		fileStream.Position = prosperoPackageSegment.Offset;
		fileStream.ReadExactly(array);
		using MemoryStream stream = new MemoryStream(array, writable: false);
		using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read);
		uint[] array2 = ReadTable(archive, "common/origin-relocinfo.dat");
		uint[] array3 = ReadTable(archive, "common/gathered-relocinfo.dat");
		uint[] array4 = ReadTable(archive, "common/target-relocinfo.dat");
		uint[] array5 = ReadTable(archive, "common/update-relocinfo.dat");
		if (array3.Length != array2.Length || array4.Length != array2.Length || array5.Length != array2.Length)
		{
			throw new InvalidDataException("Patch relocation tables disagree on record count.");
		}
		return new Tables(array2, array3, array4, array5);
	}

	public static byte[] MaterializeEncryptedVirtualImage(string patchPath, string referencePath, Tables tables)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(patchPath, "patchPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(referencePath, "referencePath");
		ArgumentNullException.ThrowIfNull(tables, "tables");
		if (tables.Count < 2)
		{
			throw new InvalidDataException("Patch relocation table contains no virtual PFS blocks.");
		}
		using FileStream fileStream = File.OpenRead(Path.GetFullPath(patchPath));
		using FileStream fileStream2 = File.OpenRead(Path.GetFullPath(referencePath));
		byte[] array = new byte[checked((tables.Count - 1) * 65536)];
		for (int i = 1; i < tables.Count; i++)
		{
			uint num = tables.Origin[i];
			uint num2 = tables.Gathered[i];
			bool flag = num != uint.MaxValue;
			bool flag2 = num2 != uint.MaxValue;
			if (flag == flag2)
			{
				throw new InvalidDataException($"Patch relocation destination block {i} must have exactly one source.");
			}
			uint num3 = (flag ? num : num2);
			Stream stream = (flag ? fileStream2 : fileStream);
			long num4;
			checked
			{
				num4 = unchecked((long)num3) * 65536L;
			}
			if (num4 < 0 || num4 > stream.Length - 65536)
			{
				throw new InvalidDataException($"Patch relocation source block {num3} is outside the {(flag ? "reference" : "patch")} package.");
			}
			stream.Position = num4;
			stream.ReadExactly(array.AsSpan((i - 1) * 65536, 65536));
		}
		return array;
	}

	private static uint[] ReadTable(ZipArchive archive, string name)
	{
		ZipArchiveEntry zipArchiveEntry = archive.GetEntry(name) ?? throw new InvalidDataException("Patch SI does not contain '" + name + "'.");
		if (zipArchiveEntry.Length < 4096 || zipArchiveEntry.Length > int.MaxValue)
		{
			throw new InvalidDataException("Patch relocation file '" + name + "' has an invalid size.");
		}
		byte[] array = new byte[checked((int)zipArchiveEntry.Length)];
		using (Stream stream = zipArchiveEntry.Open())
		{
			stream.ReadExactly(array);
		}
		if (array.Length < 40)
		{
			throw new InvalidDataException("Patch relocation file '" + name + "' has a truncated header.");
		}
		uint num = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(20, 4));
		uint num2 = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(24, 4));
		uint num3 = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(32, 4));
		uint num4 = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(36, 4));
		if (num2 != array.Length || num3 != 4096 || num4 != (long)num * 4L || num3 > array.Length || num4 > array.Length - num3)
		{
			throw new InvalidDataException("Patch relocation file '" + name + "' has inconsistent geometry.");
		}
		uint[] array2 = new uint[num];
		for (int i = 0; i < array2.Length; i++)
		{
			array2[i] = BinaryPrimitives.ReadUInt32LittleEndian(array.AsSpan(4096 + i * 4, 4));
		}
		return array2;
	}
}
