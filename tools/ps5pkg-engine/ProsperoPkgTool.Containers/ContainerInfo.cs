using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed record ContainerInfo(string FormatId, string Path, long Length, IReadOnlyDictionary<string, string> Properties);
