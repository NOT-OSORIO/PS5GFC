using System;
using System.IO;

namespace ProsperoPkgTool.Containers;

public static class ProsperoErrorInfo
{
	private const string Key = "Prospero.ErrorKind";

	public static InvalidDataException Unsupported(string message)
	{
		InvalidDataException ex = new InvalidDataException(message);
		ex.Data["Prospero.ErrorKind"] = ProsperoErrorKind.Unsupported;
		return ex;
	}

	public static bool IsUnsupported(Exception? exception)
	{
		for (Exception ex = exception; ex != null; ex = ex.InnerException)
		{
			if (ex.Data.Contains("Prospero.ErrorKind"))
			{
				object obj = ex.Data["Prospero.ErrorKind"];
				if (obj is ProsperoErrorKind && (ProsperoErrorKind)obj == ProsperoErrorKind.Unsupported)
				{
					return true;
				}
			}
		}
		return false;
	}
}
