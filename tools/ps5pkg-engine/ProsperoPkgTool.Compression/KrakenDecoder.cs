using System;
using System.Buffers.Binary;
using System.Numerics;

namespace ProsperoPkgTool.Compression;

public static class KrakenDecoder
{
	public enum KrakenDecodeStatus
	{
		Success,
		Malformed,
		UnsupportedEntropy,
		UnsupportedExcessMode
	}

	private sealed class LzStreams
	{
		public byte[] Literals = Array.Empty<byte>();

		public int LiteralCount;

		public byte[] Commands = Array.Empty<byte>();

		public int CommandCount;

		public int[] Distances = Array.Empty<int>();

		public int DistanceCount;

		public int[] Lengths = Array.Empty<int>();

		public int LengthCount;
	}

	private sealed class HuffLookup
	{
		public readonly byte[] BitsToLen = new byte[2064];

		public readonly byte[] BitsToSym = new byte[2064];
	}

	private struct HuffRange
	{
		public int Symbol;

		public int Num;
	}

	private struct HuffStreamReader(byte[] data, byte[] output)
	{
		public byte[] Data = data;

		public byte[] Output = output;

		public int Src = 0;

		public int SrcEnd = 0;

		public int SrcMid = 0;

		public int SrcMidOriginal = 0;

		public uint SrcBits = 0u;

		public uint SrcMidBits = 0u;

		public uint SrcEndBits = 0u;

		public int SrcBitpos = 0;

		public int SrcMidBitpos = 0;

		public int SrcEndBitpos = 0;

		public int OutOff = 0;

		public int OutEnd = 0;
	}

	private struct GolombReader
	{
		public byte[] Data;

		public int P;

		public int End;

		public int BitPos;
	}

	private struct BitReader
	{
		public byte[] Data;

		public int P;

		public int Bound;

		public uint Bits;

		public int BitPos;

		private bool _backward;

		public static BitReader Forward(byte[] data, int start, int end)
		{
			BitReader result = new BitReader
			{
				Data = data,
				P = start,
				Bound = end,
				Bits = 0u,
				BitPos = 24,
				_backward = false
			};
			result.RefillForward();
			return result;
		}

		public static BitReader Backward(byte[] data, int low, int high)
		{
			BitReader result = new BitReader
			{
				Data = data,
				P = high,
				Bound = low,
				Bits = 0u,
				BitPos = 24,
				_backward = true
			};
			result.RefillBackward();
			return result;
		}

		public void Refill()
		{
			if (_backward)
			{
				RefillBackward();
			}
			else
			{
				RefillForward();
			}
		}

		public void RefillForward()
		{
			while (BitPos > 0)
			{
				Bits |= (uint)(((P < Bound) ? Data[P] : 0) << BitPos);
				BitPos -= 8;
				P++;
			}
		}

		public void RefillBackward()
		{
			while (BitPos > 0)
			{
				P--;
				Bits |= (uint)(((P >= Bound) ? Data[P] : 0) << BitPos);
				BitPos -= 8;
			}
		}

		public int ReadBit()
		{
			Refill();
			uint result = Bits >> 31;
			Bits <<= 1;
			BitPos++;
			return (int)result;
		}

		public int ReadBitNoRefill()
		{
			uint result = Bits >> 31;
			Bits <<= 1;
			BitPos++;
			return (int)result;
		}

		public uint ReadBitsNoRefill(int n)
		{
			uint result = Bits >> 32 - n;
			Bits <<= n;
			BitPos += n;
			return result;
		}

		public uint ReadBitsNoRefillZero(int n)
		{
			uint result = Bits >> 1 >> 31 - n;
			Bits <<= n;
			BitPos += n;
			return result;
		}

		public int ReadFluff(int numSymbols)
		{
			if (numSymbols == 256)
			{
				return 0;
			}
			int num = 257 - numSymbols;
			if (num > numSymbols)
			{
				num = numSymbols;
			}
			num *= 2;
			int num2 = MostSignificantBit((uint)(num - 1)) + 1;
			uint num3 = Bits >> 32 - num2;
			uint num4 = (uint)((1 << num2) - num);
			if (num3 >> 1 >= num4)
			{
				Bits <<= num2;
				BitPos += num2;
				return (int)(num3 - num4);
			}
			Bits <<= num2 - 1;
			BitPos += num2 - 1;
			return (int)(num3 >> 1);
		}

		public uint ReadExtra(int n)
		{
			uint result;
			if (n <= 24)
			{
				result = ReadBitsNoRefillZero(n);
			}
			else
			{
				result = ReadBitsNoRefill(24) << n - 24;
				RefillForward();
				result += ReadBitsNoRefill(n - 24);
			}
			RefillForward();
			return result;
		}

		public uint ReadExtraBackward(int n)
		{
			uint result;
			if (n <= 24)
			{
				result = ReadBitsNoRefillZero(n);
			}
			else
			{
				result = ReadBitsNoRefill(24) << n - 24;
				RefillBackward();
				result += ReadBitsNoRefill(n - 24);
			}
			RefillBackward();
			return result;
		}

