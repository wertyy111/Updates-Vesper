using System.Diagnostics;
using System.IO;

namespace VesperLauncher.Platform;

public sealed class PlatformProcessService : IPlatformProcessService
{
    public PlatformProcessService(PlatformKind platformKind)
    {
        PlatformKind = platformKind;
    }

    public PlatformKind PlatformKind { get; }

    public Task<bool> OpenFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Task.FromResult(false);
        }

        Directory.CreateDirectory(folderPath);
        return OpenPathAsync(folderPath, cancellationToken);
    }

    public Task<bool> OpenUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Task.FromResult(false);
        }

        return OpenPathAsync(uri.AbsoluteUri, cancellationToken);
    }

    public Process StartProcess(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process did not start: {startInfo.FileName}");
    }

    private Task<bool> OpenPathAsync(string pathOrUrl, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        try
        {
            var startInfo = CreateOpenStartInfo(pathOrUrl);
            using var process = StartProcess(startInfo);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private ProcessStartInfo CreateOpenStartInfo(string pathOrUrl)
    {
        if (PlatformKind == PlatformKind.Windows)
        {
            return new ProcessStartInfo(pathOrUrl)
            {
                UseShellExecute = true
            };
        }

        var startInfo = PlatformKind == PlatformKind.MacOs
            ? new ProcessStartInfo("open")
            : new ProcessStartInfo("xdg-open");

        startInfo.UseShellExecute = false;
        startInfo.ArgumentList.Add(pathOrUrl);
        return startInfo;
    }
}

