using System;
using System.Diagnostics;

namespace ProsperoPkgTool;

internal sealed class ProsperoConsoleLog
{
	private readonly Stopwatch _clock = Stopwatch.StartNew();

	public string Elapsed => _clock.Elapsed.ToString("hh\\:mm\\:ss\\.fff");

	public void Write(string? message)
	{
		Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [+{Elapsed}] {message}");
	}

	public void Log(string? message)
	{
		Write(message);
	}
}
