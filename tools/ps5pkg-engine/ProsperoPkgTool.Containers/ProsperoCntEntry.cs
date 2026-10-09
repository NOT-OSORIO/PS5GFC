namespace ProsperoPkgTool.Containers;

public sealed record ProsperoCntEntry(uint Id, uint NameOffset, uint Flags1, uint Flags2, uint DataOffset, uint DataSize, string? Name)
{
	public bool IsEncrypted => (Flags1 & 0x80000000u) != 0;

	public string DisplayName
	{
		get
		{
			string text = Name;
			if (text == null)
			{
				text = Id switch
				{
					1u => "digests",
					16u => "entry-keys",
					32u => "image-key",
					128u => "general-digests",
					256u => "metas",
					512u => "entry-names",
					1034u => "imagedigs.dat",
					4097u => "playgo-chunk.dat",
					4608u => "icon0.png",
					4640u => "pic0.png",
					4672u => "snd0.at9",
					4736u => "icon0.dds",
					4768u => "pic0.dds",
					4800u => "pic1.dds",
					8192u => "param.json",
					8208u => "playgo-hash-table.dat",
					8209u => "playgo-ficm.dat",
					8288u => "pic2.dds",
					_ => "(unnamed)",
				};
			}
			return text;
		}
	}
}
