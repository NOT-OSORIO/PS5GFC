using System;
using System.Collections.Generic;
using System.IO;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoFileBackedPackage : IDisposable
{
	private readonly FileStream _stream;

	private readonly AesXts _xts;

	private readonly Func<long, int, byte[]> _readRegion;

	public ProsperoPackageInspection Inspection { get; }

	public byte[] Naps { get; }

	public IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> Blocks { get; }

	public long InnerImageLength { get; }

	public long MountLength { get; }

	public bool OuterPfsIcvValid { get; }

	public IReadOnlyList<ProsperoInnerPfsReader.Entry> Entries { get; }

	internal ProsperoFileBackedPackage(ProsperoPackageInspection inspection, byte[] naps, IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, long innerImageLength, long mountLength, bool outerPfsIcvValid, IReadOnlyList<ProsperoInnerPfsReader.Entry> entries, FileStream stream, AesXts xts, Func<long, int, byte[]> readRegion)
	{
		Inspection = inspection;
		Naps = naps;
		Blocks = blocks;
		InnerImageLength = innerImageLength;
		MountLength = mountLength;
		OuterPfsIcvValid = outerPfsIcvValid;
		Entries = entries;
		_stream = stream;
		_xts = xts;
		_readRegion = readRegion;
	}

	public Stream OpenMount()
	{
		return new ProsperoInnerMountStream(Blocks, InnerImageLength, _readRegion);
	}

	public byte[] ReadInnerImageRegion(long offset, int length)
	{
		return _readRegion(offset, length);
	}

	public void DecodeAllBlocks()
	{
		ProsperoPs5InnerImageReader.DecodeAllBlocks(Blocks, _readRegion, InnerImageLength);
	}

	public void Dispose()
	{
		_xts.Dispose();
		_stream.Dispose();
	}
}
