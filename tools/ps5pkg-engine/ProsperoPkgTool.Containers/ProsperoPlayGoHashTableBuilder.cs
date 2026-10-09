using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPlayGoHashTableBuilder
{
	public const int HeaderSize = 56;

	public const ulong Seed0 = 10577419142525243217uL;

	public const ulong Seed1 = 701355796979237965uL;

	public static byte[] Build(IReadOnlyList<ProsperoPlayGoFileRecord> records)
	{
		ValidateSorted(records);
		byte[] array;
		checked
		{
			array = new byte[56 + records.Count * 8];
			BinaryPrimitives.WriteUInt32LittleEndian(array, 1u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(4), 134217728u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(8), 56u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(12), (uint)(records.Count * 8));
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(24), 1414284927u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(36), (uint)records.Count);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 10577419142525243217uL);
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(48), 701355796979237965uL);
		}
		for (int i = 0; i < records.Count; i++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(56 + i * 8), records[i].PathHash);
		}
		return array;
	}

	internal static void ValidateSorted(IReadOnlyList<ProsperoPlayGoFileRecord> records)
	{
		ArgumentNullException.ThrowIfNull(records, "records");
		for (int i = 1; i < records.Count; i++)
		{
			if (records[i - 1].PathHash >= records[i].PathHash)
			{
				throw new ArgumentException("PlayGo records must be strictly unsigned-hash sorted.", "records");
			}
		}
	}
}
