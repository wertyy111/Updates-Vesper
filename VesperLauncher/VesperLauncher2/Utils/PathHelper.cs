using System;
using System.IO;
using System.Linq;

namespace VesperLauncher.Utils;

public static class PathHelper
{
    public const string AppDirectoryName = "Vesper";

    private static readonly char[] ForbiddenSegmentChars =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    ];

    public static string GetUserDataDirectory()
    {
        var root = GetPlatformDataRoot();
        return EnsureDirectory(Path.Combine(root, AppDirectoryName));
    }

    public static string GetUserCacheDirectory()
    {
        var root = GetPlatformCacheRoot();
        return EnsureDirectory(Path.Combine(root, AppDirectoryName, "cache"));
    }

    public static string GetLogsRootDirectory()
    {
        return EnsureDirectory(Path.Combine(GetUserDataDirectory(), "logs"));
    }

    public static string CreateLogSessionDirectory(string? username, DateTimeOffset? timestamp = null)
    {
        var safeUserName = SanitizePathSegment(username, "unknown-user");
        var sessionName = (timestamp ?? DateTimeOffset.Now).ToString("yyyy-MM-dd_HH-mm-ss");
        return EnsureDirectory(Path.Combine(GetLogsRootDirectory(), safeUserName, sessionName));
    }

    public static string SanitizePathSegment(string? value, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var cleaned = new string(value
            .Where(character => !char.IsControl(character) && !ForbiddenSegmentChars.Contains(character))
            .ToArray())
            .Trim();

        cleaned = cleaned.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    public static string EnsureDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    public static string GetMinecraftDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        }
        
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "minecraft");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
    }

    private static string GetPlatformDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetSpecialFolderOrFallback(Environment.SpecialFolder.ApplicationData, ".config");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    private static string GetPlatformCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetSpecialFolderOrFallback(Environment.SpecialFolder.LocalApplicationData, ".cache");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Caches");
        }

        var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return !string.IsNullOrWhiteSpace(xdgCacheHome)
            ? xdgCacheHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
    }

    private static string GetSpecialFolderOrFallback(Environment.SpecialFolder folder, string unixFallback)
    {
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), unixFallback);
    }
}

