using System;
using System.Buffers.Binary;

namespace ProsperoPkgTool.Content;

public static class ProsperoRightSprx
{
	public const int Size = 12752;

	public const uint Magic = 4009038932u;

	public static byte[]? Get()
	{
		return ProsperoPkgTool.Data.Blobs.Get("right.sprx");
	}

	public static bool IsRightSprx(ReadOnlySpan<byte> data)
	{
		if (data.Length == 12752)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(data) == 4009038932u;
		}
		return false;
	}
}
