using VesperLauncher.Core;

namespace VesperLauncher.Platform;

public static class PlatformDependencyRegistration
{
    public static void RegisterPlatformServices(this DependencyContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.RegisterSingleton<IPlatformService>(_ => PlatformServiceFactory.CreateCurrent());
        container.RegisterSingleton<IPlatformPathService>(services => services.Resolve<IPlatformService>().Paths);
        container.RegisterSingleton<IPlatformProcessService>(services => services.Resolve<IPlatformService>().Processes);
    }
}

