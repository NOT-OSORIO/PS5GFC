using System;
using System.Security.Cryptography;

namespace ProsperoPkgTool.Crypto;

public static class ProsperoEntryCipher
{
	public const int MetaRowSize = 32;

	public const int KeySeedSize = 32;

	public static int PaddedLength(int dataSize)
	{
		return checked(dataSize + 15) & -16;
	}

	public static byte[] DeriveKeySeed(ReadOnlySpan<byte> metaRow, string contentId, string passcode, uint keyIndex, bool publisherProfile)
	{
		if (metaRow.Length != 32)
		{
			throw new ArgumentException($"Meta row must be {32} bytes.", "metaRow");
		}
		byte[] array = ProsperoKeys.ComputeKeys(contentId, passcode, keyIndex, publisherProfile);
		byte[] array2 = new byte[64];
		metaRow.CopyTo(array2);
		array.CopyTo(array2, 32);
		if (!publisherProfile)
		{
			return SHA256.HashData(array2);
		}
		return Sha3.Sha3_256(array2);
	}

	public static (byte[] Key, byte[] Iv) DeriveKeyIv(ReadOnlySpan<byte> keySeed)
	{
		if (keySeed.Length != 32)
		{
			throw new ArgumentException($"Key seed must be {32} bytes.", "keySeed");
		}
		return (Key: keySeed.Slice(16, 16).ToArray(), Iv: keySeed.Slice(0, 16).ToArray());
	}

	public static byte[] Encrypt(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> keySeed)
	{
		byte[] array = new byte[PaddedLength(payload.Length)];
		payload.CopyTo(array);
		var (key, iv) = DeriveKeyIv(keySeed);
		return Transform(array, key, iv, encrypt: true);
	}

	public static byte[] Encrypt(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> metaRow, string contentId, string passcode, uint keyIndex, bool publisherProfile)
	{
		byte[] array = DeriveKeySeed(metaRow, contentId, passcode, keyIndex, publisherProfile);
		return Encrypt(payload, array);
	}

	public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> keySeed)
	{
		if (ciphertext.Length == 0 || (ciphertext.Length & 0xF) != 0)
		{
			throw new ArgumentException("Ciphertext length must be a non-zero multiple of 16.", "ciphertext");
		}
		var (key, iv) = DeriveKeyIv(keySeed);
		return Transform(ciphertext.ToArray(), key, iv, encrypt: false);
	}

	public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> metaRow, string contentId, string passcode, uint keyIndex, bool publisherProfile)
	{
		byte[] array = DeriveKeySeed(metaRow, contentId, passcode, keyIndex, publisherProfile);
		return Decrypt(ciphertext, array);
	}

	private static byte[] Transform(byte[] buffer, byte[] key, byte[] iv, bool encrypt)
	{
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.None;
		aes.KeySize = 128;
		aes.Key = key;
		aes.IV = iv;
		using ICryptoTransform cryptoTransform = (encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor());
		return cryptoTransform.TransformFinalBlock(buffer, 0, buffer.Length);
	}
}
