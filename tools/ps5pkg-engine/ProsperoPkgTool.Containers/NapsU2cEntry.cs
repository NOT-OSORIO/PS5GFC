using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public readonly record struct NapsU2cEntry(uint InfoOffset9BBase, byte[] DeltaFromBase)
{
	public IReadOnlyList<uint> StartCblockInfoIndex
	{
		get
		{
			uint[] array = new uint[8] { InfoOffset9BBase, 0u, 0u, 0u, 0u, 0u, 0u, 0u };
			for (int i = 0; i < 7 && i < DeltaFromBase.Length; i++)
			{
				array[i + 1] = InfoOffset9BBase + DeltaFromBase[i];
			}
			return array;
		}
	}
}
