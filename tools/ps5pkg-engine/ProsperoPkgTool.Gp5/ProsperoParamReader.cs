using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProsperoPkgTool.Gp5;

public static class ProsperoParamReader
{
	public static ProsperoParam Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		return Read(File.ReadAllBytes(path));
	}

	public static ProsperoParam Read(byte[] data)
	{
		ArgumentNullException.ThrowIfNull(data, "data");
		using MemoryStream stream = new MemoryStream(data, writable: false);
		return Read(stream);
	}

	public static ProsperoParam Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		JsonDocumentOptions options = new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip,
			MaxDepth = 64
		};
		using JsonDocument jsonDocument = JsonDocument.Parse(stream, options);
		JsonElement rootElement = jsonDocument.RootElement;
		if (rootElement.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("param.json root must be an object.");
		}
		string text = OptionalString(rootElement, "contentId");
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidDataException("param.json is missing string property 'contentId'.");
		}
		text = text.Trim().ToUpperInvariant();
		if (!ContentIdRegex().IsMatch(text))
		{
			throw new InvalidDataException("param.json contentId '" + text + "' must use the 36-character Sony content ID form.");
		}
		string text2 = OptionalString(rootElement, "titleId");
		text2 = (string.IsNullOrWhiteSpace(text2) ? text.Substring(7, 9) : text2.Trim().ToUpperInvariant());
		if (!TitleIdRegex().IsMatch(text2))
		{
			text2 = text.Substring(7, 9);
		}
		string contentVersion = OptionalString(rootElement, "contentVersion");
		int num = OptionalInt(rootElement, "applicationCategoryType").GetValueOrDefault();
		if (num != 1)
		{
			num = 0;
		}
		string applicationDrmType = OptionalString(rootElement, "applicationDrmType");
		string creationDate = null;
		if (rootElement.TryGetProperty("pubtools", out var value) && value.ValueKind == JsonValueKind.Object)
		{
			creationDate = OptionalString(value, "creationDate");
		}
		return new ProsperoParam(text2, text, contentVersion, num, creationDate, applicationDrmType)
		{
			TitleName = OptionalString(rootElement, "titleName")
		};
	}

	private static string? OptionalString(JsonElement parent, string name)
	{
		if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}

	private static int? OptionalInt(JsonElement parent, string name)
	{
		if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var value2))
		{
			return null;
		}
		return value2;
	}

	private static readonly Regex _TitleIdRegex = new Regex("^PPS[A-Z0-9][0-9]{5}$", RegexOptions.CultureInvariant);

	private static Regex TitleIdRegex() => _TitleIdRegex;

	private static readonly Regex _ContentIdRegex = new Regex("^[A-Z]{2}[0-9]{4}-[A-Z0-9]{9}_[0-9]{2}-[A-Z0-9]{16}$", RegexOptions.CultureInvariant);

	private static Regex ContentIdRegex() => _ContentIdRegex;
}
