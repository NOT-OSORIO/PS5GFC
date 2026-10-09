namespace ProsperoPkgTool.Containers;

public sealed record ContainerBackendDescriptor(string FormatId, string DisplayName, ContainerBackendStatus Status, ContainerCapabilities Capabilities);
