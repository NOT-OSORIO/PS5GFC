using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ProsperoPkgTool.Crypto;

namespace ProsperoPkgTool.Containers;

public sealed class ProsperoCntBuilder
{
	public sealed class Entry
	{
		public required CntEntryId Id { get; init; }

		public string? Name { get; init; }

		public required byte[] Data { get; set; }

		public uint Flags1 { get; init; }

		public uint Flags2 { get; init; }

		public uint DataOffset { get; internal set; }

		public uint DataSize => (uint)Data.Length;
	}

	private const uint FlagsPs5 = 131073u;

	private const uint Unk08Ps5 = 2147483648u;

	private const uint Unk0CPs5 = 12u;

	private const ulong PfsFlags = 11529215046068470540uL;

	public const ulong BodyOffset = 8192uL;

	private const int HeaderSize = 1440;

	public const int PackageDigestStoredOffset = 4064;

	public const int HeaderSignatureOffset = 4096;

	private const int FihRelativeImageOffset = 65536;

	public const int BlockSize = 65536;

	private static readonly uint[] SystemMediaIds = new uint[10] { 4102u, 4109u, 4608u, 4640u, 4672u, 4736u, 4768u, 4800u, 8256u, 8288u };

	private static readonly uint[] PlaygoIds = new uint[3] { 4097u, 8208u, 8209u };

	private const int ScEntryCount = 6;

	public static byte[] Build(IReadOnlyList<Entry> entries, CntBuildParameters parameters, byte[] pfsImage, byte[] imageSeed, byte[] pfsImageDigest, byte[]? nestedImageDigest = null, long nestedImageSize = 0L, byte[]? pfsSignedDigest = null)
	{
		ArgumentNullException.ThrowIfNull(pfsImage, "pfsImage");
		(byte[] Prefix, ulong PfsImageOffset) tuple = BuildPrefix(entries, parameters, pfsImage.LongLength, imageSeed, pfsImageDigest, pfsSignedDigest);
		byte[] item = tuple.Prefix;
		ulong item2 = tuple.PfsImageOffset;
		checked
		{
			byte[] array = new byte[(long)item2 + pfsImage.LongLength];
			item.CopyTo(array, 0);
			Buffer.BlockCopy(pfsImage, 0, array, (int)item2, pfsImage.Length);
			return array;
		}
	}

