using System;

namespace ProsperoPkgTool.Containers;

public static class ProsperoCntEntryPolicy
{
	private const uint Encrypted = 2147483648u;

	private const uint PublisherData = 134217728u;

	public static ProsperoCntEntryProfile Resolve(uint id, string? relativeName = null, string? applicationDrmType = null)
	{
		switch (id)
		{
		case 1u:
			return new ProsperoCntEntryProfile(1073741824u, 0u, IncludeName: false);
		case 16u:
		case 32u:
		case 128u:
		case 256u:
			return new ProsperoCntEntryProfile(1610612736u, 0u, IncludeName: false);
		case 512u:
			return new ProsperoCntEntryProfile(1073741824u, 0u, IncludeName: false);
		case 1024u:
			return Protected(3u, includeName: false);
		case 1025u:
			return Protected(IsUpgradable(applicationDrmType) ? 4u : 2u, includeName: false);
		case 1026u:
		case 1027u:
		case 1028u:
		case 1030u:
		case 1031u:
		case 1032u:
		case 1033u:
			return Protected(3u, includeName: false);
		case 8192u:
			return new ProsperoCntEntryProfile(0u, 0u, IncludeName: true);
		default:
			if (IsNestedNpbind(relativeName))
			{
				return Protected(3u, includeName: true);
			}
			return new ProsperoCntEntryProfile(134217728u, 0u, IncludeName: true);
		}
	}

	private static ProsperoCntEntryProfile Protected(uint keyIndex, bool includeName)
	{
		return new ProsperoCntEntryProfile(2147483648u, keyIndex << 12, includeName);
	}

	private static bool IsNestedNpbind(string? relativeName)
	{
		if (relativeName != null)
		{
			if (!relativeName.Equals("npbind.dat", StringComparison.OrdinalIgnoreCase))
			{
				return relativeName.EndsWith("/npbind.dat", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private static bool IsUpgradable(string? applicationDrmType)
	{
		return applicationDrmType?.Equals("upgradable", StringComparison.OrdinalIgnoreCase) ?? false;
	}
}
