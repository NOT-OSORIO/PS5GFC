using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProsperoPkgTool.Content;

namespace ProsperoPkgTool.Containers;

public static class ProsperoPublisherParamJson
{
	public const string ToolVersion = "2.79";

	private const ulong DefaultElfSdkVersion = 311029849265274880uL;

	private const ulong RequiredSystemSoftwareVersionCap = 648518346341351424uL;

	private static readonly string[] SharingServiceIds = (from _ in Enumerable.Range(0, 7)
		select new string(' ', 19)).ToArray();

	public static byte[] Build(ReadOnlySpan<byte> sourceParamJson, DateTime timestamp, ulong sdkVersion)
	{
		JsonNode jsonNode;
		try
		{
			jsonNode = JsonNode.Parse(sourceParamJson) ?? throw new InvalidDataException("sce_sys/param.json is empty.");
		}
		catch (JsonException innerException)
		{
			throw new InvalidDataException("sce_sys/param.json is not valid JSON.", innerException);
		}
		JsonObject obj = (jsonNode as JsonObject) ?? throw new InvalidDataException("sce_sys/param.json must contain a JSON object.");
		string text = obj["versionFileUri"]?.GetValue<string>() ?? string.Empty;
		if (text.Length > 255)
		{
			throw new InvalidDataException($"param.json versionFileUri is {text.Length} characters; the publisher field is limited to 255.");
		}
		obj["versionFileUri"] = text.PadRight(255, ' ');
		JsonNode jsonNode2 = (obj["pubtools"] as JsonObject)?["loudnessSnd0"]?.DeepClone();
		JsonObject jsonObject = new JsonObject
		{
			["creationDate"] = timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
			["toolVersion"] = "2.79"
		};
		if (jsonNode2 != null)
		{
			jsonObject["loudnessSnd0"] = jsonNode2;
		}
		obj["pubtools"] = jsonObject;
		obj["sdkVersion"] = VersionText(sdkVersion);
		ulong value = Math.Max(Math.Min(HexVersion(obj["requiredSystemSoftwareVersion"]), 648518346341351424uL), sdkVersion);
		obj["requiredSystemSoftwareVersion"] = VersionText(value);
		obj["applicationCategoryType"] = 0;
		JsonObject jsonObject2 = obj;
		if (jsonObject2["applicationDrmType"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["applicationDrmType"] = "standard");
		}
		jsonObject2 = obj;
		if (jsonObject2["attribute"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["attribute"] = 0);
		}
		jsonObject2 = obj;
		if (jsonObject2["attribute2"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["attribute2"] = 0);
		}
		jsonObject2 = obj;
		if (jsonObject2["attribute3"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["attribute3"] = 0);
		}
		jsonObject2 = obj;
		if (jsonObject2["conceptId"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["conceptId"] = "10000000");
		}
		jsonObject2 = obj;
		if (jsonObject2["masterVersion"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["masterVersion"] = "01.00");
		}
		jsonObject2 = obj;
		if (jsonObject2["contentVersion"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["contentVersion"] = "01.000.000");
		}
		jsonObject2 = obj;
		if (jsonObject2["ageLevel"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["ageLevel"] = new JsonObject
			{
				["JP"] = 0,
				["US"] = 0,
				["default"] = 0
			});
		}
		jsonObject2 = obj;
		if (jsonObject2["contentBadgeType"] == null)
		{
			JsonNode jsonNode3 = (jsonObject2["contentBadgeType"] = 1);
		}
		obj.Remove("originContentVersion");
		obj.Remove("targetContentVersion");
		obj["addcont"] = new JsonObject { ["serviceIdForSharing"] = new JsonArray(((IEnumerable<string>)SharingServiceIds).Select((Func<string, JsonNode>)((string id) => JsonValue.Create(id))).ToArray()) };
		string text2 = SortOrdinal(obj).ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		text2 = text2.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text2);
	}

	public static byte[] ApplySdkOverride(ReadOnlySpan<byte> sourceParamJson, ulong executableVersion)
	{
		JsonNode jsonNode;
		try
		{
			jsonNode = JsonNode.Parse(sourceParamJson) ?? throw new InvalidDataException("sce_sys/param.json is empty.");
		}
		catch (JsonException innerException)
		{
			throw new InvalidDataException("sce_sys/param.json is not valid JSON.", innerException);
		}
		JsonObject obj = (jsonNode as JsonObject) ?? throw new InvalidDataException("sce_sys/param.json must contain a JSON object.");
		ulong num = executableVersion & 0xFFFF000000000000uL;
		obj["sdkVersion"] = VersionText(num);
		ulong value = Math.Max(Math.Min(HexVersion(obj["requiredSystemSoftwareVersion"]), 648518346341351424uL), num);
		obj["requiredSystemSoftwareVersion"] = VersionText(value);
		string text = SortOrdinal(obj).ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
	}

	public static byte[] ApplyDrmType(ReadOnlySpan<byte> sourceParamJson, string drmType)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(drmType, "drmType");
		JsonNode jsonNode;
		try
		{
			jsonNode = JsonNode.Parse(sourceParamJson) ?? throw new InvalidDataException("sce_sys/param.json is empty.");
		}
		catch (JsonException innerException)
		{
			throw new InvalidDataException("sce_sys/param.json is not valid JSON.", innerException);
		}
		JsonObject obj = (jsonNode as JsonObject) ?? throw new InvalidDataException("sce_sys/param.json must contain a JSON object.");
		obj["applicationDrmType"] = drmType;
		string text = SortOrdinal(obj).ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
	}

	public static byte[] ApplyTitleOverride(ReadOnlySpan<byte> sourceParamJson, string titleName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(titleName, "titleName");
		JsonObject jsonObject = ParseObject(sourceParamJson);
		if (jsonObject["titleName"] != null)
		{
			jsonObject["titleName"] = titleName;
		}
		if (jsonObject["localizedParameters"] is JsonObject jsonObject2)
		{
			foreach (KeyValuePair<string, JsonNode> item in jsonObject2.ToList())
			{
				if (item.Value is JsonObject jsonObject3 && jsonObject3["titleName"] != null)
				{
					jsonObject3["titleName"] = titleName;
				}
			}
			jsonObject2["default"] = SetTitle(jsonObject2["default"] as JsonObject, titleName);
			jsonObject2["en-US"] = SetTitle(jsonObject2["en-US"] as JsonObject, titleName);
		}
		else
		{
			jsonObject["localizedParameters"] = new JsonObject
			{
				["default"] = new JsonObject { ["titleName"] = titleName },
				["defaultLanguage"] = "en-US",
				["en-US"] = new JsonObject { ["titleName"] = titleName }
			};
		}
		return Serialize(jsonObject);
		static JsonObject SetTitle(JsonObject? entry, string title)
		{
			if (entry == null)
			{
				entry = new JsonObject();
			}
			entry["titleName"] = title;
			return entry;
		}
	}

	public static byte[] ApplyTitleIdOverride(ReadOnlySpan<byte> sourceParamJson, string titleId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(titleId, "titleId");
		JsonObject jsonObject = ParseObject(sourceParamJson);
		jsonObject["titleId"] = titleId;
		if (titleId.Length == 9)
		{
			string text = jsonObject["contentId"]?.GetValue<string>();
			if (text != null && text.Length == 36)
			{
				jsonObject["contentId"] = string.Concat(text.AsSpan(0, 7), titleId, text.AsSpan(16));
			}
		}
		return Serialize(jsonObject);
	}

	private static JsonObject ParseObject(ReadOnlySpan<byte> source)
	{
		JsonNode jsonNode;
		try
		{
			jsonNode = JsonNode.Parse(source) ?? throw new InvalidDataException("sce_sys/param.json is empty.");
		}
		catch (JsonException innerException)
		{
			throw new InvalidDataException("sce_sys/param.json is not valid JSON.", innerException);
		}
		return (jsonNode as JsonObject) ?? throw new InvalidDataException("sce_sys/param.json must contain a JSON object.");
	}

	private static byte[] Serialize(JsonObject root)
	{
		string text = SortOrdinal(root).ToJsonString(new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
		text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
		return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
	}

	public static ulong ResolveSdkVersion(ReadOnlySpan<byte> eboot)
	{
		if (eboot.Length == 0)
		{
			return 0uL;
		}
		if (ProsperoSelfBuilder.IsElf(eboot))
		{
			if (!TryGetSdkVersion(eboot, out var sdkVersion))
			{
				return 311029849265274880uL;
			}
			return sdkVersion;
		}
		if (ProsperoSelfBuilder.IsSelf(eboot) && TryGetSdkVersion(eboot, out var sdkVersion2))
		{
			return sdkVersion2;
		}
		return 0uL;
	}

	private static bool TryGetSdkVersion(ReadOnlySpan<byte> image, out ulong sdkVersion)
	{
		sdkVersion = 0uL;
		ReadOnlySpan<byte> readOnlySpan = (ProsperoSelfBuilder.IsElf(image) ? FindElfSection(image, ".sceversion") : SelfTrailer(image));
		if (readOnlySpan.IsEmpty)
		{
			return false;
		}
		int num = -1;
		int num2 = -1;
		int num4;
		for (int i = 0; i + 4 <= readOnlySpan.Length; i += num4)
		{
			int num3 = BinaryPrimitives.ReadUInt16LittleEndian(readOnlySpan.Slice(i + 2));
			num4 = num3 + 4;
			if (num3 < 17 || num4 > readOnlySpan.Length - i)
			{
				break;
			}
			int num5 = i + 5 + (num3 - 17);
			int num6 = readOnlySpan[num5];
			int num7 = readOnlySpan[num5 + 1];
			if (num6 > num || (num6 == num && num7 > num2))
			{
				num = num6;
				num2 = num7;
			}
		}
		bool flag = ((num < 0 || num > 99) ? true : false);
		bool flag2 = flag;
		if (!flag2)
		{
			bool flag3 = ((num2 < 0 || num2 > 99) ? true : false);
			flag2 = flag3;
		}
		if (flag2)
		{
			return false;
		}
		sdkVersion = ((ulong)PackedBcd(num) << 56) | ((ulong)PackedBcd(num2) << 48);
		return true;
		static byte PackedBcd(int value)
		{
			return (byte)((value / 10 << 4) | (value % 10));
		}
	}

	private static ReadOnlySpan<byte> SelfTrailer(ReadOnlySpan<byte> image)
	{
		if (!ProsperoSelfBuilder.IsSelf(image) || image.Length < 24)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(16));
		if (num > (ulong)image.Length)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		return image.Slice((int)num);
	}

	private static ReadOnlySpan<byte> FindElfSection(ReadOnlySpan<byte> elf, string sectionName)
	{
		if (elf.Length < 64)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		ulong num = BinaryPrimitives.ReadUInt64LittleEndian(elf.Slice(40));
		int num2 = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(58));
		int num3 = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(60));
		int num4 = BinaryPrimitives.ReadUInt16LittleEndian(elf.Slice(62));
		if (num == 0L || num2 < 64 || num3 == 0 || num4 >= num3 || num > int.MaxValue)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		int num5 = (int)num;
		if (num5 + (long)num3 * (long)num2 > elf.Length)
		{
			return ReadOnlySpan<byte>.Empty;
		}
		ReadOnlySpan<byte> readOnlySpan = SectionPayload(elf, num5 + num4 * num2);
		for (int i = 0; i < num3; i++)
		{
			int num6 = num5 + i * num2;
			uint num7 = BinaryPrimitives.ReadUInt32LittleEndian(elf.Slice(num6));
			if (num7 < (uint)readOnlySpan.Length)
			{
				ReadOnlySpan<byte> span = readOnlySpan.Slice((int)num7);
				int num8 = span.IndexOf((byte)0);
				if (num8 >= 0 && span.Slice(0, num8).SequenceEqual(Encoding.ASCII.GetBytes(sectionName)))
				{
					return SectionPayload(elf, num6);
				}
			}
		}
		return ReadOnlySpan<byte>.Empty;
		static ReadOnlySpan<byte> SectionPayload(ReadOnlySpan<byte> source, int header)
		{
			ulong num9 = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(header + 24));
			ulong num10 = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(header + 32));
			if (num9 > int.MaxValue || num10 > int.MaxValue || num9 + num10 > (ulong)source.Length)
			{
				return ReadOnlySpan<byte>.Empty;
			}
			return source.Slice((int)num9, (int)num10);
		}
	}

	private static ulong HexVersion(JsonNode? node)
	{
		string text = node?.GetValue<string>();
		if (text == null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result))
		{
			return 0uL;
		}
		return result;
	}

	private static string VersionText(ulong value)
	{
		return $"0x{value:X16}".ToLowerInvariant();
	}

	private static JsonNode SortOrdinal(JsonNode? node)
	{
		if (!(node is JsonObject source))
		{
			if (node is JsonArray jsonArray)
			{
				JsonArray jsonArray2 = new JsonArray();
				{
					foreach (JsonNode item in jsonArray)
					{
						jsonArray2.Add(SortOrdinal(item));
					}
					return jsonArray2;
				}
			}
			return node?.DeepClone() ?? JsonValue.Create((string?)null, (JsonNodeOptions?)null);
		}
		JsonObject jsonObject = new JsonObject();
		foreach (KeyValuePair<string, JsonNode> item2 in source.OrderBy((KeyValuePair<string, JsonNode> p) => p.Key, StringComparer.Ordinal))
		{
			jsonObject[item2.Key] = SortOrdinal(item2.Value);
		}
		return jsonObject;
	}
}
