using System;
using System.Buffers.Binary;

namespace ProsperoPkgTool.Content;

public static class ProsperoDdsEncoder
{
	public const int HeaderSize = 148;

	private const uint DxgiFormatBc7Unorm = 98u;

	private const uint DdsMagic = 542327876u;

	private static readonly int[] Weights4 = new int[16]
	{
		0, 4, 9, 13, 17, 21, 26, 30, 34, 38,
		43, 47, 51, 55, 60, 64
	};

	public static byte[] EncodePngToDds(byte[] pngBytes)
	{
		if (pngBytes == null || pngBytes.Length == 0)
		{
			throw new ArgumentException("Empty image.", "pngBytes");
		}
		int width;
		int height;
		return EncodeRgbaToDds(ProsperoPngReader.DecodeToRgba(pngBytes, out width, out height), width, height);
	}

	public static byte[] EncodeRgbaToDds(byte[] rgba, int width, int height)
	{
		ArgumentNullException.ThrowIfNull(rgba, "rgba");
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException("width", "Invalid image dimensions.");
		}
		if (rgba.Length < checked(width * height * 4))
		{
			throw new ArgumentException("RGBA buffer is smaller than width*height*4.", "rgba");
		}
		int num = (width + 3) / 4;
		int num2 = (height + 3) / 4;
		int num3 = checked(num * num2 * 16);
		byte[] array = new byte[148 + num3];
		WriteHeader(array, width, height, (uint)num3);
		Span<byte> span = stackalloc byte[64];
		for (int i = 0; i < num2; i++)
		{
			for (int j = 0; j < num; j++)
			{
				GatherBlock(rgba, width, height, j * 4, i * 4, span);
				EncodeBlockMode6(span, array.AsSpan(148 + (i * num + j) * 16, 16));
			}
		}
		return array;
	}

	private static void WriteHeader(byte[] dst, int width, int height, uint linearSize)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(dst, 542327876u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(4), 124u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(8), 659463u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(12), (uint)height);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(16), (uint)width);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(20), linearSize);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(24), 0u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(28), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(76), 32u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(80), 4u);
		"DX10"u8.CopyTo(dst.AsSpan(84));
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(108), 4096u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(128), 98u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(132), 3u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(136), 0u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(140), 1u);
		BinaryPrimitives.WriteUInt32LittleEndian(dst.AsSpan(144), 0u);
	}

	private static void GatherBlock(byte[] rgba, int width, int height, int x0, int y0, Span<byte> block)
	{
		for (int i = 0; i < 4; i++)
		{
			int num = Math.Min(y0 + i, height - 1);
			for (int j = 0; j < 4; j++)
			{
				int num2 = Math.Min(x0 + j, width - 1);
				int num3 = (num * width + num2) * 4;
				int num4 = (i * 4 + j) * 4;
				block[num4] = rgba[num3];
				block[num4 + 1] = rgba[num3 + 1];
				block[num4 + 2] = rgba[num3 + 2];
				block[num4 + 3] = rgba[num3 + 3];
			}
		}
	}

	private static void EncodeBlockMode6(ReadOnlySpan<byte> block, Span<byte> dst)
	{
		Span<byte> span = stackalloc byte[4];
		span.Fill(byte.MaxValue);
		Span<byte> span2 = stackalloc byte[4];
		for (int i = 0; i < 16; i++)
		{
			for (int j = 0; j < 4; j++)
			{
				byte b = block[i * 4 + j];
				if (b < span[j])
				{
					span[j] = b;
				}
				if (b > span2[j])
				{
					span2[j] = b;
				}
			}
		}
		QuantizeEndpoint(span, out byte[] q, out int p, out int[] recon);
		QuantizeEndpoint(span2, out byte[] q2, out int p2, out int[] recon2);
		Span<byte> span3 = stackalloc byte[16];
		for (int k = 0; k < 16; k++)
		{
			span3[k] = BestIndex(recon, recon2, block, k * 4);
		}
		if (span3[0] >= 8)
		{
			byte[] array = q2;
			q2 = q;
			q = array;
			int num = p2;
			p2 = p;
			p = num;
			int[] array2 = recon2;
			recon2 = recon;
			recon = array2;
			for (int l = 0; l < 16; l++)
			{
				span3[l] = (byte)(15 - span3[l]);
			}
		}
		ulong lo = 0uL;
		ulong hi = 0uL;
		int pos = 0;
		Put(ref lo, ref hi, ref pos, 64u, 7);
		Put(ref lo, ref hi, ref pos, q[0], 7);
		Put(ref lo, ref hi, ref pos, q2[0], 7);
		Put(ref lo, ref hi, ref pos, q[1], 7);
		Put(ref lo, ref hi, ref pos, q2[1], 7);
		Put(ref lo, ref hi, ref pos, q[2], 7);
		Put(ref lo, ref hi, ref pos, q2[2], 7);
		Put(ref lo, ref hi, ref pos, q[3], 7);
		Put(ref lo, ref hi, ref pos, q2[3], 7);
		Put(ref lo, ref hi, ref pos, (uint)p, 1);
		Put(ref lo, ref hi, ref pos, (uint)p2, 1);
		Put(ref lo, ref hi, ref pos, span3[0], 3);
		for (int m = 1; m < 16; m++)
		{
			Put(ref lo, ref hi, ref pos, span3[m], 4);
		}
		for (int n = 0; n < 8; n++)
		{
			dst[n] = (byte)(lo >> n * 8);
		}
		for (int num2 = 0; num2 < 8; num2++)
		{
			dst[8 + num2] = (byte)(hi >> num2 * 8);
		}
	}

	private static void QuantizeEndpoint(ReadOnlySpan<byte> v, out byte[] q, out int p, out int[] recon)
	{
		q = new byte[4];
		recon = new int[4];
		long num = long.MaxValue;
		p = 0;
		Span<byte> span = stackalloc byte[4];
		Span<int> span2 = stackalloc int[4];
		for (int i = 0; i <= 1; i++)
		{
			long num2 = 0L;
			for (int j = 0; j < 4; j++)
			{
				int num3 = Math.Clamp(v[j] - i + 1 >> 1, 0, 127);
				int num4 = (num3 << 1) | i;
				int num5 = num4 - v[j];
				num2 += (long)num5 * (long)num5;
				span[j] = (byte)num3;
				span2[j] = num4;
			}
			if (num2 < num)
			{
				num = num2;
				p = i;
				span.CopyTo(q);
				span2.CopyTo(recon);
			}
		}
	}

	private static byte BestIndex(int[] e0, int[] e1, ReadOnlySpan<byte> block, int offset)
	{
		int num = 0;
		long num2 = long.MaxValue;
		for (int i = 0; i < 16; i++)
		{
			int num3 = Weights4[i];
			long num4 = 0L;
			for (int j = 0; j < 4; j++)
			{
				int num5 = (e0[j] * (64 - num3) + e1[j] * num3 + 32 >> 6) - block[offset + j];
				num4 += (long)num5 * (long)num5;
			}
			if (num4 < num2)
			{
				num2 = num4;
				num = i;
			}
		}
		return (byte)num;
	}

	private static void Put(ref ulong lo, ref ulong hi, ref int pos, uint value, int count)
	{
		for (int i = 0; i < count; i++)
		{
			if (((value >> i) & 1) != 0)
			{
				int num = pos + i;
				if (num < 64)
				{
					lo |= (ulong)(1L << num);
				}
				else
				{
					hi |= (ulong)(1L << num - 64);
				}
			}
		}
		pos += count;
	}
}
