namespace VesperLauncher.Platform;

public sealed class PlatformService : IPlatformService
{
    public PlatformService(
        PlatformKind kind,
        string displayName,
        PlatformFeatureSet features,
        IPlatformPathService paths,
        IPlatformProcessService processes)
    {
        Kind = kind;
        DisplayName = displayName;
        Features = features;
        Paths = paths;
        Processes = processes;
    }

    public PlatformKind Kind { get; }

    public string DisplayName { get; }

    public PlatformFeatureSet Features { get; }

    public IPlatformPathService Paths { get; }

    public IPlatformProcessService Processes { get; }
}

