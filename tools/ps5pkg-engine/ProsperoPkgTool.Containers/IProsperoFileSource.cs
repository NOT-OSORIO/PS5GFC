using System.IO;

namespace ProsperoPkgTool.Containers;

public interface IProsperoFileSource
{
	long Length { get; }

	Stream Open();
}
