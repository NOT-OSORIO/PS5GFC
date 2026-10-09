using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ProsperoPkgTool.Containers;

public static class DiskSpaceGuard
{
	private const long Megabyte = 1048576L;

	private const int SafetyPercent = 12;

	private const int WarnPercent = 25;

	private const long HeadroomBytes = 536870912L;

	public static IReadOnlyList<SpaceRequirement> Estimate(long rawPayloadBytes, string outputPath, string? tempDirectory)
	{
		if (rawPayloadBytes < 0)
		{
			rawPayloadBytes = 0L;
		}
		long num = rawPayloadBytes + Math.Max(16777216L, rawPayloadBytes / 100);
		long num2 = AlignUp(num, 65536L) + 33554432;
		long num3 = 65536 + num2 + 67108864;
		long num4 = num + 67108864;
		string text = RootOf(tempDirectory ?? Path.GetTempPath());
		string text2 = RootOf(outputPath);
		if (string.Equals(text, text2, StringComparison.OrdinalIgnoreCase))
		{
			return new _003C_003Ez__ReadOnlySingleElementList<SpaceRequirement>(new SpaceRequirement(text, num4 + num3, "temp workspace + output"));
		}
		return new _003C_003Ez__ReadOnlyArray<SpaceRequirement>(new SpaceRequirement[2]
		{
			new SpaceRequirement(text, num4, "temp workspace"),
			new SpaceRequirement(text2, num3, "output package")
		});
	}

	public static DiskSpaceReport Check(IReadOnlyList<SpaceRequirement> requirements, Func<string, long>? freeSpaceProbe = null)
	{
		ArgumentNullException.ThrowIfNull(requirements, "requirements");
		DiskSpaceReport diskSpaceReport = new DiskSpaceReport(DiskSpaceStatus.Ok, 0L, long.MaxValue, string.Empty, string.Empty);
		foreach (SpaceRequirement requirement in requirements)
		{
			long num = WithMargin(requirement.Bytes, 12);
			long num2 = (freeSpaceProbe ?? new Func<string, long>(AvailableFreeSpace))(requirement.Root);
			DiskSpaceStatus diskSpaceStatus = ((num2 < num) ? DiskSpaceStatus.Insufficient : ((num2 < WithMargin(requirement.Bytes, 25)) ? DiskSpaceStatus.NearLimit : DiskSpaceStatus.Ok));
			if (diskSpaceStatus > diskSpaceReport.Status)
			{
				diskSpaceReport = new DiskSpaceReport(diskSpaceStatus, num, num2, requirement.Root, requirement.What);
			}
		}
		return diskSpaceReport;
	}

	public static string Describe(DiskSpaceReport report)
	{
		return report.Status switch
		{
			DiskSpaceStatus.Insufficient => $"not enough free space: need ~{Gigabytes(report.RequiredBytes)} GB on {report.Root} ({report.What}), have {Gigabytes(report.AvailableBytes)} GB free",
			DiskSpaceStatus.NearLimit => $"low free space on {report.Root} ({report.What}): need ~{Gigabytes(report.RequiredBytes)} GB, have {Gigabytes(report.AvailableBytes)} GB free",
			_ => $"free space OK on {report.Root} ({report.What}): have {Gigabytes(report.AvailableBytes)} GB",
		};
	}

	private static long WithMargin(long bytes, int percent)
	{
		try
		{
			return checked((long)Math.Ceiling((double)bytes * (1.0 + (double)percent / 100.0)) + 536870912);
		}
		catch (OverflowException)
		{
			return long.MaxValue;
		}
	}

	private static long AvailableFreeSpace(string root)
	{
		try
		{
			return new DriveInfo(root).AvailableFreeSpace;
		}
		catch (ArgumentException)
		{
			return long.MaxValue;
		}
		catch (IOException)
		{
			return long.MaxValue;
		}
		catch (UnauthorizedAccessException)
		{
			return long.MaxValue;
		}
	}

	private static string RootOf(string path)
	{
		try
		{
			return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
		}
		catch (ArgumentException)
		{
			return path;
		}
		catch (NotSupportedException)
		{
			return path;
		}
		catch (PathTooLongException)
		{
			return path;
		}
	}

	private static long AlignUp(long value, long block)
	{
		if (value > 0)
		{
			return (value + block - 1) / block * block;
		}
		return block;
	}

	private static string Gigabytes(long bytes)
	{
		return ((double)bytes / 1073741824.0).ToString("N1", CultureInfo.InvariantCulture);
	}
}
