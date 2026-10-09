using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProsperoPkgTool.Gp5;

public static class Gp5Validator
{
	public static ValidationReport Validate(Gp5Project project, string? projectPath = null)
	{
		ArgumentNullException.ThrowIfNull(project, "project");
		ValidationReport validationReport = new ValidationReport();
		if (!project.Format.Equals("gp5", StringComparison.Ordinal))
		{
			validationReport.Error("GP5_FORMAT", "Project fmt must be 'gp5'.");
		}
		if (project.Version != null && project.Version != "1000")
		{
			validationReport.Warning("GP5_VERSION", "Unrecognized GP5 project version '" + project.Version + "'.");
		}
		ValidateVolume(project.Volume, validationReport);
		ValidateContents(project, projectPath, validationReport);
		return validationReport;
	}

	private static void ValidateVolume(Gp5Volume volume, ValidationReport report)
	{
		string volumeType = volume.VolumeType;
		if (!(volumeType == "prospero_app") && !(volumeType == "prospero_ac"))
		{
			report.Error("VOLUME_TYPE", "Volume type must be prospero_app or prospero_ac.");
		}
		Gp5Package package = volume.Package;
		if (package.Passcode.Length != 32 || package.Passcode.Any((char character) => (character < ' ' || character > '~') ? true : false))
		{
			report.Error("PASSCODE", "Package passcode must contain exactly 32 printable ASCII characters.");
		}
		if (!string.IsNullOrWhiteSpace(package.ContentId) && !ContentIdRegex().IsMatch(package.ContentId))
		{
			report.Error("CONTENT_ID", "Package content_id must use the 36-character Sony content ID format.");
		}
		if (!string.IsNullOrWhiteSpace(package.EntitlementKey) && !Hex32Regex().IsMatch(package.EntitlementKey))
		{
			report.Error("ENTITLEMENT_KEY", "entitlement_key must contain exactly 32 hexadecimal characters.");
		}
		if (!string.IsNullOrWhiteSpace(package.CreationDate) && !ValidDate(package.CreationDate))
		{
			report.Error("CREATION_DATE", "c_date must be YYYY-MM-DD or YYYY-MM-DD HH:mm:ss.");
		}
		if (volume.VolumeType == "prospero_app")
		{
			if (volume.ChunkInfo == null)
			{
				report.Error("CHUNK_INFO", "Application projects require chunk_info.");
			}
			else
			{
				ValidateChunkInfo(volume.ChunkInfo, report);
			}
		}
		else if (volume.ChunkInfo != null)
		{
			report.Warning("AC_CHUNK_INFO", "Additional-content projects normally omit chunk_info.");
			ValidateChunkInfo(volume.ChunkInfo, report);
		}
	}

