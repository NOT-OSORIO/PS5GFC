using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoPfsImageXmlOptions
{
	public string ContentId { get; set; } = "";

	public string TitleName { get; set; } = "";

	public string ContentVersion { get; set; } = "01.000.000";

	public string DrmType { get; set; } = "none";

	public string? ApplicationDrmType { get; set; }

	public string ContentType { get; set; } = "PS5GD";

	public string ApplicationType { get; set; } = "free";

	public long PackageSize { get; set; }

	public long PfsImageOffset { get; set; } = 65536L;

	public long PfsImageSize { get; set; }

	public byte[]? PfsImageSeed { get; set; }

	public string MasterVersion { get; set; } = "01.00";

	public string? LongName { get; set; }

	public ulong LongNameMasterValue { get; set; }

	public string RequiredSystemVersion { get; set; } = "00.000.000.00000000";

	public string RequiredSystemSoftwareVersion { get; set; } = "0x0000000000000000";

	public string SdkVersion { get; set; } = "0x0000000000000000";

	public uint VersionDate { get; set; } = 538969890u;

	public uint VersionHash { get; set; } = 33444585u;

	public long ContainerSize { get; set; } = 327680L;

	public long MandatorySize { get; set; }

	public long BodyOffset { get; set; } = 8192L;

	public long SupplementalOffset { get; set; } = 327680L;

	public IReadOnlyList<ProsperoPfsImageEntry> Entries { get; set; } = Array.Empty<ProsperoPfsImageEntry>();

	public byte[]? BodyDigest { get; set; }

	public byte[]? ContentDigest { get; set; }

	public byte[]? GameDigest { get; set; }

	public byte[]? HeaderDigest { get; set; }

	public byte[]? SystemDigest { get; set; }

	public byte[]? ParamDigest { get; set; }

	public byte[]? PackageDigest { get; set; }

	public byte[]? SblockDigest { get; set; }

	public byte[]? FixedInfoDigest { get; set; }

	public ProsperoInnerImageAssembler.InnerImageResult? NestedInner { get; set; }

	public OuterPfsTreeInfo? OuterPfsTree { get; set; }

	public ProsperoChunkInfoModel? ChunkInfo { get; set; }
}
