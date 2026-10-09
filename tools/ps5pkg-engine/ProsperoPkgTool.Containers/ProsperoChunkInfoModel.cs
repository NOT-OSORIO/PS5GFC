namespace ProsperoPkgTool.Containers;

public sealed class ProsperoChunkInfoModel
{
	public int PlayGoChunkDatSize { get; set; }

	public string Sdk { get; set; } = "0x00850000";

	public string Disps { get; set; } = "0x0011";

	public ulong LanguageMask { get; set; } = ulong.MaxValue;

	public long TotalSize { get; set; }

	public long Outer0Size { get; set; }

	public long Outer1Size { get; set; }
}
