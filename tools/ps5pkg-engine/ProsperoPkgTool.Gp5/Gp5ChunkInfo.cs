using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class Gp5ChunkInfo
{
	public int ChunkCount { get; set; }

	public int ScenarioCount { get; set; }

	public string? SupportedLanguages { get; set; }

	public string? DefaultLanguage { get; set; }

	public int DefaultScenarioId { get; set; }

	public List<Gp5Chunk> Chunks { get; } = new List<Gp5Chunk>();

	public List<Gp5Scenario> Scenarios { get; } = new List<Gp5Scenario>();
}
