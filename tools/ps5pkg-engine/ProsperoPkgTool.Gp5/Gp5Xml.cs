using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ProsperoPkgTool.Gp5;

public static class Gp5Xml
{
	public static Gp5Project Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		using FileStream stream = File.OpenRead(path);
		return Read(stream);
	}

	public static Gp5Project Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream, "stream");
		XmlReaderSettings settings = new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null,
			IgnoreComments = true,
			IgnoreWhitespace = true,
			CloseInput = false
		};
		using XmlReader reader = XmlReader.Create(stream, settings);
		XElement xElement = XDocument.Load(reader, LoadOptions.SetLineInfo).Root ?? throw new InvalidDataException("GP5 XML has no document element.");
		if (xElement.Name != "psproject")
		{
			throw Error(xElement, "GP5 root element must be <psproject>.");
		}
		Gp5Project gp5Project = new Gp5Project
		{
			Format = (Attribute(xElement, "fmt") ?? string.Empty),
			Version = Attribute(xElement, "version")
		};
		CaptureExtraAttributes(xElement, gp5Project.ExtraAttributes, "fmt", "version");
		XElement element = SingleRequired(xElement, "volume");
		gp5Project.Volume = ParseVolume(element);
		XElement xElement2 = SingleOptional(xElement, "files");
		XElement xElement3 = SingleOptional(xElement, "rootdir");
		if (xElement2 != null && xElement3 != null)
		{
			throw Error(xElement, "GP5 cannot contain both <files> and <rootdir>.");
		}
		if (xElement2 == null && xElement3 == null)
		{
			throw Error(xElement, "GP5 must contain either <files> or <rootdir>.");
		}
		if (xElement2 != null)
		{
			EnsureOnlyChildren(xElement2, "file");
			gp5Project.Layout = Gp5ContentLayout.FlatFiles;
			foreach (XElement item in xElement2.Elements("file"))
			{
				gp5Project.Files.Add(ParseFile(item));
			}
		}
		else
		{
			gp5Project.Layout = Gp5ContentLayout.RootDirectory;
			gp5Project.RootDirectory = ParseDirectory(xElement3, isRoot: true);
		}
		EnsureOnlyChildren(xElement, "volume", "files", "rootdir");
		return gp5Project;
	}

	public static void Write(Gp5Project project, string path)
	{
		ArgumentNullException.ThrowIfNull(project, "project");
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		string fullPath = Path.GetFullPath(path);
		string directoryName = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		using FileStream stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
		Write(project, stream);
	}

	public static void Write(Gp5Project project, Stream stream)
	{
		ArgumentNullException.ThrowIfNull(project, "project");
		ArgumentNullException.ThrowIfNull(stream, "stream");
		XElement xElement = new XElement("psproject", new XAttribute("fmt", project.Format));
		if (!string.IsNullOrWhiteSpace(project.Version))
		{
			xElement.Add(new XAttribute("version", project.Version));
		}
		AddExtraAttributes(xElement, project.ExtraAttributes, "fmt", "version");
		xElement.Add(WriteVolume(project.Volume));
		if (project.Layout == Gp5ContentLayout.FlatFiles)
		{
			XElement xElement2 = new XElement("files");
			foreach (Gp5FileEntry file in project.Files)
			{
				xElement2.Add(WriteFile(file));
			}
			xElement.Add(xElement2);
		}
		else
		{
			xElement.Add(WriteDirectory(project.RootDirectory, isRoot: true));
		}
		XDocument xDocument = new XDocument(new XDeclaration("1.0", "utf-8", "no"), xElement);
		XmlWriterSettings settings = new XmlWriterSettings
		{
			Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			Indent = true,
			IndentChars = "\t",
			NewLineChars = "\r\n",
			NewLineHandling = NewLineHandling.Replace,
			OmitXmlDeclaration = false,
			CloseOutput = false
		};
		using XmlWriter writer = XmlWriter.Create(stream, settings);
		xDocument.Save(writer);
	}

	private static Gp5Volume ParseVolume(XElement element)
	{
		EnsureOnlyChildren(element, "volume_type", "package", "chunk_info");
		Gp5Volume gp5Volume = new Gp5Volume
		{
			VolumeType = SingleRequired(element, "volume_type").Value.Trim(),
			Package = ParsePackage(SingleRequired(element, "package"))
		};
		XElement xElement = SingleOptional(element, "chunk_info");
		gp5Volume.ChunkInfo = ((xElement != null) ? ParseChunkInfo(xElement) : null);
		return gp5Volume;
	}

	private static Gp5Package ParsePackage(XElement element)
	{
		EnsureNoElements(element);
		Gp5Package gp5Package = new Gp5Package
		{
			ContentId = Attribute(element, "content_id"),
			Passcode = (Attribute(element, "passcode") ?? string.Empty),
			CreationDate = Attribute(element, "c_date"),
			EntitlementKey = Attribute(element, "entitlement_key"),
			StorageType = Attribute(element, "storage_type"),
			AppType = Attribute(element, "app_type")
		};
		CaptureExtraAttributes(element, gp5Package.ExtraAttributes, "content_id", "passcode", "c_date", "entitlement_key", "storage_type", "app_type");
		return gp5Package;
	}

	private static Gp5ChunkInfo ParseChunkInfo(XElement element)
	{
		EnsureOnlyChildren(element, "chunks", "scenarios");
		XElement xElement = SingleRequired(element, "chunks");
		XElement xElement2 = SingleRequired(element, "scenarios");
		EnsureOnlyChildren(xElement, "chunk");
		EnsureOnlyChildren(xElement2, "scenario");
		Gp5ChunkInfo gp5ChunkInfo = new Gp5ChunkInfo
		{
			ChunkCount = IntAttribute(element, "chunk_count"),
			ScenarioCount = IntAttribute(element, "scenario_count"),
			SupportedLanguages = Attribute(xElement, "supported_languages"),
			DefaultLanguage = Attribute(xElement, "default_language"),
			DefaultScenarioId = IntAttribute(xElement2, "default_id")
		};
		foreach (XElement item in xElement.Elements("chunk"))
		{
			EnsureNoElements(item);
			Gp5Chunk gp5Chunk = new Gp5Chunk
			{
				Id = IntAttribute(item, "id"),
				Label = (Attribute(item, "label") ?? string.Empty),
				Languages = Attribute(item, "languages")
			};
			CaptureExtraAttributes(item, gp5Chunk.ExtraAttributes, "id", "label", "languages");
			gp5ChunkInfo.Chunks.Add(gp5Chunk);
		}
		foreach (XElement item2 in xElement2.Elements("scenario"))
		{
			EnsureNoElements(item2);
			Gp5Scenario gp5Scenario = new Gp5Scenario
			{
				Id = IntAttribute(item2, "id"),
				Type = (Attribute(item2, "type") ?? string.Empty),
				InitialChunkCount = IntAttribute(item2, "initial_chunk_count"),
				Label = (Attribute(item2, "label") ?? string.Empty),
				Sequence = item2.Value.Trim()
			};
			CaptureExtraAttributes(item2, gp5Scenario.ExtraAttributes, "id", "type", "initial_chunk_count", "label");
			gp5ChunkInfo.Scenarios.Add(gp5Scenario);
		}
		return gp5ChunkInfo;
	}

	private static Gp5FileEntry ParseFile(XElement element)
	{
		EnsureNoElements(element);
		Gp5FileEntry gp5FileEntry = new Gp5FileEntry();
		ParseContentAttributes(element, gp5FileEntry);
		return gp5FileEntry;
	}

	private static Gp5DirectoryEntry ParseDirectory(XElement element, bool isRoot)
	{
		EnsureOnlyChildren(element, "file", "dir");
		Gp5DirectoryEntry gp5DirectoryEntry = new Gp5DirectoryEntry
		{
			IsRoot = isRoot,
			Virtual = OptionalBoolAttribute(element, "virtual"),
			DirectoryExclude = Attribute(element, "dir_exclude"),
			FileExclude = Attribute(element, "file_exclude")
		};
		ParseContentAttributes(element, gp5DirectoryEntry, "virtual", "dir_exclude", "file_exclude");
		foreach (XElement item in element.Elements())
		{
			gp5DirectoryEntry.Children.Add((item.Name == "file") ? ((Gp5ContentEntry)ParseFile(item)) : ((Gp5ContentEntry)ParseDirectory(item, isRoot: false)));
		}
		return gp5DirectoryEntry;
	}

	private static void ParseContentAttributes(XElement element, Gp5ContentEntry entry, params string[] additionalKnown)
	{
		entry.DestinationPath = Attribute(element, "dst_path") ?? string.Empty;
		entry.SourcePath = Attribute(element, "src_path");
		entry.Chunk = OptionalIntAttribute(element, "chunk");
		entry.ContentConfigLabel = Attribute(element, "content_config_label");
		entry.PfsCompression = Attribute(element, "pfs_compression");
		List<string> list = new List<string>();
		list.Add("dst_path");
		list.Add("src_path");
		list.Add("chunk");
		list.Add("content_config_label");
		list.Add("pfs_compression");
		list.AddRange(additionalKnown);
		string[] known = list.ToArray();
		CaptureExtraAttributes(element, entry.ExtraAttributes, known);
	}

	private static XElement WriteVolume(Gp5Volume volume)
	{
		XElement xElement = new XElement("volume", new XElement("volume_type", volume.VolumeType), WritePackage(volume.Package));
		if (volume.ChunkInfo != null)
		{
			xElement.Add(WriteChunkInfo(volume.ChunkInfo));
		}
		return xElement;
	}

	private static XElement WritePackage(Gp5Package package)
	{
		XElement xElement = new XElement("package");
		AddOptionalAttribute(xElement, "content_id", package.ContentId);
		AddOptionalAttribute(xElement, "passcode", package.Passcode);
		AddOptionalAttribute(xElement, "c_date", package.CreationDate);
		AddOptionalAttribute(xElement, "entitlement_key", package.EntitlementKey);
		AddOptionalAttribute(xElement, "storage_type", package.StorageType);
		AddOptionalAttribute(xElement, "app_type", package.AppType);
		AddExtraAttributes(xElement, package.ExtraAttributes, "content_id", "passcode", "c_date", "entitlement_key", "storage_type", "app_type");
		return xElement;
	}

	private static XElement WriteChunkInfo(Gp5ChunkInfo info)
	{
		XElement xElement = new XElement("chunks");
		AddOptionalAttribute(xElement, "supported_languages", info.SupportedLanguages);
		AddOptionalAttribute(xElement, "default_language", info.DefaultLanguage);
		foreach (Gp5Chunk chunk in info.Chunks)
		{
			XElement xElement2 = new XElement("chunk", new XAttribute("id", chunk.Id));
			AddOptionalAttribute(xElement2, "languages", chunk.Languages);
			AddOptionalAttribute(xElement2, "label", chunk.Label);
			AddExtraAttributes(xElement2, chunk.ExtraAttributes, "id", "languages", "label");
			xElement.Add(xElement2);
		}
		XElement xElement3 = new XElement("scenarios", new XAttribute("default_id", info.DefaultScenarioId));
		foreach (Gp5Scenario scenario in info.Scenarios)
		{
			XElement xElement4 = new XElement("scenario", new XAttribute("id", scenario.Id), new XAttribute("initial_chunk_count", scenario.InitialChunkCount), new XAttribute("label", scenario.Label), new XAttribute("type", scenario.Type), scenario.Sequence);
			AddExtraAttributes(xElement4, scenario.ExtraAttributes, "id", "initial_chunk_count", "label", "type");
			xElement3.Add(xElement4);
		}
		return new XElement("chunk_info", new XAttribute("chunk_count", info.ChunkCount), new XAttribute("scenario_count", info.ScenarioCount), xElement, xElement3);
	}

	private static XElement WriteFile(Gp5FileEntry file)
	{
		XElement xElement = new XElement("file");
		WriteContentAttributes(xElement, file);
		return xElement;
	}

	private static XElement WriteDirectory(Gp5DirectoryEntry directory, bool isRoot)
	{
		XElement xElement = new XElement(isRoot ? "rootdir" : "dir");
		if (directory.Virtual.HasValue)
		{
			xElement.Add(new XAttribute("virtual", directory.Virtual.Value ? "true" : "false"));
		}
		AddOptionalAttribute(xElement, "dir_exclude", directory.DirectoryExclude);
		AddOptionalAttribute(xElement, "file_exclude", directory.FileExclude);
		WriteContentAttributes(xElement, directory, "virtual", "dir_exclude", "file_exclude");
		foreach (Gp5ContentEntry child in directory.Children)
		{
			xElement.Add((child is Gp5FileEntry file) ? WriteFile(file) : WriteDirectory((Gp5DirectoryEntry)child, isRoot: false));
		}
		return xElement;
	}

	private static void WriteContentAttributes(XElement element, Gp5ContentEntry entry, params string[] additionalKnown)
	{
		AddOptionalAttribute(element, "dst_path", entry.DestinationPath);
		AddOptionalAttribute(element, "src_path", entry.SourcePath);
		if (entry.Chunk.HasValue)
		{
			element.Add(new XAttribute("chunk", entry.Chunk.Value));
		}
		AddOptionalAttribute(element, "content_config_label", entry.ContentConfigLabel);
		AddOptionalAttribute(element, "pfs_compression", entry.PfsCompression);
		List<string> list = new List<string>();
		list.Add("dst_path");
		list.Add("src_path");
		list.Add("chunk");
		list.Add("content_config_label");
		list.Add("pfs_compression");
		list.AddRange(additionalKnown);
		string[] known = list.ToArray();
		AddExtraAttributes(element, entry.ExtraAttributes, known);
	}

	private static XElement SingleRequired(XElement parent, string name)
	{
		return SingleOptional(parent, name) ?? throw Error(parent, "Missing required <" + name + "> element.");
	}

	private static XElement? SingleOptional(XElement parent, string name)
	{
		XElement[] array = parent.Elements(name).ToArray();
		if (array.Length > 1)
		{
			throw Error(array[1], "Duplicate <" + name + "> element.");
		}
		return array.SingleOrDefault();
	}

	private static string? Attribute(XElement element, string name)
	{
		return element.Attribute(name)?.Value;
	}

	private static int IntAttribute(XElement element, string name)
	{
		return OptionalIntAttribute(element, name) ?? throw Error(element, "Missing required '" + name + "' attribute.");
	}

	private static int? OptionalIntAttribute(XElement element, string name)
	{
		string text = Attribute(element, name);
		if (text == null)
		{
			return null;
		}
		if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0)
		{
			throw Error(element, "Attribute '" + name + "' must be a non-negative integer.");
		}
		return result;
	}

	private static bool? OptionalBoolAttribute(XElement element, string name)
	{
		string text = Attribute(element, name);
		if (text == null)
		{
			return null;
		}
		if (!bool.TryParse(text, out var result))
		{
			throw Error(element, "Attribute '" + name + "' must be true or false.");
		}
		return result;
	}

	private static void EnsureOnlyChildren(XElement parent, params string[] allowed)
	{
		HashSet<string> set = allowed.ToHashSet(StringComparer.Ordinal);
		XElement xElement = parent.Elements().FirstOrDefault((XElement child) => !set.Contains(child.Name.LocalName));
		if (xElement != null)
		{
			throw Error(xElement, $"Unsupported <{xElement.Name.LocalName}> element inside <{parent.Name.LocalName}>.");
		}
	}

	private static void EnsureNoElements(XElement element)
	{
		if (element.HasElements)
		{
			throw Error(element.Elements().First(), "<" + element.Name.LocalName + "> cannot contain child elements.");
		}
	}

	private static void CaptureExtraAttributes(XElement element, IDictionary<string, string> target, params string[] known)
	{
		HashSet<string> hashSet = known.ToHashSet(StringComparer.Ordinal);
		foreach (XAttribute item in from attribute in element.Attributes()
			where !attribute.IsNamespaceDeclaration
			select attribute)
		{
			if (!hashSet.Contains(item.Name.LocalName))
			{
				target[item.Name.LocalName] = item.Value;
			}
		}
	}

	private static void AddExtraAttributes(XElement element, IReadOnlyDictionary<string, string> attributes, params string[] known)
	{
		HashSet<string> hashSet = known.ToHashSet(StringComparer.Ordinal);
		foreach (var (text3, value) in attributes.OrderBy((KeyValuePair<string, string> item) => item.Key, StringComparer.Ordinal))
		{
			if (!hashSet.Contains(text3) && element.Attribute(text3) == null)
			{
				element.Add(new XAttribute(text3, value));
			}
		}
	}

	private static void AddOptionalAttribute(XElement element, string name, string? value)
	{
		if (!string.IsNullOrEmpty(value))
		{
			element.Add(new XAttribute(name, value));
		}
	}

	private static InvalidDataException Error(XElement element, string message)
	{
		if (element != null && ((IXmlLineInfo)element).HasLineInfo())
		{
			return new InvalidDataException($"{message} Line {((IXmlLineInfo)element).LineNumber}, position {((IXmlLineInfo)element).LinePosition}.");
		}
		return new InvalidDataException(message);
	}
}
