namespace ProsperoPkgTool.Gp5;

public sealed record Gp5ManifestFile(string DestinationPath, string SourceFullPath, int? Chunk, string? PfsCompression);
