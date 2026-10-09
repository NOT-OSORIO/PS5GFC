using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

internal interface INapsDialect
{
	string Id { get; }

	IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Decode(ReadOnlySpan<byte> naps, long sourceLength);
}
