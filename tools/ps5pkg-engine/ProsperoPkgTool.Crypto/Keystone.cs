using System;
using System.Security.Cryptography;
using System.Text;

namespace ProsperoPkgTool.Crypto;

public static class Keystone
{
	public const int Size = 96;

	private static readonly byte[] FingerprintKeyV3 = ProsperoPkgTool.Data.Blobs.Require("ks_fp3.bin");

	private static readonly byte[] MacKeyV3 = ProsperoPkgTool.Data.Blobs.Require("ks_mac3.bin");

	private static readonly byte[] FingerprintKeyV2 = ProsperoPkgTool.Data.Blobs.Require("ks_fp2.bin");

	private static readonly byte[] MacKeyV2 = ProsperoPkgTool.Data.Blobs.Require("ks_mac2.bin");

	public static int GetVersion(ReadOnlySpan<byte> keystone)
	{
		if (keystone.Length < 96)
		{
			return 0;
		}
		if (!keystone.Slice(0, 8).SequenceEqual("keystone"u8))
		{
			return 0;
		}
		return keystone[8] | (keystone[9] << 8);
	}

	public static byte[] ComputeFingerprint(string passcode, int version)
	{
		return HMACSHA256.HashData((version >= 3) ? FingerprintKeyV3 : FingerprintKeyV2, Encoding.ASCII.GetBytes(passcode));
	}

	public static byte[] ComputeMac(ReadOnlySpan<byte> keystone, int version)
	{
		if (keystone.Length < 64)
		{
			throw new ArgumentException("Keystone must be at least 0x40 bytes to MAC.", "keystone");
		}
		return HMACSHA256.HashData((version >= 3) ? MacKeyV3 : MacKeyV2, keystone.Slice(0, 64).ToArray());
	}

	public static bool Validate(ReadOnlySpan<byte> keystone, string passcode)
	{
		int version = GetVersion(keystone);
		if (version == 0 || keystone.Length < 96)
		{
			return false;
		}
		byte[] array = ComputeFingerprint(passcode, version);
		if (!keystone.Slice(32, 32).SequenceEqual(array))
		{
			return false;
		}
		byte[] array2 = ComputeMac(keystone, version);
		return keystone.Slice(64, 32).SequenceEqual(array2);
	}

	public static byte[] Create(string passcode)
	{
		byte[] array = new byte[96];
		"keystone"u8.CopyTo(array.AsSpan(0));
		array[8] = 3;
		array[9] = 0;
		ComputeFingerprint(passcode, 3).CopyTo(array, 32);
		ComputeMac(array, 3).CopyTo(array, 64);
		return array;
	}
}
