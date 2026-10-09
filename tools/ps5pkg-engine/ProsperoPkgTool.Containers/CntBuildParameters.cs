namespace ProsperoPkgTool.Containers;

public sealed class CntBuildParameters
{
	public required string ContentId { get; init; }

	public string Passcode { get; init; } = new string('0', 32);

	public uint ContentType { get; init; } = 32u;

	public uint ContentFlags { get; init; } = 33685504u;

	public uint VersionDate { get; init; } = 538969890u;

	public uint VersionHash { get; init; } = 33444585u;
}
