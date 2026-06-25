using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VesperLauncher.Platform;

namespace VesperLauncher.Launcher;

internal static class VesperSkinRegistry
{
    private static readonly object SyncLock = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions RemoteJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string RegistryPath = LauncherDataPaths.GetDataFilePath("skin-registry.json");
    private static readonly string SyncConfigPath = LauncherDataPaths.GetDataFilePath("skin-sync.json");

    public static void SaveOrUpdate(
        string username,
        string uuid,
        string textureValue,
        string? textureSignature,
        string? textureUrl,
        string? accessToken = null,
        string? publishedUuid = null,
        byte[]? skinImageBytes = null)
    {
        var entry = CreateNormalizedEntry(username, uuid, textureValue, textureSignature, textureUrl, publishedUuid);
        if (entry is null)
        {
            return;
        }

        lock (SyncLock)
        {
            try
            {
                var state = LoadStateUnsafe(RegistryPath);
                UpsertEntryUnsafe(state, entry);
                SaveStateUnsafe(RegistryPath, state);
            }
            catch
            {
                // Keep launcher startup resilient if registry persistence fails.
            }
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        try
        {
            var config = LoadSyncConfigUnsafe();
            PublishEntryUnsafe(config, entry, accessToken!, skinImageBytes);
        }
        catch
        {
            // Keep local skin handling working even if cloud publish is temporarily unavailable.
        }
    }

    public static VesperSkinRegistryEntry? TryGetByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var normalizedUsername = username.Trim();

        lock (SyncLock)
        {
            try
            {
                var config = LoadSyncConfigUnsafe();
                var state = LoadStateUnsafe(RegistryPath);
                state.Entries.TryGetValue(normalizedUsername, out var localEntry);

                var remoteEntry = TryLoadEntryByUsernameUnsafe(config, normalizedUsername);
                if (ShouldReplaceEntry(localEntry, remoteEntry))
                {
                    UpsertEntryUnsafe(state, remoteEntry!);
                    SaveStateUnsafe(RegistryPath, state);
                    return remoteEntry;
                }

                return localEntry;
            }
            catch
            {
                return null;
            }
        }
    }

