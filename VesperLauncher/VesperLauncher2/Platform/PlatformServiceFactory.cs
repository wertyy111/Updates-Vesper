namespace VesperLauncher.Platform;

public static class PlatformServiceFactory
{
    public static IPlatformService CreateCurrent()
    {
        var kind = DetectCurrentPlatform();
        return new PlatformService(
            kind,
            ResolveDisplayName(kind),
            ResolveFeatureSet(kind),
            new PlatformPathService(kind),
            new PlatformProcessService(kind));
    }

    public static PlatformKind DetectCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return PlatformKind.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return PlatformKind.MacOs;
        }

        if (OperatingSystem.IsLinux())
        {
            return PlatformKind.Linux;
        }

        return PlatformKind.Unknown;
    }

    private static string ResolveDisplayName(PlatformKind kind)
    {
        return kind switch
        {
            PlatformKind.Windows => "Windows",
            PlatformKind.Linux => "Linux",
            PlatformKind.MacOs => "macOS",
            _ => "Unknown"
        };
    }

    private static PlatformFeatureSet ResolveFeatureSet(PlatformKind kind)
    {
        return kind switch
        {
            PlatformKind.Windows => new PlatformFeatureSet(
                SupportsVesperNetService: true,
                SupportsWindowsInstaller: true,
                SupportsVelopackAutoUpdate: true,
                SupportsNativeWindowShaping: true,
                SupportsOpenFolder: true,
                SupportsOpenUrl: true),

            PlatformKind.Linux or PlatformKind.MacOs => new PlatformFeatureSet(
                SupportsVesperNetService: false,
                SupportsWindowsInstaller: false,
                SupportsVelopackAutoUpdate: false,
                SupportsNativeWindowShaping: false,
                SupportsOpenFolder: true,
                SupportsOpenUrl: true),

            _ => new PlatformFeatureSet(
                SupportsVesperNetService: false,
                SupportsWindowsInstaller: false,
                SupportsVelopackAutoUpdate: false,
                SupportsNativeWindowShaping: false,
                SupportsOpenFolder: false,
                SupportsOpenUrl: false)
        };
    }
}

