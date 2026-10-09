namespace ProsperoPkgTool.Containers;

public sealed record ProsperoFihHeader(byte SignedByte, ushort FormatVersion, ulong PfsOffset, ulong PfsSize, ulong CntOffset);
