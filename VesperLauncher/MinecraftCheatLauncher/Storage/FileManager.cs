using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VesperLauncher.Core;
using VesperLauncher.Utils;

namespace VesperLauncher.Storage;

public sealed class FileManager
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Logger _logger;

    public FileManager(Logger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> ReadTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Не удалось прочитать файл: {filePath}");
            return null;
        }
    }

    public async Task<T?> ReadJsonAsync<T>(string filePath, CancellationToken cancellationToken = default)
    {
        var json = await ReadTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, DefaultJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Не удалось разобрать JSON: {filePath}");
            return default;
        }
    }

    public async Task<bool> WriteTextAtomicAsync(
        string filePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureParentDirectory(filePath);
            var tempPath = CreateTempPath(filePath);
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            ReplaceFile(tempPath, filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Не удалось записать файл: {filePath}");
            return false;
        }
    }

    public Task<bool> WriteJsonAtomicAsync<T>(
        string filePath,
        T value,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, DefaultJsonOptions);
        return WriteTextAtomicAsync(filePath, json, cancellationToken);
    }

    public bool TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Не удалось удалить файл {filePath}: {ex.Message}");
            return false;
        }
    }

    public bool TryDeleteDirectory(string directoryPath, bool recursive)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Не удалось удалить папку {directoryPath}: {ex.Message}");
            return false;
        }
    }

    public string EnsureDirectory(string directoryPath)
    {
        return PathHelper.EnsureDirectory(directoryPath);
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string CreateTempPath(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? Path.GetTempPath();
        var fileName = $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp";
        return Path.Combine(directory, fileName);
    }

    private static void ReplaceFile(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(tempPath, targetPath);
    }
}

