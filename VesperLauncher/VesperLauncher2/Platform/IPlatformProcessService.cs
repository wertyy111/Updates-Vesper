using System.Diagnostics;

namespace VesperLauncher.Platform;

public interface IPlatformProcessService
{
    Task<bool> OpenFolderAsync(string folderPath, CancellationToken cancellationToken = default);

    Task<bool> OpenUrlAsync(string url, CancellationToken cancellationToken = default);

    Process StartProcess(ProcessStartInfo startInfo);
}

