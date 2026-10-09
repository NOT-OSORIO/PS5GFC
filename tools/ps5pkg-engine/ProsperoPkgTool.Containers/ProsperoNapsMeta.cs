using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoNapsMeta
{
	private readonly record struct Meta18Block(ulong Co, uint Cs, uint Ps, uint C0, uint C1, uint Flag, bool IsHole, uint OwnerFlag, ulong Tail, long OnDiskOffset, uint OnDiskLen);

	public const int Meta300Length = 48;

	public const ulong Meta300KindId = 1001uL;

	public const ulong PfsBlockSizeConst = 65536uL;

	public const string PfsMetadataFileName = "*PFSmetadata";

	private const int Meta18BlockSize = 65536;

	private const long Meta18UBlock = 262144L;

	private static readonly byte[] Meta18DataKey = new byte[16]
	{
		2, 45, 202, 246, 209, 17, 229, 143, 37, 147,
		110, 245, 70, 147, 69, 171
	};

	private static readonly byte[] Meta18TweakKey = new byte[16]
	{
		173, 172, 22, 55, 96, 218, 81, 70, 152, 194,
		69, 171, 76, 156, 66, 108
	};

	private static readonly byte[] Meta18Tweak = new byte[16]
	{
		60, 186, 16, 125, 0, 0, 0, 0, 0, 0,
		0, 0, 0, 0, 0, 0
	};

	public static ReadOnlySpan<int> Meta300Ids => new int[4] { 300, 301, 302, 308 };

	public static byte[] BuildMeta300(ulong innerImageDataRegionSize)
	{
		byte[] array = new byte[48];
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(16), innerImageDataRegionSize);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(24), 1001uL);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(32), innerImageDataRegionSize);
		BinaryPrimitives.WriteUInt64LittleEndian(array.AsSpan(40), 65536uL);
		return array;
	}

	public static byte[] BuildMeta300FromInnerImageSize(ulong innerImageSize)
	{
		if (innerImageSize < 65536)
		{
			throw new ArgumentOutOfRangeException("innerImageSize", $"inner-image size 0x{innerImageSize:X} is smaller than one 0x{65536uL:X} block");
		}
		return BuildMeta300(innerImageSize - 65536);
	}

	public static byte[] BuildMeta18(ulong innerImageSize, ReadOnlySpan<byte> mountImage, IReadOnlyList<(string Path, long Size)> contentFiles, ProsperoInnerImageAssembler.InnerImageResult? inner = null, long? mountLengthOverride = null, byte[]? obdgOverride = null, byte[]? rhshOverride = null)
	{
		ArgumentNullException.ThrowIfNull(contentFiles, "contentFiles");
		long num = mountLengthOverride ?? mountImage.Length;
		if (innerImageSize < 65536 || num < 65536)
		{
			return Array.Empty<byte>();
		}
		uint value = (uint)(innerImageSize / 65536);
		int num2 = (int)(num / 65536);
		List<Meta18Block> list = ((inner != null) ? BuildInnerBlocks(inner) : null);
		int num3 = list?.FindIndex((Meta18Block b) => b.Tail == 1001) ?? (-1);
		int num4 = list?.Count ?? num2;
		List<byte> list2 = new List<byte>(4096);
		Span<byte> span = stackalloc byte[24];
		WriteU32(span, 0, 1u);
		WriteU32(span, 4, 48u);
		WriteU32(span, 8, value);
		WriteU32(span, 12, (uint)innerImageSize);
		WriteU32(span, 16, 1u);
		WriteU32(span, 20, 65536u);
		WriteRecord(list2, "phdr", 1, span);
		byte[] array = new byte[Math.Max(contentFiles.Count, 1) * 24];
		uint value2;
		int num7;
		uint value3;
		int num6;
		uint value4;
		uint value5;
		Span<byte> dst;
		for (int num5 = 0; num5 < contentFiles.Count; value2 = (uint)num7, value3 = ((num6 == 0) ? 1u : 3u), value4 = ((num6 != 0) ? 1001u : 0u), value5 = ((num6 == 0 && num5 != 0) ? 1u : 0u), BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(0, 8), (ulong)Math.Max(contentFiles[num5].Size, 0L)), WriteU32(dst, 8, value2), WriteU32(dst, 12, value3), WriteU32(dst, 16, value4), WriteU32(dst, 20, value5), num5++)
		{
			dst = array.AsSpan(num5 * 24, 24);
			if (list != null)
			{
				num6 = ((contentFiles[num5].Path == "*PFSmetadata") ? 1 : 0);
				if (num6 != 0 && num3 >= 0)
				{
					num7 = num3;
					continue;
				}
			}
			else
			{
				num6 = 0;
			}
			num7 = num5;
		}
		WriteRecord(list2, "file", 2, array);
		byte[] array2;
		if (list != null)
		{
			array2 = new byte[list.Count];
			for (int num8 = 0; num8 < list.Count; num8++)
			{
				array2[num8] = (byte)((list[num8].OwnerFlag == 1) ? 1 : 15);
			}
		}
		else
		{
			array2 = new byte[num2];
			Array.Fill(array2, (byte)15);
		}
		WriteRecord(list2, "ibcl", 1, array2);
		byte[] array3 = new byte[num4 * 40];
		for (int num9 = 0; num9 < num4; num9++)
		{
			Span<byte> dst2 = array3.AsSpan(num9 * 40, 40);
			if (list != null)
			{
				Meta18Block meta18Block = list[num9];
				BinaryPrimitives.WriteUInt64LittleEndian(dst2.Slice(0, 8), meta18Block.Co);
				WriteU32(dst2, 8, meta18Block.Cs);
				WriteU32(dst2, 12, meta18Block.Ps);
				WriteU32(dst2, 16, meta18Block.C0);
				WriteU32(dst2, 20, meta18Block.C1);
				WriteU32(dst2, 24, (uint)(meta18Block.Co >> 16));
				WriteU32(dst2, 32, 1u);
				WriteU32(dst2, 36, meta18Block.Flag);
			}
			else
			{
				BinaryPrimitives.WriteUInt64LittleEndian(dst2.Slice(0, 8), (ulong)(num9 * 65536));
				WriteU32(dst2, 8, 65536u);
				WriteU32(dst2, 12, 65536u);
				WriteU32(dst2, 16, 65536u);
				WriteU32(dst2, 24, (uint)((ulong)(num9 * 65536) >> 16));
				WriteU32(dst2, 32, 1u);
				WriteU32(dst2, 36, 1074331648u);
			}
		}
		WriteRecord(list2, "i2ob", 1, array3);
		byte[] array4 = new byte[num4 * 16];
		for (int num10 = 0; num10 < num4; num10++)
		{
			Span<byte> span2 = array4.AsSpan(num10 * 16, 16);
			ulong num11 = (ulong)(((long?)list?[num10].Co) ?? ((long)(num10 * 65536)));
			BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(0, 8), num11);
			BinaryPrimitives.WriteUInt64LittleEndian(span2.Slice(8, 8), num11 >> 16);
		}
		WriteRecord(list2, "i2op", 1, array4);
		byte[] array5 = new byte[num4 * 48];
		byte[] array6 = new byte[262144];
		using (Stream stream = ((list != null) ? inner.OpenImage() : null))
		{
			if (list != null)
			{

				int rangeSize = Math.Max(256, num4 / (Environment.ProcessorCount * 8));
				System.Threading.Tasks.Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, num4, rangeSize), delegate(Tuple<int, int> range)
				{
					using Stream rangeStream = inner.OpenImage();
					for (int index = range.Item1; index < range.Item2; index++)
					{
						Span<byte> slot = array5.AsSpan(index * 48, 48);
						Meta18Block block = list[index];
						if (block.IsHole)
						{
							WriteU32(slot, 0, 0u);
							WriteU32(slot, 4, block.Ps);
							Sha3.Sha3_256(array6.AsSpan(0, (int)block.Ps)).CopyTo(slot.Slice(8, 32));
						}
						else
						{
							Sha3.Sha3_256(ReadInnerBlock(rangeStream, block.OnDiskOffset, (int)block.OnDiskLen)).AsSpan(0, 32).CopyTo(slot.Slice(8, 32));
							BinaryPrimitives.WriteUInt64LittleEndian(slot.Slice(40, 8), block.Tail);
						}
					}
				});
			}
			for (int num12 = 0; num12 < ((list != null) ? 0 : num4); num12++)
			{
				Span<byte> dst3 = array5.AsSpan(num12 * 48, 48);
				if (list != null)
				{
					Meta18Block meta18Block2 = list[num12];
					if (meta18Block2.IsHole)
					{
						WriteU32(dst3, 0, 0u);
						WriteU32(dst3, 4, meta18Block2.Ps);
						Sha3.Sha3_256(array6.AsSpan(0, (int)meta18Block2.Ps)).CopyTo(dst3.Slice(8, 32));
					}
					else
					{
						Sha3.Sha3_256(ReadInnerBlock(stream, meta18Block2.OnDiskOffset, (int)meta18Block2.OnDiskLen)).AsSpan(0, 32).CopyTo(dst3.Slice(8, 32));
						BinaryPrimitives.WriteUInt64LittleEndian(dst3.Slice(40, 8), meta18Block2.Tail);
					}
				}
				else
				{
					WriteU32(dst3, 0, (uint)num12);
					WriteU32(dst3, 4, 65536u);
					int num13 = num12 * 65536;
					int length = Math.Min(65536, mountImage.Length - num13);
					Sha3.Sha3_256(mountImage.Slice(num13, length)).CopyTo(dst3.Slice(8, 32));
				}
			}
			WriteRecord(list2, "ihsh", 1, array5);
		}
		byte[] array7 = new byte[176];
		if (rhshOverride != null && rhshOverride.Length > 0)
		{
			rhshOverride.AsSpan(0, Math.Min(rhshOverride.Length, array7.Length)).CopyTo(array7);
		}
		else
		{
			int num14 = ProsperoFihBuilder.LocateSuperblock(mountImage);
			if (num14 >= 0)
			{
				Sha3.Sha3_256(mountImage.Slice(num14, 65536)).CopyTo(array7);
			}
		}
		WriteRecord(list2, "rhsh", 1, array7);
		StringBuilder stringBuilder = new StringBuilder();
		foreach (var contentFile in contentFiles)
		{
			string item = contentFile.Path;
			stringBuilder.Append(item.Replace('\\', '/'));
			stringBuilder.Append('\0');
		}
		WriteRecord(list2, "fstr", 1, Encoding.ASCII.GetBytes(stringBuilder.ToString()));
		Span<byte> span3 = stackalloc byte[20];
		span3.Clear();
		WriteU32(span3, 4, 4u);
		WriteRecord(list2, "twek", 1, span3);
		byte[] array8 = new byte[128];
		if (obdgOverride != null && obdgOverride.Length > 0)
		{
			obdgOverride.AsSpan(0, Math.Min(obdgOverride.Length, array8.Length)).CopyTo(array8);
		}
		else
		{
			Sha3.Sha3_256(mountImage).CopyTo(array8);
		}
		WriteRecord(list2, "obdg", 1, array8);
		byte[] array9 = BuildMeta300FromInnerImageSize(innerImageSize);
		WriteRecord(list2, "pgpl", 1, array9);
		WriteRecord(list2, "pgil", 1, array9);
		WriteRecord(list2, "pgpi", 1, array9);
		WriteRecord(list2, "pgpu", 1, array9);
		int num15 = (16 - list2.Count % 16) % 16;
		WriteRecord(list2, "zero", 1, new byte[num15]);
		return AesXtsTransform(list2.ToArray(), encrypt: true);
	}

	public static byte[] BuildMeta18WithMetadataEntry(ulong innerImageSize, ReadOnlySpan<byte> mountImage, IReadOnlyList<(string Path, long Size)> contentFiles, long pfsImageSize, ProsperoInnerImageAssembler.InnerImageResult? inner = null, long? mountLengthOverride = null, byte[]? obdgOverride = null, byte[]? rhshOverride = null)
	{
		List<(string, long)> list = new List<(string, long)>(contentFiles.Count + 1);
		list.AddRange(contentFiles);
		list.Add(("*PFSmetadata", pfsImageSize));
		return BuildMeta18(innerImageSize, mountImage, list, inner, mountLengthOverride, obdgOverride, rhshOverride);
	}

	public static byte[] Sha3Stream(Stream stream)
	{
		return Sha3.Sha3_256(stream);
	}

	public static byte[] DecryptMeta18(ReadOnlySpan<byte> encrypted)
	{
		return AesXtsTransform(encrypted.ToArray(), encrypt: false);
	}

	private static List<Meta18Block> BuildInnerBlocks(ProsperoInnerImageAssembler.InnerImageResult inner)
	{
		List<Meta18Block> list = new List<Meta18Block>();
		IReadOnlyList<ProsperoInnerImageAssembler.Placement> placements = inner.Placements;
		for (int i = 0; i < placements.Count; i++)
		{
			ProsperoInnerImageAssembler.Placement placement = placements[i];
			uint num = (uint)placement.OnDiskSize;
			list.Add(new Meta18Block((ulong)placement.OnDiskOffset, num, (uint)placement.UncompressedSize, num, 0u, 1074331648u, IsHole: false, (i != 0) ? 1u : 0u, 0uL, placement.OnDiskOffset, num));
		}
		long num2 = inner.MetaBaseLogical - inner.DataEndLogical;
		if (num2 > 0)
		{
			int num3 = (int)((num2 + 262144 - 1) / 262144);
			Dictionary<uint, ulong> dictionary = new Dictionary<uint, ulong>();
			ulong num4 = (ulong)inner.BlockInfoOnDiskOffset;
			for (int j = 0; j < num3; j++)
			{
				uint num5 = (uint)Math.Min(262144L, num2 - (long)j * 262144L);
				if (!dictionary.TryGetValue(num5, out var value))
				{
					value = (dictionary[num5] = num4);
					num4 += 16;
				}
				list.Add(new Meta18Block(value, 16u, num5, 8u, 8u, 1074855936u, IsHole: true, 0u, 0uL, 0L, 0u));
			}
		}
		byte[] compressedMetadata = inner.CompressedMetadata;
		int num7 = 0;
		if (compressedMetadata.Length >= 176)
		{
			num7 = (int)BinaryPrimitives.ReadUInt32LittleEndian(compressedMetadata.AsSpan(170, 4));
		}
		ulong num8 = (ulong)(inner.MetadataOnDiskOffset + num7);
		using IEnumerator<ProsperoPfsv3Writer.CompressedBlock> enumerator = inner.MetadataBlocks.GetEnumerator();
		uint c;
		int num10;
		uint c2;
		int num9;
		ProsperoPfsv3Writer.CompressedBlock current;
		uint flag;
		for (; enumerator.MoveNext(); c = (uint)num10, c2 = ((num9 != 0 && current.MultiChunk) ? ((uint)(current.CompressedSize - current.FirstChunkCompressedSize)) : 0u), flag = ((num9 != 0 && current.MultiChunk) ? 1078263808u : 1074069504u), list.Add(new Meta18Block(num8, (uint)current.CompressedSize, (uint)current.UncompressedSize, c, c2, flag, IsHole: false, 0u, 1001uL, (long)num8, (uint)current.CompressedSize)), num8 += (uint)current.CompressedSize)
		{
			current = enumerator.Current;
			if (current.Flags != 0)
			{
				num9 = ((current.CompressedSize < current.UncompressedSize) ? 1 : 0);
				if (num9 != 0)
				{
					num10 = (current.MultiChunk ? current.FirstChunkCompressedSize : current.CompressedSize);
					continue;
				}
			}
			else
			{
				num9 = 0;
			}
			num10 = current.CompressedSize;
		}
		return list;
	}

	private static byte[] ReadInnerBlock(Stream stream, long offset, int length)
	{
		if (stream == null || offset < 0 || length <= 0 || offset >= stream.Length)
		{
			return Array.Empty<byte>();
		}
		int num = (int)Math.Min(length, stream.Length - offset);
		byte[] array = new byte[num];
		stream.Position = offset;
		int i;
		int num2;
		for (i = 0; i < num; i += num2)
		{
			num2 = stream.Read(array, i, num - i);
			if (num2 <= 0)
			{
				break;
			}
		}
		if (i < num)
		{
			Array.Resize(ref array, i);
		}
		return array;
	}

	private static void WriteU32(Span<byte> dst, int offset, uint value)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(offset, 4), value);
	}

	private static void WriteRecord(List<byte> dst, string tag, byte version, ReadOnlySpan<byte> payload)
	{
		dst.Add((byte)tag[3]);
		dst.Add((byte)tag[2]);
		dst.Add((byte)tag[1]);
		dst.Add((byte)tag[0]);
		dst.Add(version);
		dst.Add(0);
		dst.Add(0);
		dst.Add(0);
		Span<byte> destination = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)payload.Length);
		for (int i = 0; i < 8; i++)
		{
			dst.Add(destination[i]);
		}
		ReadOnlySpan<byte> readOnlySpan = payload;
		for (int j = 0; j < readOnlySpan.Length; j++)
		{
			byte item = readOnlySpan[j];
			dst.Add(item);
		}
	}

	private static byte[] AesXtsTransform(byte[] input, bool encrypt)
	{
		return AesXtsTransform(input, Meta18DataKey, Meta18TweakKey, Meta18Tweak, encrypt);
	}

	private static byte[] AesXtsTransform(byte[] input, ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> tweakKey, ReadOnlySpan<byte> initialTweak, bool encrypt)
	{
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		aes.Key = dataKey.ToArray();
		using Aes aes2 = Aes.Create();
		aes2.Mode = CipherMode.ECB;
		aes2.Padding = PaddingMode.None;
		aes2.Key = tweakKey.ToArray();
		using ICryptoTransform cryptoTransform = (encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor());
		using ICryptoTransform cryptoTransform2 = aes2.CreateEncryptor();
		byte[] array = cryptoTransform2.TransformFinalBlock(initialTweak.ToArray(), 0, 16);
		byte[] array2 = new byte[input.Length];
		byte[] array3 = new byte[16];
		for (int i = 0; i < input.Length; i += 16)
		{
			for (int j = 0; j < 16; j++)
			{
				array3[j] = (byte)(input[i + j] ^ array[j]);
			}
			byte[] array4 = cryptoTransform.TransformFinalBlock(array3, 0, 16);
			for (int k = 0; k < 16; k++)
			{
				array2[i + k] = (byte)(array4[k] ^ array[k]);
			}
			MultiplyTweakByAlpha(array);
		}
		return array2;
	}

	private static void MultiplyTweakByAlpha(byte[] tweak)
	{
		int num = 0;
		for (int i = 0; i < 16; i++)
		{
			int num2 = tweak[i];
			tweak[i] = (byte)(((num2 << 1) | num) & 0xFF);
			num = (num2 >> 7) & 1;
		}
		if (num != 0)
		{
			tweak[0] ^= 135;
		}
	}
}