	public static void BuildToFile(IReadOnlyList<Entry> entries, CntBuildParameters parameters, string pfsImagePath, long pfsImageLength, string outputPath, byte[] imageSeed, byte[] pfsImageDigest, byte[]? pfsSignedDigest = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pfsImagePath, "pfsImagePath");
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath, "outputPath");
		(byte[] Prefix, ulong PfsImageOffset) tuple = BuildPrefix(entries, parameters, pfsImageLength, imageSeed, pfsImageDigest, pfsSignedDigest);
		byte[] item = tuple.Prefix;
		ulong item2 = tuple.PfsImageOffset;
		string fullPath = Path.GetFullPath(outputPath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
		using FileStream fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.SequentialScan);
		using FileStream fileStream2 = new FileStream(pfsImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.SequentialScan);
		fileStream.SetLength(checked((long)item2 + pfsImageLength));
		fileStream.Write(item, 0, item.Length);
		fileStream2.CopyTo(fileStream, 1048576);
		fileStream.Flush();
	}

	public static byte[] BuildMetadata(IReadOnlyList<Entry> entries, CntBuildParameters parameters, long pfsImageLength, byte[] imageSeed, byte[] pfsImageDigest, byte[]? pfsSignedDigest = null)
	{
		return BuildPrefix(entries, parameters, pfsImageLength, imageSeed, pfsImageDigest, pfsSignedDigest).Prefix;
	}

	private static (byte[] Prefix, ulong PfsImageOffset) BuildPrefix(IReadOnlyList<Entry> entries, CntBuildParameters parameters, long pfsImageLength, byte[] imageSeed, byte[] pfsImageDigest, byte[]? pfsSignedDigest)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		ArgumentNullException.ThrowIfNull(parameters, "parameters");
		ArgumentNullException.ThrowIfNull(imageSeed, "imageSeed");
		ArgumentNullException.ThrowIfNull(pfsImageDigest, "pfsImageDigest");
		if (parameters.ContentId.Length != 36)
		{
			throw new ArgumentException("Content id must be exactly 36 characters.", "parameters");
		}
		Entry entry = entries.First((Entry e) => e.Id == CntEntryId.Metas);
		entry.Data = new byte[entries.Count * 32];
		Entry entry2 = entries.First((Entry e) => e.Id == CntEntryId.Digests);
		entry2.Data = new byte[entries.Count * 32];
		Entry nameTableEntry = entries.First((Entry e) => e.Id == CntEntryId.EntryNames);
		Dictionary<Entry, uint> dictionary = BuildNameTable(entries, nameTableEntry);
		ulong num = 8192uL;
		foreach (Entry entry5 in entries)
		{
			entry5.DataOffset = (uint)num;
			num = Align(num + (ulong)StoredLength(entry5), 16uL);
		}
		ulong num2 = num;
		ulong num3 = Align(8192 + (num2 - 8192), 65536uL) - 8192;
		ulong num4 = 8192 + num3;
		List<Entry> sorted = entries.OrderBy((Entry e) => (uint)e.Id).ToList();
		byte[] metaTable = new byte[entries.Count * 32];
		for (int num5 = 0; num5 < sorted.Count; num5++)
		{
			Entry entry3 = sorted[num5];
			Span<byte> destination = metaTable.AsSpan(num5 * 32, 32);
			BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)entry3.Id);
			uint value = (dictionary.TryGetValue(entry3, out var value2) ? value2 : 0u);
			BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4), value);
			BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(8), entry3.Flags1);
			BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(12), entry3.Flags2);
			BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(16), entry3.DataOffset);
			BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(20), entry3.DataSize);
		}
		entry.Data = metaTable;
		Dictionary<Entry, byte[]> stored = new Dictionary<Entry, byte[]>();
		uint dataOffset = entry.DataOffset;
		Entry imageKey = entries.First((Entry e) => e.Id == CntEntryId.ImageKey);
		Entry entry4 = entries.First((Entry e) => e.Id == CntEntryId.Imagedigs);
		ulong mandatorySize = entry4.DataOffset;
		ulong num6 = num4;
		ulong mountImageSize = (ulong)(65536 + pfsImageLength) + num6;
		ulong cntRegionOffset = (ulong)(65536 + pfsImageLength);
		ulong cntRegionSize = num6;
		byte[] array = new byte[checked((long)num4)];
		byte[] array2 = new byte[32];
		WriteHeader(array, entries, parameters, pfsImageLength, num4, num3, imageSeed, pfsImageDigest, mandatorySize, dataOffset, array2, array2, array2, array2, imageKey, entry4, mountImageSize, cntRegionOffset, cntRegionSize, pfsSignedDigest);
		byte[] contentDigest = BuildContentDigest(parameters, pfsImageDigest);
		WriteGeneralDigestsEntry(array, entries, parameters, pfsImageDigest, contentDigest);
		int num7 = sorted.IndexOf(entry2);
		byte[] array3 = new byte[entries.Count * 32];
		for (int num8 = 0; num8 < sorted.Count; num8++)
		{
			if (num8 != num7)
			{
				Sha3.Sha3_256(GetStored(sorted[num8])).CopyTo(array3.AsSpan(num8 * 32));
			}
		}
		entry2.Data = array3;
		byte[] array4 = new byte[num2 - 8192];
		foreach (Entry entry6 in entries)
		{
			int start = (int)(entry6.DataOffset - 8192);
			byte[] array5 = GetStored(entry6);
			array5.CopyTo(array4.AsSpan(start, array5.Length));
		}
		Buffer.BlockCopy(array4, 0, array, 8192, array4.Length);
		byte[] scEntries1Hash = ComputeScEntries1Hash(entries);
		byte[] scEntries2Hash = ComputeScEntries2Hash(entries);
		byte[] array6 = new byte[num3];
		Buffer.BlockCopy(array4, 0, array6, 0, array4.Length);
		byte[] bodyDigest = Sha3.Sha3_256(array6);
		byte[] digestTableHash = Sha3.Sha3_256(array3);
		WriteHeader(array, entries, parameters, pfsImageLength, num4, num3, imageSeed, pfsImageDigest, mandatorySize, dataOffset, scEntries1Hash, scEntries2Hash, bodyDigest, digestTableHash, imageKey, entry4, mountImageSize, cntRegionOffset, cntRegionSize, pfsSignedDigest);
		byte[] array7 = (byte[])array.Clone();
		byte[] source = Sha3.Sha3_256(array7.AsSpan(0, 4064));
		source.CopyTo(array7.AsSpan(4064));
		source.CopyTo(array.AsSpan(4064));
		ProsperoRsaKeys.BuildCntHeaderWrap(array7.AsSpan(0, 4096)).CopyTo(array.AsSpan(4096, 384));
		return (Prefix: array, PfsImageOffset: num4);
		byte[] GetStored(Entry e)
		{
			if (stored.TryGetValue(e, out byte[] value3))
			{
				return value3;
			}
			if (!IsEncrypted(e))
			{
				stored[e] = e.Data;
				return e.Data;
			}
			ReadOnlySpan<byte> metaRow = metaTable.AsSpan(sorted.IndexOf(e) * 32, 32);
			uint keyIndex = (e.Flags2 & 0xF000) >> 12;
			byte[] array8 = ProsperoEntryCipher.Encrypt(e.Data, metaRow, parameters.ContentId, parameters.Passcode, keyIndex, publisherProfile: true);
			stored[e] = array8;
			return array8;
		}
	}

	public static List<Entry> BuildStandardEntries(string contentId, string passcode, byte[] ekpfs, bool deterministicEntryKeys = false)
	{
		int num = 6;
		List<Entry> list = new List<Entry>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<Entry> span = CollectionsMarshal.AsSpan(list);
		span[0] = new Entry
		{
			Id = CntEntryId.EntryKeys,
			Name = null,
			Data = ProsperoRsaKeys.BuildEntryKeysEntry(contentId, passcode, deterministicEntryKeys),
			Flags1 = 1610612736u
		};
		span[1] = new Entry
		{
			Id = CntEntryId.ImageKey,
			Name = null,
			Data = ProsperoRsaKeys.BuildImageKeyEntry(ekpfs, deterministicEntryKeys),
			Flags1 = 1610612736u
		};
		span[2] = new Entry
		{
			Id = CntEntryId.GeneralDigests,
			Name = null,
			Data = new byte[480],
			Flags1 = 1610612736u
		};
		span[3] = new Entry
		{
			Id = CntEntryId.Metas,
			Name = null,
			Data = new byte[0],
			Flags1 = 1610612736u
		};
		span[4] = new Entry
		{
			Id = CntEntryId.Digests,
			Name = null,
			Data = new byte[0],
			Flags1 = 1073741824u
		};
		span[5] = new Entry
		{
			Id = CntEntryId.EntryNames,
			Name = null,
			Data = new byte[1],
			Flags1 = 1073741824u
		};
		return list;
	}

	public static uint Flags1For(CntEntryId id)
	{
		return id switch
		{
			CntEntryId.Digests => 1073741824u,
			CntEntryId.EntryKeys => 1610612736u,
			CntEntryId.ImageKey => 1610612736u,
			CntEntryId.GeneralDigests => 1610612736u,
			CntEntryId.Metas => 1610612736u,
			CntEntryId.EntryNames => 1073741824u,
			CntEntryId.ParamJson => 0u,
			_ => 134217728u,
		};
	}

	private static int IndexOf(IReadOnlyList<Entry> entries, Entry e)
	{
		for (int i = 0; i < entries.Count; i++)
		{
			if (entries[i] == e)
			{
				return i;
			}
		}
		return -1;
	}

	private static ulong Align(ulong value, ulong align)
	{
		ulong num = value % align;
		if (num != 0L)
		{
			return value + align - num;
		}
		return value;
	}

	private static bool IsEncrypted(Entry entry)
	{
		return (entry.Flags1 & 0x80000000u) != 0;
	}

	private static int StoredLength(Entry entry)
	{
		if (!IsEncrypted(entry))
		{
			return entry.Data.Length;
		}
		return ProsperoEntryCipher.PaddedLength(entry.Data.Length);
	}

	private static Dictionary<Entry, uint> BuildNameTable(IReadOnlyList<Entry> entries, Entry nameTableEntry)
	{
		Dictionary<Entry, uint> dictionary = new Dictionary<Entry, uint>();
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteByte(0);
		foreach (Entry item in entries.Where((Entry e) => !string.IsNullOrEmpty(e.Name)).OrderBy((Entry e) => e.Name, StringComparer.Ordinal).ToList())
		{
			dictionary[item] = (uint)memoryStream.Length;
			byte[] bytes = Encoding.ASCII.GetBytes(item.Name);
			memoryStream.Write(bytes, 0, bytes.Length);
			memoryStream.WriteByte(0);
		}
		nameTableEntry.Data = memoryStream.ToArray();
		return dictionary;
	}

	private static byte[]? ComputeConcatOverEntries(IReadOnlyList<Entry> entries, uint[] ids)
	{
		List<Entry> list = new List<Entry>();
		foreach (uint num in ids)
		{
			for (int j = 0; j < entries.Count; j++)
			{
				if (entries[j].Id == (CntEntryId)num)
				{
					list.Add(entries[j]);
					break;
				}
			}
		}
		if (list.Count == 0)
		{
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		foreach (Entry item in list)
		{
			memoryStream.Write(Sha3.Sha3_256(item.Data));
		}
		return Sha3.Sha3_256(memoryStream.ToArray());
	}

	private static byte[] ComputeScEntries1Hash(IReadOnlyList<Entry> entries)
	{
		using MemoryStream memoryStream = new MemoryStream();
		uint[] array = new uint[5] { 16u, 32u, 128u, 256u, 1u };
		foreach (uint id in array)
		{
			Entry entry = FindById(entries, id);
			if (entry != null)
			{
				memoryStream.Write(entry.Data);
			}
		}
		return Sha3.Sha3_256(memoryStream.ToArray());
	}

	private static byte[] ComputeScEntries2Hash(IReadOnlyList<Entry> entries)
	{
		using MemoryStream memoryStream = new MemoryStream();
		uint[] array = new uint[3] { 16u, 32u, 128u };
		foreach (uint id in array)
		{
			Entry entry = FindById(entries, id);
			if (entry != null)
			{
				memoryStream.Write(entry.Data);
			}
		}
		Entry entry2 = entries.First((Entry e) => e.Id == CntEntryId.Metas);
		memoryStream.Write(entry2.Data.AsSpan(0, 192));
		return Sha3.Sha3_256(memoryStream.ToArray());
	}

	private static Entry? FindById(IReadOnlyList<Entry> entries, uint id)
	{
		for (int i = 0; i < entries.Count; i++)
		{
			if (entries[i].Id == (CntEntryId)id)
			{
				return entries[i];
			}
		}
		return null;
	}

	private static byte[] BuildContentDigest(CntBuildParameters parameters, byte[] gameDigest)
	{
		byte[] array = new byte[56];
		Encoding.ASCII.GetBytes(parameters.ContentId).AsSpan(0, 36).CopyTo(array);
		BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(48), 0u);
		BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(52), parameters.ContentType);
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(array);
		memoryStream.Write(gameDigest);
		memoryStream.Write(new byte[32]);
		return Sha3.Sha3_256(memoryStream.ToArray());
	}

	private static void WriteGeneralDigestsEntry(byte[] cnt, IReadOnlyList<Entry> entries, CntBuildParameters parameters, byte[] gameDigest, byte[] contentDigest)
	{
		byte[] data = entries.First((Entry e) => e.Id == CntEntryId.GeneralDigests).Data;
		if (data.Length != 480)
		{
			return;
		}
		Span<byte> destination = data;
		BinaryPrimitives.WriteUInt16BigEndian(destination, 53846);
		BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2), 258);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(28), 4318u);
		int num = 32;
		contentDigest.CopyTo(destination.Slice(num, 32));
		num += 32;
		gameDigest.CopyTo(destination.Slice(num, 32));
		num += 32;
		byte[] array = cnt.AsSpan(0, 64).ToArray();
		byte[] array2 = (byte[])cnt.AsSpan(1024, 128).ToArray().Clone();
		BinaryPrimitives.WriteUInt64BigEndian(array2.AsSpan(16), 65536uL);
		using MemoryStream memoryStream = new MemoryStream();
		memoryStream.Write(array);
		memoryStream.Write(array2);
		Sha3.Sha3_256(memoryStream.ToArray()).CopyTo(destination.Slice(num, 32));
		num += 32;
		ComputeConcatOverEntries(entries, SystemMediaIds)?.CopyTo(destination.Slice(num, 32));
		num += 32;
		num += 32;
		Entry entry = entries.FirstOrDefault((Entry e) => e.Id == CntEntryId.ParamJson);
		if (entry != null)
		{
			Sha3.Sha3_256(entry.Data).CopyTo(destination.Slice(num, 32));
		}
		num += 32;
		ComputeConcatOverEntries(entries, PlaygoIds)?.CopyTo(destination.Slice(num, 32));
		num += 32;
		num += 32;
		num += 32;
		num += 32;
		num += 32;
		gameDigest.CopyTo(destination.Slice(num, 32));
		num += 32;
		num += 32;
		num += 32;
	}

	private static void WriteHeader(byte[] cnt, IReadOnlyList<Entry> entries, CntBuildParameters parameters, long pfsImageSize, ulong pfsImageOffset, ulong bodySize, byte[] imageSeed, byte[] pfsImageDigest, ulong mandatorySize, uint entryTableOffset, byte[] scEntries1Hash, byte[] scEntries2Hash, byte[] bodyDigest, byte[] digestTableHash, Entry imageKey, Entry mandatory, ulong mountImageSize, ulong cntRegionOffset, ulong cntRegionSize, byte[]? pfsSignedDigest)
	{
		Span<byte> span = cnt.AsSpan(0, 1440);
		span[0] = 127;
		span[1] = 67;
		span[2] = 78;
		span[3] = 84;
		BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4), 2);
		BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6), 1);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8), 2147483648u);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(12), 12u);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(16), (uint)entries.Count);
		BinaryPrimitives.WriteUInt16BigEndian(span.Slice(20), 6);
		BinaryPrimitives.WriteUInt16BigEndian(span.Slice(22), (ushort)entries.Count);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(24), entryTableOffset);
		uint num = 0u;
		foreach (Entry entry in entries)
		{
			bool flag;
			switch ((uint)entry.Id)
			{
			case 1u:
			case 16u:
			case 32u:
			case 128u:
			case 256u:
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				num += entry.DataSize;
			}
		}
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(28), num);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(32), 8192uL);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(40), bodySize);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(48), mandatorySize);
		Encoding.ASCII.GetBytes(parameters.ContentId).AsSpan(0, 36).CopyTo(span.Slice(64));
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(112), 0u);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(116), parameters.ContentType);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(120), parameters.ContentFlags);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(124), (uint)pfsImageOffset);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(128), parameters.VersionDate);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(132), parameters.VersionHash);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(152), 0u);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(156), 0u);
		scEntries1Hash.CopyTo(span.Slice(256));
		scEntries2Hash.CopyTo(span.Slice(288));
		digestTableHash.CopyTo(span.Slice(320));
		bodyDigest.CopyTo(span.Slice(352));
		Encoding.ASCII.GetBytes(parameters.ContentId).AsSpan(0, 36).CopyTo(span.Slice(512));
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1024), 1u);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1028), 1u);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1032), 11529215046068470540uL);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1040), 65536uL);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1048), (ulong)pfsImageSize);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1056), 0uL);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1064), mountImageSize);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1072), mountImageSize);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1080), 65536u);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1084), 0u);
		pfsImageDigest.CopyTo(span.Slice(1088));
		if (pfsSignedDigest != null && pfsSignedDigest.Length == 32)
		{
			pfsSignedDigest.CopyTo(span.Slice(1120));
		}
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1152), 0uL);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1160), 0uL);
		imageSeed.CopyTo(span.Slice(1184));
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1200), cntRegionOffset);
		BinaryPrimitives.WriteUInt64BigEndian(span.Slice(1208), cntRegionSize);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1296), imageKey.DataOffset);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1300), imageKey.DataSize);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1304), mandatory.DataOffset);
		BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1308), mandatory.DataSize);
		Sha3.Sha3_256(imageKey.Data).CopyTo(span.Slice(1312));
		Sha3.Sha3_256(mandatory.Data).CopyTo(span.Slice(1344));
	}
}
