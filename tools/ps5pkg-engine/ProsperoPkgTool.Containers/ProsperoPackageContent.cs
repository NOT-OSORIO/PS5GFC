using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPackageContent
{
	private sealed class SubReadStream(Stream baseStream, long start, long length) : Stream
	{
		private long _position;

		public override bool CanRead => true;

		public override bool CanSeek => true;

		public override bool CanWrite => false;

		public override long Length => length;

		public override long Position
		{
			get
			{
				return _position;
			}
			set
			{
				if (value < 0 || value > length)
				{
					throw new ArgumentOutOfRangeException("value");
				}
				_position = value;
			}
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			return Read(buffer.AsSpan(offset, count));
		}

		public override int Read(Span<byte> buffer)
		{
			int num = (int)Math.Min(buffer.Length, length - _position);
			if (num == 0)
			{
				return 0;
			}
			baseStream.Position = start + _position;
			int num2 = baseStream.Read(buffer.Slice(0, num));
			_position += num2;
			return num2;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			return Position = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => _position + offset,
				SeekOrigin.End => length + offset,
				_ => throw new ArgumentOutOfRangeException("origin"),
			};
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}

	public static ProsperoFileBackedPackage ReadFileBacked(string packagePath, string? passcode = null, bool repairTruncatedOffsets = false)
	{
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(packagePath);
		ProsperoFihHeader fih = prosperoPackageInspection.Fih;
		if ((object)fih == null)
		{
			throw new InvalidDataException("The package has no finalized FIH/PFS payload.");
		}
		if (prosperoPackageInspection.Kind != ProsperoPackageKind.FinalizedDebug)
		{
			throw new InvalidDataException("The file-backed reader supports finalized debug packages only.");
		}
		if (prosperoPackageInspection.Cnt.ContentId.Length != 36)
		{
			throw new InvalidDataException("The package CNT has no usable content ID for outer-PFS key derivation.");
		}
		FileStream stream = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read, FileShare.Read);
		OuterPfsReader pfs;
		AesXts xts;
		checked
		{
			try
			{
				long pfsOffset = (long)fih.PfsOffset;
				long pfsSize = (long)fih.PfsSize;
				pfs = OuterPfsReader.Open(stream, pfsOffset, pfsSize, ReadOuterSuperblockIndexHint(stream, pfsSize));
				(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ProsperoKeys.ComputeEkpfs(prosperoPackageInspection.Cnt.ContentId, passcode ?? "00000000000000000000000000000000"), pfs.Seed);
				byte[] item = tuple.TweakKey;
				byte[] item2 = tuple.DataKey;
				xts = new AesXts(item2, item);
				try
				{
					byte[] array = pfs.ReadNaps(xts);
					long innerLength = pfs.InnerImageLength;
					IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks = NapsDialectRegistry.Select(array, innerLength, (IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> candidate) =>
					{
						NapsDialectRegistry.ProbePfs(candidate, innerLength, ReadRegion, ReadInnerSuperblockHint(stream));
					});
					long mountLength = ProsperoPs5InnerImageReader.MountSize(blocks);
					IReadOnlyList<ProsperoInnerPfsReader.Entry> entries;
					using (ProsperoInnerMountStream mount = new ProsperoInnerMountStream(blocks, innerLength, ReadRegion))
					{
						entries = ProsperoInnerPfsReader.Enumerate(mount, ReadInnerSuperblockHint(stream), repairTruncatedOffsets);
					}
					return new ProsperoFileBackedPackage(prosperoPackageInspection, array, blocks, innerLength, mountLength, pfs.VerifySuperblockIcv(), entries, stream, xts, ReadRegion);
				}
				catch
				{
					xts.Dispose();
					throw;
				}
			}
			catch
			{
				stream.Dispose();
				throw;
			}
		}
		byte[] ReadRegion(long offset, int length)
		{
			return pfs.ReadInnerImageRegion(xts, offset, length);
		}
	}

	public static bool RequiresFileBacked(string packagePath, string? passcode = null)
	{
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(packagePath);
		if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedDebug)
		{
			ProsperoFihHeader fih = prosperoPackageInspection.Fih;
			if ((object)fih != null)
			{
				if (fih.PfsSize > 2147483648u)
				{
					return true;
				}
				if (prosperoPackageInspection.Cnt.ContentId.Length != 36)
				{
					return false;
				}
				try
				{
					FileStream stream = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read, FileShare.Read);
					try
					{
						OuterPfsReader pfs = OuterPfsReader.Open(stream, checked((long)fih.PfsOffset), checked((long)fih.PfsSize), ReadOuterSuperblockIndexHint(stream, (long)fih.PfsSize));
						(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ProsperoKeys.ComputeEkpfs(prosperoPackageInspection.Cnt.ContentId, passcode ?? "00000000000000000000000000000000"), pfs.Seed);
						byte[] item = tuple.TweakKey;
						byte[] item2 = tuple.DataKey;
						AesXts xts = new AesXts(item2, item);
						try
						{
							return ProsperoPs5InnerImageReader.MountSize(NapsDialectRegistry.Select(pfs.ReadNaps(xts), pfs.InnerImageLength, (IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> candidate) =>
							{
								NapsDialectRegistry.ProbePfs(candidate, pfs.InnerImageLength, (long offset, int length) => pfs.ReadInnerImageRegion(xts, offset, length), ReadInnerSuperblockHint(stream));
							})) > int.MaxValue;
						}
						finally
						{
							if (xts != null)
							{
								((IDisposable)xts).Dispose();
							}
						}
					}
					finally
					{
						if (stream != null)
						{
							((IDisposable)stream).Dispose();
						}
					}
				}
				catch (Exception ex) when ((ex is IOException || ex is InvalidDataException || ex is CryptographicException || ex is NotSupportedException || ex is ArgumentException) ? true : false)
				{
					return false;
				}
			}
		}
		return false;
	}

	private static int ReadOuterSuperblockIndexHint(Stream stream, long pfsSize)
	{
		Span<byte> buffer = stackalloc byte[96];
		stream.Position = 0L;
		stream.ReadExactly(buffer);
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(32, 8));
		if (num < 65536)
		{
			return -1;
		}
		long num2 = (long)(num - 65536);
		if (num2 % 65536 != 0L)
		{
			return -1;
		}
		long num3 = num2 / 65536;
		int num4 = (int)(pfsSize / 65536);
		if (num3 < 0 || num3 >= num4)
		{
			return -1;
		}
		return (int)num3;
	}

	private static long ReadInnerSuperblockHint(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[88];
		stream.Position = 0L;
		stream.ReadExactly(buffer);
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(80, 8));
		if (num == 0)
		{
			return -1L;
		}
		return (long)(num * 65536);
	}

	public static ProsperoDecodedPackage Read(string packagePath, string? passcode = null, string? referencePackagePath = null)
	{
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(packagePath);
		ProsperoFihHeader fih = prosperoPackageInspection.Fih;
		if ((object)fih == null)
		{
			throw new InvalidDataException("The package has no finalized FIH/PFS payload.");
		}
		if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedPatchDebug)
		{
			return ReadPatch(packagePath, referencePackagePath, passcode, prosperoPackageInspection);
		}
		if (prosperoPackageInspection.Kind == ProsperoPackageKind.FinalizedRetail)
		{
			throw new InvalidDataException("Encrypted payload: key required (retail payload keys are not available to this clean-room reader).");
		}
		if (prosperoPackageInspection.Cnt.ContentId.Length != 36)
		{
			throw new InvalidDataException("The package CNT has no usable content ID for outer-PFS key derivation.");
		}
		FileStream stream = File.OpenRead(Path.GetFullPath(packagePath));
		checked
		{
			try
			{
				OuterPfsReader outerPfsReader = OuterPfsReader.Open(stream, (long)fih.PfsOffset, (long)fih.PfsSize);
				(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ProsperoKeys.ComputeEkpfs(prosperoPackageInspection.Cnt.ContentId, passcode ?? "00000000000000000000000000000000"), outerPfsReader.Seed);
				var (array, _) = tuple;
				using AesXts xts = new AesXts(tuple.DataKey, array);
				byte[] array2 = outerPfsReader.ReadNaps(xts);
				byte[] image = outerPfsReader.ReadInnerImage(xts);
				IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> blocks = NapsDialectRegistry.Select(array2, image.Length, (IReadOnlyList<ProsperoPs5InnerImageReader.InnerBlock> candidate) =>
				{
					NapsDialectRegistry.ProbePfs(candidate, image.Length, (long offset, int length) => image.AsSpan((int)offset, length).ToArray(), ReadInnerSuperblockHint(stream));
				});
				byte[] mount = ProsperoPs5InnerImageReader.ReconstructMount(image, blocks);
				return new ProsperoDecodedPackage(prosperoPackageInspection, image, array2, mount, blocks, outerPfsReader.VerifySuperblockIcv());
			}
			finally
			{
				if (stream != null)
				{
					((IDisposable)stream).Dispose();
				}
			}
		}
	}

	private static ProsperoDecodedPackage ReadPatch(string packagePath, string? referencePackagePath, string? passcode, ProsperoPackageInspection inspection)
	{
		if (string.IsNullOrWhiteSpace(referencePackagePath))
		{
			throw new InvalidDataException("Patch payload reconstruction requires the base/reference package path.");
		}
		ProsperoPackageInspection prosperoPackageInspection = ProsperoPackageReader.Read(referencePackagePath);
		if (prosperoPackageInspection.Kind != ProsperoPackageKind.FinalizedDebug)
		{
			throw new InvalidDataException("Patch reference must be a finalized debug base package; patch-chain references are not yet verified.");
		}
		if (!string.Equals(prosperoPackageInspection.Cnt.ContentId, inspection.Cnt.ContentId, StringComparison.Ordinal))
		{
			throw new InvalidDataException("Patch and reference package content IDs do not match.");
		}
		ProsperoFihHeader fih = inspection.Fih;
		checked
		{
			if ((object)fih != null)
			{
				ProsperoLihHeader lih = inspection.Lih;
				if ((object)lih != null)
				{
					using (FileStream fileStream = File.OpenRead(Path.GetFullPath(packagePath)))
					{
						using FileStream fileStream2 = File.OpenRead(Path.GetFullPath(referencePackagePath));
						byte[] array = new byte[32];
						byte[] array2 = new byte[32];
						fileStream.Position = (long)lih.FihOffset + 208;
						fileStream.ReadExactly(array);
						fileStream2.Position = 48L;
						fileStream2.ReadExactly(array2);
						if (!((ReadOnlySpan<byte>)array.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)array2))
						{
							throw new InvalidDataException("Patch FIH reference digest does not match the supplied base package.");
						}
					}
					using FileStream fileStream3 = File.OpenRead(Path.GetFullPath(packagePath));
					OuterPfsReader outerPfsReader = OuterPfsReader.Open(fileStream3, (long)fih.PfsOffset, (long)fih.PfsSize);
					(byte[] TweakKey, byte[] DataKey) tuple = ProsperoKeys.DeriveOuterPfsXtsKeys(ProsperoKeys.ComputeEkpfs(inspection.Cnt.ContentId, passcode ?? "00000000000000000000000000000000"), outerPfsReader.Seed);
					var (array3, _) = tuple;
					using AesXts aesXts = new AesXts(tuple.DataKey, array3);
					byte[] array4 = new byte[64];
					fileStream3.Position = (long)lih.FihOffset;
					fileStream3.ReadExactly(array4);
					long num;
					int num2;
					byte[] array5;
					unchecked
					{
						num = BinaryPrimitives.ReadInt64LittleEndian(array4.AsSpan(32, 8)) / 65536;
						if (num < 2)
						{
							throw new InvalidDataException("Patch FIH metadata-sector base is invalid.");
						}
						num2 = outerPfsReader.SuperblockIndex - 1;
						if (num2 < 0)
						{
							throw new InvalidDataException("Patch outer PFS has no NAPS block before its superblock.");
						}
						array5 = new byte[65536];
					}
					fileStream3.Position = (long)fih.PfsOffset + unchecked((long)num2) * 65536L;
					fileStream3.ReadExactly(array5);
					byte[] array6 = aesXts.Decrypt(array5, 0x800000000000L | (ulong)(num - 2));
					ProsperoNapsLayout.DecodeHeader(array6);
					ProsperoPatchRelocation.Tables tables = ProsperoPatchRelocation.Read(packagePath, inspection);
					byte[] array7 = ProsperoPatchRelocation.MaterializeEncryptedVirtualImage(packagePath, referencePackagePath, tables);
					byte[] array8 = ProsperoPs5InnerImageReader.ReconstructRunEncryptedMount(array7, array6, aesXts);
					ProsperoInnerPfsReader.Enumerate(array8);
					return new ProsperoDecodedPackage(inspection, array7, array6, array8, Array.Empty<ProsperoPs5InnerImageReader.InnerBlock>(), outerPfsReader.VerifySuperblockIcv());
				}
			}
			throw new InvalidDataException("Patch LIH/FIH geometry is unavailable.");
		}
	}

	public static byte[] ReadCntEntry(string packagePath, ProsperoPackageInspection inspection, ProsperoCntEntry entry, long? length = null)
	{
		long num = checked((((object)inspection.Fih == null) ? 0 : ((long)inspection.Fih.CntOffset)) + entry.DataOffset);
		long num2 = length ?? entry.DataSize;
		using FileStream fileStream = File.OpenRead(Path.GetFullPath(packagePath));
		if (num < 0 || num2 < 0 || num2 > fileStream.Length - num)
		{
			throw new InvalidDataException($"CNT entry 0x{entry.Id:X8} is outside the package.");
		}
		byte[] array = new byte[num2];
		fileStream.Position = num;
		fileStream.ReadExactly(array);
		return array;
	}

	public static IReadOnlyList<long> CntDigestLengths(ProsperoCntEntry entry)
	{
		long num = entry.DataSize;
		if (!entry.IsEncrypted)
		{
			return new _003C_003Ez__ReadOnlySingleElementList<long>(num);
		}
		long num2 = (num + 15) & -16;
		if (num2 != num)
		{
			return new _003C_003Ez__ReadOnlyArray<long>(new long[2] { num, num2 });
		}
		return new _003C_003Ez__ReadOnlySingleElementList<long>(num);
	}

	public static bool TryOpenSi(string packagePath, ProsperoPackageInspection inspection, out string? error)
	{
		error = null;
		ProsperoPackageSegment prosperoPackageSegment = inspection.Segments.FirstOrDefault((ProsperoPackageSegment s) => s.Name == "SI");
		if ((object)prosperoPackageSegment == null || prosperoPackageSegment.Size == 0L)
		{
			return false;
		}
		try
		{
			using FileStream baseStream = File.OpenRead(Path.GetFullPath(packagePath));
			using SubReadStream stream = new SubReadStream(baseStream, prosperoPackageSegment.Offset, prosperoPackageSegment.Size);
			using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
			_ = zipArchive.Entries.Count;
			return true;
		}
		catch (Exception ex) when ((ex is InvalidDataException || ex is IOException) ? true : false)
		{
			error = ex.Message;
			return false;
		}
	}
}
