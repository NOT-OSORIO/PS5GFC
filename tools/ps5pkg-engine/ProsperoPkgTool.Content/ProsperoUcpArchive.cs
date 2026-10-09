using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ProsperoPkgTool.Content;

public static class ProsperoUcpArchive
{
	public sealed record Entry(string Name, byte[] Data);

	public const uint Magic = 2989016586u;

	public const uint Version = 1u;

	public const int HeaderSize = 96;

	public const int EntryRecordSize = 64;

	public const int NameFieldSize = 32;

	private const int DigestOffset = 28;

	private const int DigestSize = 20;

	private const int BlobAlignment = 16;

	public static bool IsUcp(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 96)
		{
			return BinaryPrimitives.ReadUInt32BigEndian(data) == 2989016586u;
		}
		return false;
	}

	public static IReadOnlyList<Entry> Read(ReadOnlySpan<byte> data)
	{
		if (!Validate(data, out string error))
		{
			throw new InvalidDataException(error);
		}
		int num = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16));
		List<Entry> list = new List<Entry>(num);
		for (int i = 0; i < num; i++)
		{
			int num2 = 96 + i * 64;
			ReadOnlySpan<byte> readOnlySpan = data.Slice(num2, 32);
			int num3 = readOnlySpan.IndexOf((byte)0);
			string name = Encoding.Latin1.GetString((num3 < 0) ? readOnlySpan : readOnlySpan.Slice(0, num3));
			int start = (int)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num2 + 32));
			int length = (int)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num2 + 40));
			list.Add(new Entry(name, data.Slice(start, length).ToArray()));
		}
		return list;
	}

	public static byte[] Build(IEnumerable<Entry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries, "entries");
		Entry[] array = entries.OrderBy((Entry e) => e.Name, StringComparer.Ordinal).ToArray();
		byte[][] array2 = new byte[array.Length][];
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		for (int num = 0; num < array.Length; num++)
		{
			string name = array[num].Name;
			if (string.IsNullOrEmpty(name))
			{
				throw new ArgumentException("A UCP entry name must not be empty.", "entries");
			}
			byte[] bytes = Encoding.Latin1.GetBytes(name);
			if (bytes.Length > 32)
			{
				throw new ArgumentException($"UCP entry name '{name}' exceeds {32} bytes.", "entries");
			}
			if (!hashSet.Add(name))
			{
				throw new ArgumentException("Duplicate UCP entry name '" + name + "'.", "entries");
			}
			array2[num] = bytes;
		}
		long num2 = 96 + (long)array.Length * 64L;
		long[] array3 = new long[array.Length];
		for (int num3 = 0; num3 < array.Length; num3++)
		{
			array3[num3] = num2;
			num2 = AlignUpStrict(num2 + array[num3].Data.Length);
		}
		long num4 = ((array.Length == 0) ? 96 : num2);
		byte[] array4 = new byte[num4];
		Span<byte> destination = array4;
		BinaryPrimitives.WriteUInt32BigEndian(destination, 2989016586u);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4), 1u);
		BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8), (ulong)num4);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(16), (uint)array.Length);
		BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(20), 64u);
		for (int num5 = 0; num5 < array.Length; num5++)
		{
			int num6 = 96 + num5 * 64;
			array2[num5].CopyTo(destination.Slice(num6));
			BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(num6 + 32), (ulong)array3[num5]);
			BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(num6 + 40), (ulong)array[num5].Data.Length);
			array[num5].Data.CopyTo(destination.Slice((int)array3[num5]));
		}
		WriteDigest(array4);
		return array4;
	}

	public static bool VerifyDigest(ReadOnlySpan<byte> data)
	{
		if (data.Length < 96)
		{
			return false;
		}
		Span<byte> span = stackalloc byte[20];
		data.Slice(28, 20).CopyTo(span);
		byte[] array = data.ToArray();
		Array.Clear(array, 28, 20);
		Span<byte> span2 = stackalloc byte[20];
		SHA1.HashData(array, span2);
		return ((ReadOnlySpan<byte>)span2).SequenceEqual((ReadOnlySpan<byte>)span);
	}

	public static byte[] WithRepairedDigest(ReadOnlySpan<byte> data)
	{
		if (!IsUcp(data))
		{
			throw new InvalidDataException("Buffer is not a UCP archive.");
		}
		byte[] array = data.ToArray();
		WriteDigest(array);
		return array;
	}

	public static byte[] RepairIfNeeded(byte[] data)
	{
		try
		{
			if (IsUcp(data) && !VerifyDigest(data))
			{
				return WithRepairedDigest(data);
			}
		}
		catch (InvalidDataException)
		{
		}
		return data;
	}

	public static bool IsRepairableSceSysPath(string sceSysRelativePath)
	{
		if (sceSysRelativePath.StartsWith("trophy2/", StringComparison.Ordinal) || sceSysRelativePath.StartsWith("uds/", StringComparison.Ordinal))
		{
			return sceSysRelativePath.EndsWith(".ucp", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	public static bool Validate(ReadOnlySpan<byte> data, out string? error)
	{
		if (data.Length < 96)
		{
			error = "Buffer is smaller than a UCP header.";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data) != 2989016586u)
		{
			error = "Bad UCP magic.";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)) != 1)
		{
			error = "Unsupported UCP version.";
			return false;
		}
		ulong num = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(8));
		if (num != (ulong)data.Length)
		{
			error = $"UCP size field ({num}) does not match buffer length ({data.Length}).";
			return false;
		}
		if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20)) != 64)
		{
			error = "Unexpected UCP entry-record size.";
			return false;
		}
		long num2 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16));
		long num3 = 96 + num2 * 64;
		if (num3 > data.Length)
		{
			error = "UCP entry table overruns the buffer.";
			return false;
		}
		for (int i = 0; i < num2; i++)
		{
			int num4 = 96 + i * 64;
			ulong num5 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num4 + 32));
			ulong num6 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(num4 + 40));
			if (num5 < (ulong)num3 || num5 + num6 > num)
			{
				error = $"UCP entry {i} range [{num5},{num5 + num6}) is outside the blob region.";
				return false;
			}
		}
		error = null;
		return true;
	}

	private static void WriteDigest(byte[] buffer)
	{
		Array.Clear(buffer, 28, 20);
		Span<byte> destination = stackalloc byte[20];
		SHA1.HashData(buffer, destination);
		destination.CopyTo(buffer.AsSpan(28));
	}

	private static long AlignUpStrict(long value)
	{
		return (value / 16 + 1) * 16;
	}
}