		public uint ReadDistance(uint v)
		{
			uint result;
			if (v < 240)
			{
				int num = (int)((v >> 4) + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = ((num2 & num3) << 4) + (v & 0xF) - 248;
			}
			else
			{
				int num = (int)(v - 240 + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = 8322816 + ((num2 & num3) << 12);
				RefillForward();
				result += Bits >> 20;
				BitPos += 12;
				Bits <<= 12;
			}
			RefillForward();
			return result;
		}

		public uint ReadDistanceBackward(uint v)
		{
			uint result;
			if (v < 240)
			{
				int num = (int)((v >> 4) + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = ((num2 & num3) << 4) + (v & 0xF) - 248;
			}
			else
			{
				int num = (int)(v - 240 + 4);
				uint num2 = BitOperations.RotateLeft(Bits | 1, num);
				BitPos += num;
				uint num3 = (uint)((2 << num) - 1);
				Bits = num2 & ~num3;
				result = 8322816 + ((num2 & num3) << 12);
				RefillBackward();
				result += Bits >> 20;
				BitPos += 12;
				Bits <<= 12;
			}
			RefillBackward();
			return result;
		}

		public bool ReadLength(out uint v)
		{
			v = 0u;
			int num = 31 - MostSignificantBit(Bits);
			if (num > 12)
			{
				return false;
			}
			BitPos += num;
			Bits <<= num;
			RefillForward();
			num += 7;
			BitPos += num;
			v = (Bits >> 32 - num) - 64;
			Bits <<= num;
			RefillForward();
			return true;
		}

		public bool ReadLengthBackward(out uint v)
		{
			v = 0u;
			int num = 31 - MostSignificantBit(Bits);
			if (num > 12)
			{
				return false;
			}
			BitPos += num;
			Bits <<= num;
			RefillBackward();
			num += 7;
			BitPos += num;
			v = (Bits >> 32 - num) - 64;
			Bits <<= num;
			RefillBackward();
			return true;
		}
	}

	public const int ChunkSize = 131072;

	public const int MaxBlockSize = 262144;

	private const int SeedSize = 8;

	private const int Chunk0SubLiteral = 1;

	private const int Chunk0IsNewLz = 2;

	private const int Chunk1SubLiteral = 16;

	private const int Chunk1IsNewLz = 32;

	private const int Chunk1RestartBit = 64;

	private static readonly uint[] CodePrefixBase = new uint[12]
	{
		0u, 0u, 2u, 6u, 14u, 30u, 62u, 126u, 254u, 510u,
		766u, 1022u
	};

	private static readonly uint[] RiceValue = BuildRiceValue();

	private static readonly byte[] RiceLength = BuildRiceLength();

	public static KrakenDecodeStatus DecodeBlock(ReadOnlySpan<byte> payload, int flags, int firstChunkComp, Span<byte> dst)
	{
		if (dst.Length <= 0)
		{
			if (payload.Length != 0)
			{
				return KrakenDecodeStatus.Malformed;
			}
			return KrakenDecodeStatus.Success;
		}
		byte[] array = payload.ToArray();
		byte[] array2 = new byte[dst.Length];
		bool newLz = (flags & 2) != 0;
		int litMode = (((flags & 1) == 0) ? 1 : 0);
		KrakenDecodeStatus krakenDecodeStatus;
		if (dst.Length <= 131072)
		{
			krakenDecodeStatus = DecodeSubChunk(array, 0, array.Length, array2, 0, dst.Length, newLz, restart: true, litMode);
		}
		else
		{
			if (firstChunkComp <= 0 || firstChunkComp > array.Length)
			{
				return KrakenDecodeStatus.Malformed;
			}
			bool newLz2 = (flags & 0x20) != 0;
			int litMode2 = (((flags & 0x10) == 0) ? 1 : 0);
			int srcLen = array.Length - firstChunkComp;
			int dstLen = dst.Length - 131072;
			krakenDecodeStatus = DecodeSubChunk(array, 0, firstChunkComp, array2, 0, 131072, newLz, restart: true, litMode);
			if (krakenDecodeStatus != KrakenDecodeStatus.Success)
			{
				return krakenDecodeStatus;
			}
			krakenDecodeStatus = DecodeSubChunk(array, firstChunkComp, srcLen, array2, 131072, dstLen, newLz2, (flags & 0x40) != 0, litMode2);
		}
		if (krakenDecodeStatus == KrakenDecodeStatus.Success)
		{
			array2.AsSpan(0, dst.Length).CopyTo(dst);
		}
		return krakenDecodeStatus;
	}

	private static KrakenDecodeStatus DecodeSubChunk(byte[] src, int srcStart, int srcLen, byte[] outBuf, int dstStart, int dstLen, bool newLz, bool restart, int litMode)
	{
		if (srcLen < 0 || srcStart + srcLen > src.Length || dstLen < 0 || dstStart + dstLen > outBuf.Length)
		{
			return KrakenDecodeStatus.Malformed;
		}
		if (srcLen == dstLen)
		{
			Array.Copy(src, srcStart, outBuf, dstStart, dstLen);
			return KrakenDecodeStatus.Success;
		}
		if (!newLz)
		{
			return DecodeBareEntropy(src, srcStart, srcLen, outBuf, dstStart, dstLen);
		}
		return DecodeChunk(src, srcStart, srcLen, outBuf, dstStart, dstLen, restart, litMode);
	}

	private static KrakenDecodeStatus DecodeBareEntropy(byte[] src, int srcStart, int srcLen, byte[] outBuf, int dstStart, int dstLen)
	{
		if (srcLen < 0 || srcStart + srcLen > src.Length || dstLen < 0 || dstStart + dstLen > outBuf.Length)
		{
			return KrakenDecodeStatus.Malformed;
		}
		int num = DecodeBytes(src, srcStart, srcStart + srcLen, dstLen, out byte[] output, out int decodedSize);
		if (num < 0)
		{
			if (num != -2)
			{
				return KrakenDecodeStatus.Malformed;
			}
			return KrakenDecodeStatus.UnsupportedEntropy;
		}
		if (num != srcLen || decodedSize != dstLen)
		{
			return KrakenDecodeStatus.Malformed;
		}
		Array.Copy(output, 0, outBuf, dstStart, dstLen);
		return KrakenDecodeStatus.Success;
	}

	private static KrakenDecodeStatus DecodeChunk(byte[] src, int srcStart, int srcLen, byte[] dst, int dstStart, int dstSize, bool withSeed, int literalMode)
	{
		if (dstSize <= 0 || dstStart + dstSize > dst.Length || srcLen < 0 || srcStart + srcLen > src.Length)
		{
			return KrakenDecodeStatus.Malformed;
		}
		int srcEnd = srcStart + srcLen;
		int sp = srcStart;
		int offset = ((!withSeed) ? dstStart : 0);
		LzStreams lzt = new LzStreams();
		int num = ReadLzTable(src, ref sp, srcEnd, dst, dstStart, dstSize, offset, lzt);
		if (num < 0)
		{
			if (num != -2)
			{
				return KrakenDecodeStatus.Malformed;
			}
			return KrakenDecodeStatus.UnsupportedEntropy;
		}
		if (!ProcessLzRuns(literalMode, dst, dstStart, dstSize, offset, lzt))
		{
			return KrakenDecodeStatus.Malformed;
		}
		return KrakenDecodeStatus.Success;
	}

	private static int ReadLzTable(byte[] src, ref int sp, int srcEnd, byte[] dst, int dstStart, int dstSize, int offset, LzStreams lzt)
	{
		if (offset == 0)
		{
			if (srcEnd - sp < 8)
			{
				return -1;
			}
			Array.Copy(src, sp, dst, dstStart, 8);
			sp += 8;
		}
		bool excessFlag = false;
		int num = 0;
		if (sp < srcEnd && (src[sp] & 0x80) != 0)
		{
			byte b = src[sp++];
			if ((b & 0xC0) != 128)
			{
				return -1;
			}
			excessFlag = true;
			num = b & 0x3F;
			if (num > 31)
			{
				if (sp >= srcEnd)
				{
					return -1;
				}
				num += src[sp++] * 32;
			}
			srcEnd -= num;
			if (srcEnd < sp)
			{
				return -1;
			}
		}
		int num2 = DecodeBytes(src, sp, srcEnd, dstSize, out byte[] output, out int decodedSize);
		if (num2 < 0)
		{
			return num2;
		}
		sp += num2;
		lzt.Literals = output;
		lzt.LiteralCount = decodedSize;
		num2 = DecodeBytes(src, sp, srcEnd, dstSize, out byte[] output2, out int decodedSize2);
		if (num2 < 0)
		{
			return num2;
		}
		sp += num2;
		lzt.Commands = output2;
		lzt.CommandCount = decodedSize2;
		if (srcEnd - sp < 3)
		{
			return -1;
		}
		int num3 = 0;
		byte[] output3 = Array.Empty<byte>();
		byte[] output4;
		int decodedSize3;
		if ((src[sp] & 0x80) != 0)
		{
			num3 = src[sp] - 127;
			sp++;
			num2 = DecodeBytes(src, sp, srcEnd, decodedSize2, out output4, out decodedSize3);
			if (num2 < 0)
			{
				return num2;
			}
			sp += num2;
			if (num3 != 1)
			{
				num2 = DecodeBytes(src, sp, srcEnd, decodedSize3, out output3, out int decodedSize4);
				if (num2 < 0)
				{
					return num2;
				}
				if (decodedSize4 != decodedSize3)
				{
					return -1;
				}
				sp += num2;
			}
		}
		else
		{
			num2 = DecodeBytes(src, sp, srcEnd, decodedSize2, out output4, out decodedSize3);
			if (num2 < 0)
			{
				return num2;
			}
			sp += num2;
		}
		lzt.DistanceCount = decodedSize3;
		num2 = DecodeBytes(src, sp, srcEnd, dstSize >> 2, out byte[] output5, out int decodedSize5);
		if (num2 < 0)
		{
			return num2;
		}
		sp += num2;
		lzt.LengthCount = decodedSize5;
		lzt.Distances = new int[decodedSize3];
		lzt.Lengths = new int[decodedSize5];
		if (!UnpackOffsets(src, sp, srcEnd, excessFlag, num, output4, decodedSize3, num3, output3, output5, decodedSize5, lzt.Distances, lzt.Lengths))
		{
			return -1;
		}
		return sp;
	}

	private static bool UnpackOffsets(byte[] src, int bsBegin, int bsEnd, bool excessFlag, int excessCount, byte[] packedOffs, int offsCount, int offsScaling, byte[] packedOffsExtra, byte[] packedLitLen, int lenCount, int[] offsStream, int[] lenStream)
	{
		BitReader bitReader = BitReader.Forward(src, bsBegin, bsEnd);
		BitReader bitReader2 = BitReader.Backward(src, bsBegin, bsEnd);
		int num = 0;
		if (!excessFlag)
		{
			if (bitReader2.Bits < 8192)
			{
				return false;
			}
			int num2 = 31 - MostSignificantBit(bitReader2.Bits);
			bitReader2.BitPos += num2;
			bitReader2.Bits <<= num2;
			bitReader2.RefillBackward();
			num2++;
			num = (int)((bitReader2.Bits >> 32 - num2) - 1);
			bitReader2.BitPos += num2;
			bitReader2.Bits <<= num2;
			bitReader2.RefillBackward();
		}
		else
		{
			for (int i = 0; i < lenCount; i++)
			{
				if (packedLitLen[i] == byte.MaxValue)
				{
					num++;
				}
			}
		}
		if (offsScaling == 0)
		{
			int num3;
			for (num3 = 0; num3 < offsCount; num3++)
			{
				offsStream[num3] = (int)(0 - bitReader.ReadDistance(packedOffs[num3]));
				num3++;
				if (num3 >= offsCount)
				{
					break;
				}
				offsStream[num3] = (int)(0 - bitReader2.ReadDistanceBackward(packedOffs[num3]));
			}
		}
		else
		{
			int num4;
			for (num4 = 0; num4 < offsCount; num4++)
			{
				uint num5 = packedOffs[num4];
				if (num5 >> 3 > 26)
				{
					return false;
				}
				uint num6 = (8 + (num5 & 7) << (int)(num5 >> 3)) | bitReader.ReadExtra((int)(num5 >> 3));
				offsStream[num4] = (int)(8 - num6);
				num4++;
				if (num4 >= offsCount)
				{
					break;
				}
				num5 = packedOffs[num4];
				if (num5 >> 3 > 26)
				{
					return false;
				}
				num6 = (8 + (num5 & 7) << (int)(num5 >> 3)) | bitReader2.ReadExtraBackward((int)(num5 >> 3));
				offsStream[num4] = (int)(8 - num6);
			}
			if (offsScaling != 1)
			{
				CombineScaledDistances(offsStream, offsCount, offsScaling, packedOffsExtra);
			}
		}
		if (num > 512)
		{
			return false;
		}
		uint[] array = new uint[num];
		if (excessFlag)
		{
			BitReader bitReader3 = BitReader.Forward(src, bsEnd, bsEnd + excessCount);
			BitReader bitReader4 = BitReader.Backward(src, bsEnd, bsEnd + excessCount);
			int j;
			for (j = 0; j + 1 < num; j += 2)
			{
				if (!bitReader3.ReadLength(out array[j]))
				{
					return false;
				}
				if (!bitReader4.ReadLengthBackward(out array[j + 1]))
				{
					return false;
				}
			}
			if (j < num && !bitReader3.ReadLength(out array[j]))
			{
				return false;
			}
		}
		else
		{
			int k;
			for (k = 0; k + 1 < num; k += 2)
			{
				if (!bitReader.ReadLength(out array[k]))
				{
					return false;
				}
				if (!bitReader2.ReadLengthBackward(out array[k + 1]))
				{
					return false;
				}
			}
			if (k < num && !bitReader.ReadLength(out array[k]))
			{
				return false;
			}
		}
		int num7 = bitReader.P - (24 - bitReader.BitPos >> 3);
		int num8 = bitReader2.P + (24 - bitReader2.BitPos >> 3);
		if (num7 != num8 && !excessFlag)
		{
			return false;
		}
		int num9 = 0;
		for (int l = 0; l < lenCount; l++)
		{
			uint num10 = packedLitLen[l];
			if (num10 == 255)
			{
				if (num9 >= num)
				{
					return false;
				}
				num10 = array[num9++] + 255;
			}
			lenStream[l] = (int)(num10 + 3);
		}
		return num9 == num;
	}

	private static void CombineScaledDistances(int[] offs, int count, int scale, byte[] extra)
	{
		for (int i = 0; i < count; i++)
		{
			offs[i] = (sbyte)extra[i] - offs[i] * scale;
		}
	}

	private static int DecodeBytes(byte[] src, int sp, int srcEnd, int outputCap, out byte[] output, out int decodedSize)
	{
		output = Array.Empty<byte>();
		decodedSize = 0;
		int num = sp;
		if (srcEnd - sp < 2)
		{
			return -1;
		}
		int num2 = (src[sp] >> 4) & 7;
		int num3;
		if (num2 == 0)
		{
			if (src[sp] >= 128)
			{
				num3 = ((src[sp] << 8) | src[sp + 1]) & 0xFFF;
				sp += 2;
			}
			else
			{
				if (srcEnd - sp < 3)
				{
					return -1;
				}
				num3 = (src[sp] << 16) | (src[sp + 1] << 8) | src[sp + 2];
				if ((num3 & -262144) != 0)
				{
					return -1;
				}
				sp += 3;
			}
			if (num3 > outputCap || srcEnd - sp < num3)
			{
				return -1;
			}
			output = new byte[num3];
			Array.Copy(src, sp, output, 0, num3);
			decodedSize = num3;
			return sp + num3 - num;
		}
		int num5;
		if (src[sp] >= 128)
		{
			if (srcEnd - sp < 3)
			{
				return -1;
			}
			uint num4 = (uint)((src[sp] << 16) | (src[sp + 1] << 8) | src[sp + 2]);
			num3 = (int)(num4 & 0x3FF);
			num5 = (int)(num3 + ((num4 >> 10) & 0x3FF) + 1);
			sp += 3;
		}
		else
		{
			if (srcEnd - sp < 5)
			{
				return -1;
			}
			int num6 = (src[sp + 1] << 24) | (src[sp + 2] << 16) | (src[sp + 3] << 8) | src[sp + 4];
			num3 = num6 & 0x3FFFF;
			num5 = (((num6 >>> 18) | (src[sp] << 14)) & 0x3FFFF) + 1;
			if (num3 >= num5)
			{
				return -1;
			}
			sp += 5;
		}
		if (srcEnd - sp < num3 || num5 > outputCap)
		{
			return -1;
		}
		output = new byte[num5];
		if (num2 == 2 || num2 == 4)
		{
			int num7 = DecodeHuffman(src, sp, num3, output, num5, num2 >> 1);
			if (num7 != num3)
			{
				return -1;
			}
			decodedSize = num5;
			return sp + num3 - num;
		}
		return -2;
	}

	private static int DecodeHuffman(byte[] src, int srcStart, int srcSize, byte[] output, int outputSize, int type)
	{
		int num = srcStart + srcSize;
		BitReader bits = BitReader.Forward(src, srcStart, num);
		uint[] array = (uint[])CodePrefixBase.Clone();
		byte[] array2 = new byte[1280];
		int num2;
		if (bits.ReadBitNoRefill() == 0)
		{
			num2 = HuffReadCodeLengthsOld(ref bits, array2, array);
		}
		else
		{
			if (bits.ReadBitNoRefill() != 0)
			{
				return -1;
			}
			num2 = HuffReadCodeLengthsNew(ref bits, array2, array);
		}
		if (num2 < 1)
		{
			return -1;
		}
		int num3 = bits.P - (24 - bits.BitPos) / 8;
		if (num2 == 1)
		{
			byte b = array2[0];
			for (int i = 0; i < outputSize; i++)
			{
				output[i] = b;
			}
			return srcSize;
		}
		HuffLookup huffLookup = new HuffLookup();
		if (!HuffBuildLookup(CodePrefixBase, array, huffLookup, array2))
		{
			return -1;
		}
		HuffLookup huffLookup2 = new HuffLookup();
		ReverseBits2048(huffLookup.BitsToLen, huffLookup2.BitsToLen);
		ReverseBits2048(huffLookup.BitsToSym, huffLookup2.BitsToSym);
		if (type == 1)
		{
			if (num3 + 3 > num)
			{
				return -1;
			}
			int num4 = src[num3] | (src[num3 + 1] << 8);
			num3 += 2;
			HuffStreamReader huffStreamReader = new HuffStreamReader(src, output);
			huffStreamReader.OutOff = 0;
			huffStreamReader.OutEnd = outputSize;
			huffStreamReader.Src = num3;
			huffStreamReader.SrcEnd = num;
			huffStreamReader.SrcMid = num3 + num4;
			huffStreamReader.SrcMidOriginal = num3 + num4;
			HuffStreamReader hr = huffStreamReader;
			if (!DecodeHuffmanCore(ref hr, huffLookup2))
			{
				return -1;
			}
		}
		else
		{
			if (num3 + 6 > num)
			{
				return -1;
			}
			int num5 = outputSize + 1 >> 1;
			int num6 = src[num3] | (src[num3 + 1] << 8) | (src[num3 + 2] << 16);
			num3 += 3;
			if (num6 > num - num3)
			{
				return -1;
			}
			int num7 = num3 + num6;
			int num8 = src[num3] | (src[num3 + 1] << 8);
			num3 += 2;
			if (num7 - num3 < num8 + 2 || num - num7 < 3)
			{
				return -1;
			}
			int num9 = src[num7] | (src[num7 + 1] << 8);
			if (num - (num7 + 2) < num9 + 2)
			{
				return -1;
			}
			HuffStreamReader huffStreamReader = new HuffStreamReader(src, output);
			huffStreamReader.OutOff = 0;
			huffStreamReader.OutEnd = num5;
			huffStreamReader.Src = num3;
			huffStreamReader.SrcEnd = num7;
			huffStreamReader.SrcMid = num3 + num8;
			huffStreamReader.SrcMidOriginal = num3 + num8;
			HuffStreamReader hr2 = huffStreamReader;
			if (!DecodeHuffmanCore(ref hr2, huffLookup2))
			{
				return -1;
			}
			huffStreamReader = new HuffStreamReader(src, output);
			huffStreamReader.OutOff = num5;
			huffStreamReader.OutEnd = outputSize;
			huffStreamReader.Src = num7 + 2;
			huffStreamReader.SrcEnd = num;
			huffStreamReader.SrcMid = num7 + 2 + num9;
			huffStreamReader.SrcMidOriginal = num7 + 2 + num9;
			HuffStreamReader hr3 = huffStreamReader;
			if (!DecodeHuffmanCore(ref hr3, huffLookup2))
			{
				return -1;
			}
		}
		return srcSize;
	}

	private static bool HuffBuildLookup(uint[] prefixBase, uint[] prefixCur, HuffLookup lut, byte[] syms)
	{
		uint num = 0u;
		for (uint num2 = 1u; num2 < 11; num2++)
		{
			uint num3 = prefixBase[num2];
			uint num4 = prefixCur[num2] - num3;
			if (num4 != 0)
			{
				uint num5 = (uint)(1 << (int)(11 - num2));
				uint num6 = num4 << (int)(11 - num2);
				if (num + num6 > 2048)
				{
					return false;
				}
				FillByte(lut.BitsToLen, (int)num, (byte)num2, (int)num6);
				int num7 = (int)num;
				uint num8 = 0u;
				while (num8 != num4)
				{
					FillByte(lut.BitsToSym, num7, syms[num3 + num8], (int)num5);
					num8++;
					num7 += (int)num5;
				}
				num += num6;
			}
		}
		if (prefixCur[11] - prefixBase[11] != 0)
		{
			uint num9 = prefixCur[11] - prefixBase[11];
			if (num + num9 > 2048)
			{
				return false;
			}
			FillByte(lut.BitsToLen, (int)num, 11, (int)num9);
			Array.Copy(syms, (int)prefixBase[11], lut.BitsToSym, (int)num, (int)num9);
			num += num9;
		}
		return num == 2048;
	}

	private static void FillByte(byte[] dst, int off, byte v, int n)
	{
		for (int i = 0; i < n; i++)
		{
			dst[off + i] = v;
		}
	}

	private static void ReverseBits2048(byte[] input, byte[] output)
	{
		for (int i = 0; i < 2048; i++)
		{
			output[i] = input[Reverse11(i)];
		}
	}

	private static int Reverse11(int v)
	{
		int num = 0;
		for (int i = 0; i < 11; i++)
		{
			num = (num << 1) | (v & 1);
			v >>= 1;
		}
		return num;
	}

	private static int HuffReadCodeLengthsOld(ref BitReader bits, byte[] syms, uint[] codePrefix)
	{
		if (bits.ReadBitNoRefill() != 0)
		{
			int num = 0;
			int num2 = 0;
			int num3 = 32;
			int num4 = (int)bits.ReadBitsNoRefill(2);
			uint num5 = (uint)(1 << 31 - (20 >>> num4));
			bool flag = bits.ReadBit() != 0;
			do
			{
				if (!flag)
				{
					if ((bits.Bits & 0xFF000000u) == 0)
					{
						return -1;
					}
					num += (int)(bits.ReadBitsNoRefill(2 * (CountLeadingZeros(bits.Bits) + 1)) - 2 + 1);
					if (num >= 256)
					{
						break;
					}
				}
				flag = false;
				bits.Refill();
				if ((bits.Bits & 0xFF000000u) == 0)
				{
					return -1;
				}
				int num6 = (int)(bits.ReadBitsNoRefill(2 * (CountLeadingZeros(bits.Bits) + 1)) - 2 + 1);
				if (num + num6 > 256)
				{
					return -1;
				}
				bits.Refill();
				num2 += num6;
				do
				{
					if (bits.Bits < num5)
					{
						return -1;
					}
					int num7 = CountLeadingZeros(bits.Bits);
					int num8 = (int)bits.ReadBitsNoRefill(num7 + num4 + 1) + (num7 - 1 << num4);
					int num9 = (-(num8 & 1) ^ (num8 >> 1)) + (num3 + 2 >> 2);
					if (num9 < 1 || num9 > 11)
					{
						return -1;
					}
					num3 = num9 + (3 * num3 + 2 >> 2);
					bits.Refill();
					syms[codePrefix[num9]++] = (byte)num++;
				}
				while (--num6 != 0);
			}
			while (num != 256);
			if (num != 256 || num2 < 2)
			{
				return -1;
			}
			return num2;
		}
		int num10 = (int)bits.ReadBitsNoRefill(8);
		switch (num10)
		{
		case 0:
			return -1;
		case 1:
			syms[0] = (byte)bits.ReadBitsNoRefill(8);
			break;
		default:
		{
			int num11 = (int)bits.ReadBitsNoRefill(3);
			if (num11 > 4)
			{
				return -1;
			}
			for (int i = 0; i < num10; i++)
			{
				bits.Refill();
				int num12 = (int)bits.ReadBitsNoRefill(8);
				int num13 = (int)(bits.ReadBitsNoRefillZero(num11) + 1);
				if (num13 > 11)
				{
					return -1;
				}
				syms[codePrefix[num13]++] = (byte)num12;
			}
			break;
		}
		}
		return num10;
	}

	private static int HuffReadCodeLengthsNew(ref BitReader bits, byte[] syms, uint[] codePrefix)
	{
		int bitcount = (int)bits.ReadBitsNoRefill(2);
		int num = (int)(bits.ReadBitsNoRefill(8) + 1);
		int num2 = bits.ReadFluff(num);
		byte[] array = new byte[528];
		GolombReader br = new GolombReader
		{
			Data = bits.Data,
			BitPos = ((bits.BitPos - 24) & 7),
			End = bits.Bound,
			P = bits.P - (24 - bits.BitPos + 7 >> 3)
		};
		if (!DecodeGolombRiceLengths(array, num + num2, ref br))
		{
			return -1;
		}
		for (int i = 0; i < 16; i++)
		{
			array[num + num2 + i] = 0;
		}
		if (!DecodeGolombRiceBits(array, num, bitcount, ref br))
		{
			return -1;
		}
		bits.BitPos = 24;
		bits.P = br.P;
		bits.Bits = 0u;
		bits.Refill();
		bits.Bits <<= br.BitPos;
		bits.BitPos += br.BitPos;
		uint num3 = 30u;
		for (int j = 0; j < num; j++)
		{
			int num4 = array[j];
			num4 = -(num4 & 1) ^ (num4 >> 1);
			int num5 = num4 + (int)(num3 >> 2) + 1;
			if (num5 < 1 || num5 > 11)
			{
				return -1;
			}
			array[j] = (byte)num5;
			num3 += (uint)num4;
		}
		HuffRange[] array2 = new HuffRange[128];
		int num6 = HuffConvertToRanges(array2, num, num2, array, num, ref bits);
		if (num6 <= 0)
		{
			return -1;
		}
		int num7 = 0;
		for (int k = 0; k < num6; k++)
		{
			int symbol = array2[k].Symbol;
			int num8 = array2[k].Num;
			do
			{
				syms[codePrefix[array[num7++]]++] = (byte)symbol++;
			}
			while (--num8 != 0);
		}
		return num;
	}

	private static int HuffConvertToRanges(HuffRange[] range, int numSymbols, int p, byte[] symlen, int symlenOff, ref BitReader bits)
	{
		int num = p >> 1;
		int num2 = 0;
		if ((p & 1) != 0)
		{
			bits.Refill();
			int num3 = symlen[symlenOff++];
			if (num3 >= 8)
			{
				return -1;
			}
			num2 = (int)bits.ReadBitsNoRefill(num3 + 1) + (1 << num3 + 1) - 1;
		}
		int num4 = 0;
		for (int i = 0; i < num; i++)
		{
			bits.Refill();
			int num3 = symlen[symlenOff];
			if (num3 >= 9)
			{
				return -1;
			}
			int num5 = (int)bits.ReadBitsNoRefillZero(num3) + (1 << num3);
			num3 = symlen[symlenOff + 1];
			if (num3 >= 8)
			{
				return -1;
			}
			int num6 = (int)bits.ReadBitsNoRefill(num3 + 1) + (1 << num3 + 1) - 1;
			range[i].Symbol = num2;
			range[i].Num = num5;
			num4 += num5;
			num2 += num5 + num6;
			symlenOff += 2;
		}
		if (num2 >= 256 || num4 >= numSymbols || num2 + numSymbols - num4 > 256)
		{
			return -1;
		}
		range[num].Symbol = num2;
		range[num].Num = numSymbols - num4;
		return num + 1;
	}

	private static bool DecodeHuffmanCore(ref HuffStreamReader hr, HuffLookup lut)
	{
		byte[] data = hr.Data;
		byte[] output = hr.Output;
		byte[] bitsToLen = lut.BitsToLen;
		byte[] bitsToSym = lut.BitsToSym;
		int num = hr.Src;
		uint num2 = hr.SrcBits;
		int num3 = hr.SrcBitpos;
		int num4 = hr.SrcMid;
		uint num5 = hr.SrcMidBits;
		int num6 = hr.SrcMidBitpos;
		int num7 = hr.SrcEnd;
		uint num8 = hr.SrcEndBits;
		int num9 = hr.SrcEndBitpos;
		int outOff = hr.OutOff;
		int outEnd = hr.OutEnd;
		if (num > num4)
		{
			return false;
		}
		while (outOff < outEnd)
		{
			if (num4 - num <= 1)
			{
				if (num4 - num == 1)
				{
					num2 |= (uint)(data[num] << num3);
				}
			}
			else
			{
				num2 |= (uint)((data[num] | (data[num + 1] << 8)) << num3);
			}
			int num10 = (int)(num2 & 0x7FF);
			int num11 = bitsToLen[num10];
			num3 -= num11;
			num2 >>= num11;
			output[outOff++] = bitsToSym[num10];
			num += 7 - num3 >> 3;
			num3 &= 7;
			if (outOff < outEnd)
			{
				if (num7 - num4 <= 1)
				{
					if (num7 - num4 == 1)
					{
						num8 |= (uint)(data[num4] << num9);
						num5 |= (uint)(data[num4] << num6);
					}
				}
				else
				{
					uint num12 = (uint)(data[num7 - 2] | (data[num7 - 1] << 8));
					num8 |= (((num12 >> 8) | (num12 << 8)) & 0xFFFF) << num9;
					num5 |= (uint)((data[num4] | (data[num4 + 1] << 8)) << num6);
				}
				num10 = (int)(num8 & 0x7FF);
				num11 = bitsToLen[num10];
				output[outOff++] = bitsToSym[num10];
				num9 -= num11;
				num8 >>= num11;
				num7 -= 7 - num9 >> 3;
				num9 &= 7;
				if (outOff < outEnd)
				{
					num10 = (int)(num5 & 0x7FF);
					num11 = bitsToLen[num10];
					output[outOff++] = bitsToSym[num10];
					num6 -= num11;
					num5 >>= num11;
					num4 += 7 - num6 >> 3;
					num6 &= 7;
				}
			}
			if (num > num4 || num4 > num7)
			{
				return false;
			}
		}
		if (num != hr.SrcMidOriginal || num7 != num4)
		{
			return false;
		}
		return true;
	}

	private static bool DecodeGolombRiceLengths(byte[] dst, int size, ref GolombReader br)
	{
		int num = br.P;
		int end = br.End;
		int num2 = 0;
		if (num >= end)
		{
			return false;
		}
		int num3 = -br.BitPos;
		uint num4 = (uint)(At(br.Data, num++) & (255 >> br.BitPos));
		while (true)
		{
			if (num4 == 0)
			{
				num3 += 8;
			}
			else
			{
				uint num5 = RiceValue[num4];
				uint v = (uint)num3 + (num5 & 0xF0F0F0F);
				WriteLE32(dst, num2, v);
				WriteLE32(dst, num2 + 4, (num5 >> 4) & 0xF0F0F0F);
				num2 += RiceLength[num4];
				if (num2 >= size)
				{
					break;
				}
				num3 = (int)(num5 >> 28);
			}
			if (num >= end)
			{
				return false;
			}
			num4 = At(br.Data, num++);
		}
		if (num2 > size)
		{
			int num6 = num2 - size;
			do
			{
				num4 &= num4 - 1;
			}
			while (--num6 != 0);
		}
		int bitPos = 0;
		if ((num4 & 1) == 0)
		{
			num--;
			int num7 = LeastSignificantBit(num4);
			bitPos = 8 - num7;
		}
		br.P = num;
		br.BitPos = bitPos;
		return true;
	}

	private static bool DecodeGolombRiceBits(byte[] dst, int size, int bitcount, ref GolombReader br)
	{
		if (bitcount == 0)
		{
			return true;
		}
		int num = 0;
		int num2 = br.P;
		int bitPos = br.BitPos;
		int num3 = bitPos + bitcount * size;
		if (num3 + 7 >> 3 > br.End - num2)
		{
			return false;
		}
		br.P = num2 + (num3 >> 3);
		br.BitPos = num3 & 7;
		ulong v = ReadLE64(dst, size);
		if (bitcount == 1)
		{
			do
			{
				ulong num4 = (byte)(Swap32(ReadLE32(br.Data, num2)) >> 24 - bitPos);
				num2++;
				num4 = (num4 | (num4 << 28)) & 0xF0000000FL;
				num4 = (num4 | (num4 << 14)) & 0x3000300030003L;
				num4 = (num4 | (num4 << 7)) & 0x101010101010101L;
				ulong num5 = ReadLE64(dst, num);
				WriteLE64(dst, num, num5 * 2 + Swap64(num4));
				num += 8;
			}
			while (num < size);
		}
		else if (bitcount == 2)
		{
			do
			{
				ulong num6 = (ushort)(Swap32(ReadLE32(br.Data, num2)) >> 16 - bitPos);
				num2 += 2;
				num6 = (num6 | (num6 << 24)) & 0xFF000000FFL;
				num6 = (num6 | (num6 << 12)) & 0xF000F000F000FL;
				num6 = (num6 | (num6 << 6)) & 0x303030303030303L;
				ulong num7 = ReadLE64(dst, num);
				WriteLE64(dst, num, num7 * 4 + Swap64(num6));
				num += 8;
			}
			while (num < size);
		}
		else
		{
			do
			{
				ulong num8 = (Swap32(ReadLE32(br.Data, num2)) >> 8 - bitPos) & 0xFFFFFF;
				num2 += 3;
				num8 = (num8 | (num8 << 20)) & 0xFFF00000FFFL;
				num8 = (num8 | (num8 << 10)) & 0x3F003F003F003FL;
				num8 = (num8 | (num8 << 5)) & 0x707070707070707L;
				ulong num9 = ReadLE64(dst, num);
				WriteLE64(dst, num, num9 * 8 + Swap64(num8));
				num += 8;
			}
			while (num < size);
		}
		WriteLE64(dst, size, v);
		return true;
	}

	private static bool ProcessLzRuns(int mode, byte[] dst, int dstStart, int dstSize, int offset, LzStreams lzt)
	{
		int dstEnd = dstStart + dstSize;
		int dstPos = dstStart + ((offset == 0) ? 8 : 0);
		int dstStart2 = dstStart - offset;
		return mode switch
		{
			1 => ProcessLzRunsRaw(lzt, dst, dstPos, dstEnd, dstStart2),
			0 => ProcessLzRunsSub(lzt, dst, dstPos, dstEnd, dstStart2),
			_ => false,
		};
	}

	private static bool ProcessLzRunsRaw(LzStreams lzt, byte[] dst, int dstPos, int dstEnd, int dstStart)
	{
		byte[] commands = lzt.Commands;
		int num = 0;
		int commandCount = lzt.CommandCount;
		int[] lengths = lzt.Lengths;
		int num2 = 0;
		int lengthCount = lzt.LengthCount;
		byte[] literals = lzt.Literals;
		int num3 = 0;
		int literalCount = lzt.LiteralCount;
		int[] distances = lzt.Distances;
		int num4 = 0;
		int distanceCount = lzt.DistanceCount;
		Span<int> span = stackalloc int[7];
		span[3] = -8;
		span[4] = -8;
		span[5] = -8;
		while (num < commandCount)
		{
			byte b = commands[num++];
			uint num5 = (uint)(b & 3);
			int num6 = b >>> 6;
			uint num7 = (uint)((b >>> 2) & 0xF);
			if (num5 == 3)
			{
				if (num2 >= lengthCount)
				{
					return false;
				}
				num5 = (uint)lengths[num2++];
			}
			span[6] = ((num4 < distanceCount) ? distances[num4] : 0);
			if (num5 != 0)
			{
				if (num3 + num5 > (uint)literalCount || dstPos + num5 > (uint)dstEnd)
				{
					return false;
				}
				for (uint num8 = 0u; num8 < num5; num8++)
				{
					dst[dstPos + num8] = literals[num3 + num8];
				}
				dstPos += (int)num5;
				num3 += (int)num5;
			}
			int num9 = span[num6 + 3];
			span[num6 + 3] = span[num6 + 2];
			span[num6 + 2] = span[num6 + 1];
			span[num6 + 1] = span[num6];
			span[3] = num9;
			num4 += ((num6 + 1) & 4) >> 2;
			if (dstPos + num9 < dstStart)
			{
				return false;
			}
			int num10 = dstPos + num9;
			uint num11;
			if (num7 != 15)
			{
				num11 = num7 + 2;
			}
			else
			{
				if (num2 >= lengthCount)
				{
					return false;
				}
				num11 = (uint)(14 + lengths[num2++]);
			}
			if (dstPos + num11 > (uint)dstEnd)
			{
				return false;
			}
			for (uint num12 = 0u; num12 < num11; num12++)
			{
				dst[dstPos + num12] = dst[num10 + num12];
			}
			dstPos += (int)num11;
		}
		if (num4 != distanceCount || num2 != lengthCount)
		{
			return false;
		}
		int num13 = dstEnd - dstPos;
		if (num13 != literalCount - num3)
		{
			return false;
		}
		for (int i = 0; i < num13; i++)
		{
			dst[dstPos + i] = literals[num3 + i];
		}
		return true;
	}

	private static bool ProcessLzRunsSub(LzStreams lzt, byte[] dst, int dstPos, int dstEnd, int dstStart)
	{
		byte[] commands = lzt.Commands;
		int num = 0;
		int commandCount = lzt.CommandCount;
		int[] lengths = lzt.Lengths;
		int num2 = 0;
		int lengthCount = lzt.LengthCount;
		byte[] literals = lzt.Literals;
		int num3 = 0;
		int literalCount = lzt.LiteralCount;
		int[] distances = lzt.Distances;
		int num4 = 0;
		int distanceCount = lzt.DistanceCount;
		Span<int> span = stackalloc int[7];
		span[3] = -8;
		span[4] = -8;
		span[5] = -8;
		int num5 = -8;
		while (num < commandCount)
		{
			byte b = commands[num++];
			uint num6 = (uint)(b & 3);
			int num7 = b >>> 6;
			uint num8 = (uint)((b >>> 2) & 0xF);
			if (num6 == 3)
			{
				if (num2 >= lengthCount)
				{
					return false;
				}
				num6 = (uint)lengths[num2++];
			}
			span[6] = ((num4 < distanceCount) ? distances[num4] : 0);
			if (num6 != 0)
			{
				if (num3 + num6 > (uint)literalCount || dstPos + num6 > (uint)dstEnd)
				{
					return false;
				}
				for (uint num9 = 0u; num9 < num6; num9++)
				{
					dst[dstPos + num9] = (byte)(literals[num3 + num9] + dst[dstPos + (int)num9 + num5]);
				}
				dstPos += (int)num6;
				num3 += (int)num6;
			}
			int num10 = span[num7 + 3];
			span[num7 + 3] = span[num7 + 2];
			span[num7 + 2] = span[num7 + 1];
			span[num7 + 1] = span[num7];
			span[3] = num10;
			num5 = num10;
			num4 += ((num7 + 1) & 4) >> 2;
			if (dstPos + num10 < dstStart)
			{
				return false;
			}
			int num11 = dstPos + num10;
			uint num12;
			if (num8 != 15)
			{
				num12 = num8 + 2;
			}
			else
			{
				if (num2 >= lengthCount)
				{
					return false;
				}
				num12 = (uint)(14 + lengths[num2++]);
			}
			if (dstPos + num12 > (uint)dstEnd)
			{
				return false;
			}
			for (uint num13 = 0u; num13 < num12; num13++)
			{
				dst[dstPos + num13] = dst[num11 + num13];
			}
			dstPos += (int)num12;
		}
		if (num4 != distanceCount || num2 != lengthCount)
		{
			return false;
		}
		int num14 = dstEnd - dstPos;
		if (num14 != literalCount - num3)
		{
			return false;
		}
		for (int i = 0; i < num14; i++)
		{
			dst[dstPos + i] = (byte)(literals[num3 + i] + dst[dstPos + i + num5]);
		}
		return true;
	}

	private static int MostSignificantBit(uint x)
	{
		if (x != 0)
		{
			return 31 - BitOperations.LeadingZeroCount(x);
		}
		return 0;
	}

	private static int LeastSignificantBit(uint x)
	{
		if (x != 0)
		{
			return BitOperations.TrailingZeroCount(x);
		}
		return 0;
	}

	private static int CountLeadingZeros(uint x)
	{
		if (x != 0)
		{
			return BitOperations.LeadingZeroCount(x);
		}
		return 31;
	}

	private static byte At(byte[] b, int i)
	{
		if ((uint)i >= (uint)b.Length)
		{
			return 0;
		}
		return b[i];
	}

	private static uint ReadLE32(byte[] b, int off)
	{
		uint num = 0u;
		for (int i = 0; i < 4; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				num |= (uint)(b[off + i] << 8 * i);
			}
		}
		return num;
	}

	private static ulong ReadLE64(byte[] b, int off)
	{
		ulong num = 0uL;
		for (int i = 0; i < 8; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				num |= (ulong)b[off + i] << 8 * i;
			}
		}
		return num;
	}

