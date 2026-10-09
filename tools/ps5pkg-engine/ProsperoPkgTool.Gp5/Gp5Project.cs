using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class Gp5Project
{
	public string Format { get; set; } = "gp5";

	public string? Version { get; set; } = "1000";

	public Gp5Volume Volume { get; set; } = new Gp5Volume();

	public Gp5ContentLayout Layout { get; set; }

	public List<Gp5FileEntry> Files { get; } = new List<Gp5FileEntry>();

	public Gp5DirectoryEntry RootDirectory { get; set; } = new Gp5DirectoryEntry
	{
		IsRoot = true,
		Virtual = true
	};

	public Dictionary<string, string> ExtraAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
