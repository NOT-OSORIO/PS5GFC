using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ProsperoPkgTool.Crypto;

public static class ProsperoRsaKeys
{
	public const int ModulusSize = 384;

	public const int PasscodeModulusCount = 7;

	public const int ImageKeyEntrySize = 2048;

	public const int HeaderSignatureSize = 384;

	public const int EntryKeysSize = 2944;

	public const int CntHeaderSize = 4096;

	public const int CntWrapPasscodeIndex = 3;

	private static readonly byte[] s_passcodeBlob = LoadResource("passcode.bin");

	private static readonly byte[] s_mountImageBlob = LoadResource("mount_image.bin");

	private static readonly byte[] s_metadataModulus = LoadResource("metadata_modulus.bin");

	public static bool IsAvailable
	{
		get
		{
			if (s_passcodeBlob.Length == 2688 && s_mountImageBlob.Length == 384)
			{
				return s_metadataModulus.Length == 384;
			}
			return false;
		}
	}

	public static ReadOnlySpan<byte> PasscodeBlob => s_passcodeBlob;

	public static ReadOnlySpan<byte> MountImageModulus => s_mountImageBlob;

	public static ReadOnlySpan<byte> MetadataModulus => s_metadataModulus;

	public static ReadOnlySpan<byte> GetPasscodeModulus(int index)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(index, 0, "index");
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 7, "index");
		return s_passcodeBlob.AsSpan(index * 384, 384);
	}

	public static byte[] RsaPkcs1Encrypt(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> data)
	{
		if (modulus.Length != 384)
		{
			throw new ArgumentException($"Modulus must be {384} bytes.", "modulus");
		}
		using RSA rSA = RSA.Create();
		rSA.ImportParameters(new RSAParameters
		{
			Modulus = modulus.ToArray(),
			Exponent = new byte[3] { 1, 0, 1 }
		});
		return rSA.Encrypt(data.ToArray(), RSAEncryptionPadding.Pkcs1);
	}

	public static byte[] BuildImageKeyEntry(ReadOnlySpan<byte> ekpfs, bool deterministic = false)
	{
		if (ekpfs.Length != 32)
		{
			throw new ArgumentException("EKPFS must be 32 bytes.", "ekpfs");
		}
		byte[] array = new byte[2048];
		int num;
		for (int i = 0; i < 2048; i += num)
		{
			byte[] array2 = (deterministic ? RsaPkcs1EncryptDeterministic(MountImageModulus, ekpfs) : RsaPkcs1Encrypt(MountImageModulus, ekpfs));
			num = Math.Min(384, 2048 - i);
			array2.AsSpan(0, num).CopyTo(array.AsSpan(i));
		}
		return array;
	}

	public static byte[] BuildEntryKeysEntry(string contentId, string passcode, bool deterministic = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(contentId, "contentId");
		ArgumentException.ThrowIfNullOrWhiteSpace(passcode, "passcode");
		byte[] array = new byte[2944];
		int num = 0;
		Span<byte> span = stackalloc byte[48];
		span.Clear();
		Encoding.ASCII.GetBytes(contentId, span);
		Sha3.Sha3_256(span).CopyTo(array.AsSpan(num));
		num += 32;
		for (int i = 0; i < 7; i++)
		{
			byte[] array2 = ProsperoKeys.ComputeKeys(contentId, passcode, (uint)i, useSha3: true);
			byte[] array3 = Sha3.Sha3_256(array2);
			for (int j = 0; j < 32; j++)
			{
				array3[j] ^= array2[j];
			}
			array3.CopyTo(array.AsSpan(num));
			num += 32;
		}
		for (int k = 0; k < 7; k++)
		{
			byte[] array4 = ((k == 0) ? Encoding.ASCII.GetBytes(passcode) : ProsperoKeys.ComputeKeys(contentId, passcode, (uint)k, useSha3: true));
			(deterministic ? RsaPkcs1EncryptDeterministic(GetPasscodeModulus(k), array4) : RsaPkcs1Encrypt(GetPasscodeModulus(k), array4)).CopyTo(array.AsSpan(num));
			num += 384;
		}
		return array;
	}

	public static byte[] BuildCntHeaderWrap(ReadOnlySpan<byte> cntHeader)
	{
		if (cntHeader.Length < 4096)
		{
			throw new ArgumentException($"The CNT header wrap requires at least 0x{4096:X} bytes.", "cntHeader");
		}
		byte[] array = Sha3.Sha3_256(cntHeader.Slice(0, 4096));
		return RsaPkcs1EncryptDeterministic(GetPasscodeModulus(3), array);
	}

	public static bool VerifyCntHeaderWrap(ReadOnlySpan<byte> cntHeader, ReadOnlySpan<byte> storedWrap)
	{
		if (storedWrap.Length != 384 || cntHeader.Length < 4096)
		{
			return false;
		}
		byte[] array = cntHeader.Slice(0, 4096).ToArray();
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(array.AsSpan(1040, 8));
		if (num != 0L && num != 65536)
		{
			BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(1040, 8), 65536uL);
		}
		return CryptographicOperations.FixedTimeEquals(BuildCntHeaderWrap(array), storedWrap);
	}

	public static byte[] RsaPkcs1EncryptDeterministic(ReadOnlySpan<byte> modulus, ReadOnlySpan<byte> data)
	{
		if (modulus.Length != 384)
		{
			throw new ArgumentException($"Modulus must be {384} bytes.", "modulus");
		}
		if (data.Length > modulus.Length - 11)
		{
			throw new ArgumentException("RSA PKCS#1 v1.5 input is too large for the modulus.", "data");
		}
		byte[] array = new byte[modulus.Length];
		array[1] = 2;
		int num = array.Length - data.Length - 3;
		byte[] array2 = new byte[modulus.Length + data.Length];
		modulus.CopyTo(array2);
		data.CopyTo(array2.AsSpan(modulus.Length));
		byte[] array3 = SHA256.HashData(SHA256.HashData(array2));
		uint[] array4 = new uint[array3.Length / 4];
		for (int i = 0; i < array4.Length; i++)
		{
			int num2 = i * 4;
			array4[i] = (uint)((array3[num2] << 24) | (array3[num2 + 1] << 16) | (array3[num2 + 2] << 8) | array3[num2 + 3]);
		}
		MersenneTwister mersenneTwister = new MersenneTwister(array4);
		int num3 = 2;
		int num4 = 2 + num;
		while (num3 < num4)
		{
			byte[] array5 = new byte[48];
			for (int j = 0; j < 12; j++)
			{
				uint num5 = mersenneTwister.NextUInt32();
				int num6 = j * 4;
				array5[num6] = (byte)(num5 >> 24);
				array5[num6 + 1] = (byte)(num5 >> 16);
				array5[num6 + 2] = (byte)(num5 >> 8);
				array5[num6 + 3] = (byte)num5;
			}
			byte[] array6 = SHA256.HashData(array5);
			foreach (byte b in array6)
			{
				if (b != 0)
				{
					array[num3++] = b;
					if (num3 == num4)
					{
						break;
					}
				}
			}
		}
		array[num4] = 0;
		data.CopyTo(array.AsSpan(num4 + 1));
		return RsaPublicModExp(array, modulus, 65537);
	}

	private static byte[] RsaPublicModExp(byte[] value, ReadOnlySpan<byte> modulusBytes, int exponent)
	{
		byte[] array = BigInteger.ModPow(new BigInteger(value, isUnsigned: true, isBigEndian: true), modulus: new BigInteger(modulusBytes, isUnsigned: true, isBigEndian: true), exponent: exponent).ToByteArray(isUnsigned: true, isBigEndian: true);
		if (array.Length > modulusBytes.Length)
		{
			throw new CryptographicException("RSA result exceeds the modulus size.");
		}
		byte[] array2 = new byte[modulusBytes.Length];
		array.CopyTo(array2, array2.Length - array.Length);
		return array2;
	}

	private static byte[] LoadResource(string name)
	{
		return ProsperoPkgTool.Data.Blobs.Require(name);
	}
}
