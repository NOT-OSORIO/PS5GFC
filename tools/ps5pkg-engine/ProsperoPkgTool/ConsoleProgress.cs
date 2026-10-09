using System;

namespace ProsperoPkgTool;

internal sealed class ConsoleProgress : IProgress<ProsperoBuildProgress>
{
	private readonly bool _enabled;

	private readonly bool _redirected;

	private readonly ProsperoConsoleLog? _log;

	private string? _lastStage;

	private int _lastPercent = -1;

	private int _lastMilestone = -1;

	private DateTime _lastEmitUtc = DateTime.UtcNow;

	private bool _wroteLine;

	public ConsoleProgress(bool enabled, ProsperoConsoleLog? log = null)
	{
		_enabled = enabled;
		_log = log;
		try
		{
			_redirected = Console.IsOutputRedirected;
		}
		catch
		{
			_redirected = true;
		}
	}

	public void Report(ProsperoBuildProgress value)
	{
		if (_enabled && (value.Total > 1 || value.BytesTotal > 0))
		{
			bool flag = value.BytesTotal > 0;
			double num = (flag ? value.ByteFraction : value.Fraction);
			int num2 = (int)(100.0 * num);
			if (!string.Equals(value.Stage, _lastStage, StringComparison.Ordinal))
			{
				_lastMilestone = -1;
			}
			int num3 = num2 / 5;
			if (num3 != _lastMilestone || (DateTime.UtcNow - _lastEmitUtc).TotalSeconds >= 30.0)
			{
				_lastMilestone = num3;
				string value2 = (flag ? $"{(double)value.BytesDone / 1048576.0:N0}/{(double)value.BytesTotal / 1048576.0:N0} MiB" : $"{value.Done:N0}/{value.Total:N0}");
				string value3 = (string.IsNullOrEmpty(value.CurrentPath) ? "" : ("  " + value.CurrentPath));
				Emit($"    {num2,3}%  {value.Stage} ({value2}){value3}", value.Stage, num2);
			}
		}
	}

	public void Report(int done, int total, string file)
	{
		if (_enabled && total > 0)
		{
			int num = (int)(100.0 * (double)done / (double)total);
			Emit($"    {num,3}%  {done}/{total}  {file}", "Extract", num);
		}
	}

	private void Emit(string line, string stage, int percent)
	{
		_lastEmitUtc = DateTime.UtcNow;
		if (_log != null)
		{
			_log.Write(line);
			_lastStage = stage;
			_lastPercent = percent;
			return;
		}
		if (_redirected)
		{
			if (stage != _lastStage || percent != _lastPercent)
			{
				Console.WriteLine(line);
				_lastStage = stage;
				_lastPercent = percent;
			}
			return;
		}
		int num = SafeWidth();
		if (line.Length < num)
		{
			line += new string(' ', num - line.Length);
		}
		Console.Write('\r');
		Console.Write(line);
		_lastStage = stage;
		_lastPercent = percent;
		_wroteLine = true;
	}

	public void Finish()
	{
		if (_log == null && _enabled && !_redirected && _wroteLine)
		{
			Console.WriteLine();
		}
		_lastStage = null;
		_lastPercent = -1;
		_lastMilestone = -1;
		_wroteLine = false;
	}

	private static int SafeWidth()
	{
		try
		{
			int windowWidth = Console.WindowWidth;
			return (windowWidth > 0) ? windowWidth : 100;
		}
		catch
		{
			return 100;
		}
	}
}
