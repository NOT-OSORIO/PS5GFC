using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class Gp5DirectoryEntry : Gp5ContentEntry
{
	public bool IsRoot { get; set; }

	public bool? Virtual { get; set; }

	public string? DirectoryExclude { get; set; }

	public string? FileExclude { get; set; }

	public List<Gp5ContentEntry> Children { get; } = new List<Gp5ContentEntry>();
}
