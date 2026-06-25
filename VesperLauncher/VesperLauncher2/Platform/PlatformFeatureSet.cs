namespace VesperLauncher.Platform;

public sealed record PlatformFeatureSet(
    bool SupportsVesperNetService,
    bool SupportsWindowsInstaller,
    bool SupportsVelopackAutoUpdate,
    bool SupportsNativeWindowShaping,
    bool SupportsOpenFolder,
    bool SupportsOpenUrl);

