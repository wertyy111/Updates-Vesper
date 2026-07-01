using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class InstallerOperations
{
    private const string EmbeddedVelopackSetupName = "velopack-setup.exe";
    private const long MinimumFreeSpaceBytes = 800L * 1024 * 1024;

    public static InstallResult Install(InstallerOptions options, Action<InstallProgress> reportProgress)
    {
        var installDir = InstallerPaths.ResolveInstallDirectory(options.InstallDirectoryOverride);
        Report(reportProgress, "Закрываем запущенный лаунчер...", 3);
        KillRunningLauncherProcesses();
        Report(reportProgress, "Проверяем папку установки...", 5);
        Directory.CreateDirectory(installDir);
        EnsureEnoughFreeSpace(installDir);

        var setupPath = ExtractVelopackSetup(reportProgress, installDir);
        try
        {
            Report(reportProgress, "Запускаем установку Vesper Launcher...", 25);
            RunVelopackSetup(setupPath, installDir, options);

            Report(reportProgress, "Проверяем установленный лаунчер...", 92);
            var launcherExe = FindInstalledLauncherExecutable(installDir);
            if (launcherExe is null)
            {
                throw new InvalidOperationException(
                    "Установка завершилась, но VesperLauncher.exe не найден. Попробуй выбрать другую папку или запустить установщик еще раз.");
            }

            Report(reportProgress, "Установка завершена.", 100);
            return new InstallResult(installDir, launcherExe, FindInstalledUpdateExe(installDir) ?? string.Empty);
        }
        finally
        {
            TryDeleteTempSetup(setupPath);
        }
    }

    public static UninstallResult Uninstall(InstallerOptions options, Action<InstallProgress> reportProgress)
    {
        var installDir = InstallerPaths.ResolveInstallDirectoryForUninstall(options.InstallDirectoryOverride);
        Report(reportProgress, "Закрываем запущенный лаунчер...", 5);
        KillRunningLauncherProcesses();
        var updateExe = FindInstalledUpdateExe(installDir);
        if (updateExe is null)
        {
            throw new InvalidOperationException("Не найден Update.exe установленного Vesper Launcher.");
        }

        Report(reportProgress, "Запускаем удаление Vesper Launcher...", 20);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = updateExe,
            UseShellExecute = false
        });

        process?.WaitForExit();
        Report(reportProgress, "Удаление передано Velopack.", 100);
        return new UninstallResult(installDir, options.RemoveUserData);
    }

    public static void TryLaunch(string exePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
        }
    }

    private static string ExtractVelopackSetup(Action<InstallProgress> reportProgress, string installDir)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(EmbeddedVelopackSetupName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                "В установщик не встроен Velopack setup. Пересобери setup через Publish-VelopackUpdate.ps1.");
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "VesperSetupTemp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var setupPath = Path.Combine(tempDirectory, "Vesper.Internal.Setup.exe");

        Report(reportProgress, "Подготавливаем установщик...", 12);
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Не удалось открыть встроенный Velopack setup.");
        using var file = File.Create(setupPath);
        resource.CopyTo(file);

        return setupPath;
    }

    private static void RunVelopackSetup(string setupPath, string installDir, InstallerOptions options)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "VesperLauncherVelopackSetup.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = setupPath,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--installto");
        startInfo.ArgumentList.Add(installDir);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(logPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить встроенный Velopack setup.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Velopack setup завершился с кодом {process.ExitCode}. Лог: {logPath}");
        }
    }

    private static void EnsureEnoughFreeSpace(string installDir)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(installDir));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < MinimumFreeSpaceBytes)
        {
            throw new InvalidOperationException(
                $"На диске {drive.Name} мало места. Нужно минимум {FormatBytes(MinimumFreeSpaceBytes)}, доступно {FormatBytes(drive.AvailableFreeSpace)}.");
        }
    }

    private static string? FindInstalledLauncherExecutable(string installDir)
    {
        var candidates = new[]
        {
            Path.Combine(installDir, "current", "VesperLauncher.exe"),
            Path.Combine(installDir, "VesperLauncher.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.Exists(installDir)
            ? Directory.EnumerateFiles(installDir, "VesperLauncher.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault()
            : null;
    }

    private static string? FindInstalledUpdateExe(string installDir)
    {
        var candidates = new[]
        {
            Path.Combine(installDir, "Update.exe"),
            Path.Combine(installDir, "current", "Update.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.Exists(installDir)
            ? Directory.EnumerateFiles(installDir, "Update.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault()
            : null;
    }

    private static void TryDeleteTempSetup(string setupPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(setupPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void Report(Action<InstallProgress> reportProgress, string status, int percent)
    {
        reportProgress(new InstallProgress(status, Math.Clamp(percent, 0, 100)));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static void KillRunningLauncherProcesses()
    {
        try
        {
            var names = new[] { "VesperLauncher", "Vesper.Launcher", "VesperLauncher" };
            foreach (var name in names)
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }
    }
}

