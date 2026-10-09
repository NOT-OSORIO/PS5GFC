using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoPlayGoChunkBuildResult
{
	public required byte[] Data { get; init; }

	public required IReadOnlyList<ProsperoPlayGoExtent> Extents { get; init; }

	public required IReadOnlyList<IReadOnlyList<uint>> ChunkExtentIds { get; init; }
}