	private static void WriteLE32(byte[] b, int off, uint v)
	{
		for (int i = 0; i < 4; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				b[off + i] = (byte)(v >> 8 * i);
			}
		}
	}

	private static void WriteLE64(byte[] b, int off, ulong v)
	{
		for (int i = 0; i < 8; i++)
		{
			if ((uint)(off + i) < (uint)b.Length)
			{
				b[off + i] = (byte)(v >> 8 * i);
			}
		}
	}

	private static uint Swap32(uint v)
	{
		return BinaryPrimitives.ReverseEndianness(v);
	}

	private static ulong Swap64(ulong v)
	{
		return BinaryPrimitives.ReverseEndianness(v);
	}

	private static uint[] BuildRiceValue()
	{
		return new uint[256]
		{
			2147483648u, 7u, 268435462u, 6u, 536870917u, 261u, 268435461u, 5u, 805306372u, 516u,
			268435716u, 260u, 536870916u, 65540u, 268435460u, 4u, 1073741827u, 771u, 268435971u, 515u,
			536871171u, 65795u, 268435715u, 259u, 805306371u, 131075u, 268500995u, 65539u, 536870915u, 16777219u,
			268435459u, 3u, 1342177282u, 1026u, 268436226u, 770u, 536871426u, 66050u, 268435970u, 514u,
			805306626u, 131330u, 268501250u, 65794u, 536871170u, 16777474u, 268435714u, 258u, 1073741826u, 196610u,
			268566530u, 131074u, 536936450u, 16842754u, 268500994u, 65538u, 805306370u, 33554434u, 285212674u, 16777218u,
			536870914u, 18u, 268435458u, 2u, 1610612737u, 1281u, 268436481u, 1025u, 536871681u, 66305u,
			268436225u, 769u, 805306881u, 131585u, 268501505u, 66049u, 536871425u, 16777729u, 268435969u, 513u,
			1073742081u, 196865u, 268566785u, 131329u, 536936705u, 16843009u, 268501249u, 65793u, 805306625u, 33554689u,
			285212929u, 16777473u, 536871169u, 273u, 268435713u, 257u, 1342177281u, 262145u, 268632065u, 196609u,
			537001985u, 16908289u, 268566529u, 131073u, 805371905u, 33619969u, 285278209u, 16842753u, 536936449u, 65553u,
			268500993u, 65537u, 1073741825u, 50331649u, 301989889u, 33554433u, 553648129u, 16777233u, 285212673u, 16777217u,
			805306369u, 33u, 268435473u, 17u, 536870913u, 4097u, 268435457u, 1u, 1879048192u, 1536u,
			268436736u, 1280u, 536871936u, 66560u, 268436480u, 1024u, 805307136u, 131840u, 268501760u, 66304u,
			536871680u, 16777984u, 268436224u, 768u, 1073742336u, 197120u, 268567040u, 131584u, 536936960u, 16843264u,
			268501504u, 66048u, 805306880u, 33554944u, 285213184u, 16777728u, 536871424u, 528u, 268435968u, 512u,
			1342177536u, 262400u, 268632320u, 196864u, 537002240u, 16908544u, 268566784u, 131328u, 805372160u, 33620224u,
			285278464u, 16843008u, 536936704u, 65808u, 268501248u, 65792u, 1073742080u, 50331904u, 301990144u, 33554688u,
			553648384u, 16777488u, 285212928u, 16777472u, 805306624u, 288u, 268435728u, 272u, 536871168u, 4352u,
			268435712u, 256u, 1610612736u, 327680u, 268697600u, 262144u, 537067520u, 16973824u, 268632064u, 196608u,
			805437440u, 33685504u, 285343744u, 16908288u, 537001984u, 131088u, 268566528u, 131072u, 1073807360u, 50397184u,
			302055424u, 33619968u, 553713664u, 16842768u, 285278208u, 16842752u, 805371904u, 65568u, 268501008u, 65552u,
			536936448u, 69632u, 268500992u, 65536u, 1342177280u, 67108864u, 318767104u, 50331648u, 570425344u, 33554448u,
			301989888u, 33554432u, 822083584u, 16777248u, 285212688u, 16777232u, 553648128u, 16781312u, 285212672u, 16777216u,
			1073741824u, 48u, 268435488u, 32u, 536870928u, 4112u, 268435472u, 16u, 805306368u, 8192u,
			268439552u, 4096u, 536870912u, 1048576u, 268435456u, 0u
		};
	}

