using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace ProsperoPkgTool.Crypto;

public static class Sha3
{
	public sealed class Sha3StreamState
	{
		private readonly byte[] _state = new byte[200];

		private readonly byte[] _buffer = new byte[136];

		private int _buffered;

		private bool _finished;

		public void Append(ReadOnlySpan<byte> data)
		{
			if (_finished)
			{
				throw new InvalidOperationException("The SHA3 stream state has already been finalized.");
			}
			if (_buffered > 0)
			{
				int num = Math.Min(136 - _buffered, data.Length);
				data.Slice(0, num).CopyTo(_buffer.AsSpan(_buffered));
				_buffered += num;
				data = data.Slice(num);
				if (_buffered == 136)
				{
					XorBlockIntoState(_state, _buffer);
					KeccakF1600(_state);
					_buffered = 0;
				}
			}
			while (data.Length >= 136)
			{
				XorBlockIntoState(_state, data.Slice(0, 136));
				KeccakF1600(_state);
				data = data.Slice(136);
			}
			if (data.Length > 0)
			{
				data.CopyTo(_buffer);
				_buffered = data.Length;
			}
		}

		public byte[] Finish()
		{
			if (_finished)
			{
				throw new InvalidOperationException("The SHA3 stream state has already been finalized.");
			}
			_finished = true;
			Span<byte> span = stackalloc byte[136];
			span.Clear();
			_buffer.AsSpan(0, _buffered).CopyTo(span);
			span[_buffered] ^= 6;
			span[135] ^= 128;
			XorBlockIntoState(_state, span);
			KeccakF1600(_state);
			byte[] array = new byte[32];
			_state.AsSpan(0, 32).CopyTo(array);
			return array;
		}
	}

	private const int StateSizeBytes = 200;

	private const int RateBytes = 136;

	private const int DigestSizeBytes = 32;

	private const byte DomainSuffix = 6;

	private static readonly ulong[] RoundConstants = new ulong[24]
	{
		1uL, 32898uL, 9223372036854808714uL, 9223372039002292224uL, 32907uL, 2147483649uL, 9223372039002292353uL, 9223372036854808585uL, 138uL, 136uL,
		2147516425uL, 2147483658uL, 2147516555uL, 9223372036854775947uL, 9223372036854808713uL, 9223372036854808579uL, 9223372036854808578uL, 9223372036854775936uL, 32778uL, 9223372039002259466uL,
		9223372039002292353uL, 9223372036854808704uL, 2147483649uL, 9223372039002292232uL
	};

	private static readonly int[] RotationOffsets = new int[25]
	{
		0, 36, 3, 41, 18, 1, 44, 10, 45, 2,
		62, 6, 43, 15, 61, 28, 55, 25, 21, 56,
		27, 20, 39, 8, 14
	};

	public static byte[] Sha3_256(ReadOnlySpan<byte> data)
	{
		if (SHA3_256.IsSupported)
		{
			byte[] array = new byte[32];
			SHA3_256.HashData(data, array);
			return array;
		}
		return ManagedSha3_256(data);
	}

	private static byte[] ManagedSha3_256(ReadOnlySpan<byte> data)
	{
		Span<byte> span = stackalloc byte[200];
		span.Clear();
		int i;
		for (i = 0; i + 136 <= data.Length; i += 136)
		{
			XorBlockIntoState(span, data.Slice(i, 136));
			KeccakF1600(span);
		}
		Span<byte> span2 = stackalloc byte[136];
		span2.Clear();
		data.Slice(i).CopyTo(span2);
		span2[data.Length - i] ^= 6;
		span2[135] ^= 128;
		XorBlockIntoState(span, span2);
		KeccakF1600(span);
		byte[] array = new byte[32];
		span.Slice(0, 32).CopyTo(array);
		return array;
	}

	public static byte[] Sha3_256(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		if (SHA3_256.IsSupported)
		{
			return SHA3_256.HashData(stream);
		}
		Sha3StreamState sha3StreamState = new Sha3StreamState();
		byte[] array = new byte[1048576];
		int length;
		while ((length = stream.Read(array, 0, array.Length)) > 0)
		{
			sha3StreamState.Append(array.AsSpan(0, length));
		}
		return sha3StreamState.Finish();
	}

	private static void XorBlockIntoState(Span<byte> state, ReadOnlySpan<byte> block)
	{
		for (int i = 0; i < block.Length; i++)
		{
			state[i] ^= block[i];
		}
	}

	private static void KeccakF1600(Span<byte> stateBytes)
	{
		Span<ulong> span = stackalloc ulong[25];
		for (int i = 0; i < 25; i++)
		{
			span[i] = BinaryPrimitives.ReadUInt64LittleEndian(stateBytes.Slice(i * 8, 8));
		}
		Span<ulong> span2 = stackalloc ulong[5];
		Span<ulong> span3 = stackalloc ulong[5];
		Span<ulong> span4 = stackalloc ulong[25];
		ulong[] roundConstants = RoundConstants;
		foreach (ulong num in roundConstants)
		{
			for (int k = 0; k < 5; k++)
			{
				span2[k] = span[k] ^ span[k + 5] ^ span[k + 10] ^ span[k + 15] ^ span[k + 20];
			}
			for (int l = 0; l < 5; l++)
			{
				span3[l] = span2[(l + 4) % 5] ^ RotateLeft(span2[(l + 1) % 5], 1);
			}
			for (int m = 0; m < 5; m++)
			{
				for (int n = 0; n < 5; n++)
				{
					span[n + 5 * m] ^= span3[n];
				}
			}
			span4[1] = RotateLeft(span[6], 44);
			span4[2] = RotateLeft(span[12], 43);
			span4[3] = RotateLeft(span[18], 21);
			span4[4] = RotateLeft(span[24], 14);
			span4[5] = RotateLeft(span[3], 28);
			span4[6] = RotateLeft(span[9], 20);
			span4[7] = RotateLeft(span[10], 3);
			span4[8] = RotateLeft(span[16], 45);
			span4[9] = RotateLeft(span[22], 61);
			span4[10] = RotateLeft(span[1], 1);
			span4[11] = RotateLeft(span[7], 6);
			span4[12] = RotateLeft(span[13], 25);
			span4[13] = RotateLeft(span[19], 8);
			span4[14] = RotateLeft(span[20], 18);
			span4[15] = RotateLeft(span[4], 27);
			span4[16] = RotateLeft(span[5], 36);
			span4[17] = RotateLeft(span[11], 10);
			span4[18] = RotateLeft(span[17], 15);
			span4[19] = RotateLeft(span[23], 56);
			span4[20] = RotateLeft(span[2], 62);
			span4[21] = RotateLeft(span[8], 55);
			span4[22] = RotateLeft(span[14], 39);
			span4[23] = RotateLeft(span[15], 41);
			span4[24] = RotateLeft(span[21], 2);
			span4[0] = span[0];
			for (int num2 = 0; num2 < 5; num2++)
			{
				for (int num3 = 0; num3 < 5; num3++)
				{
					span[num3 + 5 * num2] = span4[num3 + 5 * num2] ^ (~span4[(num3 + 1) % 5 + 5 * num2] & span4[(num3 + 2) % 5 + 5 * num2]);
				}
			}
			span[0] ^= num;
		}
		for (int num4 = 0; num4 < 25; num4++)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(stateBytes.Slice(num4 * 8, 8), span[num4]);
		}
	}

	private static ulong RotateLeft(ulong value, int offset)
	{
		if (offset != 0)
		{
			return (value << offset) | (value >> 64 - offset);
		}
		return value;
	}
}
