using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public abstract class Gp5ContentEntry
{
	public string DestinationPath { get; set; } = string.Empty;

	public string? SourcePath { get; set; }

	public int? Chunk { get; set; }

	public string? ContentConfigLabel { get; set; }

	public string? PfsCompression { get; set; }

	public Dictionary<string, string> ExtraAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
