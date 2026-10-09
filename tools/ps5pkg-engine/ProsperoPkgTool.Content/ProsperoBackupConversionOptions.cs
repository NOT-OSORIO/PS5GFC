namespace ProsperoPkgTool.Content;

public sealed class ProsperoBackupConversionOptions
{
	public string BackupFolder { get; set; } = "";

	public string OutputFolder { get; set; } = "";

	public string DecryptedSubfolder { get; set; } = "decrypted";

	public string ContentId { get; set; } = "";

	public string Passcode { get; set; } = ProsperoDebugLicense.DefaultPasscode;

	public string Version { get; set; } = "";

	public string StagingFolder { get; set; } = "";

	public bool KeepStaging { get; set; }

	public bool UseEmbeddedRightSprx { get; set; }

	public bool Verbose { get; set; }
}
