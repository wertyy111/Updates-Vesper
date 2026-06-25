using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLauncher.Launcher;

public sealed class VersionStateMachine
{
    public VersionState GetState(
        string gameDirectory,
        MinecraftVersionEntry? version,
        bool isDownloading = false,
        double? progressPercent = null,
        bool hasUpdate = false)
    {
        if (isDownloading)
        {
            return new VersionState(
                VersionInstallState.Downloading,
                BuildDownloadingText(progressPercent),
                progressPercent);
        }

        if (version is null)
        {
            return VersionState.NotInstalled;
        }

        if (!IsVersionInstalled(gameDirectory, version.Id))
        {
            return VersionState.NotInstalled;
        }

        if (hasUpdate)
        {
            return VersionState.UpdatingAvailable;
        }

        return VersionState.Installed;
    }

    public async Task<VersionIntegrityResult> CheckIntegrityAsync(
        string gameDirectory,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var paths = ResolveVersionPaths(gameDirectory, versionId);
        if (!File.Exists(paths.JsonPath))
        {
            return VersionIntegrityResult.Missing("version json is missing");
        }

        if (!File.Exists(paths.JarPath))
        {
            return VersionIntegrityResult.Missing("version jar is missing");
        }

        var expectedSha1 = await TryReadClientSha1Async(paths.JsonPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedSha1))
        {
            return VersionIntegrityResult.ValidWithoutChecksum;
        }

        var actualSha1 = await ComputeSha1Async(paths.JarPath, cancellationToken).ConfigureAwait(false);
        return string.Equals(expectedSha1, actualSha1, StringComparison.OrdinalIgnoreCase)
            ? VersionIntegrityResult.Valid
            : VersionIntegrityResult.InvalidChecksum(expectedSha1, actualSha1);
    }

    public bool IsVersionInstalled(string gameDirectory, string versionId)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || string.IsNullOrWhiteSpace(versionId))
        {
            return false;
        }

        var paths = ResolveVersionPaths(gameDirectory, versionId);
        return File.Exists(paths.JsonPath) && File.Exists(paths.JarPath);
    }

    public static VersionFilePaths ResolveVersionPaths(string gameDirectory, string versionId)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", versionId);
        return new VersionFilePaths(
            versionDirectory,
            Path.Combine(versionDirectory, $"{versionId}.json"),
            Path.Combine(versionDirectory, $"{versionId}.jar"));
    }

    private static string BuildDownloadingText(double? progressPercent)
    {
        return progressPercent.HasValue
            ? $"Загрузка... {Math.Clamp(progressPercent.Value, 0, 100):0}%"
            : "Загрузка...";
    }

    private static async Task<string?> TryReadClientSha1Async(
        string versionJsonPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(versionJsonPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (document.RootElement.TryGetProperty("downloads", out var downloads) &&
                downloads.TryGetProperty("client", out var client) &&
                client.TryGetProperty("sha1", out var sha1Element))
            {
                return sha1Element.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public enum VersionInstallState
{
    NotInstalled,
    Downloading,
    Installed,
    UpdatingAvailable
}

public sealed record VersionState(
    VersionInstallState State,
    string ButtonText,
    double? ProgressPercent = null)
{
    public static VersionState NotInstalled { get; } = new(VersionInstallState.NotInstalled, "Установить");
    public static VersionState Installed { get; } = new(VersionInstallState.Installed, "Играть");
    public static VersionState UpdatingAvailable { get; } = new(VersionInstallState.UpdatingAvailable, "Доступно обновление");
}

public sealed record VersionIntegrityResult(
    bool IsInstalled,
    bool IsChecksumValid,
    string? ExpectedSha1,
    string? ActualSha1,
    string? Problem)
{
    public static VersionIntegrityResult Valid { get; } = new(true, true, null, null, null);
    public static VersionIntegrityResult ValidWithoutChecksum { get; } = new(true, true, null, null, null);

    public static VersionIntegrityResult Missing(string problem) =>
        new(false, false, null, null, problem);

    public static VersionIntegrityResult InvalidChecksum(string expectedSha1, string actualSha1) =>
        new(true, false, expectedSha1, actualSha1, "jar checksum mismatch");
}

public sealed record VersionFilePaths(
    string VersionDirectory,
    string JsonPath,
    string JarPath);

