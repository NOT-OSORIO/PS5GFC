using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPlayGoFicmBuilder
{
	public static byte[] Build(IReadOnlyList<ProsperoPlayGoFileRecord> records)
	{
		ProsperoPlayGoHashTableBuilder.ValidateSorted(records);
		byte[] array;
		checked
		{
			array = new byte[16 + records.Count * 2];
			BinaryPrimitives.WriteUInt32LittleEndian(array, 1u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(8), 16u);
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(12), (uint)(records.Count * 2));
		}
		for (int i = 0; i < records.Count; i++)
		{
			array[16 + i * 2] = records[i].ChunkId;
		}
		return array;
	}
}
