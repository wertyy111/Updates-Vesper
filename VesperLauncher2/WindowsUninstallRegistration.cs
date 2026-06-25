using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VesperLauncher;

internal static class WindowsUninstallRegistration
{
    private static readonly HashSet<string> EstimatedSizeExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "GameData",
        ".launcher-data",
        ".launcher-temp-probe",
        ".launcher-updates"
    };
    private static readonly HashSet<string> EstimatedSizeExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "installer.log",
        "launcher-errors.log",
        "_last_java_stdout.log",
        "_last_java_stderr.log"
    };
    private const string AppName = "Vesper Launcher";
    private const string Publisher = "Vesper Launcher";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VesperLauncher";
    private const string UninstallExecutableName = "uninstall.exe";
    private const string LegacyUninstallScriptName = "uninstall.ps1";
    private const string LegacyUninstallCommandName = "uninstall.cmd";
    private const string StartMenuShortcutName = "Vesper Launcher";
    private const string DesktopShortcutName = "Vesper Launcher";
    private const string UninstallShortcutName = "Uninstall Vesper Launcher";
    private static readonly string[] LegacyUninstallShortcutNames =
    [
        "РЈРґР°Р»РёС‚СЊ Vesper Launcher",
        "Р Р€Р Т‘Р В°Р В»Р С‘РЎвЂљРЎРЉ Vesper Launcher"
    ];

    public static void EnsureRegistered(Action<string> logInfo, Action<Exception, string> logError)
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return;
            }

            RemoveLegacyUninstallArtifacts(installDir);

            var uninstallExecutablePath = Path.Combine(installDir, UninstallExecutableName);
            if (!File.Exists(uninstallExecutablePath))
            {
                logInfo("Деинсталлятор uninstall.exe еще не найден, регистрация пропущена.");
                return;
            }

            CreateShellShortcuts(exePath, uninstallExecutablePath);
            WriteRegistryEntry(installDir, exePath, uninstallExecutablePath);
            logInfo("Проверена регистрация деинсталлятора Windows.");
        }
        catch (Exception ex)
        {
            logError(ex, "Не удалось зарегистрировать деинсталлятор Windows");
        }
    }

    private static void WriteRegistryEntry(string installDir, string exePath, string uninstallExecutablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
        if (key is null)
        {
            return;
        }

        var version = FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? "1.0.0";
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("UninstallString", $"\"{uninstallExecutablePath}\" /UNINSTALL");
        key.SetValue("QuietUninstallString", $"\"{uninstallExecutablePath}\" /UNINSTALL /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", CalculateInstalledApplicationSizeInKilobytes(installDir), RegistryValueKind.DWord);
    }

    private static void RemoveLegacyUninstallArtifacts(string installDir)
    {
        DeleteFileIfExists(Path.Combine(installDir, LegacyUninstallScriptName));
        DeleteFileIfExists(Path.Combine(installDir, LegacyUninstallCommandName));
    }

    private static void CreateShellShortcuts(string exePath, string uninstallExecutablePath)
    {
        var programsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            AppName);

        DeleteLegacyUninstallShortcuts(programsFolder);
        TryCreateShortcut(programsFolder, exePath, StartMenuShortcutName, null, exePath);
        TryCreateShortcut(
            programsFolder,
            uninstallExecutablePath,
            UninstallShortcutName,
            "/UNINSTALL",
            exePath);

        RefreshDesktopShortcutIfPresent(exePath);
    }

    private static void RefreshDesktopShortcutIfPresent(string exePath)
    {
        var desktopShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{DesktopShortcutName}.lnk");

        if (!File.Exists(desktopShortcutPath))
        {
            return;
        }

        TryCreateShortcut(
            Path.GetDirectoryName(desktopShortcutPath) ?? string.Empty,
            exePath,
            DesktopShortcutName,
            null,
            exePath);
    }

    private static void TryCreateShortcut(
        string folder,
        string targetPath,
        string shortcutName,
        string? arguments,
        string iconLocation)
    {
        try
        {
            CreateShortcut(folder, targetPath, shortcutName, arguments, iconLocation);
        }
        catch
        {
            // Shell shortcut COM automation is flaky on some Windows 11 setups.
            // Registration should continue even if the Start Menu entry cannot be recreated.
        }
    }

    private static void CreateShortcut(
        string folder,
        string targetPath,
        string shortcutName,
        string? arguments,
        string iconLocation)
    {
        Directory.CreateDirectory(folder);
        var shortcutPath = Path.Combine(folder, $"{shortcutName}.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null)
            {
                return;
            }

            SetComProperty(shortcut, "TargetPath", targetPath);

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                SetComProperty(shortcut, "Arguments", arguments);
            }

            var workingDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                SetComProperty(shortcut, "WorkingDirectory", workingDirectory);
            }

            var normalizedIconLocation = NormalizeIconLocation(iconLocation);
            if (!string.IsNullOrWhiteSpace(normalizedIconLocation))
            {
                SetComProperty(shortcut, "IconLocation", normalizedIconLocation);
            }

            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void SetComProperty(object comObject, string propertyName, string value)
    {
        comObject.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, comObject, [value]);
    }

    private static string NormalizeIconLocation(string iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return string.Empty;
        }

        return File.Exists(iconLocation)
            ? $"{iconLocation},0"
            : string.Empty;
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    private static void DeleteLegacyUninstallShortcuts(string programsFolder)
    {
        foreach (var legacyShortcutName in LegacyUninstallShortcutNames)
        {
            if (string.Equals(legacyShortcutName, UninstallShortcutName, StringComparison.Ordinal))
            {
                continue;
            }

            DeleteFileIfExists(Path.Combine(programsFolder, $"{legacyShortcutName}.lnk"));
        }
    }

    private static void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            File.SetAttributes(filePath, FileAttributes.Normal);
            File.Delete(filePath);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private static int CalculateInstalledApplicationSizeInKilobytes(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return 0;
            }

            long totalBytes = 0;
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (ShouldExcludeFromEstimatedSize(directoryPath, filePath))
                    {
                        continue;
                    }

                    totalBytes += new FileInfo(filePath).Length;
                }
                catch
                {
                    // ignore unstable files
                }
            }

            return (int)Math.Clamp(totalBytes / 1024L, 0L, int.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    private static bool ShouldExcludeFromEstimatedSize(string rootDirectory, string filePath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        return EstimatedSizeExcludedDirectories.Contains(segments[0]) ||
               (segments.Length == 1 && EstimatedSizeExcludedFiles.Contains(segments[0]));
    }
}


