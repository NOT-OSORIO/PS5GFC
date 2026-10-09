using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed record ProsperoDecodedPackage(ProsperoPackageInspection Inspection, byte[] InnerImage, byte[] Naps, byte[] Mount, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Blocks, bool OuterPfsIcvValid);
