using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed record SourceFolderInspection(string RootPath, ProsperoParam? Param, PlayGoProject? PlayGo, IReadOnlyList<string> IncludedFiles, IReadOnlyList<string> ExcludedFiles, ValidationReport Validation);
