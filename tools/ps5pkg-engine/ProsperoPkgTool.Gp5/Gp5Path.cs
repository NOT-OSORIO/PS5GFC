using System;
using System.IO;
using System.Linq;

namespace ProsperoPkgTool.Gp5;

public static class Gp5Path
{
	public static string ToProjectRelative(string relativePath)
	{
		string text = NormalizeRelative(relativePath);
		return "\\" + text.Replace('/', '\\');
	}

	public static string NormalizeDestination(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		string text = path.Replace('\\', '/').TrimStart('/');
		if (text.Length == 0 || text.Contains(':', StringComparison.Ordinal))
		{
			throw new ArgumentException("Destination path is empty or rooted.", "path");
		}
		string[] array = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (array.Any((string segment) => (segment == "." || segment == "..") ? true : false))
		{
			throw new ArgumentException("Destination path contains a traversal segment.", "path");
		}
		return string.Join('/', array);
	}

	public static string ResolveSource(string projectPath, string sourcePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectPath, "projectPath");
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath, "sourcePath");
		string path = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Environment.CurrentDirectory;
		string text = sourcePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
		bool num = text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && Path.IsPathFullyQualified(text);
		bool flag = text.StartsWith(new string(Path.DirectorySeparatorChar, 2), StringComparison.Ordinal);
		if (!num && !flag)
		{
			text = text.TrimStart(Path.DirectorySeparatorChar);
		}
		if (!(num | flag))
		{
			return Path.GetFullPath(Path.Combine(path, text));
		}
		return Path.GetFullPath(text);
	}

	public static bool IsWithin(string root, string candidate)
	{
		string text = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		string fullPath = Path.GetFullPath(candidate);
		if (!fullPath.Equals(text, StringComparison.OrdinalIgnoreCase))
		{
			return fullPath.StartsWith(text + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static string NormalizeRelative(string path)
	{
		string[] array = path.Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 0 || array.Any((string segment) => (segment == "." || segment == "..") ? true : false))
		{
			throw new ArgumentException("Path must be a contained relative path.", "path");
		}
		return string.Join('/', array);
	}
}
