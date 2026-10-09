namespace ProsperoPkgTool.Containers;

public sealed record DiskSpaceReport(DiskSpaceStatus Status, long RequiredBytes, long AvailableBytes, string Root, string What);
