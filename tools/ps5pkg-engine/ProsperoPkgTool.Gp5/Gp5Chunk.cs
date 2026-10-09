using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class Gp5Chunk
{
	public int Id { get; set; }

	public string Label { get; set; } = string.Empty;

	public string? Languages { get; set; }

	public Dictionary<string, string> ExtraAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
