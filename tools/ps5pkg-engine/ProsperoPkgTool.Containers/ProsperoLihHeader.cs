namespace ProsperoPkgTool.Containers;

public sealed record ProsperoLihHeader(ushort FormatVersion, uint Unknown10High, ulong FihOffset, ulong PackageSize, ulong LihSize, ulong FihSize, ulong CntOffset, ulong CntSize, ulong SiOffset, ulong SiSize);
