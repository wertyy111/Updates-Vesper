using VesperLauncher.Platform;

namespace VesperLauncher.PhotinoShell;

internal static class PhotinoWindowChromeFactory
{
    public static IPhotinoWindowChrome Create(IPlatformService platform)
    {
        return platform.Features.SupportsNativeWindowShaping && OperatingSystem.IsWindows()
            ? new WindowsPhotinoWindowChrome()
            : new DefaultPhotinoWindowChrome();
    }
}
