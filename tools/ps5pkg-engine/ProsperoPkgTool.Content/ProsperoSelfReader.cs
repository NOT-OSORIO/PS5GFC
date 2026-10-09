using System;
using System.Buffers.Binary;
using System.IO;

namespace ProsperoPkgTool.Content;

public static class ProsperoSelfReader
{
	private const int ContainerHeaderSize = 32;

	private const int SegmentEntrySize = 32;

	private const int ExtInfoSize = 64;

	private const int ElfHeaderSize = 64;

	private const int ElfProgramHeaderSize = 56;

	private const int ProgramHeaderCountOffset = 56;

	public static bool IsSelf(ReadOnlySpan<byte> data)
	{
		bool flag = data.Length >= 32;
		if (flag)
		{
			uint num = BinaryPrimitives.ReadUInt32LittleEndian(data);
			bool flag2 = ((num == 490542415 || num == 4009038932u) ? true : false);
			flag = flag2;
		}
		return flag;
	}

	public static ProsperoSelfImage Parse(ReadOnlySpan<byte> data)
	{
		if (!TryParse(data, out ProsperoSelfImage image))
		{
			throw new InvalidDataException("The buffer is not a structurally valid SELF container.");
		}
		return image;
	}

	public static bool TryParse(ReadOnlySpan<byte> data, out ProsperoSelfImage? image)
	{
		image = null;
		if (data.Length < 32 || !IsSelf(data))
		{
			return false;
		}
		uint programType = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8));
		ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12));
		ushort metaSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14));
		ulong fileSize = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16));
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(24));
		if (32 + (long)num * 32L > data.Length)
		{
			return false;
		}
		int embeddedElfOffset = 32 + num * 32;
		ProsperoSelfExtInfo extInfo = TryReadExtInfo(data, embeddedElfOffset);
		image = new ProsperoSelfImage(programType, headerSize, metaSize, fileSize, num, extInfo);
		return true;
	}

	public static bool TryReadAuthorityId(ReadOnlySpan<byte> data, out ulong authorityId)
	{
		authorityId = 0uL;
		if (!TryParse(data, out ProsperoSelfImage image) || (object)image.ExtInfo == null)
		{
			return false;
		}
		authorityId = image.ExtInfo.AuthorityId;
		return true;
	}

	private static ProsperoSelfExtInfo? TryReadExtInfo(ReadOnlySpan<byte> data, int embeddedElfOffset)
	{
		if (embeddedElfOffset < 0 || embeddedElfOffset + 64 > data.Length)
		{
			return null;
		}
		if (!ProsperoSelfBuilder.IsElf(data.Slice(embeddedElfOffset)))
		{
			return null;
		}
		ushort num = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(embeddedElfOffset + 56));
		int num2 = 64 + num * 56;
		if ((long)embeddedElfOffset + (long)num2 > data.Length)
		{
			return null;
		}
		int num3 = AlignUp(embeddedElfOffset + num2, 16);
		if (num3 + 64 > data.Length)
		{
			return null;
		}
		ulong authorityId = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3));
		ulong programType = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3 + 8));
		ulong appVersion = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3 + 16));
		ulong firmwareVersion = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(num3 + 24));
		byte[] elfDigest = data.Slice(num3 + 32, 32).ToArray();
		return new ProsperoSelfExtInfo(authorityId, programType, appVersion, firmwareVersion, elfDigest);
	}

	private static int AlignUp(int value, int alignment)
	{
		return (value + alignment - 1) & ~(alignment - 1);
	}
}
