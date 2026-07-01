using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using VesperLauncher.Platform;

namespace VesperLauncher.Core;

internal sealed class MinecraftVersionManifestService
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly string _cachePath;

    public MinecraftVersionManifestService(IPlatformService platform)
    {
        _cachePath = Path.Combine(platform.Paths.GetLauncherDataDirectory(), "mojang-version-manifest-cache.json");
    }

    public static MinecraftVersionCatalog LoadFallbackCatalogForStartup()
    {
        return CreateFallbackCatalog();
    }

    public async Task<MinecraftVersionCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var online = await TryLoadOnlineAsync(cancellationToken).ConfigureAwait(false);
        if (online is not null)
        {
            SaveCache(online);
            return ToCatalog(online);
        }

        var cached = TryLoadCache();
        if (cached is not null)
        {
            return ToCatalog(cached);
        }

        return CreateFallbackCatalog();
    }

    private static async Task<MojangVersionManifest?> TryLoadOnlineAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(ManifestUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<MojangVersionManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private MojangVersionManifest? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MojangVersionManifest>(File.ReadAllText(_cachePath));
        }
        catch
        {
            return null;
        }
    }

    private void SaveCache(MojangVersionManifest manifest)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_cachePath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Offline launch can still continue with in-memory data.
        }
    }

    private static MinecraftVersionCatalog ToCatalog(MojangVersionManifest manifest)
    {
        var versions = manifest.Versions
            .Where(version => !string.IsNullOrWhiteSpace(version.Id))
            .GroupBy(version => version.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(version => new MinecraftVersionCatalogEntry(
                version.Id.Trim(),
                string.IsNullOrWhiteSpace(version.Type) ? "release" : version.Type.Trim(),
                version.Url?.Trim() ?? string.Empty,
                version.Sha1?.Trim() ?? string.Empty,
                version.ReleaseTime ?? DateTimeOffset.UtcNow))
            .ToArray();

        var latestRelease = string.IsNullOrWhiteSpace(manifest.Latest.Release)
            ? versions.FirstOrDefault(version => version.IsRelease)?.Id ?? versions.FirstOrDefault()?.Id ?? "1.21"
            : manifest.Latest.Release;

        if (!versions.Any(version => string.Equals(version.Id, latestRelease, StringComparison.OrdinalIgnoreCase)))
        {
            versions = new[] { new MinecraftVersionCatalogEntry(latestRelease, "release", string.Empty, string.Empty, DateTimeOffset.UtcNow) }
                .Concat(versions)
                .ToArray();
        }

        return new MinecraftVersionCatalog(latestRelease, versions);
    }

    private static MinecraftVersionCatalog CreateFallbackCatalog()
    {
        var versions = new[]
        {
            new MinecraftVersionCatalogEntry("1.21.6", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.21.5", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.21.4", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.21.1", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.20.6", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.20.4", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.20.1", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.19.4", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.18.2", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new MinecraftVersionCatalogEntry("1.16.5", "release", string.Empty, string.Empty, DateTimeOffset.UtcNow)
        };

        return new MinecraftVersionCatalog(versions[0].Id, versions);
    }

    private sealed class MojangVersionManifest
    {
        [JsonPropertyName("latest")]
        public MojangLatestVersions Latest { get; set; } = new();

        [JsonPropertyName("versions")]
        public List<MojangVersionEntry> Versions { get; set; } = [];
    }

    private sealed class MojangLatestVersions
    {
        [JsonPropertyName("release")]
        public string Release { get; set; } = string.Empty;
    }

    private sealed class MojangVersionEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        [JsonPropertyName("releaseTime")]
        public DateTimeOffset? ReleaseTime { get; set; }
    }
}

internal sealed record MinecraftVersionCatalog(string LatestRelease, IReadOnlyList<MinecraftVersionCatalogEntry> Versions)
{
    public IReadOnlyList<string> Releases => Versions
        .Where(version => version.IsRelease)
        .Select(version => version.Id)
        .ToArray();
}

internal sealed record MinecraftVersionCatalogEntry(
    string Id,
    string Type,
    string MetadataUrl,
    string MetadataSha1,
    DateTimeOffset ReleaseTime)
{
    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);
}
