using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ProsperoPkgTool.Crypto;

public static class ProsperoKeys
{
	public const int ContentIdPaddedLength = 48;

	public const int PasscodeLength = 32;

	public const int KeySize = 32;

	public static byte[] ComputeKeys(string contentId, string passcode, uint index, bool useSha3)
	{
		ArgumentNullException.ThrowIfNull(contentId, "contentId");
		ArgumentNullException.ThrowIfNull(passcode, "passcode");
		byte[] bytes = Encoding.ASCII.GetBytes(passcode);
		if (bytes.Length != 32)
		{
			throw new ArgumentException($"Passcode must be exactly {32} ASCII characters.", "passcode");
		}
		byte[] array = new byte[48];
		Encoding.ASCII.GetBytes(contentId).AsSpan().CopyTo(array);
		Span<byte> span = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(span, index);
		byte[] array2 = Hash(useSha3, span);
		byte[] array3 = Hash(useSha3, array);
		byte[] array4 = new byte[96];
		array2.CopyTo(array4, 0);
		array3.CopyTo(array4, 32);
		bytes.CopyTo(array4, 64);
		return Hash(useSha3, array4);
	}

	public static byte[] ComputeEkpfs(string contentId, string passcode)
	{
		return ComputeKeys(contentId, passcode, 1u, useSha3: true);
	}

	public static (byte[] TweakKey, byte[] DataKey) DeriveOuterPfsXtsKeys(byte[] ekpfs, ReadOnlySpan<byte> seed)
	{
		ArgumentNullException.ThrowIfNull(ekpfs, "ekpfs");
		if (ekpfs.Length != 32)
		{
			throw new ArgumentException($"EKPFS must be {32} bytes.", "ekpfs");
		}
		if (seed.Length != 16)
		{
			throw new ArgumentException("PFS seed must be 16 bytes.", "seed");
		}
		byte[] key = HMACSHA256.HashData(ekpfs, seed.ToArray());
		byte[] array = new byte[4 + seed.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(array, 1u);
		seed.CopyTo(array.AsSpan(4));
		byte[] array2 = HMACSHA256.HashData(key, array);
		byte[] subArray = array2[..16];
		byte[] subArray2 = array2[16..32];
		return (TweakKey: subArray, DataKey: subArray2);
	}

	private static byte[] Hash(bool useSha3, ReadOnlySpan<byte> data)
	{
		if (!useSha3)
		{
			return SHA256.HashData(data);
		}
		return Sha3.Sha3_256(data);
	}
}
