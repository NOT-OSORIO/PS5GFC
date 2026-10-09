namespace ProsperoPkgTool.Content;

public sealed record ProsperoSelfExtInfo(ulong AuthorityId, ulong ProgramType, ulong AppVersion, ulong FirmwareVersion, byte[] ElfDigest);
