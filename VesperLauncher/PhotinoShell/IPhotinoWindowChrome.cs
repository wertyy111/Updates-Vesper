using Photino.NET;

namespace VesperLauncher.PhotinoShell;

internal interface IPhotinoWindowChrome
{
    bool SupportsNativeWindowShaping { get; }

    void ScheduleLauncherWindowBounds(PhotinoWindow window, CancellationToken cancellationToken);

    void ScheduleRestoreBoundsGuard(PhotinoWindow window, CancellationToken cancellationToken);

    bool TryApplyLauncherWindowBounds(PhotinoWindow window, bool updatePosition);

    void ApplySplashWindowBounds(PhotinoWindow window);

    void SetWindowVisibility(PhotinoWindow window, bool visible);

    void MinimizeWindow(PhotinoWindow window);

    void ApplyWindowBackdrop(PhotinoWindow window, int backdropType);

    void StartWindowDrag(PhotinoWindow window);

    void StartWindowResize(PhotinoWindow window, string direction);
}