	private static void ValidateChunkInfo(Gp5ChunkInfo info, ValidationReport report)
	{
		if (info.ChunkCount != info.Chunks.Count)
		{
			report.Error("CHUNK_COUNT", $"chunk_count is {info.ChunkCount}, but {info.Chunks.Count} chunks are defined.");
		}
		if (info.ScenarioCount != info.Scenarios.Count)
		{
			report.Error("SCENARIO_COUNT", $"scenario_count is {info.ScenarioCount}, but {info.Scenarios.Count} scenarios are defined.");
		}
		int chunkCount = info.ChunkCount;
		if ((chunkCount < 1 || chunkCount > 1000) ? true : false)
		{
			report.Error("CHUNK_RANGE", "Application chunk count must be between 1 and 1000.");
		}
		chunkCount = info.ScenarioCount;
		if ((chunkCount < 1 || chunkCount > 32) ? true : false)
		{
			report.Error("SCENARIO_RANGE", "Application scenario count must be between 1 and 32.");
		}
		int[] array = info.Chunks.Select((Gp5Chunk chunk) => chunk.Id).ToArray();
		if (array.Distinct().Count() != array.Length)
		{
			report.Error("CHUNK_ID_DUPLICATE", "Chunk IDs must be unique.");
		}
		chunkCount = info.ChunkCount;
		if (chunkCount >= 0 && chunkCount <= 1000 && !IsContiguousFromZero(array, info.ChunkCount))
		{
			report.Error("CHUNK_ID_SEQUENCE", "Chunk IDs must be contiguous from zero.");
		}
		int[] array2 = info.Scenarios.Select((Gp5Scenario scenario) => scenario.Id).ToArray();
		if (array2.Distinct().Count() != array2.Length)
		{
			report.Error("SCENARIO_ID_DUPLICATE", "Scenario IDs must be unique.");
		}
		chunkCount = info.ScenarioCount;
		if (chunkCount >= 0 && chunkCount <= 32 && !IsContiguousFromZero(array2, info.ScenarioCount))
		{
			report.Error("SCENARIO_ID_SEQUENCE", "Scenario IDs must be contiguous from zero.");
		}
		if (!array2.Contains(info.DefaultScenarioId))
		{
			report.Error("DEFAULT_SCENARIO", "default_id does not identify a defined scenario.");
		}
		if (!string.IsNullOrWhiteSpace(info.DefaultLanguage) && (string.IsNullOrWhiteSpace(info.SupportedLanguages) || !SplitWords(info.SupportedLanguages).Contains(info.DefaultLanguage, StringComparer.Ordinal)))
		{
			report.Error("DEFAULT_LANGUAGE", "default_language must be present in supported_languages.");
		}
		foreach (Gp5Scenario scenario in info.Scenarios)
		{
			if (!scenario.Type.Equals("playmode", StringComparison.Ordinal))
			{
				report.Error("SCENARIO_TYPE", $"Scenario {scenario.Id} type must be playmode.");
			}
			chunkCount = info.ChunkCount;
			if ((chunkCount >= 1 && chunkCount <= 1000) || 1 == 0)
			{
				if (!TryExpandSequence(scenario.Sequence, info.ChunkCount, out IReadOnlyList<int> chunks, out string error))
				{
					report.Error("SCENARIO_SEQUENCE", $"Scenario {scenario.Id}: {error}");
				}
				else if (scenario.InitialChunkCount < 0 || scenario.InitialChunkCount > chunks.Count)
				{
					report.Error("INITIAL_CHUNKS", $"Scenario {scenario.Id} initial_chunk_count exceeds its sequence length.");
				}
			}
		}
	}

	private static void ValidateContents(Gp5Project project, string? projectPath, ValidationReport report)
	{
		HashSet<string> destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (project.Layout == Gp5ContentLayout.FlatFiles)
		{
			if (project.Files.Count == 0)
			{
				report.Warning("EMPTY_PROJECT", "The GP5 project contains no files.");
			}
			{
				foreach (Gp5FileEntry file in project.Files)
				{
					ValidateEntry(file, isRoot: false, project, projectPath, destinations, report);
				}
				return;
			}
		}
		ValidateEntry(project.RootDirectory, isRoot: true, project, projectPath, destinations, report);
		foreach (Gp5ContentEntry item in Flatten(project.RootDirectory))
		{
			ValidateEntry(item, isRoot: false, project, projectPath, destinations, report);
		}
	}

