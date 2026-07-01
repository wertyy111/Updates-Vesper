using Photino.NET;

namespace VesperLauncher.PhotinoShell;

internal sealed class DefaultPhotinoWindowChrome : IPhotinoWindowChrome
{
    public bool SupportsNativeWindowShaping => false;

    public void ScheduleLauncherWindowBounds(PhotinoWindow window, CancellationToken cancellationToken)
    {
    }

    public void ScheduleRestoreBoundsGuard(PhotinoWindow window, CancellationToken cancellationToken)
    {
    }

    public bool TryApplyLauncherWindowBounds(PhotinoWindow window, bool updatePosition)
    {
        return false;
    }

    public void ApplySplashWindowBounds(PhotinoWindow window)
    {
        var targetWidth = 440;
        var targetHeight = 240;
        window.SetSize(targetWidth, targetHeight);
        window.Center();
    }

    public void SetWindowVisibility(PhotinoWindow window, bool visible)
    {
    }

    public void MinimizeWindow(PhotinoWindow window)
    {
        window.Minimized = true;
    }

    public void ApplyWindowBackdrop(PhotinoWindow window, int backdropType)
    {
    }

    public void StartWindowDrag(PhotinoWindow window)
    {
    }

    public void StartWindowResize(PhotinoWindow window, string direction)
    {
    }
}
