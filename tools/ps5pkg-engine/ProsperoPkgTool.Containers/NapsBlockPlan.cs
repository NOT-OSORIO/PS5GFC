using System.Collections;
using System.Collections.Generic;

namespace ProsperoPkgTool.Containers;

public sealed class NapsBlockPlan : IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock>, IEnumerable<ProsperoPs5InnerImageReader.InnerBlock>, IEnumerable, IReadOnlyCollection<ProsperoPs5InnerImageReader.InnerBlock>
{
	private readonly IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> _blocks;

	public string Dialect { get; }

	public IReadOnlyList<string> Diagnostics { get; }

	public int Count => _blocks.Count;

	public ProsperoPs5InnerImageReader.InnerBlock this[int index] => _blocks[index];

	internal NapsBlockPlan(string dialect, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, IReadOnlyList<string> diagnostics)
	{
		Dialect = dialect;
		_blocks = blocks;
		Diagnostics = diagnostics;
	}

	public IEnumerator<ProsperoPs5InnerImageReader.InnerBlock> GetEnumerator()
	{
		return _blocks.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}
}
