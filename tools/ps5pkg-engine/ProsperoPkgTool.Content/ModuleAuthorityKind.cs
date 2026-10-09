namespace ProsperoPkgTool.Content;

public enum ModuleAuthorityKind
{
	NotExecutable,
	RawElf,
	FakeAuthoritySelf,
	GenuineAuthoritySelf,
	UnknownAuthoritySelf,
	SignedEncrypted
}
