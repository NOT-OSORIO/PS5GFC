namespace ProsperoPkgTool.Containers;

public sealed record ProsperoCntHeader(ushort Version, ushort Flags, uint Unk0C, uint EntryCount, ushort ScEntryCount, ushort EntryCount2, uint EntryTableOffset, uint MainEntDataSize, ulong BodyOffset, ulong BodySize, string ContentId, uint DrmType, uint ContentType, uint ContentFlags);
