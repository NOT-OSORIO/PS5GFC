using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProsperoPkgTool.Containers;

public interface IProsperoContainerBackend
{
	ContainerBackendDescriptor Descriptor { get; }

	ValueTask<bool> DetectAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken));

	Task<ContainerInfo> ReadInfoAsync(string path, CancellationToken cancellationToken = default(CancellationToken));

	IAsyncEnumerable<ContainerEntry> ListAsync(string path, CancellationToken cancellationToken = default(CancellationToken));

	Task ExtractAsync(string path, string outputDirectory, CancellationToken cancellationToken = default(CancellationToken));

	Task<VerificationResult> VerifyAsync(string path, CancellationToken cancellationToken = default(CancellationToken));
}
