using System;

namespace ProsperoPkgTool.Containers;

[Flags]
public enum ContainerCapabilities
{
	None = 0,
	Inspect = 1,
	List = 2,
	Extract = 4,
	Verify = 8,
	Create = 0x10,
	Edit = 0x20,
	Repair = 0x40
}
