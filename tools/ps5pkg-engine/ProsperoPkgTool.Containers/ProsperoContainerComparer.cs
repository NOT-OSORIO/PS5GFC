using System;
using System.Collections.Generic;
using System.Linq;

namespace ProsperoPkgTool.Containers;

public static class ProsperoContainerComparer
{
	private const long InnerPfsMemoryLimit = 536870912L;

	private static readonly string[] SegmentNames = new string[5] { "LIH", "FIH", "PFS", "SC", "SI" };

	public static IReadOnlyList<string> Compare(string referencePath, string candidatePath)
	{
		ProsperoPackageInspection reference = ProsperoPackageReader.Read(referencePath);
		ProsperoPackageInspection candidate = ProsperoPackageReader.Read(candidatePath);
		List<string> list = new List<string>();
		CompareStructure(reference, candidate, list);
		CompareInnerPfs(reference, referencePath, candidate, candidatePath, list);
		return list;
	}

	private static void CompareStructure(ProsperoPackageInspection reference, ProsperoPackageInspection candidate, List<string> differences)
	{
		if (reference.Kind != candidate.Kind)
		{
			differences.Add($"Type: {reference.Kind} != {candidate.Kind}");
		}
		if (reference.Cnt.EntryCount != candidate.Cnt.EntryCount)
		{
			differences.Add($"EntryCount: {reference.Cnt.EntryCount} != {candidate.Cnt.EntryCount}");
		}
		if (reference.Cnt.EntryTableOffset != candidate.Cnt.EntryTableOffset)
		{
			differences.Add($"EntryTableOffset: {reference.Cnt.EntryTableOffset:X} != {candidate.Cnt.EntryTableOffset:X}");
		}
		if (!string.Equals(reference.Cnt.ContentId, candidate.Cnt.ContentId, StringComparison.Ordinal))
		{
			differences.Add("ContentId: " + reference.Cnt.ContentId + " != " + candidate.Cnt.ContentId);
		}
		if (reference.Cnt.ContentType != candidate.Cnt.ContentType)
		{
			differences.Add($"ContentType: {reference.Cnt.ContentType} != {candidate.Cnt.ContentType}");
		}
		int num = Math.Min(reference.Entries.Count, candidate.Entries.Count);
		for (int i = 0; i < num; i++)
		{
			ProsperoCntEntry prosperoCntEntry = reference.Entries[i];
			ProsperoCntEntry prosperoCntEntry2 = candidate.Entries[i];
			if (prosperoCntEntry.Id != prosperoCntEntry2.Id || prosperoCntEntry.DataSize != prosperoCntEntry2.DataSize || prosperoCntEntry.Flags1 != prosperoCntEntry2.Flags1)
			{
				differences.Add($"Entry[{i}] {prosperoCntEntry.DisplayName}/{prosperoCntEntry2.DisplayName}: id={prosperoCntEntry.Id:X}/{prosperoCntEntry2.Id:X} size={prosperoCntEntry.DataSize}/{prosperoCntEntry2.DataSize} flags={prosperoCntEntry.Flags1:X}/{prosperoCntEntry2.Flags1:X}");
			}
		}
		if (reference.Entries.Count != candidate.Entries.Count)
		{
			differences.Add($"Entry count differs: {reference.Entries.Count} vs {candidate.Entries.Count}");
		}
		CompareFih(reference, candidate, differences);
		CompareSegments(reference, candidate, differences);
	}

	private static void CompareFih(ProsperoPackageInspection reference, ProsperoPackageInspection candidate, List<string> differences)
	{
		ProsperoFihHeader fih = reference.Fih;
		if ((object)fih != null)
		{
			ProsperoFihHeader fih2 = candidate.Fih;
			if ((object)fih2 != null)
			{
				if (fih.FormatVersion != fih2.FormatVersion)
				{
					differences.Add($"FIH.FormatVersion: {fih.FormatVersion} != {fih2.FormatVersion}");
				}
				if (fih.SignedByte != fih2.SignedByte)
				{
					differences.Add($"FIH.SignedByte: 0x{fih.SignedByte:X2} != 0x{fih2.SignedByte:X2}");
				}
				if (fih.PfsOffset != fih2.PfsOffset)
				{
					differences.Add($"FIH.PfsOffset: 0x{fih.PfsOffset:X} != 0x{fih2.PfsOffset:X}");
				}
				if (fih.PfsSize != fih2.PfsSize)
				{
					differences.Add($"FIH.PfsSize: 0x{fih.PfsSize:X} != 0x{fih2.PfsSize:X}");
				}
				if (fih.CntOffset != fih2.CntOffset)
				{
					differences.Add($"FIH.CntOffset: 0x{fih.CntOffset:X} != 0x{fih2.CntOffset:X}");
				}
				return;
			}
		}
		if ((object)reference.Fih == null != ((object)candidate.Fih == null))
		{
			differences.Add("FIH: " + (((object)reference.Fih == null) ? "absent" : "present") + " != " + (((object)candidate.Fih == null) ? "absent" : "present"));
		}
	}

