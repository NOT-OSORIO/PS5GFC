using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class Gp5Scenario
{
	public int Id { get; set; }

	public string Type { get; set; } = "playmode";

	public int InitialChunkCount { get; set; }

	public string Label { get; set; } = string.Empty;

	public string Sequence { get; set; } = "0";

	public Dictionary<string, string> ExtraAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
