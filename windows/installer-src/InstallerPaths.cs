using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

internal static class InstallerPaths
{
	public static bool ResolveDefaultCreateDesktopShortcut()
	{
		if (TryGetExistingInstallDirectory() == null)
		{
			return true;
		}
		return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Vesper Launcher.lnk"));
	}

	public static string ResolveInstallDirectory(string? installDirectoryOverride)
	{
		string resolved;
		if (!string.IsNullOrWhiteSpace(installDirectoryOverride))
		{
			resolved = Path.GetFullPath(installDirectoryOverride.Trim().Trim('"'));
		}
		else
		{
			string text = TryGetExistingInstallDirectory();
			if (!string.IsNullOrWhiteSpace(text))
			{
				resolved = Path.GetFullPath(text);
			}
			else
			{
				resolved = Path.GetFullPath(GetDefaultInstallDirectory());
			}
		}

		try
		{
			string root = Path.GetPathRoot(resolved) ?? string.Empty;
			if (!string.IsNullOrEmpty(root) && string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
			{
				resolved = Path.Combine(resolved, "Vesper Launcher");
			}
		}
		catch
		{
			// Ignore path format errors.
		}

		return resolved;
	}

	public static string ResolveInstallDirectoryForUninstall(string? installDirectoryOverride)
	{
		string resolved;
		if (!string.IsNullOrWhiteSpace(installDirectoryOverride))
		{
			resolved = Path.GetFullPath(installDirectoryOverride.Trim().Trim('"'));
		}
		else
		{
			string directoryName = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
			if (!string.IsNullOrWhiteSpace(directoryName) && LooksLikeInstalledLauncher(directoryName))
			{
				resolved = Path.GetFullPath(directoryName);
			}
			else
			{
				string text = TryGetRegisteredInstallDirectory();
				if (!string.IsNullOrWhiteSpace(text))
				{
					resolved = Path.GetFullPath(text);
				}
				else
				{
					resolved = ResolveInstallDirectory(null);
				}
			}
		}

		try
		{
			string root = Path.GetPathRoot(resolved) ?? string.Empty;
			if (!string.IsNullOrEmpty(root) && string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
			{
				resolved = Path.Combine(resolved, "Vesper Launcher");
			}
		}
		catch
		{
			// Ignore path format errors.
		}

		return resolved;
	}

	public static bool PathsEqual(string left, string right)
	{
		return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
	}

	private static string? TryGetExistingInstallDirectory()
	{
		foreach (string candidateInstallDirectory in GetCandidateInstallDirectories())
		{
			if (LooksLikeInstalledLauncher(candidateInstallDirectory))
			{
				return candidateInstallDirectory;
			}
		}
		return null;
	}

	private static IEnumerable<string> GetCandidateInstallDirectories()
	{
		string text = TryGetRegisteredInstallDirectory();
		if (!string.IsNullOrWhiteSpace(text))
		{
			yield return text;
		}
		string[] array = new string[2]
		{
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Vesper Launcher", "Vesper Launcher.lnk"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Vesper Launcher.lnk")
		};
		string[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			string text2 = TryResolveShortcutTarget(array2[i]);
			if (!string.IsNullOrWhiteSpace(text2))
			{
				string directoryName = Path.GetDirectoryName(text2);
				if (!string.IsNullOrWhiteSpace(directoryName))
				{
					yield return directoryName;
				}
			}
		}
		yield return GetDefaultInstallDirectory();
	}

	private static string? TryGetRegisteredInstallDirectory()
	{
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\VesperLauncher", writable: false);
			string text = registryKey?.GetValue("InstallLocation") as string;
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}
		catch
		{
			return null;
		}
	}

	private static bool LooksLikeInstalledLauncher(string directory)
	{
		if (string.IsNullOrWhiteSpace(directory))
		{
			return false;
		}
		if (File.Exists(Path.Combine(directory, "VesperLauncher.exe")))
		{
			return true;
		}
		if (Directory.Exists(Path.Combine(directory, "Assets")))
		{
			return Directory.Exists(Path.Combine(directory, "BundledVersions"));
		}
		return false;
	}

	private static string GetDefaultInstallDirectory()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vesper Launcher");
	}

	private static string? TryResolveShortcutTarget(string shortcutPath)
	{
		try
		{
			if (!File.Exists(shortcutPath))
			{
				return null;
			}
			Type typeFromProgID = Type.GetTypeFromProgID("WScript.Shell");
			if ((object)typeFromProgID == null)
			{
				return null;
			}
			dynamic val = Activator.CreateInstance(typeFromProgID);
			dynamic val2 = val.CreateShortcut(shortcutPath);
			string text = val2.TargetPath as string;
			return string.IsNullOrWhiteSpace(text) ? null : text;
		}
		catch
		{
			return null;
		}
	}
}
