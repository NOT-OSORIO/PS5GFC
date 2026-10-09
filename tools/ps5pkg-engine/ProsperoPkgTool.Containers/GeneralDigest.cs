using System;

namespace ProsperoPkgTool.Containers;

[Flags]
public enum GeneralDigest
{
	ContentDigest = 2,
	GameDigest = 4,
	HeaderDigest = 8,
	SystemDigest = 0x10,
	MajorParamDigest = 0x20,
	ParamDigest = 0x40,
	PlaygoDigest = 0x80,
	TrophyDigest = 0x100,
	ManualDigest = 0x200,
	KeymapDigest = 0x400,
	OriginDigest = 0x800,
	TargetDigest = 0x1000,
	OriginGameDigest = 0x2000,
	TargetGameDigest = 0x4000
}
