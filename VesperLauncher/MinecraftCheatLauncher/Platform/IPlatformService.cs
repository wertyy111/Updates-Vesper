namespace VesperLauncher.Platform;

public interface IPlatformService
{
    PlatformKind Kind { get; }

    string DisplayName { get; }

    PlatformFeatureSet Features { get; }

    IPlatformPathService Paths { get; }

    IPlatformProcessService Processes { get; }
}

