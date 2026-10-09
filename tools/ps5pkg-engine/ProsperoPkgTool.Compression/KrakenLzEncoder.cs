using System;
using System.Collections.Generic;
using System.Numerics;

namespace ProsperoPkgTool.Compression;

internal static class KrakenLzEncoder
{
	private sealed class MsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private int _current;

		private int _bits;

		public void WriteBit(int bit)
		{
			_current = (_current << 1) | (bit & 1);
			if (++_bits == 8)
			{
				_bytes.Add((byte)_current);
				_current = 0;
				_bits = 0;
			}
		}

		public void WriteBits(uint value, int count)
		{
			for (int num = count - 1; num >= 0; num--)
			{
				WriteBit((int)((value >> num) & 1));
			}
		}

		public byte[] ToArray()
		{
			if (_bits == 0)
			{
				return _bytes.ToArray();
			}
			byte[] array = new byte[_bytes.Count + 1];
			_bytes.CopyTo(array, 0);
			array[^1] = (byte)(_current << 8 - _bits);
			return array;
		}
	}

	private const int SeedSize = 8;

	private const int MinMatch = 4;

	private const int NoMatchZone = 16;

	private const int LiteralTail = 8;

	private const int MaxLengthEscapes = 512;

	private const int ChunkSize = 131072;

	private const int MaxBlockSize = 262144;

	private const int HashBits = 18;

	private const int HashSize = 262144;

	private const int MaxChain = 128;

	[ThreadStatic]
	private static int[]? t_head;

	[ThreadStatic]
	private static int[]? t_prev;

	internal static KrakenEncoder.EncodedBlock? Encode(ReadOnlySpan<byte> input)
	{
		int length = input.Length;
		if ((length < 8 || length > 262144) ? true : false)
		{
			return null;
		}
		byte[] array = input.ToArray();
		int num = array.Length;
		int[] array2 = t_head ?? (t_head = new int[262144]);
		Array.Fill(array2, -1);
		int[] array3 = t_prev;
		int[] prev = ((array3 != null && array3.Length >= num) ? array3 : (t_prev = new int[num]));
		int nextInsert = 0;
		int chunkEnd = Math.Min(num, 131072);
		byte[] array4 = EncodeChunk(array, array2, prev, ref nextInsert, 0, chunkEnd, withSeed: true);
		if (array4 == null)
		{
			return null;
		}
		if (num <= 131072)
		{
			if (array4.Length >= num)
			{
				return null;
			}
			return new KrakenEncoder.EncodedBlock
			{
				Payload = array4,
				Flags = 2,
				FirstChunkCompressedLength = 0,
				UncompressedLength = num,
				Mode = KrakenEncoder.LiteralMode.NewLz
			};
		}
		byte[] array5 = EncodeChunk(array, array2, prev, ref nextInsert, 131072, num, withSeed: false);
		if (array5 == null)
		{
			return null;
		}
		if (array4.Length + array5.Length >= num)
		{
			return null;
		}
		byte[] array6 = new byte[array4.Length + array5.Length];
		array4.CopyTo(array6, 0);
		array5.CopyTo(array6, array4.Length);
		return new KrakenEncoder.EncodedBlock
		{
			Payload = array6,
			Flags = 34,
			FirstChunkCompressedLength = array4.Length,
			UncompressedLength = num,
			Mode = KrakenEncoder.LiteralMode.NewLz
		};
	}

	private static byte[]? EncodeChunk(byte[] data, int[] head, int[] prev, ref int nextInsert, int chunkStart, int chunkEnd, bool withSeed)
	{
		int num = (withSeed ? 8 : chunkStart);
		int num2 = chunkEnd - 16;
		int matchEndLimit = chunkEnd - 8;
		List<byte> list = new List<byte>(chunkEnd - chunkStart);
		List<byte> list2 = new List<byte>();
		List<byte> list3 = new List<byte>();
		List<byte> list4 = new List<byte>();
		List<uint> list5 = new List<uint>();
		MsbBitWriter msbBitWriter = new MsbBitWriter();
		MsbBitWriter msbBitWriter2 = new MsbBitWriter();
		int num3 = 0;
		int num4 = num;
		int num5 = num;
		while (num5 < num2)
		{
			EnsureInserted(data, head, prev, ref nextInsert, num5);
			FindMatch(data, head, prev, num5, matchEndLimit, out var bestLength, out var bestDistance);
			if (bestLength < 4)
			{
				num5++;
				continue;
			}
			if (num5 + 1 < num2)
			{
				EnsureInserted(data, head, prev, ref nextInsert, num5 + 1);
				FindMatch(data, head, prev, num5 + 1, matchEndLimit, out var bestLength2, out var _);
				if (bestLength2 > bestLength + 1)
				{
					num5++;
					continue;
				}
			}
			int num6 = num5 - num4;
			for (int i = num4; i < num5; i++)
			{
				list.Add(data[i]);
			}
			byte b = (byte)((num6 <= 2) ? ((byte)num6) : 3);
			byte b2 = (byte)((bestLength <= 16) ? ((byte)(bestLength - 2)) : 15);
			list2.Add((byte)(0xC0 | (b2 << 2) | b));
			EncodeOffset(bestDistance, out var packedOffs, out var extraBits, out var extraValue);
			list3.Add(packedOffs);
			if (extraBits > 0)
			{
				if ((num3 & 1) == 0)
				{
					msbBitWriter.WriteBits(extraValue, extraBits);
				}
				else
				{
					msbBitWriter2.WriteBits(extraValue, extraBits);
				}
			}
			num3++;
			if (num6 >= 3)
			{
				AppendLiteralLength(list4, list5, num6);
			}
			if (bestLength > 16)
			{
				AppendMatchLength(list4, list5, bestLength);
			}
			if (list5.Count > 512)
			{
				return null;
			}
			num5 += bestLength;
			num4 = num5;
		}
		EnsureInserted(data, head, prev, ref nextInsert, chunkEnd);
		for (int j = num4; j < chunkEnd; j++)
		{
			list.Add(data[j]);
		}
		if (list2.Count != list3.Count)
		{
			return null;
		}
		byte[] array = EncodeExcess(list5);
		byte[] array2 = EncodeExcessCount(array.Length);
		byte[] array3 = EncodeArray(list);
		byte[] array4 = EncodeArray(list2);
		byte[] array5 = EncodeArray(list3);
		byte[] array6 = EncodeArray(list4);
		byte[] array7 = msbBitWriter.ToArray();
		byte[] array8 = msbBitWriter2.ToArray();
		byte[] array9 = new byte[array7.Length + array8.Length];
		array7.CopyTo(array9, 0);
		for (int k = 0; k < array8.Length; k++)
		{
			array9[array9.Length - 1 - k] = array8[k];
		}
		byte[] array10 = new byte[(withSeed ? 8 : 0) + array2.Length + array3.Length + array4.Length + 1 + array5.Length + array6.Length + array9.Length + array.Length];
		int num7 = 0;
		if (withSeed)
		{
			data.AsSpan(0, 8).CopyTo(array10.AsSpan(num7));
			num7 += 8;
		}
		array2.CopyTo(array10.AsSpan(num7));
		num7 += array2.Length;
		array3.CopyTo(array10.AsSpan(num7));
		num7 += array3.Length;
		array4.CopyTo(array10.AsSpan(num7));
		num7 += array4.Length;
		array10[num7++] = 128;
		array5.CopyTo(array10.AsSpan(num7));
		num7 += array5.Length;
		array6.CopyTo(array10.AsSpan(num7));
		num7 += array6.Length;
		array9.CopyTo(array10.AsSpan(num7));
		num7 += array9.Length;
		array.CopyTo(array10.AsSpan(num7));
		return array10;
	}

	private static void EnsureInserted(byte[] data, int[] head, int[] prev, ref int nextInsert, int upto)
	{
		int num = data.Length;
		while (nextInsert < upto && nextInsert + 3 < num)
		{
			int num2 = Hash(data, nextInsert);
			prev[nextInsert] = head[num2];
			head[num2] = nextInsert;
			nextInsert++;
		}
		if (nextInsert < upto)
		{
			nextInsert = upto;
		}
	}

	private static int Hash(byte[] data, int p)
	{
		return (data[p] | (data[p + 1] << 8) | (data[p + 2] << 16) | (data[p + 3] << 24)) * -1640531535 >>> 14;
	}

	private static void FindMatch(byte[] data, int[] head, int[] prev, int pos, int matchEndLimit, out int bestLength, out int bestDistance)
	{
		bestLength = 0;
		bestDistance = 0;
		int num = matchEndLimit - pos;
		if (num < 4)
		{
			return;
		}
		int num2 = Hash(data, pos);
		int num3 = head[num2];
		int num4 = 0;
		while (num3 >= 0 && num4 < 128)
		{
			int num5 = pos - num3;
			if (num5 >= 8 && data[num3] == data[pos] && data[num3 + 1] == data[pos + 1] && data[num3 + 2] == data[pos + 2] && data[num3 + 3] == data[pos + 3])
			{
				int i;
				for (i = 4; i < num && data[num3 + i] == data[pos + i]; i++)
				{
				}
				if (i > bestLength)
				{
					bestLength = i;
					bestDistance = num5;
					if (i >= num)
					{
						break;
					}
				}
			}
			num3 = prev[num3];
			num4++;
		}
	}

	private static void EncodeOffset(int distance, out byte packedOffs, out int extraBits, out uint extraValue)
	{
		int num = distance + 8;
		int i;
		for (i = 0; num >> i >= 16; i++)
		{
		}
		int num2 = (num >> i) - 8;
		extraValue = (uint)(num - (8 + num2 << i));
		extraBits = i;
		packedOffs = (byte)((i << 3) | num2);
	}

	private static void AppendLiteralLength(List<byte> lengths, List<uint> escapes, int litLength)
	{
		int num = litLength - 3;
		if (num < 255)
		{
			lengths.Add((byte)num);
			return;
		}
		lengths.Add(byte.MaxValue);
		escapes.Add((uint)(litLength - 258));
	}

	private static void AppendMatchLength(List<byte> lengths, List<uint> escapes, int matchLength)
	{
		int num = matchLength - 17;
		if (num < 255)
		{
			lengths.Add((byte)num);
			return;
		}
		lengths.Add(byte.MaxValue);
		escapes.Add((uint)(matchLength - 272));
	}

	private static byte[] EncodeArray(IReadOnlyList<byte> data)
	{
		byte[] array = RawArray(data);
		if (data.Count < 2)
		{
			return array;
		}
		byte[] array2 = new byte[data.Count];
		for (int i = 0; i < array2.Length; i++)
		{
			array2[i] = data[i];
		}
		byte[] array3 = KrakenHuffmanArrayEncoder.TryEncode(array2);
		if (array3 == null || array3.Length >= array.Length)
		{
			return array;
		}
		return array3;
	}

	private static byte[] RawArray(IReadOnlyList<byte> data)
	{
		byte[] array = new byte[3 + data.Count];
		array[0] = (byte)(data.Count >> 16);
		array[1] = (byte)(data.Count >> 8);
		array[2] = (byte)data.Count;
		for (int i = 0; i < data.Count; i++)
		{
			array[3 + i] = data[i];
		}
		return array;
	}

	private static byte[] EncodeExcessCount(int count)
	{
		if (count > 31)
		{
			int num = (count - 32) / 32;
			int num2 = count - num * 32;
			return new byte[2]
			{
				(byte)(0x80 | num2),
				(byte)num
			};
		}
		return new byte[1] { (byte)(0x80 | count) };
	}

	private static byte[] EncodeExcess(IReadOnlyList<uint> values)
	{
		MsbBitWriter msbBitWriter = new MsbBitWriter();
		MsbBitWriter msbBitWriter2 = new MsbBitWriter();
		for (int i = 0; i < values.Count; i++)
		{
			if ((i & 1) == 0)
			{
				WriteLength(msbBitWriter, values[i]);
			}
			else
			{
				WriteLength(msbBitWriter2, values[i]);
			}
		}
		byte[] array = msbBitWriter.ToArray();
		byte[] array2 = msbBitWriter2.ToArray();
		byte[] array3 = new byte[array.Length + array2.Length];
		array.CopyTo(array3, 0);
		for (int j = 0; j < array2.Length; j++)
		{
			array3[array3.Length - 1 - j] = array2[j];
		}
		return array3;
	}

	private static void WriteLength(MsbBitWriter writer, uint value)
	{
		uint num = value + 64;
		int num2 = BitOperations.Log2(num) - 6;
		if (num2 < 0 || num2 > 12)
		{
			throw new InvalidOperationException("Kraken length value is outside the encodable range.");
		}
		for (int i = 0; i < num2; i++)
		{
			writer.WriteBit(0);
		}
		writer.WriteBit(1);
		int num3 = num2 + 6;
		writer.WriteBits(num - (uint)(1 << num3), num3);
	}
}
