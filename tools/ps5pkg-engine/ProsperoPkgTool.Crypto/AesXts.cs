using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ProsperoPkgTool.Crypto;

public sealed class AesXts : IDisposable
{
	private readonly Aes _dataCipher;

	private readonly Aes _tweakCipher;

	public AesXts(ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> tweakKey)
	{
		if (dataKey.Length != 16)
		{
			throw new ArgumentException("XTS data key must be 16 bytes (AES-128).", "dataKey");
		}
		if (tweakKey.Length != 16)
		{
			throw new ArgumentException("XTS tweak key must be 16 bytes (AES-128).", "tweakKey");
		}
		_dataCipher = CreateAes(dataKey);
		_tweakCipher = CreateAes(tweakKey);
	}

	private static Aes CreateAes(ReadOnlySpan<byte> key)
	{
		Aes aes = Aes.Create();
		aes.Key = key.ToArray();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		return aes;
	}

	public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ulong sector)
	{
		return Crypt(ciphertext, sector, encrypt: false);
	}

	public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ulong sector)
	{
		return Crypt(plaintext, sector, encrypt: true);
	}

	private byte[] Crypt(ReadOnlySpan<byte> input, ulong sector, bool encrypt)
	{
		if (input.Length == 0 || input.Length % 16 != 0)
		{
			throw new ArgumentException("XTS data units here must be a non-empty multiple of 16 bytes.", "input");
		}
		Span<byte> span = stackalloc byte[16];
		BinaryPrimitives.WriteUInt64LittleEndian(span, sector);
		byte[] first = _tweakCipher.EncryptEcb(span, PaddingMode.None);
		ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(first);
		ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(first.AsSpan(8));
		byte[] tweaks = new byte[input.Length];
		for (int i = 0; i < tweaks.Length; i += 16)
		{
			BinaryPrimitives.WriteUInt64LittleEndian(tweaks.AsSpan(i), lo);
			BinaryPrimitives.WriteUInt64LittleEndian(tweaks.AsSpan(i + 8), hi);
			ulong carry = hi >> 63;
			hi = (hi << 1) | (lo >> 63);
			lo = (lo << 1) ^ (carry * 135);
		}
		byte[] output = new byte[input.Length];
		Xor(input, tweaks, output);
		byte[] middle = new byte[input.Length];
		if (encrypt)
		{
			_dataCipher.EncryptEcb(output, middle, PaddingMode.None);
		}
		else
		{
			_dataCipher.DecryptEcb(output, middle, PaddingMode.None);
		}
		Xor(middle, tweaks, output);
		return output;
	}

	private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination)
	{
		int i = 0;
		if (System.Numerics.Vector.IsHardwareAccelerated)
		{
			int width = System.Numerics.Vector<byte>.Count;
			for (; i <= destination.Length - width; i += width)
			{
				(new System.Numerics.Vector<byte>(a.Slice(i)) ^ new System.Numerics.Vector<byte>(b.Slice(i))).CopyTo(destination.Slice(i));
			}
		}
		for (; i < destination.Length; i++)
		{
			destination[i] = (byte)(a[i] ^ b[i]);
		}
	}

	public void Dispose()
	{
		_dataCipher.Dispose();
		_tweakCipher.Dispose();
	}
}
