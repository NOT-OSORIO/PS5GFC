using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed record PlayGoScenario(int Id, string Label, int InitialChunkCount, IReadOnlyList<int> Chunks);
