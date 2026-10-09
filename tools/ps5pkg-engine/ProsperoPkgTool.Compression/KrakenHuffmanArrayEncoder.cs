using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Compression;

internal static class KrakenHuffmanArrayEncoder
{
	private sealed class MsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private uint _accumulator;

		private int _bits;

		public void Write(uint value, int count)
		{
			for (int num = count - 1; num >= 0; num--)
			{
				_accumulator = (_accumulator << 1) | ((value >> num) & 1);
				if (++_bits == 8)
				{
					_bytes.Add((byte)_accumulator);
					_accumulator = 0u;
					_bits = 0;
				}
			}
		}

		public byte[] ToBytesPadded()
		{
			if (_bits > 0)
			{
				_bytes.Add((byte)(_accumulator << 8 - _bits));
				_accumulator = 0u;
				_bits = 0;
			}
			return _bytes.ToArray();
		}
	}

	private sealed class LsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private uint _accumulator;

		private int _bits;

		public void Write(uint value, int count)
		{
			if (count != 0)
			{
				_accumulator |= (value & (uint)((1 << count) - 1)) << _bits;
				_bits += count;
				while (_bits >= 8)
				{
					_bytes.Add((byte)_accumulator);
					_accumulator >>= 8;
					_bits -= 8;
				}
			}
		}

		public byte[] ToBytesPadded()
		{
			if (_bits > 0)
			{
				_bytes.Add((byte)_accumulator);
				_accumulator = 0u;
				_bits = 0;
			}
			return _bytes.ToArray();
		}
	}

	private const int Alphabet = 256;

	private const int MaxBits = 11;

	private const int ChunkType = 2;

	private static readonly uint[] CodePrefixBase = new uint[12]
	{
		0u, 0u, 2u, 6u, 14u, 30u, 62u, 126u, 254u, 510u,
		766u, 1022u
	};

	public static byte[]? TryEncode(ReadOnlySpan<byte> data)
	{
		int length = data.Length;
		if (length < 2 || length > 262143)
		{
			return null;
		}
		Span<int> span = stackalloc int[256];
		for (int i = 0; i < length; i++)
		{
			span[data[i]]++;
		}
		int num = 0;
		for (int j = 0; j < 256; j++)
		{
			if (span[j] != 0)
			{
				num++;
			}
		}
		if (num == 1)
		{
			int num2 = 0;
			for (int k = 0; k < 256; k++)
			{
				if (span[k] != 0)
				{
					num2 = k;
					break;
				}
			}
			int index = ((num2 == 0) ? 1 : 0);
			span[index] = 1;
			num = 2;
		}
		byte[] array = BuildLengthLimitedLengths(span.ToArray());
		if (array == null)
		{
			return null;
		}
		ushort[] array2 = BuildCanonicalCodes(array);
		ushort[] array3 = new ushort[256];
		for (int l = 0; l < 256; l++)
		{
			if (array[l] != 0)
			{
				array3[l] = (ushort)ReverseBits(array2[l], array[l]);
			}
		}
		byte[] array4 = BuildSimpleCodeLengthTable(array, num);
		if (array4 == null)
		{
			return null;
		}
		LsbBitWriter lsbBitWriter = new LsbBitWriter();
		LsbBitWriter lsbBitWriter2 = new LsbBitWriter();
		LsbBitWriter lsbBitWriter3 = new LsbBitWriter();
		for (int m = 0; m < length; m++)
		{
			byte b = data[m];
			uint value = array3[b];
			int count = array[b];
			switch (m % 3)
			{
			case 0:
				lsbBitWriter.Write(value, count);
				break;
			case 1:
				lsbBitWriter2.Write(value, count);
				break;
			default:
				lsbBitWriter3.Write(value, count);
				break;
			}
		}
		byte[] array5 = lsbBitWriter.ToBytesPadded();
		byte[] array6 = lsbBitWriter2.ToBytesPadded();
		byte[] array7 = lsbBitWriter3.ToBytesPadded();
		if (array5.Length > 65535)
		{
			return null;
		}
		int num3 = array4.Length + 2 + array5.Length + array7.Length + array6.Length;
		if (num3 >= length)
		{
			return null;
		}
		byte[] array8 = BuildEntropyArray(num3, length, out var bodyOffset);
		int num4 = bodyOffset;
		array4.CopyTo(array8, num4);
		num4 += array4.Length;
		array8[num4++] = (byte)(array5.Length & 0xFF);
		array8[num4++] = (byte)((array5.Length >> 8) & 0xFF);
		array5.CopyTo(array8, num4);
		num4 += array5.Length;
		array7.CopyTo(array8, num4);
		num4 += array7.Length;
		for (int n = 0; n < array6.Length; n++)
		{
			array8[num4 + n] = array6[array6.Length - 1 - n];
		}
		return array8;
	}

	private static byte[] BuildEntropyArray(int srcSize, int dstSize, out int bodyOffset)
	{
		byte[] array = new byte[5 + srcSize];
		int num = dstSize - 1;
		array[0] = (byte)(0x20 | ((num >> 14) & 0xF));
		uint num2 = (uint)(srcSize | ((num & 0x3FFF) << 18));
		array[1] = (byte)(num2 >> 24);
		array[2] = (byte)(num2 >> 16);
		array[3] = (byte)(num2 >> 8);
		array[4] = (byte)num2;
		bodyOffset = 5;
		return array;
	}

	private static byte[]? BuildSimpleCodeLengthTable(byte[] codeLengths, int distinct)
	{
		if (distinct < 2 || distinct >= 256)
		{
			return null;
		}
		int num = 0;
		for (int i = 0; i < 256; i++)
		{
			if (codeLengths[i] > num)
			{
				num = codeLengths[i];
			}
		}
		int num2 = BitWidth(num - 1);
		if (num2 > 4)
		{
			return null;
		}
		MsbBitWriter msbBitWriter = new MsbBitWriter();
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write((uint)distinct, 8);
		msbBitWriter.Write((uint)num2, 3);
		for (int j = 0; j < 256; j++)
		{
			if (codeLengths[j] != 0)
			{
				msbBitWriter.Write((uint)j, 8);
				if (num2 > 0)
				{
					msbBitWriter.Write((uint)(codeLengths[j] - 1), num2);
				}
			}
		}
		return msbBitWriter.ToBytesPadded();
	}

	private static byte[]? BuildLengthLimitedLengths(int[] freq)
	{
		List<int> list = new List<int>();
		for (int i = 0; i < 256; i++)
		{
			if (freq[i] > 0)
			{
				list.Add(i);
			}
		}
		int count = list.Count;
		switch (count)
		{
		case 0:
			return null;
		case 1:
		{
			byte[] array3 = new byte[256];
			array3[list[0]] = 1;
			return array3;
		}
		default:
		{
			list.Sort((int a, int b) => (freq[a] == freq[b]) ? a.CompareTo(b) : freq[a].CompareTo(freq[b]));
			List<long> list2 = new List<long>();
			List<int> list3 = new List<int>();
			List<int> list4 = new List<int>();
			List<int> list5 = new List<int>();
			List<int> list6 = new List<int>();
			for (int num = 0; num < count; num++)
			{
				list2.Add(freq[list[num]]);
				list3.Add(-1);
				list4.Add(-1);
				list5.Add(list[num]);
				list6.Add(list2.Count - 1);
			}
			List<int> list7 = new List<int>(list6);
			for (int num2 = 1; num2 < 11; num2++)
			{
				List<int> list8 = new List<int>();
				for (int num3 = 0; num3 + 1 < list7.Count; num3 += 2)
				{
					int num4 = list7[num3];
					int num5 = list7[num3 + 1];
					list2.Add(list2[num4] + list2[num5]);
					list3.Add(num4);
					list4.Add(num5);
					list5.Add(-1);
					list8.Add(list2.Count - 1);
				}
				list7 = MergeByWeight(list6, list8, list2);
			}
			int num6 = 2 * count - 2;
			int[] array = new int[256];
			for (int num7 = 0; num7 < num6 && num7 < list7.Count; num7++)
			{
				AddLeafCounts(list7[num7], list3, list4, list5, array);
			}
			byte[] array2 = new byte[256];
			long num8 = 0L;
			for (int num9 = 0; num9 < 256; num9++)
			{
				int num10 = array[num9];
				if (freq[num9] > 0 && (num10 < 1 || num10 > 11))
				{
					return null;
				}
				array2[num9] = (byte)num10;
				if (freq[num9] > 0)
				{
					num8 += 1L << 11 - num10;
				}
			}
			if (num8 != 2048)
			{
				return null;
			}
			return array2;
		}
		}
	}

	private static List<int> MergeByWeight(List<int> a, List<int> b, List<long> weight)
	{
		List<int> list = new List<int>(a.Count + b.Count);
		int num = 0;
		int num2 = 0;
		while (num < a.Count && num2 < b.Count)
		{
			list.Add((weight[a[num]] <= weight[b[num2]]) ? a[num++] : b[num2++]);
		}
		while (num < a.Count)
		{
			list.Add(a[num++]);
		}
		while (num2 < b.Count)
		{
			list.Add(b[num2++]);
		}
		return list;
	}

	private static void AddLeafCounts(int item, List<int> left, List<int> right, List<int> leafOf, int[] counts)
	{
		Stack<int> stack = new Stack<int>();
		stack.Push(item);
		while (stack.Count > 0)
		{
			int index = stack.Pop();
			if (leafOf[index] >= 0)
			{
				counts[leafOf[index]]++;
				continue;
			}
			stack.Push(left[index]);
			stack.Push(right[index]);
		}
	}

	private static ushort[] BuildCanonicalCodes(byte[] codeLengths)
	{
		ushort[] array = new ushort[256];
		uint num = 0u;
		for (int i = 1; i <= 11; i++)
		{
			int num2 = 1 << 11 - i;
			for (int j = 0; j < 256; j++)
			{
				if (codeLengths[j] == i)
				{
					array[j] = (ushort)(num >> 11 - i);
					num += (uint)num2;
				}
			}
		}
		return array;
	}

	private static uint ReverseBits(uint value, int bitCount)
	{
		uint num = 0u;
		for (int i = 0; i < bitCount; i++)
		{
			num = (num << 1) | (value & 1);
			value >>= 1;
		}
		return num;
	}

	private static int BitWidth(int value)
	{
		int num = 0;
		while (value > 0)
		{
			num++;
			value >>= 1;
		}
		return num;
	}
}
