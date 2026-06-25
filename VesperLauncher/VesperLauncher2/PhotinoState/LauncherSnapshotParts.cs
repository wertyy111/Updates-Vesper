namespace VesperLauncher.PhotinoState;

internal sealed record LauncherSnapshotParts(
    string ActiveSection,
    string ActiveSettingsTab,
    bool IsBusy,
    bool IsGameRunning,
    bool CanAccessFriends,
    int NotificationsCount,
    object Theme,
    object Main,
    object Account,
    object Settings,
    object Skin,
    object Background,
    object Mods,
    object Friends);

