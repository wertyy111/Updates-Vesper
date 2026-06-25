using System.Text.Json;

namespace VesperLauncher.PhotinoHost;

internal interface ILauncherBackendHost : IDisposable
{
    void Start();

    Task<bool> WaitForLauncherReadyAsync();

    Task<object> GetSnapshotAsync();

    Task ExecuteCommandAsync(string command, JsonElement payload);
}

