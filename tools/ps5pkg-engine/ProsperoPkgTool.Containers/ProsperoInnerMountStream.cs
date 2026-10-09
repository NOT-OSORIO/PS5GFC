using System;
using System.Collections.Generic;
using System.IO;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoInnerMountStream : Stream
{
	private readonly IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> _blocks;

	private readonly long _innerImageLength;

	private readonly Func<long, int, byte[]> _readInnerImage;

	private readonly long _length;

	private long _position;

	private int _cachedIndex = -1;

	private byte[]? _cachedBlock;

	public override bool CanRead => true;

	public override bool CanSeek => true;

	public override bool CanWrite => false;

	public override long Length => _length;

	public override long Position
	{
		get
		{
			return _position;
		}
		set
		{
			if (value < 0)
			{
				throw new ArgumentOutOfRangeException("value");
			}
			_position = value;
		}
	}

	public ProsperoInnerMountStream(IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks, long innerImageLength, Func<long, int, byte[]> readInnerImage)
	{
		ArgumentNullException.ThrowIfNull(blocks, "blocks");
		ArgumentNullException.ThrowIfNull(readInnerImage, "readInnerImage");
		if (blocks.Count == 0)
		{
			throw new ArgumentException("The inner image has no decodable blocks.", "blocks");
		}
		_blocks = blocks;
		_innerImageLength = innerImageLength;
		_readInnerImage = readInnerImage;
		_length = ProsperoPs5InnerImageReader.MountSize(blocks);
		if (_length <= 0)
		{
			throw new InvalidDataException("The inner mount has no addressable bytes.");
		}
	}

	public override void Flush()
	{
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		long num = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => _position + offset,
			SeekOrigin.End => _length + offset,
			_ => throw new ArgumentOutOfRangeException("origin"),
		};
		if (num < 0)
		{
			throw new IOException("Cannot seek before the start of the inner mount.");
		}
		_position = num;
		return _position;
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer, "buffer");
		if (offset < 0 || count < 0 || offset + count > buffer.Length)
		{
			throw new ArgumentOutOfRangeException("offset");
		}
		long num = _length - _position;
		if (num <= 0 || count == 0)
		{
			return 0;
		}
		int num2 = (int)Math.Min(count, num);
		int i;
		int num5;
		for (i = 0; i < num2; i += num5)
		{
			long num3 = _position + i;
			int index = FindBlock(num3);
			ProsperoPs5InnerImageReader.InnerBlock innerBlock = _blocks[index];
			int num4 = checked((int)(num3 - innerBlock.UncompressedOffset));
			num5 = Math.Min(innerBlock.UncompressedLength - num4, num2 - i);
			if (num5 <= 0)
			{
				throw new InvalidDataException($"Inner mount offset 0x{num3:X} maps to an empty NAPS block.");
			}
			Array.Copy(GetBlock(index), num4, buffer, offset + i, num5);
		}
		_position += i;
		return i;
	}

	private int FindBlock(long position)
	{
		int num = 0;
		int num2 = _blocks.Count - 1;
		while (num <= num2)
		{
			int num3 = num + (num2 - num >> 1);
			ProsperoPs5InnerImageReader.InnerBlock innerBlock = _blocks[num3];
			if (position < innerBlock.UncompressedOffset)
			{
				num2 = num3 - 1;
				continue;
			}
			if (position >= innerBlock.UncompressedOffset + innerBlock.UncompressedLength)
			{
				num = num3 + 1;
				continue;
			}
			return num3;
		}
		throw new InvalidDataException($"Inner mount offset 0x{position:X} is not covered by any NAPS block.");
	}

	private byte[] GetBlock(int index)
	{
		if (_cachedIndex == index && _cachedBlock != null)
		{
			return _cachedBlock;
		}
		_cachedBlock = ProsperoPs5InnerImageReader.DecodeBlock(_blocks, index, _readInnerImage, _innerImageLength);
		_cachedIndex = index;
		return _cachedBlock;
	}
}
