using System.IO;
using System.Linq;

namespace VesperLauncher.Platform;

internal static class LauncherDataPaths
{
    private const string InstallLocalDataDirectoryName = ".launcher-data";
    private const string LegacyWindowsDataDirectoryName = "VesperLauncher";
    private const long MinimumWritableFreeBytes = 64L * 1024L * 1024L;

    private static readonly IPlatformPathService PlatformPaths = PlatformServiceFactory.CreateCurrent().Paths;

    public static string GetPreferredDataDirectory()
    {
        var candidates = new[]
        {
            GetInstallLocalDataDirectory(ensureExists: false),
            TryResolvePlatformLauncherDataDirectory(),
            Path.Combine(ResolveLocalApplicationDataDirectory(), "Vesper")
        };

        foreach (var candidate in candidates)
        {
            if (TryEnsureWritableDirectory(candidate, out var writableDirectory))
            {
                return writableDirectory;
            }
        }

        return GetLegacyWindowsDataDirectory();
    }

    public static string GetInstallLocalDataDirectory(bool ensureExists = true)
    {
        var path = Path.Combine(AppContext.BaseDirectory, InstallLocalDataDirectoryName);
        if (ensureExists)
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    public static string GetLegacyWindowsDataDirectory(bool ensureExists = true)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = string.IsNullOrWhiteSpace(appData)
            ? PlatformPaths.UserDataDirectory
            : appData;
        var path = Path.Combine(root, LegacyWindowsDataDirectoryName);
        if (ensureExists)
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    public static string GetDataFilePath(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new ArgumentException("File name must not be empty.", nameof(fileName));
        }

        var preferredDirectory = GetPreferredDataDirectory();
        var preferredPath = Path.Combine(preferredDirectory, safeFileName);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        TryCopyFirstExistingFile(
            GetCompatibilityDataFilePaths(safeFileName),
            preferredPath);

        return preferredPath;
    }

    public static IReadOnlyList<string> GetDataFileCandidates(string fileName, bool includeBaseDirectoryFile = false)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string>
        {
            Path.Combine(GetPreferredDataDirectory(), safeFileName),
            Path.Combine(GetInstallLocalDataDirectory(ensureExists: false), safeFileName),
            Path.Combine(GetLegacyWindowsDataDirectory(ensureExists: false), safeFileName)
        };

        if (includeBaseDirectoryFile)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, safeFileName));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string GetFirstExistingDataFilePath(string fileName, bool includeBaseDirectoryFile = false)
    {
        foreach (var candidate in GetDataFileCandidates(fileName, includeBaseDirectoryFile))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return GetDataFilePath(fileName);
    }

    public static void MigrateDirectoryContents(string sourceDirectory, string destinationDirectory, string searchPattern = "*.*")
    {
        try
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourceFilePath in Directory.EnumerateFiles(sourceDirectory, searchPattern, SearchOption.TopDirectoryOnly))
            {
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFilePath));
                if (!File.Exists(destinationPath))
                {
                    File.Copy(sourceFilePath, destinationPath, overwrite: false);
                }
            }
        }
        catch
        {
            // Keep startup resilient if an old profile directory cannot be migrated.
        }
    }

    private static IEnumerable<string> GetCompatibilityDataFilePaths(string fileName)
    {
        yield return Path.Combine(GetInstallLocalDataDirectory(ensureExists: false), fileName);
        yield return Path.Combine(GetLegacyWindowsDataDirectory(ensureExists: false), fileName);
    }

    private static string? TryResolvePlatformLauncherDataDirectory()
    {
        try
        {
            return PlatformPaths.GetLauncherDataDirectory();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveLocalApplicationDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return localAppData;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return appData;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? AppContext.BaseDirectory
            : userProfile;
    }

    private static bool TryEnsureWritableDirectory(string? directoryPath, out string writableDirectory)
    {
        writableDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directoryPath);
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(directoryPath));
            if (!string.IsNullOrWhiteSpace(driveRoot))
            {
                var driveInfo = new DriveInfo(driveRoot);
                if (driveInfo.IsReady && driveInfo.AvailableFreeSpace < MinimumWritableFreeBytes)
                {
                    return false;
                }
            }

            var probePath = Path.Combine(directoryPath, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probePath, new byte[4096]);
            File.Delete(probePath);
            writableDirectory = directoryPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryCopyFirstExistingFile(IEnumerable<string> sourcePaths, string destinationPath)
    {
        try
        {
            foreach (var sourcePath in sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(sourcePath, destinationPath, overwrite: false);
                return;
            }
        }
        catch
        {
            // The preferred path will still be returned; callers can recreate the file.
        }
    }
}

