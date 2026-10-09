using System;
using System.Collections.Generic;
using System.IO;

namespace ProsperoPkgTool.Gp5;

public static class Gp5Manifest
{
	public static IReadOnlyList<Gp5ManifestFile> Resolve(Gp5Project project, string projectPath)
	{
		ArgumentNullException.ThrowIfNull(project, "project");
		ArgumentException.ThrowIfNullOrWhiteSpace(projectPath, "projectPath");
		string fullPath = Path.GetFullPath(projectPath);
		List<Gp5ManifestFile> list = new List<Gp5ManifestFile>();
		HashSet<string> destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (project.Layout == Gp5ContentLayout.FlatFiles)
		{
			foreach (Gp5FileEntry file in project.Files)
			{
				AddFile(file, string.Empty, fullPath, list, destinations);
			}
		}
		else
		{
			WalkDirectory(project.RootDirectory, string.Empty, fullPath, list, destinations);
		}
		return list;
	}

	private static void WalkDirectory(Gp5DirectoryEntry directory, string prefix, string projectPath, ICollection<Gp5ManifestFile> results, ISet<string> destinations)
	{
		string prefix2 = Combine(prefix, directory.DestinationPath);
		foreach (Gp5ContentEntry child in directory.Children)
		{
			if (child is Gp5DirectoryEntry directory2)
			{
				WalkDirectory(directory2, prefix2, projectPath, results, destinations);
			}
			else if (child is Gp5FileEntry file)
			{
				AddFile(file, prefix2, projectPath, results, destinations);
			}
		}
	}

	private static void AddFile(Gp5FileEntry file, string prefix, string projectPath, ICollection<Gp5ManifestFile> results, ISet<string> destinations)
	{
		string text = Combine(prefix, file.DestinationPath, required: true);
		if (!destinations.Add(text))
		{
			throw new InvalidDataException("GP5 assigns the destination path '" + text + "' more than once.");
		}
		string text2 = (string.IsNullOrWhiteSpace(file.SourcePath) ? file.DestinationPath : file.SourcePath);
		string text3 = Gp5Path.ResolveSource(projectPath, text2);
		if (!File.Exists(text3))
		{
			throw new InvalidDataException("GP5 source file does not exist: " + text2 + ".");
		}
		results.Add(new Gp5ManifestFile(text, text3, file.Chunk, file.PfsCompression));
	}

	private static string Combine(string prefix, string? path, bool required = false)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			if (required)
			{
				throw new InvalidDataException("GP5 file entry is missing its dst_path.");
			}
			return prefix;
		}
		string text;
		try
		{
			text = Gp5Path.NormalizeDestination(path);
		}
		catch (ArgumentException ex)
		{
			throw new InvalidDataException(ex.Message, ex);
		}
		if (!string.IsNullOrEmpty(prefix))
		{
			return prefix + "/" + text;
		}
		return text;
	}
}
