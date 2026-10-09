using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace ProsperoPkgTool.Content;

public static class ProsperoSdkVersions
{
	public readonly record struct Release(int Major, string Version, ulong ExecutableVersion)
	{
		public ulong PackageVersion => ToPackageVersion(ExecutableVersion);
	}

	private static readonly Release[] KnownReleases = new Release[11]
	{
		new Release(1, "1.00.00.50", 72057937635311617uL),
		new Release(2, "2.00.00.26", 144115351284613121uL),
		new Release(3, "3.00.00.27", 216172949617508881uL),
		new Release(4, "4.00.00.31", 288230586605109777uL),
		new Release(5, "5.00.00.33", 360288189232971777uL),
		new Release(6, "6.00.00.38", 432345804745736193uL),
		new Release(7, "7.00.00.38", 504403398783664913uL),
		new Release(8, "8.00.00.41", 576461031476297729uL),
		new Release(9, "9.00.00.40", 648518621219259409uL),
		new Release(10, "10.00.00.40", 1152921779484753921uL),
		new Release(11, "11.00.00.40", 1224979373522681857uL)
	};

	public static IReadOnlyList<Release> Releases { get; } = Array.AsReadOnly(KnownReleases);

	public static bool TryGetByMajor(int major, out Release release)
	{
		Release[] knownReleases = KnownReleases;
		for (int i = 0; i < knownReleases.Length; i++)
		{
			Release release2 = knownReleases[i];
			if (release2.Major == major)
			{
				release = release2;
				return true;
			}
		}
		release = default;
		return false;
	}

	public static Release GetByMajor(int major)
	{
		if (!TryGetByMajor(major, out var release))
		{
			throw new ArgumentOutOfRangeException("major", major, "Unknown Prospero SDK major version.");
		}
		return release;
	}

	public static ulong ToPackageVersion(ulong executableVersion)
	{
		return executableVersion & 0xFFFF000000000000uL;
	}

	public static byte[] BuildTuple(ulong executableVersion)
	{
		byte[] array = new byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(array, executableVersion);
		return array;
	}

	public static bool TryParse(string? text, out ulong executableVersion)
	{
		executableVersion = 0uL;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		text = text.Trim();
		if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			return ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out executableVersion);
		}
		if (!int.TryParse(text.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result <= 0)
		{
			return false;
		}
		if (!TryGetByMajor(result, out var release))
		{
			return false;
		}
		executableVersion = release.ExecutableVersion;
		return true;
	}
}
