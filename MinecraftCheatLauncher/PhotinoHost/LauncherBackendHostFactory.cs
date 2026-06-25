using VesperLauncher.Platform;

namespace VesperLauncher.PhotinoHost;

internal static class LauncherBackendHostFactory
{
    public static ILauncherBackendHost CreateCurrent()
    {
        var platform = PlatformServiceFactory.CreateCurrent();

        if (platform.Kind == PlatformKind.Windows)
        {
            var wpfBackendHost = TryCreateWpfBackendHost();
            if (wpfBackendHost is not null)
            {
                return wpfBackendHost;
            }
        }

        return new LauncherFallbackBackendHost(platform);
    }

    private static ILauncherBackendHost? TryCreateWpfBackendHost()
    {
        var type = typeof(LauncherBackendHostFactory).Assembly.GetType(
            "VesperLauncher.PhotinoHost.LauncherWpfBackendHost",
            throwOnError: false);

        if (type is null || !typeof(ILauncherBackendHost).IsAssignableFrom(type))
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(type, nonPublic: true) as ILauncherBackendHost;
        }
        catch
        {
            return null;
        }
    }
}

