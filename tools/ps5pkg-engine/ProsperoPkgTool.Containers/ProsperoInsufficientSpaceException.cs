using System.IO;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoInsufficientSpaceException : IOException
{
	public long RequiredBytes { get; }

	public long AvailableBytes { get; }

	public string Root { get; }

	public ProsperoInsufficientSpaceException(string message, long requiredBytes, long availableBytes, string root)
		: base(message)
	{
		RequiredBytes = requiredBytes;
		AvailableBytes = availableBytes;
		Root = root;
	}
}
