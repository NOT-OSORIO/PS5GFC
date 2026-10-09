using System;
using System.Collections.Generic;

namespace ProsperoPkgTool.Gp5;

public sealed class Gp5Package
{
	public string? ContentId { get; set; }

	public string Passcode { get; set; } = "00000000000000000000000000000000";

	public string? CreationDate { get; set; }

	public string? EntitlementKey { get; set; }

	public string? StorageType { get; set; }

	public string? AppType { get; set; }

	public Dictionary<string, string> ExtraAttributes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
