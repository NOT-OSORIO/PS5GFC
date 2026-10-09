namespace ProsperoPkgTool.Gp5;

public sealed class Gp5Volume
{
	public string VolumeType { get; set; } = "prospero_app";

	public Gp5Package Package { get; set; } = new Gp5Package();

	public Gp5ChunkInfo? ChunkInfo { get; set; }
}
