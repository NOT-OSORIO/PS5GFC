namespace ProsperoPkgTool.Containers;

public enum ProsperoPackageCategory
{
	BaseGame = 0,
	Application = BaseGame,
	UpdatePatch = 1,
	Dlc = 2,
	DlcData = 3,
	DlcNoData = 4,
	OtherKnown = 5,
	Unknown = 6
}
