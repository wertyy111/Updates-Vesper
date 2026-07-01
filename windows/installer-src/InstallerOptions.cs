using System;
using System.Collections.Generic;

internal sealed record InstallerOptions(string? InstallDirectoryOverride, bool RunSilently, bool SuppressMessageBoxes, bool CloseApplications, bool CreateDesktopShortcut, bool LaunchAfterInstall, bool AcceptLicense, bool RunUninstall, bool RemoveUserData)
{
	public static InstallerOptions Parse(string[] args)
	{
		string installDirectoryOverride = null;
		bool runSilently = false;
		bool suppressMessageBoxes = false;
		bool closeApplications = false;
		bool createDesktopShortcut = InstallerPaths.ResolveDefaultCreateDesktopShortcut();
		bool launchAfterInstall = true;
		bool acceptLicense = true;
		bool runUninstall = false;
		bool removeUserData = false;
		for (int i = 0; i < args.Length; i++)
		{
			string text = args[i].Trim();
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			if (text.Equals("--dir", StringComparison.OrdinalIgnoreCase) || text.Equals("/DIR", StringComparison.OrdinalIgnoreCase))
			{
				if (i + 1 < args.Length)
				{
					installDirectoryOverride = args[++i];
				}
			}
			else if (text.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase) || text.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
			{
				string text2 = text;
				int num = text.IndexOf('=') + 1;
				installDirectoryOverride = text2.Substring(num, text2.Length - num).Trim().Trim('"');
			}
			else if (text.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase) || text.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) || text.Equals("--silent", StringComparison.OrdinalIgnoreCase))
			{
				runSilently = true;
			}
			else if (text.Equals("/SUPPRESSMSGBOXES", StringComparison.OrdinalIgnoreCase))
			{
				suppressMessageBoxes = true;
			}
			else if (text.Equals("/CLOSEAPPLICATIONS", StringComparison.OrdinalIgnoreCase))
			{
				closeApplications = true;
			}
			else if (text.Equals("--no-desktop-shortcut", StringComparison.OrdinalIgnoreCase))
			{
				createDesktopShortcut = false;
			}
			else if (text.Equals("--no-launch", StringComparison.OrdinalIgnoreCase) || text.Equals("/NOLAUNCH", StringComparison.OrdinalIgnoreCase))
			{
				launchAfterInstall = false;
			}
			else if (text.Equals("/ACCEPTEULA", StringComparison.OrdinalIgnoreCase) || text.Equals("--accept-eula", StringComparison.OrdinalIgnoreCase) || text.Equals("--accept-license", StringComparison.OrdinalIgnoreCase))
			{
				acceptLicense = true;
			}
			else if (text.Equals("/UNINSTALL", StringComparison.OrdinalIgnoreCase) || text.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
			{
				runUninstall = true;
			}
			else if (text.Equals("/REMOVEUSERDATA", StringComparison.OrdinalIgnoreCase) || text.Equals("--remove-user-data", StringComparison.OrdinalIgnoreCase))
			{
				removeUserData = true;
			}
		}
		return new InstallerOptions(installDirectoryOverride, runSilently, suppressMessageBoxes, closeApplications, createDesktopShortcut, launchAfterInstall, acceptLicense, runUninstall, removeUserData);
	}

	public string ToArgumentString()
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrWhiteSpace(InstallDirectoryOverride))
		{
			list.Add("/DIR=\"" + InstallDirectoryOverride + "\"");
		}
		if (RunSilently)
		{
			list.Add("/VERYSILENT");
		}
		if (SuppressMessageBoxes)
		{
			list.Add("/SUPPRESSMSGBOXES");
		}
		if (CloseApplications)
		{
			list.Add("/CLOSEAPPLICATIONS");
		}
		if (!CreateDesktopShortcut)
		{
			list.Add("--no-desktop-shortcut");
		}
		if (!LaunchAfterInstall)
		{
			list.Add("/NOLAUNCH");
		}
		if (RunUninstall)
		{
			list.Add("/UNINSTALL");
		}
		if (RemoveUserData)
		{
			list.Add("/REMOVEUSERDATA");
		}
		return string.Join(" ", list);
	}
}