	private static void CompareSegments(ProsperoPackageInspection reference, ProsperoPackageInspection candidate, List<string> differences)
	{
		Dictionary<string, ProsperoPackageSegment> dictionary = reference.Segments.ToDictionary((ProsperoPackageSegment s) => s.Name, (ProsperoPackageSegment s) => s, StringComparer.Ordinal);
		Dictionary<string, ProsperoPackageSegment> dictionary2 = candidate.Segments.ToDictionary((ProsperoPackageSegment s) => s.Name, (ProsperoPackageSegment s) => s, StringComparer.Ordinal);
		string[] segmentNames = SegmentNames;
		foreach (string text in segmentNames)
		{
			bool flag = dictionary.TryGetValue(text, out var value);
			bool flag2 = dictionary2.TryGetValue(text, out var value2);
			if (flag && !flag2)
			{
				differences.Add($"Segment {text}: present in reference (0x{value.Offset:X}, 0x{value.Size:X}) but absent in candidate");
			}
			else if (!flag & flag2)
			{
				differences.Add($"Segment {text}: absent in reference but present in candidate (0x{value2.Offset:X}, 0x{value2.Size:X})");
			}
			else if (flag && flag2)
			{
				if (value.Offset != value2.Offset)
				{
					differences.Add($"Segment {text}.Offset: 0x{value.Offset:X} != 0x{value2.Offset:X}");
				}
				if (value.Size != value2.Size)
				{
					differences.Add($"Segment {text}.Size: 0x{value.Size:X} != 0x{value2.Size:X}");
				}
			}
		}
	}

	private static void CompareInnerPfs(ProsperoPackageInspection reference, string referencePath, ProsperoPackageInspection candidate, string candidatePath, List<string> differences)
	{
		if (!TryReadInnerPfs(reference, referencePath, out IReadOnlyList<ProsperoInnerPfsReader.Entry> entries) || !TryReadInnerPfs(candidate, candidatePath, out IReadOnlyList<ProsperoInnerPfsReader.Entry> entries2))
		{
			return;
		}
		Dictionary<string, (bool, long)> dictionary = entries.ToDictionary((ProsperoInnerPfsReader.Entry e) => e.Path, (ProsperoInnerPfsReader.Entry e) => (IsDirectory: e.IsDirectory, Size: e.Size), StringComparer.Ordinal);
		Dictionary<string, (bool, long)> dictionary2 = entries2.ToDictionary((ProsperoInnerPfsReader.Entry e) => e.Path, (ProsperoInnerPfsReader.Entry e) => (IsDirectory: e.IsDirectory, Size: e.Size), StringComparer.Ordinal);
		foreach (string item in dictionary.Keys.Union(dictionary2.Keys, StringComparer.Ordinal).OrderBy((string p) => p, StringComparer.Ordinal))
		{
			bool flag = dictionary.TryGetValue(item, out var value);
			bool flag2 = dictionary2.TryGetValue(item, out var value2);
			if (flag && !flag2)
			{
				differences.Add("InnerPfs missing in candidate: " + item + SizeSuffix(value));
			}
			else if (!flag & flag2)
			{
				differences.Add("InnerPfs extra in candidate: " + item + SizeSuffix(value2));
			}
			else if (value.Item1 != value2.Item1)
			{
				differences.Add($"InnerPfs type {item}: {(value.Item1 ? "dir" : "file")} != {(value2.Item1 ? "dir" : "file")}");
			}
			else if (value.Item2 != value2.Item2)
			{
				differences.Add($"InnerPfs size {item}: {value.Item2} != {value2.Item2}");
			}
		}
	}

	private static string SizeSuffix((bool IsDirectory, long Size) entry)
	{
		if (!entry.IsDirectory)
		{
			return $" ({entry.Size})";
		}
		return string.Empty;
	}

	private static bool TryReadInnerPfs(ProsperoPackageInspection inspection, string path, out IReadOnlyList<ProsperoInnerPfsReader.Entry> entries)
	{
		entries = Array.Empty<ProsperoInnerPfsReader.Entry>();
		if (inspection.Kind != ProsperoPackageKind.FinalizedDebug)
		{
			return false;
		}
		ProsperoFihHeader fih = inspection.Fih;
		if ((object)fih == null || fih.PfsSize > 536870912)
		{
			return false;
		}
		try
		{
			ProsperoDecodedPackage prosperoDecodedPackage = ProsperoPackageContent.Read(path);
			entries = ProsperoInnerPfsReader.Enumerate(prosperoDecodedPackage.Mount);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
