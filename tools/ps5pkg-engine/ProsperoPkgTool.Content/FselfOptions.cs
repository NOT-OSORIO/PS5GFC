namespace ProsperoPkgTool.Content;

public sealed class FselfOptions
{
	public ulong AppVersion { get; init; }

	public ulong FirmwareVersion { get; init; }

	public ulong? AuthorityId { get; init; }

	public string? SceVersionName { get; init; }

	public byte[]? SceVersionRecord { get; init; }

	public ulong? SdkVersionOverride { get; init; }
}
