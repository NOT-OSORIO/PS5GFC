using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ProsperoPkgTool.Containers;

public static class TempWorkspace
{
	public const string Prefix = "libprospero-";

	private const string OwnerFile = "build.lock";

	private static readonly ConcurrentDictionary<string, byte> Active = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	private static int _exitHookRegistered;

	public static string Create(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root, "root");
		Directory.CreateDirectory(root);
		string text = Path.Combine(root, "libprospero-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(text);
		try
		{
			File.WriteAllText(Path.Combine(text, "build.lock"), $"{Environment.ProcessId}\n{DateTime.UtcNow:O}\n{Environment.MachineName}\n");
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		Active[text] = 1;
		RegisterExitHook();
		return text;
	}

	public static void Delete(string workspace)
	{
		Active.TryRemove(workspace, out var _);
		try
		{
			if (Directory.Exists(workspace))
			{
				Directory.Delete(workspace, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	public static IReadOnlyList<string> SweepOrphans(string root, TimeSpan maxAge, Action<string>? log = null)
	{
		List<string> list = new List<string>();
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
		{
			return list;
		}
		foreach (string item in Directory.EnumerateDirectories(root, "libprospero-*"))
		{
			string fileName = Path.GetFileName(item);
			if (fileName.Length == "libprospero-".Length + 32 && Guid.TryParseExact(fileName.AsSpan("libprospero-".Length), "N", out var _) && IsOrphan(item, maxAge))
			{
				Delete(item);
				if (!Directory.Exists(item))
				{
					list.Add(fileName);
					log?.Invoke("Removed stale build workspace: " + fileName);
				}
			}
		}
		return list;
	}

	private static bool IsOrphan(string workspace, TimeSpan maxAge)
	{
		if (TryReadOwner(workspace, out int pid, out string machine))
		{
			if (!string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			return !IsProcessAlive(pid);
		}
		return AgeExceeds(workspace, maxAge);
	}

	private static bool AgeExceeds(string workspace, TimeSpan maxAge)
	{
		try
		{
			return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(workspace) > maxAge;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void RegisterExitHook()
	{
		if (Interlocked.Exchange(ref _exitHookRegistered, 1) == 0)
		{
			AppDomain.CurrentDomain.ProcessExit += (object? _, EventArgs _) =>
			{
				DeleteActiveWorkspaces();
			};
			AppDomain.CurrentDomain.UnhandledException += (object _, UnhandledExceptionEventArgs _) =>
			{
				DeleteActiveWorkspaces();
			};
		}
	}

	private static void DeleteActiveWorkspaces()
	{
		foreach (string key in Active.Keys)
		{
			Delete(key);
		}
	}

	private static bool TryReadOwner(string workspace, out int pid, out string machine)
	{
		pid = 0;
		machine = string.Empty;
		try
		{
			string[] array = File.ReadAllLines(Path.Combine(workspace, "build.lock"));
			return array.Length >= 3 && int.TryParse(array[0], out pid) && (machine = array[2]).Length > 0;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool IsProcessAlive(int pid)
	{
		try
		{
			using Process process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}
}
