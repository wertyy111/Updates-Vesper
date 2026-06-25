using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VesperLauncher.Core;
using VesperLauncher.Utils;

namespace VesperLauncher.Storage;

public sealed class CacheManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly object _syncRoot = new();
    private readonly Dictionary<string, MemoryEntry> _memoryEntries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lruKeys = new();
    private readonly Logger _logger;
    private readonly FileManager _fileManager;
    private readonly int _maxMemoryEntries;

    public CacheManager(Logger logger, FileManager fileManager, int maxMemoryEntries = 256)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
        _maxMemoryEntries = Math.Clamp(maxMemoryEntries, 16, 10_000);
        CacheDirectory = PathHelper.EnsureDirectory(Path.Combine(PathHelper.GetUserCacheDirectory(), "general"));
    }

    public string CacheDirectory { get; }

    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var safeKey = NormalizeKey(key);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(ttl);
        var entry = new CacheEnvelope<T>(expiresAtUtc, value);

        lock (_syncRoot)
        {
            SetMemoryEntry(safeKey, value, expiresAtUtc);
        }

        var path = GetCacheFilePath(safeKey);
        await _fileManager.WriteJsonAtomicAsync(path, entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> TryGetAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        var safeKey = NormalizeKey(key);
        if (TryGetMemoryEntry<T>(safeKey, out var cached))
        {
            return cached;
        }

        var path = GetCacheFilePath(safeKey);
        var envelope = await _fileManager.ReadJsonAsync<CacheEnvelope<T>>(path, cancellationToken)
            .ConfigureAwait(false);

        if (envelope is null || envelope.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _fileManager.TryDeleteFile(path);
            return default;
        }

        lock (_syncRoot)
        {
            SetMemoryEntry(safeKey, envelope.Value, envelope.ExpiresAtUtc);
        }

        return envelope.Value;
    }

    public void Remove(string key)
    {
        var safeKey = NormalizeKey(key);
        lock (_syncRoot)
        {
            _memoryEntries.Remove(safeKey);
            _lruKeys.Remove(safeKey);
        }

        _fileManager.TryDeleteFile(GetCacheFilePath(safeKey));
    }

    public void ClearMemory()
    {
        lock (_syncRoot)
        {
            _memoryEntries.Clear();
            _lruKeys.Clear();
        }
    }

    public void ClearExpired()
    {
        ClearExpiredMemoryEntries();

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(CacheDirectory, "*.json"))
            {
                if (TryReadExpiry(filePath) is { } expiresAtUtc &&
                    expiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _fileManager.TryDeleteFile(filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Не удалось очистить устаревший кеш: {ex.Message}");
        }
    }

    private bool TryGetMemoryEntry<T>(string safeKey, out T? value)
    {
        value = default;
        lock (_syncRoot)
        {
            if (!_memoryEntries.TryGetValue(safeKey, out var entry))
            {
                return false;
            }

            if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow || entry.Value is not T typedValue)
            {
                _memoryEntries.Remove(safeKey);
                _lruKeys.Remove(safeKey);
                return false;
            }

            TouchKey(safeKey);
            value = typedValue;
            return true;
        }
    }

    private void SetMemoryEntry<T>(string safeKey, T value, DateTimeOffset expiresAtUtc)
    {
        _memoryEntries[safeKey] = new MemoryEntry(value, expiresAtUtc);
        TouchKey(safeKey);
        TrimMemoryEntries();
    }

    private void TouchKey(string safeKey)
    {
        _lruKeys.Remove(safeKey);
        _lruKeys.AddFirst(safeKey);
    }

    private void TrimMemoryEntries()
    {
        while (_memoryEntries.Count > _maxMemoryEntries && _lruKeys.Last is not null)
        {
            var key = _lruKeys.Last.Value;
            _lruKeys.RemoveLast();
            _memoryEntries.Remove(key);
        }
    }

    private void ClearExpiredMemoryEntries()
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in new List<string>(_memoryEntries.Keys))
            {
                if (_memoryEntries[key].ExpiresAtUtc <= now)
                {
                    _memoryEntries.Remove(key);
                    _lruKeys.Remove(key);
                }
            }
        }
    }

    private string GetCacheFilePath(string safeKey)
    {
        return Path.Combine(CacheDirectory, $"{safeKey}.json");
    }

    private static string NormalizeKey(string key)
    {
        var hash = HashHelper.ComputeSha256(key);
        return PathHelper.SanitizePathSegment(hash, "cache-entry");
    }

    private static DateTimeOffset? TryReadExpiry(string filePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        if (document.RootElement.TryGetProperty("expiresAtUtc", out var expiresAtElement) &&
            expiresAtElement.TryGetDateTimeOffset(out var expiresAtUtc))
        {
            return expiresAtUtc;
        }

        return null;
    }

    private sealed record MemoryEntry(object? Value, DateTimeOffset ExpiresAtUtc);

    private sealed record CacheEnvelope<T>(DateTimeOffset ExpiresAtUtc, T Value);
}

