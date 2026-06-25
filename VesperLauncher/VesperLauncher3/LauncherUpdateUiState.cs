namespace VesperLauncher;

public sealed class LauncherUpdateUiState
{
    public required string Message { get; init; }

    public string? DetailMessage { get; init; }

    public int? ProgressPercent { get; init; }

    public bool IsIndeterminate { get; init; } = true;

    public string? ProgressText { get; init; }
}

