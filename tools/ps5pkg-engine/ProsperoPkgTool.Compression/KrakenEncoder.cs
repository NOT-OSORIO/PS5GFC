using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Compression;

public static class KrakenEncoder
{
	public sealed class EncodedBlock
	{
		public required byte[] Payload { get; init; }

		public required int Flags { get; init; }

		public required int FirstChunkCompressedLength { get; init; }

		public required int UncompressedLength { get; init; }

		public required LiteralMode Mode { get; init; }
	}

	public enum LiteralMode
	{
		UniformHuffman,
		FixedWidthHuffman,
		NewLz
	}

	private sealed class MsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private byte _current;

		private int _used;

		public void Write(uint value, int count)
		{
			for (int num = count - 1; num >= 0; num--)
			{
				_current |= (byte)(((value >> num) & 1) << 7 - _used++);
				if (_used == 8)
				{
					_bytes.Add(_current);
					_current = 0;
					_used = 0;
				}
			}
		}

		public byte[] ToPaddedBytes()
		{
			if (_used != 0)
			{
				_bytes.Add(_current);
			}
			return _bytes.ToArray();
		}
	}

	private sealed class LsbBitWriter
	{
		private readonly List<byte> _bytes = new List<byte>();

		private byte _current;

		private int _used;

		public void Write(uint value, int count)
		{
			for (int i = 0; i < count; i++)
			{
				_current |= (byte)(((value >> i) & 1) << _used++);
				if (_used == 8)
				{
					_bytes.Add(_current);
					_current = 0;
					_used = 0;
				}
			}
		}

		public byte[] ToPaddedBytes()
		{
			if (_used != 0)
			{
				_bytes.Add(_current);
			}
			return _bytes.ToArray();
		}
	}

	public const int ChunkSize = 131072;

	public const int MaxBlockSize = 262144;

	public static EncodedBlock? TryEncodeNewLz(ReadOnlySpan<byte> input)
	{
		return KrakenLzEncoder.Encode(input);
	}

	public static EncodedBlock? TryEncode(ReadOnlySpan<byte> input)
	{
		int length = input.Length;
		if ((length <= 0 || length > 262144) ? true : false)
		{
			return null;
		}
		byte[] array = EncodeLiteralArray(input[..Math.Min(input.Length, 131072)], out var mode);
		if (array == null)
		{
			return null;
		}
		if (input.Length <= 131072)
		{
			if (array.Length >= input.Length)
			{
				return null;
			}
			if (array.Length == 8)
			{
				return KrakenLzEncoder.Encode(input);
			}
			return new EncodedBlock
			{
				Payload = array,
				Flags = 0,
				FirstChunkCompressedLength = 0,
				UncompressedLength = input.Length,
				Mode = mode
			};
		}
		byte[] array2 = EncodeLiteralArray(input.Slice(131072), out var mode2);
		if (array2 == null)
		{
			return null;
		}
		byte[] array3 = new byte[array.Length + array2.Length];
		array.CopyTo(array3, 0);
		array2.CopyTo(array3, array.Length);
		if (array3.Length >= input.Length)
		{
			return null;
		}
		if (array.Length == 8 && array3.Length == 16)
		{
			return KrakenLzEncoder.Encode(input);
		}
		return new EncodedBlock
		{
			Payload = array3,
			Flags = 0,
			FirstChunkCompressedLength = array.Length,
			UncompressedLength = input.Length,
			Mode = ((mode != LiteralMode.UniformHuffman || mode2 != LiteralMode.UniformHuffman) ? LiteralMode.FixedWidthHuffman : LiteralMode.UniformHuffman)
		};
	}

	private static byte[]? EncodeLiteralArray(ReadOnlySpan<byte> input, out LiteralMode mode)
	{
		byte[] array = EncodeUniformArray(input);
		if (array != null)
		{
			mode = LiteralMode.UniformHuffman;
			return array;
		}
		mode = LiteralMode.FixedWidthHuffman;
		return EncodeFixedWidthArray(input);
	}

	private static byte[]? EncodeUniformArray(ReadOnlySpan<byte> input)
	{
		if (input.Length == 0 || input.Length > 131072)
		{
			return null;
		}
		byte b = input[0];
		if (input.IndexOfAnyExcept(b) >= 0)
		{
			return null;
		}
		byte[] array = new byte[3]
		{
			0,
			(byte)(0x40 | (b >> 2)),
			(byte)((b & 3) << 6)
		};
		int num = input.Length - 1;
		int num2 = array.Length;
		byte item = (byte)(0x20 | (num >> 14));
		uint num3 = (uint)(num2 | ((num & 0x3FFF) << 18));
		List<byte> list = new List<byte>();
		list.Add(item);
		list.Add((byte)(num3 >> 24));
		list.Add((byte)(num3 >> 16));
		list.Add((byte)(num3 >> 8));
		list.Add((byte)num3);
		list.AddRange(array);
		return list.ToArray();
	}

	private static byte[]? EncodeFixedWidthArray(ReadOnlySpan<byte> input)
	{
		if (input.Length == 0 || input.Length > 131072)
		{
			return null;
		}
		Span<bool> span = stackalloc bool[256];
		int num = 0;
		ReadOnlySpan<byte> readOnlySpan = input;
		for (int i = 0; i < readOnlySpan.Length; i++)
		{
			byte index = readOnlySpan[i];
			if (!span[index])
			{
				span[index] = true;
				num++;
			}
		}
		if (num < 2)
		{
			return null;
		}
		int num2 = 2;
		int num3 = 1;
		while (num2 < num)
		{
			num2 <<= 1;
			num3++;
		}
		if (num2 >= 256)
		{
			return null;
		}
		byte[] array = new byte[num2];
		int num4 = 0;
		for (int j = 0; j < 256; j++)
		{
			if (num4 >= num2)
			{
				break;
			}
			if (span[j])
			{
				array[num4++] = (byte)j;
			}
		}
		for (int k = 0; k < 256; k++)
		{
			if (num4 >= num2)
			{
				break;
			}
			if (!span[k])
			{
				array[num4++] = (byte)k;
			}
		}
		Span<int> span2 = stackalloc int[256];
		span2.Fill(-1);
		for (int l = 0; l < array.Length; l++)
		{
			span2[array[l]] = l;
		}
		MsbBitWriter msbBitWriter = new MsbBitWriter();
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write(0u, 1);
		msbBitWriter.Write((uint)num2, 8);
		msbBitWriter.Write(4u, 3);
		byte[] array2 = array;
		foreach (byte value in array2)
		{
			msbBitWriter.Write(value, 8);
			msbBitWriter.Write((uint)(num3 - 1), 4);
		}
		byte[] array3 = msbBitWriter.ToPaddedBytes();
		LsbBitWriter lsbBitWriter = new LsbBitWriter();
		LsbBitWriter lsbBitWriter2 = new LsbBitWriter();
		LsbBitWriter lsbBitWriter3 = new LsbBitWriter();
		for (int m = 0; m < input.Length; m++)
		{
			int value2 = ReverseLowBits(span2[input[m]], num3);
			switch (m % 3)
			{
			case 0:
				lsbBitWriter.Write((uint)value2, num3);
				break;
			case 1:
				lsbBitWriter2.Write((uint)value2, num3);
				break;
			default:
				lsbBitWriter3.Write((uint)value2, num3);
				break;
			}
		}
		byte[] array4 = lsbBitWriter.ToPaddedBytes();
		byte[] array5 = lsbBitWriter3.ToPaddedBytes();
		byte[] array6 = lsbBitWriter2.ToPaddedBytes();
		Array.Reverse(array6);
		if (array4.Length > 65535)
		{
			return null;
		}
		byte[] array7 = new byte[array3.Length + 2 + array4.Length + array5.Length + array6.Length];
		array3.CopyTo(array7, 0);
		int num5 = array3.Length;
		array7[num5++] = (byte)array4.Length;
		array7[num5++] = (byte)(array4.Length >> 8);
		array4.CopyTo(array7, num5);
		num5 += array4.Length;
		array5.CopyTo(array7, num5);
		num5 += array5.Length;
		array6.CopyTo(array7, num5);
		return FrameEntropyArray(array7, input.Length);
	}

	private static byte[]? FrameEntropyArray(byte[] entropy, int outputLength)
	{
		bool flag = entropy.Length >= outputLength || entropy.Length > 262143;
		if (!flag)
		{
			bool flag2 = ((outputLength <= 0 || outputLength > 131072) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			return null;
		}
		int num = outputLength - 1;
		byte item = (byte)(0x20 | (num >> 14));
		uint num2 = (uint)(entropy.Length | ((num & 0x3FFF) << 18));
		List<byte> list = new List<byte>();
		list.Add(item);
		list.Add((byte)(num2 >> 24));
		list.Add((byte)(num2 >> 16));
		list.Add((byte)(num2 >> 8));
		list.Add((byte)num2);
		list.AddRange(entropy);
		return list.ToArray();
	}

	private static int ReverseLowBits(int value, int count)
	{
		int num = 0;
		for (int i = 0; i < count; i++)
		{
			num = (num << 1) | (value & 1);
			value >>= 1;
		}
		return num;
	}
}
