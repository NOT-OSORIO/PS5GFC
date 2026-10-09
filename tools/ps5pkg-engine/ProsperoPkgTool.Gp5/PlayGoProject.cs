using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class PlayGoProject
{
	public ushort VersionMajor { get; init; }

	public ushort VersionMinor { get; init; }

	public string ContentId { get; init; } = string.Empty;

	public int DefaultScenarioId { get; init; }

	public uint HeaderFlags { get; init; }

	public IReadOnlyList<PlayGoChunk> Chunks { get; init; } = Array.Empty<PlayGoChunk>();

	public IReadOnlyList<PlayGoScenario> Scenarios { get; init; } = Array.Empty<PlayGoScenario>();

	public IReadOnlyList<PlayGoExtent> Extents { get; init; } = Array.Empty<PlayGoExtent>();

	public IReadOnlyList<PlayGoAssignment> Assignments { get; init; } = Array.Empty<PlayGoAssignment>();

	public IReadOnlyDictionary<string, byte> FileChunkAssignments { get; init; } = new Dictionary<string, byte>(StringComparer.Ordinal);
}
