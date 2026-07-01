using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

internal static class ProcessPathUtilities
{
	public static string? TryGetLaunchedExecutablePath()
	{
		foreach (string candidate in GetCandidates())
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				continue;
			}
			try
			{
				string fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
				if (File.Exists(fullPath))
				{
					return fullPath;
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static IEnumerable<string?> GetCandidates()
	{
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		if (commandLineArgs.Length != 0)
		{
			yield return commandLineArgs[0];
		}
		string text;
		try
		{
			text = Process.GetCurrentProcess().MainModule?.FileName;
		}
		catch
		{
			text = null;
		}
		yield return text;
		yield return Environment.ProcessPath;
	}
}
