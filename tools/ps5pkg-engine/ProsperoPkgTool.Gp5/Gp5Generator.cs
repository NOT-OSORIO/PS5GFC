using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ProsperoPkgTool.Gp5;

public static class Gp5Generator
{
	public static Gp5GenerationResult Generate(string sourceDirectory, Gp5GeneratorOptions? options = null, string? intendedProjectPath = null)
	{
		if (options == null)
		{
			options = new Gp5GeneratorOptions();
		}
		SourceFolderInspection sourceFolderInspection = SourceFolderValidator.Inspect(sourceDirectory);
		if ((object)sourceFolderInspection.Param == null)
		{
			throw new InvalidDataException("A valid sce_sys/param.json is required before GP5 generation.");
		}
		ProsperoParam param = sourceFolderInspection.Param;
		Gp5Project gp5Project = new Gp5Project
		{
			Format = "gp5",
			Version = "1000",
			Layout = Gp5ContentLayout.FlatFiles,
			Volume = new Gp5Volume
			{
				VolumeType = param.VolumeType,
				Package = new Gp5Package
				{
					ContentId = param.ContentId,
					Passcode = options.Passcode,
					CreationDate = param.CreationDate
				}
			}
		};
		if (param.ApplicationCategoryType == 0)
		{
			gp5Project.Volume.ChunkInfo = BuildChunkInfo(sourceFolderInspection.PlayGo);
		}
		foreach (string includedFile in sourceFolderInspection.IncludedFiles)
		{
			gp5Project.Files.Add(new Gp5FileEntry
			{
				DestinationPath = Gp5Path.ToProjectRelative(includedFile),
				SourcePath = Gp5Path.ToProjectRelative(includedFile)
			});
		}
		string projectPath = intendedProjectPath ?? Path.Combine(sourceFolderInspection.RootPath, param.TitleId + ".gp5");
		ValidationReport validationReport = Gp5Validator.Validate(gp5Project, projectPath);
		ValidationReport validationReport2 = new ValidationReport();
		validationReport2.AddRange(sourceFolderInspection.Validation.Issues);
		validationReport2.AddRange(validationReport.Issues);
		return new Gp5GenerationResult(gp5Project, sourceFolderInspection, validationReport2);
	}

	private static Gp5ChunkInfo BuildChunkInfo(PlayGoProject? playGo)
	{
		if (playGo == null)
		{
			return new Gp5ChunkInfo
			{
				ChunkCount = 1,
				ScenarioCount = 1,
				DefaultScenarioId = 0,
				Chunks =
				{
					new Gp5Chunk
					{
						Id = 0,
						Label = "Chunk #0"
					}
				},
				Scenarios =
				{
					new Gp5Scenario
					{
						Id = 0,
						InitialChunkCount = 1,
						Label = "Scenario #0",
						Type = "playmode",
						Sequence = "0"
					}
				}
			};
		}
		Gp5ChunkInfo gp5ChunkInfo = new Gp5ChunkInfo
		{
			ChunkCount = playGo.Chunks.Count,
			ScenarioCount = playGo.Scenarios.Count,
			DefaultScenarioId = playGo.DefaultScenarioId
		};
		foreach (PlayGoChunk chunk in playGo.Chunks)
		{
			gp5ChunkInfo.Chunks.Add(new Gp5Chunk
			{
				Id = chunk.Id,
				Label = chunk.Label
			});
		}
		foreach (PlayGoScenario scenario in playGo.Scenarios)
		{
			gp5ChunkInfo.Scenarios.Add(new Gp5Scenario
			{
				Id = scenario.Id,
				InitialChunkCount = scenario.InitialChunkCount,
				Label = scenario.Label,
				Type = "playmode",
				Sequence = FormatSequence(scenario.Chunks)
			});
		}
		return gp5ChunkInfo;
	}

	internal static string FormatSequence(IReadOnlyList<int> chunks)
	{
		List<string> list = new List<string>();
		int num = 0;
		while (num < chunks.Count)
		{
			int num2 = chunks[num];
			int num3 = num2;
			int i;
			for (i = num + 1; i < chunks.Count && chunks[i] == num3 + 1; i++)
			{
				num3 = chunks[i];
			}
			list.Add((num3 > num2) ? $"{num2}-{num3}" : num2.ToString(CultureInfo.InvariantCulture));
			num = i;
		}
		return string.Join(' ', list);
	}
}
