using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed record ProsperoPackageInspection(string Path, long FileSize, ProsperoPackageKind Kind, ProsperoFihHeader? Fih, ProsperoCntHeader Cnt, IReadOnlyList<ProsperoCntEntry> Entries, IReadOnlyList<ProsperoPackageSegment> Segments, VerificationResult Structure, ProsperoLihHeader? Lih = null);
