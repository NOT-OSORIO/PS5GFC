namespace ProsperoPkgTool.Content;

public sealed record ModuleLaunchReadiness(string Path, ModuleAuthorityKind Kind, ulong AuthorityId, bool WillRunOnDebugConsole, string Note);
