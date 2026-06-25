namespace VesperLauncher.Platform;

public interface IPlatformPathService
{
    string UserProfileDirectory { get; }

    string UserDataDirectory { get; }

    string UserCacheDirectory { get; }

    string LogsDirectory { get; }

    string MinecraftDirectory { get; }

    string GetLauncherDataDirectory();

    string GetLauncherCacheDirectory();

    string GetLauncherLogsDirectory();

    string GetSkinsCacheDirectory();

    string GetVersionsCacheDirectory();
}

