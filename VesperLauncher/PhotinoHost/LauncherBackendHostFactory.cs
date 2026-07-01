using VesperLauncher.Platform;

namespace VesperLauncher.PhotinoHost;

internal static class LauncherBackendHostFactory
{
    public static ILauncherBackendHost CreateCurrent()
    {
        var platform = PlatformServiceFactory.CreateCurrent();
        return new LauncherFallbackBackendHost(platform);
    }
}