	private static byte[] BuildRiceLength()
	{
		return new byte[256]
		{
			0, 1, 1, 2, 1, 2, 2, 3, 1, 2,
			2, 3, 2, 3, 3, 4, 1, 2, 2, 3,
			2, 3, 3, 4, 2, 3, 3, 4, 3, 4,
			4, 5, 1, 2, 2, 3, 2, 3, 3, 4,
			2, 3, 3, 4, 3, 4, 4, 5, 2, 3,
			3, 4, 3, 4, 4, 5, 3, 4, 4, 5,
			4, 5, 5, 6, 1, 2, 2, 3, 2, 3,
			3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
			2, 3, 3, 4, 3, 4, 4, 5, 3, 4,
			4, 5, 4, 5, 5, 6, 2, 3, 3, 4,
			3, 4, 4, 5, 3, 4, 4, 5, 4, 5,
			5, 6, 3, 4, 4, 5, 4, 5, 5, 6,
			4, 5, 5, 6, 5, 6, 6, 7, 1, 2,
			2, 3, 2, 3, 3, 4, 2, 3, 3, 4,
			3, 4, 4, 5, 2, 3, 3, 4, 3, 4,
			4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
			2, 3, 3, 4, 3, 4, 4, 5, 3, 4,
			4, 5, 4, 5, 5, 6, 3, 4, 4, 5,
			4, 5, 5, 6, 4, 5, 5, 6, 5, 6,
			6, 7, 2, 3, 3, 4, 3, 4, 4, 5,
			3, 4, 4, 5, 4, 5, 5, 6, 3, 4,
			4, 5, 4, 5, 5, 6, 4, 5, 5, 6,
			5, 6, 6, 7, 3, 4, 4, 5, 4, 5,
			5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
			4, 5, 5, 6, 5, 6, 6, 7, 5, 6,
			6, 7, 6, 7, 7, 8
		};
	}
}