	private static void ValidateEntry(Gp5ContentEntry entry, bool isRoot, Gp5Project project, string? projectPath, ISet<string> destinations, ValidationReport report)
	{
		if (!isRoot)
		{
			try
			{
				string text = Gp5Path.NormalizeDestination(entry.DestinationPath);
				if (!destinations.Add(text))
				{
					report.Error("DUPLICATE_DESTINATION", "Duplicate destination path '" + text + "'.", text);
				}
			}
			catch (ArgumentException ex)
			{
				report.Error("DESTINATION_PATH", ex.Message, entry.DestinationPath);
			}
		}
		if (entry.Chunk.HasValue && (project.Volume.ChunkInfo == null || entry.Chunk < 0 || entry.Chunk >= project.Volume.ChunkInfo.ChunkCount))
		{
			report.Error("ENTRY_CHUNK", $"Entry chunk {entry.Chunk} is not defined.", entry.DestinationPath);
		}
		bool num = entry is Gp5FileEntry || (entry is Gp5DirectoryEntry { Virtual: var flag } && flag.HasValue && flag != true);
		string text2 = entry.SourcePath;
		if (num && string.IsNullOrWhiteSpace(text2))
		{
			text2 = entry.DestinationPath;
		}
		if (projectPath == null || string.IsNullOrWhiteSpace(text2))
		{
			return;
		}
		try
		{
			string text3 = Gp5Path.ResolveSource(projectPath, text2);
			if (!Gp5Path.IsWithin(Path.GetDirectoryName(Path.GetFullPath(projectPath)), text3))
			{
				report.Warning("EXTERNAL_SOURCE", "Source path is outside the GP5 project directory.", text2);
			}
			if (!((entry is Gp5FileEntry) ? File.Exists(text3) : Directory.Exists(text3)))
			{
				report.Error("SOURCE_MISSING", "Source path does not exist.", text2);
			}
		}
		catch (Exception ex2) when ((ex2 is ArgumentException || ex2 is NotSupportedException || ex2 is PathTooLongException) ? true : false)
		{
			report.Error("SOURCE_PATH", ex2.Message, text2);
		}
	}

	public static bool TryExpandSequence(string sequence, int chunkCount, out IReadOnlyList<int> chunks, out string? error)
	{
		List<int> list = new List<int>();
		HashSet<int> hashSet = new HashSet<int>();
		string[] array = SplitWords(sequence);
		foreach (string text in array)
		{
			string[] array2 = text.Split('-');
			int num = array2.Length;
			bool flag = ((num < 1 || num > 2) ? true : false);
			if (flag || !int.TryParse(array2[0], out var result) || (array2.Length == 2 && !int.TryParse(array2[1], out var _)))
			{
				chunks = Array.Empty<int>();
				error = "Invalid chunk token '" + text + "'.";
				return false;
			}
			int num2 = ((array2.Length == 2) ? int.Parse(array2[1], CultureInfo.InvariantCulture) : result);
			if (result < 0 || num2 < result || num2 >= chunkCount)
			{
				chunks = Array.Empty<int>();
				error = $"Chunk token '{text}' is outside 0..{chunkCount - 1}.";
				return false;
			}
			for (int j = result; j <= num2; j++)
			{
				if (!hashSet.Add(j))
				{
					chunks = Array.Empty<int>();
					error = $"Chunk {j} appears more than once.";
					return false;
				}
				list.Add(j);
			}
		}
		chunks = list;
		error = ((list.Count == 0) ? "Scenario sequence is empty." : null);
		return list.Count > 0;
	}

	private static IEnumerable<Gp5ContentEntry> Flatten(Gp5DirectoryEntry directory)
	{
		foreach (Gp5ContentEntry child in directory.Children)
		{
			yield return child;
			if (!(child is Gp5DirectoryEntry directory2))
			{
				continue;
			}
			foreach (Gp5ContentEntry item in Flatten(directory2))
			{
				yield return item;
			}
		}
	}

	private static string[] SplitWords(string value)
	{
		return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static bool IsContiguousFromZero(IEnumerable<int> values, int expectedCount)
	{
		int num = 0;
		foreach (int item in values.Order())
		{
			if (item != num++)
			{
				return false;
			}
		}
		return num == expectedCount;
	}

	private static bool ValidDate(string value)
	{
		DateTime result;
		return DateTime.TryParseExact(value, new string[2] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
	}

	private static readonly Regex _ContentIdRegex = new Regex("^[A-Z]{2}[0-9]{4}-[A-Z0-9]{9}_[0-9]{2}-[A-Z0-9]{16}$", RegexOptions.CultureInvariant);

	private static Regex ContentIdRegex() => _ContentIdRegex;

	private static readonly Regex _Hex32Regex = new Regex("^[0-9A-Fa-f]{32}$", RegexOptions.CultureInvariant);

	private static Regex Hex32Regex() => _Hex32Regex;
}
