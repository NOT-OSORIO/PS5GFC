using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ProsperoPkgTool.Content;

public static class ProsperoPngReader
{
	private static ReadOnlySpan<byte> Signature => new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 };

	public static byte[] DecodeToRgba(byte[] png, out int width, out int height)
	{
		ArgumentNullException.ThrowIfNull(png, "png");
		if (png.Length < Signature.Length || !((ReadOnlySpan<byte>)png.AsSpan(0, Signature.Length)).SequenceEqual(Signature))
		{
			throw new InvalidDataException("Not a PNG file (bad signature).");
		}
		width = 0;
		height = 0;
		int num = 0;
		int num2 = -1;
		bool flag = false;
		byte[] array = null;
		byte[] paletteAlpha = null;
		MemoryStream memoryStream = new MemoryStream();
		int num3 = Signature.Length;
		while (num3 + 8 <= png.Length)
		{
			int num4 = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(num3)));
			string text = Encoding.ASCII.GetString(png, num3 + 4, 4);
			int num5 = num3 + 8;
			if (num4 < 0 || num5 + num4 + 4 > png.Length)
			{
				throw new InvalidDataException("PNG chunk '" + text + "' is truncated.");
			}
			ReadOnlySpan<byte> readOnlySpan = png.AsSpan(num5, num4);
			switch (text)
			{
			case "IHDR":
				if (num4 != 13)
				{
					throw new InvalidDataException("PNG IHDR must be 13 bytes.");
				}
				checked
				{
					width = (int)BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan);
					height = (int)BinaryPrimitives.ReadUInt32BigEndian(readOnlySpan.Slice(4));
					num = readOnlySpan[8];
					num2 = readOnlySpan[9];
					if (readOnlySpan[10] != 0)
					{
						throw new InvalidDataException("PNG uses an unknown compression method.");
					}
					if (readOnlySpan[11] != 0)
					{
						throw new InvalidDataException("PNG uses an unknown filter method.");
					}
					flag = readOnlySpan[12] != 0;
					if (width <= 0 || height <= 0)
					{
						throw new InvalidDataException("PNG has invalid dimensions.");
					}
					break;
				}
			case "PLTE":
				if (num4 % 3 != 0)
				{
					throw new InvalidDataException("PNG PLTE length must be a multiple of 3.");
				}
				array = readOnlySpan.ToArray();
				break;
			case "tRNS":
				if (num2 == 3)
				{
					paletteAlpha = readOnlySpan.ToArray();
				}
				break;
			case "IDAT":
				memoryStream.Write(readOnlySpan);
				break;
			case "IEND":
				num3 = png.Length;
				continue;
			}
			num3 = num5 + num4 + 4;
		}
		if (width <= 0 || height <= 0)
		{
			throw new InvalidDataException("PNG is missing its IHDR chunk.");
		}
		if (flag)
		{
			throw new NotSupportedException("Adam7-interlaced PNG images are not supported.");
		}
		if ((num != 8 && num != 16) || 1 == 0)
		{
			throw new NotSupportedException($"PNG bit depth {num} is not supported (only 8 and 16).");
		}
		int num6 = num2 switch
		{
			0 => 1,
			2 => 3,
			3 => 1,
			4 => 2,
			6 => 4,
			_ => throw new NotSupportedException($"PNG colour type {num2} is not supported."),
		};
		if (num2 == 3 && array == null)
		{
			throw new InvalidDataException("Palette PNG is missing its PLTE chunk.");
		}
		int num7 = num6 * num;
		int bpp = Math.Max(1, num7 / 8);
		int num8 = (width * num7 + 7) / 8;
		return ToRgba(Unfilter(Inflate(memoryStream.ToArray(), checked((num8 + 1) * height)), height, num8, bpp), width, height, num, num2, num6, array, paletteAlpha);
	}

	private static byte[] Inflate(byte[] zlib, int expected)
	{
		using MemoryStream stream = new MemoryStream(zlib, writable: false);
		using ZLibStream zLibStream = new ZLibStream(stream, CompressionMode.Decompress);
		using MemoryStream memoryStream = ((expected > 0) ? new MemoryStream(expected) : new MemoryStream());
		zLibStream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

	private static byte[] Unfilter(byte[] raw, int height, int stride, int bpp)
	{
		if (raw.Length < checked(height * (stride + 1)))
		{
			throw new InvalidDataException("PNG pixel data is shorter than the declared dimensions.");
		}
		byte[] array = new byte[height * stride];
		int num = 0;
		for (int i = 0; i < height; i++)
		{
			int num2 = raw[num++];
			int num3 = i * stride;
			int num4 = num3 - stride;
			for (int j = 0; j < stride; j++)
			{
				int num5 = raw[num + j];
				int num6 = ((j >= bpp) ? array[num3 + j - bpp] : 0);
				int num7 = ((i > 0) ? array[num4 + j] : 0);
				int c = ((i > 0 && j >= bpp) ? array[num4 + j - bpp] : 0);
				array[num3 + j] = (byte)(num2 switch
				{
					0 => (uint)num5,
					1 => (uint)(num5 + num6),
					2 => (uint)(num5 + num7),
					3 => (uint)(num5 + (num6 + num7 >> 1)),
					4 => (uint)(num5 + Paeth(num6, num7, c)),
					_ => throw new InvalidDataException($"PNG uses unknown scanline filter {num2}."),
				});
			}
			num += stride;
		}
		return array;
	}

	private static int Paeth(int a, int b, int c)
	{
		int num = a + b - c;
		int num2 = Math.Abs(num - a);
		int num3 = Math.Abs(num - b);
		int num4 = Math.Abs(num - c);
		if (num2 <= num3 && num2 <= num4)
		{
			return a;
		}
		if (num3 > num4)
		{
			return c;
		}
		return b;
	}

	private static byte[] ToRgba(byte[] data, int width, int height, int bitDepth, int colorType, int channels, byte[]? palette, byte[]? paletteAlpha)
	{
		byte[] array = new byte[checked(width * height * 4)];
		int num = bitDepth / 8;
		int num2 = width * channels * num;
		for (int i = 0; i < height; i++)
		{
			int num3 = i * num2;
			for (int j = 0; j < width; j++)
			{
				int num4 = num3 + j * channels * num;
				int num5 = (i * width + j) * 4;
				switch (colorType)
				{
				case 0:
					array[num5 + 2] = (array[num5 + 1] = (array[num5] = data[num4]));
					array[num5 + 3] = byte.MaxValue;
					break;
				case 2:
					array[num5] = data[num4];
					array[num5 + 1] = data[num4 + num];
					array[num5 + 2] = data[num4 + 2 * num];
					array[num5 + 3] = byte.MaxValue;
					break;
				case 3:
				{
					int num6 = data[num4];
					int num7 = num6 * 3;
					if (num7 + 2 >= palette.Length)
					{
						throw new InvalidDataException("PNG palette index is out of range.");
					}
					array[num5] = palette[num7];
					array[num5 + 1] = palette[num7 + 1];
					array[num5 + 2] = palette[num7 + 2];
					array[num5 + 3] = ((paletteAlpha != null && num6 < paletteAlpha.Length) ? paletteAlpha[num6] : byte.MaxValue);
					break;
				}
				case 4:
				{
					byte b = data[num4];
					byte b2 = data[num4 + num];
					array[num5] = b;
					array[num5 + 1] = b;
					array[num5 + 2] = b;
					array[num5 + 3] = b2;
					break;
				}
				case 6:
					array[num5] = data[num4];
					array[num5 + 1] = data[num4 + num];
					array[num5 + 2] = data[num4 + 2 * num];
					array[num5 + 3] = data[num4 + 3 * num];
					break;
				}
			}
		}
		return array;
	}
}
