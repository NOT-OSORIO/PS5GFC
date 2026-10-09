using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ProsperoPkgTool.Containers;

namespace ProsperoPkgTool.Gp5;

public static class PlayGoProjectFactory
{
	public static PlayGoProject FromGp5(Gp5Project gp5, string contentId)
	{
		ArgumentNullException.ThrowIfNull(gp5, "gp5");
		ArgumentException.ThrowIfNullOrWhiteSpace(contentId, "contentId");
		Gp5ChunkInfo info = gp5.Volume.ChunkInfo ?? throw new InvalidDataException("GP5 has no chunk_info; PlayGo package creation requires it.");
		if (info.ChunkCount != info.Chunks.Count || info.ScenarioCount != info.Scenarios.Count)
		{
			throw new InvalidDataException("GP5 chunk_info counts do not match its chunk/scenario records.");
		}
		List<PlayGoChunk> chunks = (from c in info.Chunks
			orderby c.Id
			select new PlayGoChunk(c.Id, c.Label, ParseLanguageMask(c.Languages))).ToList();
		List<PlayGoScenario> scenarios = (from s in info.Scenarios
			orderby s.Id
			select new PlayGoScenario(s.Id, s.Label, s.InitialChunkCount, ExpandSequence(s.Sequence, info.ChunkCount, s.Id))).ToList();
		Dictionary<string, byte> dictionary = new Dictionary<string, byte>(StringComparer.Ordinal);
		foreach (Gp5FileEntry item in EnumerateFiles(gp5))
		{
			if (item.Chunk.HasValue)
			{
				int value = item.Chunk.Value;
				if ((value < 0 || value > 255) ? true : false)
				{
					throw ProsperoErrorInfo.Unsupported($"GP5 file '{item.DestinationPath}' has an unsupported chunk ID {item.Chunk}.");
				}
				string text = NormalizePath(item.DestinationPath);
				if (!dictionary.TryAdd(text, (byte)item.Chunk.Value))
				{
					throw new InvalidDataException("GP5 assigns PlayGo chunk more than once to '" + text + "'.");
				}
			}
		}
		return new PlayGoProject
		{
			VersionMajor = 4096,
			ContentId = contentId,
			DefaultScenarioId = info.DefaultScenarioId,
			Chunks = chunks,
			Scenarios = scenarios,
			FileChunkAssignments = dictionary
		};
	}

	private static IReadOnlyList<int> ExpandSequence(string value, int count, int scenarioId)
	{
		if (!Gp5Validator.TryExpandSequence(value, count, out IReadOnlyList<int> chunks, out string error))
		{
			throw new InvalidDataException($"GP5 scenario {scenarioId}: {error}");
		}
		return chunks;
	}

	private static IEnumerable<Gp5FileEntry> EnumerateFiles(Gp5Project project)
	{
		if (project.Layout != Gp5ContentLayout.FlatFiles)
		{
			return Flatten(project.RootDirectory).OfType<Gp5FileEntry>();
		}
		return project.Files;
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

	private static string NormalizePath(string path)
	{
		try
		{
			return Gp5Path.NormalizeDestination(path).Replace('\\', '/').ToUpperInvariant();
		}
		catch (ArgumentException ex)
		{
			throw new InvalidDataException(ex.Message, ex);
		}
	}

	private static ulong ParseLanguageMask(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return ulong.MaxValue;
		}
		string text = value.Trim();
		NumberStyles numberStyles = (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.AllowHexSpecifier : NumberStyles.None);
		if (numberStyles != NumberStyles.None)
		{
			text = text.Substring(2);
		}
		if (!ulong.TryParse(text, numberStyles, CultureInfo.InvariantCulture, out var result))
		{
			throw new InvalidDataException("GP5 chunk languages must be a decimal or 0x-prefixed uint64 mask in the clean-room PlayGo workflow.");
		}
		return result;
	}
}
