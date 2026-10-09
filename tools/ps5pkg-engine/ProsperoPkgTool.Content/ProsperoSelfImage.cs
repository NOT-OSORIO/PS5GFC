namespace ProsperoPkgTool.Content;

public sealed record ProsperoSelfImage(uint ProgramType, ushort HeaderSize, ushort MetaSize, ulong FileSize, ushort SegmentCount, ProsperoSelfExtInfo? ExtInfo);
