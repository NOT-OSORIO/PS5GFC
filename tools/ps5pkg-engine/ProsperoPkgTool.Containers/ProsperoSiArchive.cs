using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public static class ProsperoSiArchive
{
	public const string PfsImageXmlPath = "common/etc/pfsimage.xml";

	public const string PlayGoChunkDatPath = "common/etc/playgo-chunk.dat";

	public const string NapsMeta18Path = "common/etc/naps_meta_18.dat";

	private const int ChunkCrcBlockSize = 65536;

	private const int SupplementalOffset = 327680;

	public static IReadOnlyList<ProsperoSiMember> BuildMembers(string contentId, byte[]? pfsImageXml, byte[]? playGoChunkDat = null, byte[]? napsMeta18 = null, byte[]? napsMeta300 = null, byte[]? playGoChunkCrc = null, ReadOnlySpan<byte> finalizedMountImage = default(ReadOnlySpan<byte>), bool includePfsImageXml = true)
	{
		ArgumentException.ThrowIfNullOrEmpty(contentId, "contentId");
		if (playGoChunkCrc == null)
		{
			playGoChunkCrc = ((finalizedMountImage.Length > 0) ? BuildChunkCrc(finalizedMountImage) : null);
		}
		List<ProsperoSiMember> list = new List<ProsperoSiMember>();
		if (napsMeta18 != null)
		{
			list.Add(new ProsperoSiMember("common/etc/naps_meta_18.dat", napsMeta18));
		}
		if (napsMeta300 != null)
		{
			ReadOnlySpan<int> meta300Ids = ProsperoNapsMeta.Meta300Ids;
			for (int i = 0; i < meta300Ids.Length; i++)
			{
				int value = meta300Ids[i];
				list.Add(new ProsperoSiMember($"common/etc/naps_meta_{value}.dat", napsMeta300));
			}
		}
		if (includePfsImageXml && pfsImageXml != null)
		{
			list.Add(new ProsperoSiMember("common/etc/pfsimage.xml", pfsImageXml));
		}
		if (playGoChunkDat != null)
		{
			list.Add(new ProsperoSiMember("common/etc/playgo-chunk.dat", playGoChunkDat));
		}
		if (playGoChunkCrc != null)
		{
			list.Add(new ProsperoSiMember("config/" + contentId + "/playgo-chunk.crc", playGoChunkCrc));
		}
		return list;
	}

	public static byte[] BuildDebugSiSegment(ProsperoPfsImageXmlOptions pfsImageXml, byte[]? playGoChunkDat, ReadOnlySpan<byte> mountImage, ulong innerImageSize, IReadOnlyList<(string Path, long Size)> contentFiles, ICollection<string>? warnings = null, bool includePfsImageXml = true)
	{
		ArgumentNullException.ThrowIfNull(pfsImageXml, "pfsImageXml");
		byte[] napsMeta = null;
		if (innerImageSize >= 65536)
		{
			napsMeta = ProsperoNapsMeta.BuildMeta300FromInnerImageSize(innerImageSize);
		}
		byte[] napsMeta2 = null;
		if (innerImageSize >= 65536 && mountImage.Length >= 65536)
		{
			byte[] array = ProsperoNapsMeta.BuildMeta18WithMetadataEntry(innerImageSize, mountImage, contentFiles, pfsImageXml.PfsImageSize, pfsImageXml.NestedInner);
			if (array.Length != 0)
			{
				napsMeta2 = array;
			}
		}
		byte[] pfsImageXml2 = (includePfsImageXml ? Encoding.UTF8.GetBytes(BuildPfsImageXml(pfsImageXml, warnings)) : null);
		return WriteZip(BuildMembers(pfsImageXml.ContentId, pfsImageXml2, playGoChunkDat, napsMeta2, napsMeta, null, mountImage, includePfsImageXml));
	}

	public static byte[] BuildChunkCrc(ReadOnlySpan<byte> mountImage)
	{
		if (mountImage.Length == 0)
		{
			return Array.Empty<byte>();
		}
		int num = (mountImage.Length + 65536 - 1) / 65536;
		byte[] array = new byte[num * 4];
		for (int i = 0; i < num; i++)
		{
			int num2 = i * 65536;
			int length = Math.Min(65536, mountImage.Length - num2);
			uint value = ProsperoCrc32C.Compute(mountImage.Slice(num2, length));
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(i * 4), value);
		}
		return array;
	}

	public static byte[] BuildChunkCrc(Stream mountStream, long length)
	{
		ArgumentNullException.ThrowIfNull(mountStream, "mountStream");
		if (length <= 0)
		{
			return Array.Empty<byte>();
		}
		long num = (length + 65536 - 1) / 65536;
		byte[] array = new byte[num * 4];
		byte[] array2 = new byte[65536];
		long num2 = 0L;
		for (long num3 = 0L; num3 < num; num3++)
		{
			int num4 = (int)Math.Min(65536L, length - num2);
			int length2 = ReadFully(mountStream, array2, num4);
			uint value = ProsperoCrc32C.Compute(array2.AsSpan(0, length2));
			BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan((int)(num3 * 4)), value);
			num2 += num4;
		}
		return array;
	}

	private static (byte[] Digest, byte[] ChunkCrc) HashAndChunkCrc(Stream stream, long length)
	{
		byte[] chunkCrc = new byte[checked((int)unchecked(checked(length + 65536 - 1) / 65536)) * 4];
		const int BatchBytes = 64 * 65536;
		using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256);
		using System.Collections.Concurrent.BlockingCollection<(byte[] Buffer, int Length)> filled = new System.Collections.Concurrent.BlockingCollection<(byte[], int)>(3);
		using System.Collections.Concurrent.BlockingCollection<byte[]> free = new System.Collections.Concurrent.BlockingCollection<byte[]>(5);
		for (int i = 0; i < 5; i++)
		{
			free.Add(new byte[BatchBytes]);
		}
		using System.Threading.CancellationTokenSource stop = new System.Threading.CancellationTokenSource();
		System.Threading.Tasks.Task reader = System.Threading.Tasks.Task.Run(delegate
		{
			try
			{
				long remaining = length;
				while (remaining > 0)
				{
					byte[] buffer = free.Take(stop.Token);
					int want = (int)Math.Min(BatchBytes, remaining);
					int got = ReadFully(stream, buffer, want);
					filled.Add((buffer, got), stop.Token);
					remaining -= got;
					if (got < want)
					{
						break;
					}
				}
			}
			finally
			{
				filled.CompleteAdding();
			}
		});
		int chunkIndex = 0;
		try
		{
			foreach ((byte[] Buffer, int Length) item in filled.GetConsumingEnumerable())
			{
				incrementalHash.AppendData(item.Buffer, 0, item.Length);
				for (int offset = 0; offset < item.Length; offset += 65536)
				{
					int size = Math.Min(65536, item.Length - offset);
					uint value = ProsperoCrc32C.Compute(item.Buffer.AsSpan(offset, size));
					BinaryPrimitives.WriteUInt32LittleEndian(chunkCrc.AsSpan(chunkIndex * 4), value);
					chunkIndex++;
				}
				free.Add(item.Buffer);
			}
		}
		finally
		{
			stop.Cancel();
			try
			{
				reader.Wait();
			}
			catch (AggregateException ae) when (ae.InnerExceptions.All((Exception e) => e is OperationCanceledException))
			{
			}
		}
		return (Digest: incrementalHash.GetHashAndReset(), ChunkCrc: chunkCrc);
	}

	public static byte[] BuildDebugSiSegmentFromStream(ProsperoPfsImageXmlOptions pfsImageXml, byte[]? playGoChunkDat, Stream mountStream, long mountLength, ulong innerImageSize, IReadOnlyList<(string Path, long Size)> contentFiles, byte[]? outerSuperblockBlock, ICollection<string>? warnings = null, bool includePfsImageXml = true)
	{
		ArgumentNullException.ThrowIfNull(pfsImageXml, "pfsImageXml");
		ArgumentNullException.ThrowIfNull(mountStream, "mountStream");
		long position = (mountStream.CanSeek ? mountStream.Position : 0);
		byte[] array = null;
		if (innerImageSize >= 65536)
		{
			array = ProsperoNapsMeta.BuildMeta300FromInnerImageSize(innerImageSize);
		}
		byte[] array2 = null;
		byte[] array3 = null;
		if (mountLength > 0)
		{
			mountStream.Position = 0L;
			(array2, array3) = HashAndChunkCrc(mountStream, mountLength);
		}
		byte[] array4 = null;
		if (array2 != null && innerImageSize >= 65536 && mountLength >= 65536)
		{
			byte[] rhshOverride = null;
			if (outerSuperblockBlock != null && outerSuperblockBlock.Length > 0)
			{
				byte[] array5 = new byte[176];
				Sha3.Sha3_256(outerSuperblockBlock).CopyTo(array5);
				rhshOverride = array5;
			}
			byte[] array6 = new byte[128];
			array2.CopyTo(array6, 0);
			byte[] array7 = ProsperoNapsMeta.BuildMeta18WithMetadataEntry(innerImageSize, default, contentFiles, pfsImageXml.PfsImageSize, pfsImageXml.NestedInner, mountLength, array6, rhshOverride);
			if (array7.Length != 0)
			{
				array4 = array7;
			}
		}
		byte[] pfsImageXml2 = (includePfsImageXml ? Encoding.UTF8.GetBytes(BuildPfsImageXml(pfsImageXml, warnings)) : null);
		string contentId = pfsImageXml.ContentId;
		byte[] napsMeta = array4;
		byte[] napsMeta2 = array;
		byte[] playGoChunkCrc = array3;
		bool includePfsImageXml2 = includePfsImageXml;
		byte[] result = WriteZip(BuildMembers(contentId, pfsImageXml2, playGoChunkDat, napsMeta, napsMeta2, playGoChunkCrc, default, includePfsImageXml2));
		if (mountStream.CanSeek)
		{
			mountStream.Position = position;
		}
		return result;
	}

	private static int ReadFully(Stream stream, byte[] buffer, int length)
	{
		int i;
		int num;
		for (i = 0; i < length; i += num)
		{
			num = stream.Read(buffer, i, length - i);
			if (num <= 0)
			{
				break;
			}
		}
		return i;
	}

	public static string BuildPfsImageXml(ProsperoPfsImageXmlOptions options, ICollection<string>? warnings = null)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		ArgumentException.ThrowIfNullOrEmpty(options.ContentId, "options.ContentId");
		NoteIfPlaceholder(options.ContentDigest, "content-digest", reproducible: true);
		NoteIfPlaceholder(options.GameDigest, "game-digest", reproducible: true);
		NoteIfPlaceholder(options.HeaderDigest, "header-digest", reproducible: true);
		NoteIfPlaceholder(options.SystemDigest, "system-digest", reproducible: true);
		NoteIfPlaceholder(options.ParamDigest, "param-digest", reproducible: true);
		NoteIfPlaceholder(options.PackageDigest, "package-digest", reproducible: true);
		NoteIfPlaceholder(options.BodyDigest, "body-digest", reproducible: true);
		byte[] pfsImageSeed = options.PfsImageSeed;
		string value = Indent(FormatDigest((pfsImageSeed != null && pfsImageSeed.Length > 0) ? pfsImageSeed : null), "      ");
		long containerSize = options.ContainerSize;
		long bodyOffset = options.BodyOffset;
		long v = containerSize - bodyOffset;
		long num = options.PfsImageOffset + options.PfsImageSize;
		long v2 = num + containerSize;
		string value2 = options.LongName ?? BuildLongName(options);
		StringBuilder stringBuilder = new StringBuilder(8192);
		stringBuilder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
		stringBuilder.Append("<package-configuration version=\"1.0\" type=\"package-info\">\n");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(49, 1, stringBuilder2);
		handler.AppendLiteral("  <config version=\"");
		handler.AppendFormatted(options.ContentVersion);
		handler.AppendLiteral("\" metadata=\"0\" primary=\"yes\">\n");
		stringBuilder3.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("    <content-id>");
		handler.AppendFormatted(options.ContentId);
		handler.AppendLiteral("</content-id>\n");
		stringBuilder4.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("    <primary-id>");
		handler.AppendFormatted(options.ContentId);
		handler.AppendLiteral("</primary-id>\n");
		stringBuilder5.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder2);
		handler.AppendLiteral("    <longname>");
		handler.AppendFormatted(value2);
		handler.AppendLiteral("</longname>\n");
		stringBuilder6.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder7 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(56, 1, stringBuilder2);
		handler.AppendLiteral("    <required-system-version>");
		handler.AppendFormatted(options.RequiredSystemVersion);
		handler.AppendLiteral("</required-system-version>\n");
		stringBuilder7.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder2);
		handler.AppendLiteral("    <drm-type>");
		handler.AppendFormatted(options.DrmType);
		handler.AppendLiteral("</drm-type>\n");
		stringBuilder8.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder9 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("    <content-type>");
		handler.AppendFormatted(options.ContentType);
		handler.AppendLiteral("</content-type>\n");
		stringBuilder9.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder10 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <application-type>");
		handler.AppendFormatted(options.ApplicationType);
		handler.AppendLiteral("</application-type>\n");
		stringBuilder10.Append(ref handler);
		stringBuilder.Append("    <num-of-images>1</num-of-images>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder11 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("    <package-size>");
		handler.AppendFormatted(options.PackageSize);
		handler.AppendLiteral("</package-size>\n");
		stringBuilder11.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder12 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("    <version-date>0x");
		handler.AppendFormatted(options.VersionDate, "x8");
		handler.AppendLiteral("</version-date>\n");
		stringBuilder12.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder13 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("    <version-hash>0x");
		handler.AppendFormatted(options.VersionHash, "x8");
		handler.AppendLiteral("</version-hash>\n");
		stringBuilder13.Append(ref handler);
		stringBuilder.Append("  </config>\n");
		stringBuilder.Append("  <digests version=\"1.2\" major-param-version=\"0\">\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder14 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder2);
		handler.AppendLiteral("    <content-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.ContentDigest), "      "));
		handler.AppendLiteral("\n    </content-digest>\n");
		stringBuilder14.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder15 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(44, 1, stringBuilder2);
		handler.AppendLiteral("    <game-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.GameDigest), "      "));
		handler.AppendLiteral("\n    </game-digest>\n");
		stringBuilder15.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder16 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <header-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.HeaderDigest), "      "));
		handler.AppendLiteral("\n    </header-digest>\n");
		stringBuilder16.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder17 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <system-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.SystemDigest), "      "));
		handler.AppendLiteral("\n    </system-digest>\n");
		stringBuilder17.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder18 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(46, 1, stringBuilder2);
		handler.AppendLiteral("    <param-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.ParamDigest), "      "));
		handler.AppendLiteral("\n    </param-digest>\n");
		stringBuilder18.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder19 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder2);
		handler.AppendLiteral("    <package-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.PackageDigest), "      "));
		handler.AppendLiteral("\n    </package-digest>\n");
		stringBuilder19.Append(ref handler);
		stringBuilder.Append("  </digests>\n");
		stringBuilder.Append("  <params>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder20 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(46, 1, stringBuilder2);
		handler.AppendLiteral("    <applicationDrmType>");
		handler.AppendFormatted(options.ApplicationDrmType ?? options.ApplicationType);
		handler.AppendLiteral("</applicationDrmType>\n");
		stringBuilder20.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder21 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
		handler.AppendLiteral("    <contentId>");
		handler.AppendFormatted(options.ContentId);
		handler.AppendLiteral("</contentId>\n");
		stringBuilder21.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder22 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <contentVersion>");
		handler.AppendFormatted(options.ContentVersion);
		handler.AppendLiteral("</contentVersion>\n");
		stringBuilder22.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder23 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("    <masterVersion>");
		handler.AppendFormatted(options.MasterVersion);
		handler.AppendLiteral("</masterVersion>\n");
		stringBuilder23.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder24 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(68, 1, stringBuilder2);
		handler.AppendLiteral("    <requiredSystemSoftwareVersion>");
		handler.AppendFormatted(options.RequiredSystemSoftwareVersion);
		handler.AppendLiteral("</requiredSystemSoftwareVersion>\n");
		stringBuilder24.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder25 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
		handler.AppendLiteral("    <sdkVersion>");
		handler.AppendFormatted(options.SdkVersion);
		handler.AppendLiteral("</sdkVersion>\n");
		stringBuilder25.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder26 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
		handler.AppendLiteral("    <titleName>");
		handler.AppendFormatted(options.TitleName);
		handler.AppendLiteral("</titleName>\n");
		stringBuilder26.Append(ref handler);
		stringBuilder.Append("  </params>\n");
		stringBuilder.Append("  <container nth-of-image=\"1\">\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder27 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <container-size>");
		handler.AppendFormatted(Hex16(containerSize));
		handler.AppendLiteral("</container-size>\n");
		stringBuilder27.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder28 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <mandatory-size>");
		handler.AppendFormatted(Hex16(options.MandatorySize));
		handler.AppendLiteral("</mandatory-size>\n");
		stringBuilder28.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder29 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
		handler.AppendLiteral("    <body-offset>");
		handler.AppendFormatted(Hex16(bodyOffset));
		handler.AppendLiteral("</body-offset>\n");
		stringBuilder29.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder30 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
		handler.AppendLiteral("    <body-size>");
		handler.AppendFormatted(Hex16(v));
		handler.AppendLiteral("</body-size>\n");
		stringBuilder30.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder31 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(44, 1, stringBuilder2);
		handler.AppendLiteral("    <body-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.BodyDigest), "      "));
		handler.AppendLiteral("\n    </body-digest>\n");
		stringBuilder31.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder32 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("    <promote-size>");
		handler.AppendFormatted(Hex8(containerSize));
		handler.AppendLiteral("</promote-size>\n");
		stringBuilder32.Append(ref handler);
		stringBuilder.Append("  </container>\n");
		stringBuilder.Append("  <mount-image nth-of-image=\"1\" nested-image=\"yes\">\n");
		stringBuilder.Append("    <pfs-offset-align>0x0000000000010000</pfs-offset-align>\n");
		stringBuilder.Append("    <pfs-size-align>0x0000000000010000</pfs-size-align>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder33 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <pfs-image-offset>");
		handler.AppendFormatted(Hex16(options.PfsImageOffset));
		handler.AppendLiteral("</pfs-image-offset>\n");
		stringBuilder33.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder34 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(38, 1, stringBuilder2);
		handler.AppendLiteral("    <pfs-image-size>");
		handler.AppendFormatted(Hex16(options.PfsImageSize));
		handler.AppendLiteral("</pfs-image-size>\n");
		stringBuilder34.Append(ref handler);
		stringBuilder.Append("    <fixed-info-size>0x00010000</fixed-info-size>\n");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder35 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder2);
		handler.AppendLiteral("    <pfs-image-seed>\n      ");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\n    </pfs-image-seed>\n");
		stringBuilder35.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder36 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <sblock-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.SblockDigest ?? options.GameDigest), "      "));
		handler.AppendLiteral("\n    </sblock-digest>\n");
		stringBuilder36.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder37 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(56, 1, stringBuilder2);
		handler.AppendLiteral("    <fixed-info-digest>\n      ");
		handler.AppendFormatted(Indent(FormatDigest(options.FixedInfoDigest), "      "));
		handler.AppendLiteral("\n    </fixed-info-digest>\n");
		stringBuilder37.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder38 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(46, 1, stringBuilder2);
		handler.AppendLiteral("    <mount-image-offset>");
		handler.AppendFormatted(Hex16(0L));
		handler.AppendLiteral("</mount-image-offset>\n");
		stringBuilder38.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder39 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <mount-image-size>");
		handler.AppendFormatted(Hex16(v2));
		handler.AppendLiteral("</mount-image-size>\n");
		stringBuilder39.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder40 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(42, 1, stringBuilder2);
		handler.AppendLiteral("    <container-offset>");
		handler.AppendFormatted(Hex16(num));
		handler.AppendLiteral("</container-offset>\n");
		stringBuilder40.Append(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder41 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
		handler.AppendLiteral("    <supplemental-offset>");
		handler.AppendFormatted(Hex16(options.SupplementalOffset));
		handler.AppendLiteral("</supplemental-offset>\n");
		stringBuilder41.Append(ref handler);
		stringBuilder.Append("  </mount-image>\n");
		IReadOnlyList<ProsperoPfsImageEntry> entries = options.Entries;
		if (entries != null && entries.Count > 0)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder42 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
			handler.AppendLiteral("  <entries nth-of-image=\"1\" num=\"");
			handler.AppendFormatted(entries.Count);
			handler.AppendLiteral("\">\n");
			stringBuilder42.Append(ref handler);
			foreach (ProsperoPfsImageEntry item in entries)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder43 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(39, 3, stringBuilder2);
				handler.AppendLiteral("    <entry offset=\"");
				handler.AppendFormatted(Hex8(item.Offset));
				handler.AppendLiteral("\" size=\"");
				handler.AppendFormatted(Hex8(item.Size));
				handler.AppendLiteral("\" name=\"");
				handler.AppendFormatted(item.Name);
				handler.AppendLiteral("\"/>\n");
				stringBuilder43.Append(ref handler);
			}
			stringBuilder.Append("  </entries>\n");
		}
		AppendIntrospectionSections(stringBuilder, options);
		stringBuilder.Append("</package-configuration>\n");
		return stringBuilder.ToString();
		void NoteIfPlaceholder(byte[]? array, string field, bool reproducible)
		{
			if (array == null || array.Length == 0)
			{
				warnings?.Add(reproducible ? ("pfsimage.xml <" + field + "> emitted as all-zero placeholder (reproducible — supply it from the builder/produced CNT).") : ("pfsimage.xml <" + field + "> emitted as all-zero placeholder (keyed finalization product, not reproducible off-console)."));
			}
		}
	}

	private static string BuildLongName(ProsperoPfsImageXmlOptions o)
	{
		string value = o.ContentVersion.Replace(".", "", StringComparison.Ordinal);
		string value2 = (o.ContentType.StartsWith("PS5", StringComparison.OrdinalIgnoreCase) ? o.ContentType.Substring(3) : o.ContentType);
		string value3 = o.LongNameMasterValue.ToString("x16", CultureInfo.InvariantCulture);
		return $"{o.ContentId}-C{value}-M{value3}-{value2}";
	}

	private static void AppendIntrospectionSections(StringBuilder sb, ProsperoPfsImageXmlOptions options)
	{
		ProsperoChunkInfoModel chunkInfo = options.ChunkInfo;
		if (chunkInfo != null)
		{
			AppendChunkInfo(sb, options.ContentId, chunkInfo);
		}
		OuterPfsTreeInfo outerPfsTree = options.OuterPfsTree;
		if (outerPfsTree != null)
		{
			AppendPfsImage(sb, outerPfsTree, options.PfsImageOffset);
		}
		ProsperoInnerImageAssembler.InnerImageResult nestedInner = options.NestedInner;
		if (nestedInner != null)
		{
			AppendNestedImageFromInner(sb, nestedInner);
		}
	}

	private static void AppendChunkInfo(StringBuilder sb, string contentId, ProsperoChunkInfoModel c)
	{
		string value = "0x" + c.LanguageMask.ToString("x16", CultureInfo.InvariantCulture);
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(52, 3, stringBuilder);
		handler.AppendLiteral("  <chunkinfo size=\"");
		handler.AppendFormatted(c.PlayGoChunkDatSize);
		handler.AppendLiteral("\" nested=\"true\" sdk=\"");
		handler.AppendFormatted(c.Sdk);
		handler.AppendLiteral("\" disps=\"");
		handler.AppendFormatted(c.Disps);
		handler.AppendLiteral("\">\n");
		stringBuilder2.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder);
		handler.AppendLiteral("    <contentid>");
		handler.AppendFormatted(contentId);
		handler.AppendLiteral("</contentid>\n");
		stringBuilder3.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(40, 1, stringBuilder);
		handler.AppendLiteral("    <languages default=\"1\">");
		handler.AppendFormatted(value);
		handler.AppendLiteral("</languages>\n");
		stringBuilder4.Append(ref handler);
		sb.Append("    <scenarios num=\"1\" default=\"0\" groups=\"0\">\n");
		sb.Append("      <scenario id=\"0\" type=\"33\" name=\"Scenario #0\">\n");
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(72, 2, stringBuilder);
		handler.AppendLiteral("        <overall initials=\"1\" num=\"1\" init-size=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\" total=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\">0</overall>\n");
		stringBuilder5.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder6 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(72, 2, stringBuilder);
		handler.AppendLiteral("        <default initials=\"1\" num=\"1\" init-size=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\" total=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\">0</default>\n");
		stringBuilder6.Append(ref handler);
		sb.Append("      </scenario>\n");
		sb.Append("    </scenarios>\n");
		sb.Append("    <chunks num=\"1\" default=\"0xffffffffffffffff\">\n");
		stringBuilder = sb;
		StringBuilder stringBuilder7 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(110, 3, stringBuilder);
		handler.AppendLiteral("      <chunk id=\"0\" flag=\"0x80\" locus=\"0x03\" language=\"");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\" disps=\"");
		handler.AppendFormatted(c.Disps);
		handler.AppendLiteral("\" num=\"2\" size=\"");
		handler.AppendFormatted(c.TotalSize);
		handler.AppendLiteral("\" name=\"Chunk #0\">0 1</chunk>\n");
		stringBuilder7.Append(ref handler);
		sb.Append("    </chunks>\n");
		sb.Append("    <outers num=\"2\" overlapped=\"0\" language-overlapped=\"0\">\n");
		stringBuilder = sb;
		StringBuilder stringBuilder8 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(61, 2, stringBuilder);
		handler.AppendLiteral("      <outer id=\"0\" image=\"0\" offset=\"");
		handler.AppendFormatted(Hex16Blob(0L));
		handler.AppendLiteral("\" size=\"");
		handler.AppendFormatted(Hex16Blob(c.Outer0Size));
		handler.AppendLiteral("\" chunks=\"1\"/>\n");
		stringBuilder8.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder9 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(61, 2, stringBuilder);
		handler.AppendLiteral("      <outer id=\"1\" image=\"0\" offset=\"");
		handler.AppendFormatted(Hex16Blob(c.Outer0Size));
		handler.AppendLiteral("\" size=\"");
		handler.AppendFormatted(Hex16Blob(c.Outer1Size));
		handler.AppendLiteral("\" chunks=\"1\"/>\n");
		stringBuilder9.Append(ref handler);
		sb.Append("    </outers>\n");
		sb.Append("  </chunkinfo>\n");
	}

	private static void AppendPfsImage(StringBuilder sb, OuterPfsTreeInfo info, long pfsImageOffset)
	{
		long value = (long)info.DinodeBlock * (long)info.BlockSize;
		StringBuilder stringBuilder = sb;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(64, 2, stringBuilder);
		handler.AppendLiteral("  <pfs-image version=\"2\" readonly=\"true\" offset=\"");
		handler.AppendFormatted(pfsImageOffset);
		handler.AppendLiteral("\" metadata=\"");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\">\n");
		stringBuilder2.Append(ref handler);
		string value2 = (info.Signed ? " signed=\"true\"" : "");
		string value3 = (info.Encrypted ? " encrypted=\"true\"" : "");
		stringBuilder = sb;
		StringBuilder stringBuilder3 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(70, 3, stringBuilder);
		handler.AppendLiteral("    <sblock");
		handler.AppendFormatted(value2);
		handler.AppendFormatted(value3);
		handler.AppendLiteral(" ignore-case=\"true\" index-size=\"32\" blocks=\"");
		handler.AppendFormatted(info.DinodeBlockCount);
		handler.AppendLiteral("\" backups=\"0\">\n");
		stringBuilder3.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder4 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(53, 3, stringBuilder);
		handler.AppendLiteral("      <image-size block-size=\"");
		handler.AppendFormatted(info.BlockSize);
		handler.AppendLiteral("\" num=\"");
		handler.AppendFormatted(info.ImageBlocks);
		handler.AppendLiteral("\">");
		handler.AppendFormatted(Hex16Blob(info.ImageBlocks * info.BlockSize));
		handler.AppendLiteral("</image-size>\n");
		stringBuilder4.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder5 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(48, 3, stringBuilder);
		handler.AppendLiteral("      <super-inode blocks=\"");
		handler.AppendFormatted(info.DinodeBlockCount);
		handler.AppendLiteral("\" inodes=\"");
		handler.AppendFormatted(info.InodeCount);
		handler.AppendLiteral("\" root=\"");
		handler.AppendFormatted(info.RootInode);
		handler.AppendLiteral("\">\n");
		stringBuilder5.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder6 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(67, 3, stringBuilder);
		handler.AppendLiteral("        <inode size=\"");
		handler.AppendFormatted(info.DinodeSize);
		handler.AppendLiteral("\" links=\"1\" mode=\"0x0000\" imode=\"");
		handler.AppendFormatted(Imode(info.DinodeFlags));
		handler.AppendLiteral("\" index=\"");
		handler.AppendFormatted(info.DinodeBlock);
		handler.AppendLiteral("\"/>\n");
		stringBuilder6.Append(ref handler);
		sb.Append("      </super-inode>\n");
		stringBuilder = sb;
		StringBuilder stringBuilder7 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder);
		handler.AppendLiteral("      <seed>");
		handler.AppendFormatted(Hex16Blob(info.Seed, 16));
		handler.AppendLiteral("</seed>\n");
		stringBuilder7.Append(ref handler);
		stringBuilder = sb;
		StringBuilder stringBuilder8 = stringBuilder;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 1, stringBuilder);
		handler.AppendLiteral("      <icv>");
		handler.AppendFormatted(Hex16Blob(info.SuperblockIcv, 32));
		handler.AppendLiteral("</icv>\n");
		stringBuilder8.Append(ref handler);
		sb.Append("    </sblock>\n");
		OuterPfsTreeNode root = info.Root;
		if (root != null)
		{
			AppendOuterNode(sb, root, info.BlockSize, "    ");
		}
		sb.Append("  </pfs-image>\n");
	}

	private static void AppendOuterNode(StringBuilder sb, OuterPfsTreeNode n, int blockSize, string pad)
	{
		string pad2 = pad + "  ";
		if (n.IsDirectory)
		{
			string value = ((n.Name.Length == 0) ? "root" : "dir");
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(55, 8, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<");
			handler.AppendFormatted(value);
			handler.AppendLiteral(" size=\"");
			handler.AppendFormatted(n.StoredSize);
			handler.AppendLiteral("\" links=\"");
			handler.AppendFormatted(n.Nlink);
			handler.AppendLiteral("\" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" index=\"");
			handler.AppendFormatted(n.StartBlock);
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.Inode);
			handler.AppendLiteral("\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\">\n");
			stringBuilder2.Append(ref handler);
			foreach (OuterPfsTreeNode child in n.Children)
			{
				AppendOuterNode(sb, child, blockSize, pad2);
			}
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 2, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("</");
			handler.AppendFormatted(value);
			handler.AppendLiteral(">\n");
			stringBuilder3.Append(ref handler);
		}
		else
		{
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(51, 6, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<file size=\"");
			handler.AppendFormatted(n.StoredSize);
			handler.AppendLiteral("\" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" index=\"");
			handler.AppendFormatted(n.StartBlock);
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.Inode);
			handler.AppendLiteral("\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\"/>\n");
			stringBuilder4.Append(ref handler);
		}
	}

	private static void AppendNestedImageFromInner(StringBuilder sb, ProsperoInnerImageAssembler.InnerImageResult inner)
	{
		IReadOnlyList<ProsperoInnerImageAssembler.MetaNodeInfo> nodes = inner.Nodes;
		long metaBaseLogical = inner.MetaBaseLogical;
		Dictionary<ulong, long> dictionary = new Dictionary<ulong, long>();
		foreach (ProsperoInnerImageAssembler.Placement placement in inner.Placements)
		{
			dictionary[(ulong)placement.LogicalOffset] = placement.OnDiskOffset;
		}
		Dictionary<int, List<ProsperoInnerImageAssembler.MetaNodeInfo>> dictionary2 = new Dictionary<int, List<ProsperoInnerImageAssembler.MetaNodeInfo>>();
		ProsperoInnerImageAssembler.MetaNodeInfo metaNodeInfo = null;
		foreach (ProsperoInnerImageAssembler.MetaNodeInfo item in nodes)
		{
			if (item.IsDirectory && item.Name.Length == 0)
			{
				metaNodeInfo = item;
				continue;
			}
			if (!dictionary2.TryGetValue(item.ParentInode, out var value))
			{
				value = (dictionary2[item.ParentInode] = new List<ProsperoInnerImageAssembler.MetaNodeInfo>());
			}
			value.Add(item);
		}
		if (metaNodeInfo != null)
		{
			int num = nodes.Count((ProsperoInnerImageAssembler.MetaNodeInfo n) => !n.IsDirectory && n.ParentInode != -1);
			long num2 = inner.MetadataPlaintext.Length;
			long num3 = inner.CompressedMetadata.Length;
			sb.Append("  <nested-image version=\"2\" readonly=\"true\" offset=\"0\">\n");
			sb.Append("    <sblock ignore-case=\"true\" index-size=\"32\" blocks=\"1\" backups=\"0\">\n");
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(53, 3, stringBuilder);
			handler.AppendLiteral("      <image-size block-size=\"");
			handler.AppendFormatted(65536);
			handler.AppendLiteral("\" num=\"");
			handler.AppendFormatted(inner.Ndblock);
			handler.AppendLiteral("\">");
			handler.AppendFormatted(Hex16Blob(inner.Ndblock * 65536));
			handler.AppendLiteral("</image-size>\n");
			stringBuilder2.Append(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(50, 1, stringBuilder);
			handler.AppendLiteral("      <super-inode blocks=\"1\" inodes=\"");
			handler.AppendFormatted(nodes.Count);
			handler.AppendLiteral("\" root=\"0\">\n");
			stringBuilder3.Append(ref handler);
			stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(77, 2, stringBuilder);
			handler.AppendLiteral("        <inode size=\"");
			handler.AppendFormatted(65536);
			handler.AppendLiteral("\" links=\"1\" mode=\"0x0000\" imode=\"0x00000010\" index=\"");
			handler.AppendFormatted(metaBaseLogical / 65536 + 1);
			handler.AppendLiteral("\"/>\n");
			stringBuilder4.Append(ref handler);
			sb.Append("      </super-inode>\n");
			sb.Append("    </sblock>\n");
			stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(70, 6, stringBuilder);
			handler.AppendLiteral("    <metadata size=\"");
			handler.AppendFormatted(num3);
			handler.AppendLiteral("\" plain=\"");
			handler.AppendFormatted(num2);
			handler.AppendLiteral("\" comp=\"");
			handler.AppendFormatted(CompLabel(num3, num2, 65536));
			handler.AppendLiteral("\" offset=\"");
			handler.AppendFormatted(inner.MetadataOnDiskOffset);
			handler.AppendLiteral("\" poffset=\"");
			handler.AppendFormatted(metaBaseLogical);
			handler.AppendLiteral("\" afid=\"");
			handler.AppendFormatted(num + 1);
			handler.AppendLiteral("\"/>\n");
			stringBuilder5.Append(ref handler);
			AppendInnerMountNode(sb, metaNodeInfo, dictionary2, dictionary, "    ");
			sb.Append("  </nested-image>\n");
		}
	}

	private static void AppendInnerMountNode(StringBuilder sb, ProsperoInnerImageAssembler.MetaNodeInfo n, Dictionary<int, List<ProsperoInnerImageAssembler.MetaNodeInfo>> childrenOf, Dictionary<ulong, long> onDiskByLogical, string pad)
	{
		string pad2 = pad + "  ";
		if (n.IsDirectory)
		{
			bool flag = n.Name.Length == 0;
			int key = (flag ? (-1) : ((int)n.Inode));
			string value = (flag ? "root" : "dir");
			string value2 = (flag ? "" : (" mode=\"" + Mode4(n.Mode) + "\""));
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(58, 9, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<");
			handler.AppendFormatted(value);
			handler.AppendLiteral(" plain=\"");
			handler.AppendFormatted(65536);
			handler.AppendLiteral("\" poffset=\"");
			handler.AppendFormatted(n.LogicalOffset);
			handler.AppendLiteral("\" links=\"");
			handler.AppendFormatted(n.Nlink);
			handler.AppendLiteral("\"");
			handler.AppendFormatted(value2);
			handler.AppendLiteral(" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.Inode);
			handler.AppendLiteral("\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\">\n");
			stringBuilder2.Append(ref handler);
			if (childrenOf.TryGetValue(key, out List<ProsperoInnerImageAssembler.MetaNodeInfo> value3))
			{
				foreach (ProsperoInnerImageAssembler.MetaNodeInfo item in value3.OrderBy((ProsperoInnerImageAssembler.MetaNodeInfo c) => c.Inode))
				{
					AppendInnerMountNode(sb, item, childrenOf, onDiskByLogical, pad2);
				}
			}
			stringBuilder = sb;
			StringBuilder stringBuilder3 = stringBuilder;
			handler = new StringBuilder.AppendInterpolatedStringHandler(4, 2, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("</");
			handler.AppendFormatted(value);
			handler.AppendLiteral(">\n");
			stringBuilder3.Append(ref handler);
		}
		else if (n.ParentInode == -1)
		{
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder4 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(54, 6, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<file plain=\"");
			handler.AppendFormatted(n.Size);
			handler.AppendLiteral("\" poffset=\"");
			handler.AppendFormatted(n.LogicalOffset);
			handler.AppendLiteral("\" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.Inode);
			handler.AppendLiteral("\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\"/>\n");
			stringBuilder4.Append(ref handler);
		}
		else
		{
			long value4 = (onDiskByLogical.TryGetValue(n.LogicalOffset, out var value5) ? value5 : ((long)n.LogicalOffset));
			StringBuilder stringBuilder = sb;
			StringBuilder stringBuilder5 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(87, 9, stringBuilder);
			handler.AppendFormatted(pad);
			handler.AppendLiteral("<file size=\"");
			handler.AppendFormatted(n.Size);
			handler.AppendLiteral("\" plain=\"");
			handler.AppendFormatted(n.Size);
			handler.AppendLiteral("\" offset=\"");
			handler.AppendFormatted(value4);
			handler.AppendLiteral("\" mode=\"");
			handler.AppendFormatted(Mode4(n.Mode));
			handler.AppendLiteral("\" imode=\"");
			handler.AppendFormatted(Imode(n.Flags));
			handler.AppendLiteral("\" inode=\"");
			handler.AppendFormatted(n.Inode);
			handler.AppendLiteral("\" afid=\"");
			handler.AppendFormatted(n.Afid);
			handler.AppendLiteral("\" chunk=\"0\" name=\"");
			handler.AppendFormatted(n.Name);
			handler.AppendLiteral("\"/>\n");
			stringBuilder5.Append(ref handler);
		}
	}

	public static string FormatDigest(ReadOnlySpan<byte> digest)
	{
		ReadOnlySpan<byte> readOnlySpan = ((!digest.IsEmpty) ? digest : ((ReadOnlySpan<byte>)stackalloc byte[32]));
		ReadOnlySpan<byte> readOnlySpan2 = readOnlySpan;
		StringBuilder stringBuilder = new StringBuilder(readOnlySpan2.Length * 5);
		for (int i = 0; i < readOnlySpan2.Length; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append((i % 16 == 0) ? '\n' : ' ');
			}
			stringBuilder.Append("0x").Append(readOnlySpan2[i].ToString("x2", CultureInfo.InvariantCulture));
		}
		return stringBuilder.ToString();
	}

	private static string Indent(string digest, string pad)
	{
		return digest.Replace("\n", "\n" + pad);
	}

	private static string Hex16(long v)
	{
		return "0x" + v.ToString("x16", CultureInfo.InvariantCulture);
	}

	private static string Hex8(long v)
	{
		return "0x" + v.ToString("x8", CultureInfo.InvariantCulture);
	}

	private static string Imode(uint flags)
	{
		return "0x" + flags.ToString("x8", CultureInfo.InvariantCulture);
	}

	private static string Mode4(ushort mode)
	{
		return "0x" + mode.ToString("x4", CultureInfo.InvariantCulture);
	}

	private static string Hex16Blob(long v)
	{
		return "0x" + v.ToString("x16", CultureInfo.InvariantCulture);
	}

	private static string Hex16Blob(byte[]? data, int len)
	{
		StringBuilder stringBuilder = new StringBuilder(2 + len * 2);
		stringBuilder.Append("0x");
		for (int i = 0; i < len; i++)
		{
			stringBuilder.Append(((byte)((data != null && i < data.Length) ? data[i] : 0)).ToString("X2", CultureInfo.InvariantCulture));
		}
		return stringBuilder.ToString();
	}

	private static string CompLabel(long stored, long plain, int blockSize)
	{
		long value = ((plain > 0) ? ((long)Math.Round((double)stored * 100.0 / (double)plain)) : 0);
		long value2 = ((blockSize > 0) ? ((stored + blockSize - 1) / blockSize) : 0);
		long value3 = ((blockSize > 0) ? ((plain + blockSize - 1) / blockSize) : 0);
		return $"{value}% ({value2}/{value3})";
	}

	public static byte[] WriteZip(IReadOnlyList<ProsperoSiMember> members)
	{
		ArgumentNullException.ThrowIfNull(members, "members");
		using MemoryStream memoryStream = new MemoryStream();
		using BinaryWriter binaryWriter = new BinaryWriter(memoryStream, Encoding.ASCII, leaveOpen: true);
		(uint, int, int, long)[] array = new (uint, int, int, long)[members.Count];
		for (int i = 0; i < members.Count; i++)
		{
			ProsperoSiMember prosperoSiMember = members[i];
			byte[] bytes = Encoding.ASCII.GetBytes(prosperoSiMember.Path);
			uint num = ZipCrc32.Compute(prosperoSiMember.Content);
			long position = memoryStream.Position;
			array[i] = (num, prosperoSiMember.Content.Length, bytes.Length, position);
			binaryWriter.Write(67324752u);
			binaryWriter.Write((ushort)20);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)33);
			binaryWriter.Write(num);
			binaryWriter.Write((uint)prosperoSiMember.Content.Length);
			binaryWriter.Write((uint)prosperoSiMember.Content.Length);
			binaryWriter.Write((ushort)bytes.Length);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write(bytes);
			binaryWriter.Write(prosperoSiMember.Content);
		}
		long position2 = memoryStream.Position;
		for (int j = 0; j < members.Count; j++)
		{
			byte[] bytes2 = Encoding.ASCII.GetBytes(members[j].Path);
			binaryWriter.Write(33639248u);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)20);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)33);
			binaryWriter.Write(array[j].Item1);
			binaryWriter.Write((uint)array[j].Item2);
			binaryWriter.Write((uint)array[j].Item2);
			binaryWriter.Write((ushort)bytes2.Length);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write((ushort)0);
			binaryWriter.Write(0u);
			binaryWriter.Write((uint)array[j].Item4);
			binaryWriter.Write(bytes2);
		}
		long num2 = memoryStream.Position - position2;
		binaryWriter.Write(101010256u);
		binaryWriter.Write((ushort)0);
		binaryWriter.Write((ushort)0);
		binaryWriter.Write((ushort)members.Count);
		binaryWriter.Write((ushort)members.Count);
		binaryWriter.Write((uint)num2);
		binaryWriter.Write((uint)position2);
		binaryWriter.Write((ushort)0);
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}
}
