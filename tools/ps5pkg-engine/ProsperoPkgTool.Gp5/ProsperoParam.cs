namespace ProsperoPkgTool.Gp5;

public sealed record ProsperoParam(string TitleId, string ContentId, string? ContentVersion, int ApplicationCategoryType, string? CreationDate, string? ApplicationDrmType)
{
	public string? TitleName { get; init; }

	public string VolumeType
	{
		get
		{
			if (ApplicationCategoryType != 1)
			{
				return "prospero_app";
			}
			return "prospero_ac";
		}
	}
}
