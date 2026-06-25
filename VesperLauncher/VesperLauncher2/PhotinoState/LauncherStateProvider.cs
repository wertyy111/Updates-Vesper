namespace VesperLauncher.PhotinoState;

internal sealed class LauncherStateProvider
{
    public LauncherSnapshot CreateSnapshot(LauncherSnapshotParts parts)
    {
        return new LauncherSnapshot(
            parts.ActiveSection,
            parts.ActiveSettingsTab,
            parts.IsBusy,
            parts.IsGameRunning,
            parts.CanAccessFriends,
            parts.NotificationsCount,
            parts.Theme,
            parts.Main,
            parts.Account,
            parts.Settings,
            parts.Skin,
            parts.Background,
            parts.Mods,
            parts.Friends);
    }
}

