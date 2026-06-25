using System.IO;
using VesperLauncher.Utils;

namespace VesperLauncher.Platform;

public sealed class PlatformPathService : IPlatformPathService
{
    private const string LauncherDirectoryName = "Vesper";

    public PlatformPathService(PlatformKind platformKind)
    {
        PlatformKind = platformKind;
        UserProfileDirectory = ResolveUserProfileDirectory();
        UserDataDirectory = ResolveUserDataDirectory(platformKind);
        UserCacheDirectory = ResolveUserCacheDirectory(platformKind);
        LogsDirectory = Path.Combine(GetLauncherDataDirectory(), "logs");
        MinecraftDirectory = ResolveMinecraftDirectory(platformKind);
    }

    public PlatformKind PlatformKind { get; }

    public string UserProfileDirectory { get; }

    public string UserDataDirectory { get; }

    public string UserCacheDirectory { get; }

    public string LogsDirectory { get; }

    public string MinecraftDirectory { get; }

    public string GetLauncherDataDirectory()
    {
        return EnsureDirectory(Path.Combine(UserDataDirectory, LauncherDirectoryName));
    }

    public string GetLauncherCacheDirectory()
    {
        return EnsureDirectory(Path.Combine(UserCacheDirectory, LauncherDirectoryName, "cache"));
    }

    public string GetLauncherLogsDirectory()
    {
        return EnsureDirectory(LogsDirectory);
    }

    public string GetSkinsCacheDirectory()
    {
        return EnsureDirectory(Path.Combine(GetLauncherCacheDirectory(), "skins"));
    }

    public string GetVersionsCacheDirectory()
    {
        return EnsureDirectory(Path.Combine(GetLauncherCacheDirectory(), "versions"));
    }

    private string ResolveMinecraftDirectory(PlatformKind platformKind)
    {
        return platformKind switch
        {
            PlatformKind.Windows => Path.Combine(UserDataDirectory, ".minecraft"),
            PlatformKind.MacOs => Path.Combine(UserDataDirectory, "minecraft"),
            _ => Path.Combine(UserProfileDirectory, ".minecraft")
        };
    }

    private static string ResolveUserDataDirectory(PlatformKind platformKind)
    {
        return platformKind switch
        {
            PlatformKind.Windows => GetSpecialFolderOrFallback(Environment.SpecialFolder.ApplicationData, ".config"),
            PlatformKind.MacOs => Path.Combine(ResolveUserProfileDirectory(), "Library", "Application Support"),
            _ => GetEnvironmentPathOrFallback("XDG_DATA_HOME", ".local", "share")
        };
    }

    private static string ResolveUserCacheDirectory(PlatformKind platformKind)
    {
        return platformKind switch
        {
            PlatformKind.Windows => GetSpecialFolderOrFallback(Environment.SpecialFolder.LocalApplicationData, ".cache"),
            PlatformKind.MacOs => Path.Combine(ResolveUserProfileDirectory(), "Library", "Caches"),
            _ => GetEnvironmentPathOrFallback("XDG_CACHE_HOME", ".cache")
        };
    }

    private static string ResolveUserProfileDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? AppContext.BaseDirectory
            : profile;
    }

    private static string GetSpecialFolderOrFallback(Environment.SpecialFolder folder, string unixFallback)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path)
            ? Path.Combine(ResolveUserProfileDirectory(), unixFallback)
            : path;
    }

    private static string GetEnvironmentPathOrFallback(string variableName, params string[] fallbackSegments)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value)
            ? Path.Combine([ResolveUserProfileDirectory(), .. fallbackSegments])
            : value;
    }

    private static string EnsureDirectory(string directoryPath)
    {
        return PathHelper.EnsureDirectory(directoryPath);
    }
}

