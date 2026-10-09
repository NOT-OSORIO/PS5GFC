namespace ProsperoPkgTool.Containers;

public sealed record ProsperoPlayGoFileRecord(string Path, byte ChunkId, ulong PathHash);
