using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ProsperoPkgTool.Gp5;

public static class SourceFolderValidator
{
	public static SourceFolderInspection Inspect(string sourceDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory, "sourceDirectory");
		string fullPath = Path.GetFullPath(sourceDirectory);
		ValidationReport validationReport = new ValidationReport();
		if (!Directory.Exists(fullPath))
		{
			validationReport.Error("SOURCE_DIRECTORY", "Source directory does not exist.", fullPath);
			return new SourceFolderInspection(fullPath, null, null, Array.Empty<string>(), Array.Empty<string>(), validationReport);
		}
		string path = Path.Combine(fullPath, "sce_sys", "param.json");
		ProsperoParam prosperoParam = null;
		if (!File.Exists(path))
		{
			validationReport.Error("PARAM_MISSING", "sce_sys/param.json is required.", path);
		}
		else
		{
			try
			{
				prosperoParam = ProsperoParamReader.Read(path);
			}
			catch (Exception ex) when ((ex is IOException || ex is JsonException || ex is InvalidDataException) ? true : false)
			{
				validationReport.Error("PARAM_INVALID", ex.Message, path);
			}
		}
		if ((object)prosperoParam != null && prosperoParam.ApplicationCategoryType == 0)
		{
			if (!File.Exists(Path.Combine(fullPath, "eboot.bin")))
			{
				validationReport.Error("EBOOT_MISSING", "Application source requires eboot.bin.", Path.Combine(fullPath, "eboot.bin"));
			}
			WarnIfMissing(validationReport, fullPath, "sce_sys/pic0.png", "Sony verification requires pic0.png for an original application package.");
			WarnIfMissing(validationReport, fullPath, "sce_sys/pic1.png", "Sony verification requires pic1.png for an original application package.");
			WarnIfMissing(validationReport, fullPath, "sce_sys/pic2.png", "Sony verification requires pic2.png for an original application package.");
			if (!Directory.Exists(Path.Combine(fullPath, "sce_module")))
			{
				validationReport.Warning("SCE_MODULE_MISSING", "Sony verification requires sce_module for an original application package.", Path.Combine(fullPath, "sce_module"));
			}
		}
		if (!File.Exists(Path.Combine(fullPath, "sce_sys", "icon0.png")))
		{
			validationReport.Warning("ICON_MISSING", "sce_sys/icon0.png is not present.", Path.Combine(fullPath, "sce_sys", "icon0.png"));
		}
		PlayGoProject playGoProject = null;
		string path2 = Path.Combine(fullPath, "sce_sys", "playgo-chunk.dat");
		if (File.Exists(path2))
		{
			try
			{
				playGoProject = PlayGoChunkReader.Read(path2);
				if ((object)prosperoParam != null && playGoProject.ContentId.Length > 0 && !playGoProject.ContentId.Equals(prosperoParam.ContentId, StringComparison.Ordinal))
				{
					validationReport.Warning("PLAYGO_CONTENT_ID", "PlayGo content ID does not match param.json; the package content ID is used instead.", path2);
				}
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is InvalidDataException) ? true : false)
			{
				validationReport.Error("PLAYGO_INVALID", ex2.Message, path2);
			}
		}
		else if ((object)prosperoParam != null && prosperoParam.ApplicationCategoryType == 0)
		{
			validationReport.Warning("PLAYGO_DEFAULT", "playgo-chunk.dat is absent; a one-chunk default will be generated.", path2);
		}
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		Enumerate(fullPath, fullPath, list, list2, validationReport);
		list.Sort(StringComparer.Ordinal);
		list2.Sort(StringComparer.Ordinal);
		return new SourceFolderInspection(fullPath, prosperoParam, playGoProject, list, list2, validationReport);
	}

	private static void WarnIfMissing(ValidationReport report, string root, string relativePath, string message)
	{
		string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(path))
		{
			report.Warning("MEDIA_MISSING", message, path);
		}
	}

	public static bool IsPublishingArtifact(string relativePath)
	{
		string text = relativePath.Replace('\\', '/').TrimStart('/');
		if (text.StartsWith("sce_suppl/", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string text2 = text.ToLowerInvariant();
		if (text2.StartsWith("sce_sys/about/", StringComparison.Ordinal) || text2.EndsWith(".esbak", StringComparison.Ordinal))
		{
			return true;
		}
		bool flag;
		switch (text2)
		{
		case "sce_sys/pfs-version.dat":
		case "sce_sys/playgo-ficm.dat":
		case "sce_sys/playgo-chunk.dat":
		case "sce_sys/playgo-hash-table.dat":
		case "sce_sys/imagedigs.dat":
		case "sce_sys/keystone":
		case "sce_sys/npbind.dat":
		case "sce_sys/nptitle.dat":
		case "sce_sys/ext_info.dat":
		case "disc_info.dat":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return true;
		}
		string fileName = Path.GetFileName(text);
		if ((Path.GetDirectoryName(text)?.Replace('\\', '/') ?? string.Empty).Equals("sce_sys", StringComparison.OrdinalIgnoreCase) && (fileName.StartsWith("icon", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("pic", StringComparison.OrdinalIgnoreCase)) && Path.GetExtension(fileName).Equals(".dds", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string extension = Path.GetExtension(fileName);
		if (!extension.Equals(".gp5", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
		{
			return fileName.EndsWith(".naps_metric.json", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static void Enumerate(string root, string directory, ICollection<string> included, ICollection<string> excluded, ValidationReport report)
	{
		IEnumerable<string> enumerable;
		IEnumerable<string> enumerable2;
		try
		{
			enumerable = Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToArray();
			enumerable2 = Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal).ToArray();
		}
		catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
		{
			report.Error("SOURCE_ENUMERATION", ex.Message, directory);
			return;
		}
		foreach (string item in enumerable)
		{
			string text = Path.GetRelativePath(root, item).Replace('\\', '/');
			if (IsPublishingArtifact(text))
			{
				excluded.Add(text);
			}
			else
			{
				included.Add(text);
			}
		}
		foreach (string item2 in enumerable2)
		{
			FileAttributes attributes;
			try
			{
				attributes = File.GetAttributes(item2);
			}
			catch (Exception ex2) when ((ex2 is IOException || ex2 is UnauthorizedAccessException) ? true : false)
			{
				report.Error("SOURCE_ATTRIBUTES", ex2.Message, item2);
				continue;
			}
			if (attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				report.Warning("REPARSE_POINT", "Reparse-point directory was skipped.", item2);
				continue;
			}
			string text2 = Path.GetRelativePath(root, item2).Replace('\\', '/');
			if (text2.Equals("sce_suppl", StringComparison.OrdinalIgnoreCase))
			{
				excluded.Add(text2 + "/");
			}
			else
			{
				Enumerate(root, item2, included, excluded, report);
			}
		}
	}
}