    public static VesperSkinRegistryEntry? TryGetByUuid(string uuid)
    {
        var normalizedUuid = NormalizeUuid(uuid);
        if (string.IsNullOrWhiteSpace(normalizedUuid))
        {
            return null;
        }

        lock (SyncLock)
        {
            try
            {
                var config = LoadSyncConfigUnsafe();
                var state = LoadStateUnsafe(RegistryPath);
                var localEntry = TryFindLocalByUuidUnsafe(state, normalizedUuid);

                var remoteEntry = TryLoadEntryByUuidUnsafe(config, normalizedUuid);
                if (ShouldReplaceEntry(localEntry, remoteEntry))
                {
                    UpsertEntryUnsafe(state, remoteEntry!);
                    SaveStateUnsafe(RegistryPath, state);
                    return remoteEntry;
                }

                return localEntry;
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool ShouldReplaceEntry(VesperSkinRegistryEntry? existingEntry, VesperSkinRegistryEntry? candidateEntry)
    {
        return candidateEntry is not null &&
               (existingEntry is null || candidateEntry.UpdatedAtUtc >= existingEntry.UpdatedAtUtc);
    }

    private static VesperSkinRegistryEntry? CreateNormalizedEntry(
        string username,
        string uuid,
        string textureValue,
        string? textureSignature,
        string? textureUrl,
        string? publishedUuid = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(uuid) ||
            string.IsNullOrWhiteSpace(textureValue))
        {
            return null;
        }

        var normalizedUsername = username.Trim();
        var normalizedUuid = NormalizeUuid(uuid);
        if (string.IsNullOrWhiteSpace(normalizedUuid))
        {
            return null;
        }

        var normalizedTextureValue = textureValue.Trim();
        if (normalizedTextureValue.Length == 0)
        {
            return null;
        }

        var normalizedTextureSignature = string.IsNullOrWhiteSpace(textureSignature)
            ? null
            : textureSignature.Trim();
        var normalizedTextureUrl = string.IsNullOrWhiteSpace(textureUrl)
            ? null
            : textureUrl.Trim();
        var normalizedPublishedUuid = NormalizeUuid(publishedUuid);
        if (string.Equals(normalizedPublishedUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase))
        {
            normalizedPublishedUuid = null;
        }

        return new VesperSkinRegistryEntry(
            normalizedUsername,
            normalizedUuid,
            normalizedPublishedUuid,
            normalizedTextureValue,
            normalizedTextureSignature,
            normalizedTextureUrl,
            updatedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static VesperSkinRegistryEntry? TryFindLocalByUuidUnsafe(VesperSkinRegistryState state, string normalizedUuid)
    {
        foreach (var entry in state.Entries.Values)
        {
            if (string.Equals(entry.Uuid, normalizedUuid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.PublishedUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BuildOfflineUuid(entry.Username), normalizedUuid, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static void UpsertEntryUnsafe(VesperSkinRegistryState state, VesperSkinRegistryEntry entry)
    {
        state.Entries[entry.Username] = entry;
    }

    private static VesperSkinRegistryEntry? TryLoadEntryByUsernameUnsafe(VesperSkinRegistrySyncConfig config, string username)
    {
        if (string.IsNullOrWhiteSpace(config.LookupByUsernameUrl))
        {
            return null;
        }

        var requestUrl = $"{config.LookupByUsernameUrl}?username={Uri.EscapeDataString(username)}";
        return LoadEntryFromUrlUnsafe(requestUrl);
    }

    private static VesperSkinRegistryEntry? TryLoadEntryByUuidUnsafe(VesperSkinRegistrySyncConfig config, string uuid)
    {
        if (string.IsNullOrWhiteSpace(config.LookupByUuidUrl))
        {
            return null;
        }

        var requestUrl = $"{config.LookupByUuidUrl}?uuid={Uri.EscapeDataString(uuid)}";
        return LoadEntryFromUrlUnsafe(requestUrl);
    }

    private static VesperSkinRegistryEntry? LoadEntryFromUrlUnsafe(string requestUrl)
    {
        using var response = Http.GetAsync(requestUrl).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var payload = JsonSerializer.Deserialize<VesperSkinRegistryLookupResponse>(json, RemoteJsonOptions);
        if (payload?.Entry is null)
        {
            return null;
        }

        return CreateNormalizedEntry(
            payload.Entry.Username,
            payload.Entry.OfflineUuid ?? payload.Entry.Uuid,
            payload.Entry.TextureValue,
            payload.Entry.TextureSignature,
            payload.Entry.TextureUrl,
            payload.Entry.Uuid,
            payload.Entry.UpdatedAtUtc);
    }

    private static void PublishEntryUnsafe(
        VesperSkinRegistrySyncConfig config,
        VesperSkinRegistryEntry entry,
        string accessToken,
        byte[]? skinImageBytes)
    {
        if (string.IsNullOrWhiteSpace(config.PublishUrl) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, config.PublishUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                publishedUuid = entry.PublishedUuid ?? entry.Uuid,
                offlineUuid = entry.Uuid,
                textureValue = entry.TextureValue,
                textureSignature = entry.TextureSignature,
                textureUrl = entry.TextureUrl,
                imageBase64 = skinImageBytes is { Length: > 0 } ? Convert.ToBase64String(skinImageBytes) : null,
                imageContentType = skinImageBytes is { Length: > 0 } ? "image/png" : null
            }),
            Encoding.UTF8,
            "application/json");

        using var response = Http.Send(request);
        response.EnsureSuccessStatusCode();
    }

    private static VesperSkinRegistrySyncConfig LoadSyncConfigUnsafe()
    {
        var envSharedPath = Environment.GetEnvironmentVariable("VESPER_SKIN_REGISTRY_PATH");
        var envPublishUrl = Environment.GetEnvironmentVariable("VESPER_SKIN_PUBLISH_URL");
        var envLookupByUsernameUrl = Environment.GetEnvironmentVariable("VESPER_SKIN_LOOKUP_BY_USERNAME_URL");
        var envLookupByUuidUrl = Environment.GetEnvironmentVariable("VESPER_SKIN_LOOKUP_BY_UUID_URL");

        VesperSkinRegistrySyncConfig? fileConfig = null;
        if (File.Exists(SyncConfigPath))
        {
            try
            {
                var json = File.ReadAllText(SyncConfigPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    fileConfig = JsonSerializer.Deserialize<VesperSkinRegistrySyncConfig>(json);
                }
            }
            catch
            {
                fileConfig = null;
            }
        }

        return new VesperSkinRegistrySyncConfig
        {
            SharedRegistryPath = NormalizeOptionalPath(
                !string.IsNullOrWhiteSpace(envSharedPath)
                    ? envSharedPath
                    : fileConfig?.SharedRegistryPath),
            PublishUrl = NormalizeOptionalUrl(
                !string.IsNullOrWhiteSpace(envPublishUrl)
                    ? envPublishUrl
                    : fileConfig?.PublishUrl),
            LookupByUsernameUrl = NormalizeOptionalUrl(
                !string.IsNullOrWhiteSpace(envLookupByUsernameUrl)
                    ? envLookupByUsernameUrl
                    : fileConfig?.LookupByUsernameUrl),
            LookupByUuidUrl = NormalizeOptionalUrl(
                !string.IsNullOrWhiteSpace(envLookupByUuidUrl)
                    ? envLookupByUuidUrl
                    : fileConfig?.LookupByUuidUrl)
        };
    }

    private static string? NormalizeOptionalPath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(rawPath.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeOptionalUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var trimmed = rawUrl.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : null;
    }

    private static VesperSkinRegistryState LoadStateUnsafe(string path)
    {
        if (!File.Exists(path))
        {
            return new VesperSkinRegistryState();
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VesperSkinRegistryState();
        }

        var parsedState = JsonSerializer.Deserialize<VesperSkinRegistryState>(json);
        return NormalizeStateUnsafe(parsedState);
    }

    private static VesperSkinRegistryState NormalizeStateUnsafe(VesperSkinRegistryState? parsedState)
    {
        if (parsedState?.Entries is null)
        {
            return new VesperSkinRegistryState();
        }

        var normalizedState = new VesperSkinRegistryState();
        foreach (var entry in parsedState.Entries.Values)
        {
            var normalizedEntry = CreateNormalizedEntry(
                entry.Username,
                entry.Uuid,
                entry.TextureValue,
                entry.TextureSignature,
                entry.TextureUrl,
                entry.PublishedUuid,
                entry.UpdatedAtUtc);
            if (normalizedEntry is not null)
            {
                normalizedState.Entries[normalizedEntry.Username] = normalizedEntry;
            }
        }

        return normalizedState;
    }

    private static void SaveStateUnsafe(string path, VesperSkinRegistryState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }

    private static string? NormalizeUuid(string? rawUuid)
    {
        if (string.IsNullOrWhiteSpace(rawUuid))
        {
            return null;
        }

        var compact = rawUuid.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length != 32)
        {
            return null;
        }

        return $"{compact[..8]}-{compact[8..12]}-{compact[12..16]}-{compact[16..20]}-{compact[20..32]}";
    }

    private static string BuildOfflineUuid(string username)
    {
        var input = Encoding.UTF8.GetBytes($"OfflinePlayer:{username}");
        var hash = MD5.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }
}

internal sealed record VesperSkinRegistryEntry(
    string Username,
    string Uuid,
    string? PublishedUuid,
    string TextureValue,
    string? TextureSignature,
    string? TextureUrl,
    DateTimeOffset UpdatedAtUtc);

internal sealed class VesperSkinRegistryState
{
    public Dictionary<string, VesperSkinRegistryEntry> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class VesperSkinRegistrySyncConfig
{
    public string? SharedRegistryPath { get; init; }
    public string? PublishUrl { get; init; }
    public string? LookupByUsernameUrl { get; init; }
    public string? LookupByUuidUrl { get; init; }
}

internal sealed class VesperSkinRegistryLookupResponse
{
    public VesperSkinRegistryLookupEntry? Entry { get; init; }
}

internal sealed class VesperSkinRegistryLookupEntry
{
    public string Username { get; init; } = string.Empty;
    public string Uuid { get; init; } = string.Empty;
    public string? OfflineUuid { get; init; }
    public string TextureValue { get; init; } = string.Empty;
    public string? TextureSignature { get; init; }
    public string? TextureUrl { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

