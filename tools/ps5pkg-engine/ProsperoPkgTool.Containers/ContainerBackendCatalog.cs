using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public static class ContainerBackendCatalog
{
	public static IReadOnlyList<ContainerBackendDescriptor> All { get; } = new _003C_003Ez__ReadOnlyArray<ContainerBackendDescriptor>(new ContainerBackendDescriptor[5]
	{
		new ContainerBackendDescriptor("ps5-pkg", "PS5 CNT/FIH fake/debug package", ContainerBackendStatus.Experimental, ContainerCapabilities.Inspect | ContainerCapabilities.List | ContainerCapabilities.Extract | ContainerCapabilities.Verify | ContainerCapabilities.Create),
		new ContainerBackendDescriptor("ffpkg", "FFPKG / UFS2 image", ContainerBackendStatus.Planned, ContainerCapabilities.None),
		new ContainerBackendDescriptor("ffpfsc", "FFPFSC compressed image", ContainerBackendStatus.Planned, ContainerCapabilities.None),
		new ContainerBackendDescriptor("exfat", "Raw exFAT image", ContainerBackendStatus.Planned, ContainerCapabilities.None),
		new ContainerBackendDescriptor("folder", "Unpacked application folder", ContainerBackendStatus.Planned, ContainerCapabilities.None)
	});
}
