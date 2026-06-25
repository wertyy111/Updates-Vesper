using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VesperLauncher.Platform;
using Velopack;
using Velopack.Sources;

namespace VesperLauncher;

internal sealed class LauncherAutoUpdateService
{
    private const long MinimumPreferredFreeSpaceBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Action<Exception, string> _logError;
    private readonly Action<string> _logInfo;
    private int _fallbackLaunchRequested;

    public LauncherAutoUpdateService(Action<Exception, string> logError, Action<string> logInfo)
    {
        _logError = logError;
        _logInfo = logInfo;
    }

    public event Action? FallbackLaunchRequested;
    public event Action<LauncherUpdateUiState>? UiStateChanged;

    public async Task<bool> RunBeforeLaunchAsync()
    {
        if (!PlatformServiceFactory.CreateCurrent().Features.SupportsVelopackAutoUpdate)
        {
            ReportReady("Автообновления лаунчера отключены для текущей платформы.");
            return true;
        }

        var config = LoadConfig();
        if (config is null || !config.Enabled || !config.CheckOnStartup)
        {
            ReportReady("Обновления выключены. Запускаем лаунчер.");
            return true;
        }

        try
        {
            ReportStatus("Проверяем обновления...", "Подключаемся к источнику Velopack...");

            using var checkTimeout = new CancellationTokenSource(BuildStartupDecisionTimeout(config));
            var manager = CreateUpdateManager(config);

            if (!manager.IsInstalled)
            {
                _logInfo("Velopack update skipped: app is not installed by Velopack.");
                ReportReady("Лаунчер запущен не из установленной Velopack-сборки.");
                return true;
            }

            if (manager.UpdatePendingRestart is { } pendingUpdate)
            {
                return ApplyPreparedUpdate(manager, pendingUpdate, config);
            }

            var updateInfo = await manager.CheckForUpdatesAsync().WaitAsync(checkTimeout.Token).ConfigureAwait(false);
            if (updateInfo is null)
            {
                ReportReady("Обновлений нет. Запускаем текущую версию.");
                return true;
            }

            var targetVersion = updateInfo.TargetFullRelease.Version.ToString();
            _logInfo($"Velopack update found: {targetVersion}.");

            if (!config.InstallOnStartup)
            {
                ReportReady($"Доступна версия {targetVersion}. Автоустановка выключена.");
                return true;
            }

            if (!HasEnoughFreeSpace(AppContext.BaseDirectory, MinimumPreferredFreeSpaceBytes, out var freeBytes))
            {
                ReportReady("Недостаточно места для обновления. Запускаем текущую версию.");
                _logInfo($"Velopack update skipped: only {FormatBytes(freeBytes)} free.");
                return true;
            }

            ReportStatus(
                $"Найдена версия {targetVersion}.",
                "Скачиваем безопасный пакет обновления...",
                0,
                false,
                "0%");

            using var downloadTimeout = new CancellationTokenSource(BuildDownloadTimeout(config));
            try
            {
                await manager.DownloadUpdatesAsync(
                    updateInfo,
                    progress => ReportStatus(
                        "Скачиваем обновление...",
                        $"{Math.Clamp(progress, 0, 100)}%",
                        progress,
                        false,
                        $"{Math.Clamp(progress, 0, 100)}%"),
                    downloadTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (downloadTimeout.IsCancellationRequested)
            {
                _logError(ex, "Скачивание Velopack обновления превысило лимит времени");
                ContinueWithCurrentVersion("Скачивание обновления заняло слишком много времени. Запускаем текущую версию.");
                return true;
            }

            return ApplyPreparedUpdate(manager, updateInfo.TargetFullRelease, config);
        }
        catch (OperationCanceledException ex)
        {
            _logError(ex, "Проверка Velopack обновлений превысила лимит времени");
            ContinueWithCurrentVersion("Сервер обновлений не ответил. Запускаем лаунчер.");
            return true;
        }
        catch (Exception ex)
        {
            _logError(ex, "Ошибка Velopack обновления");
            ContinueWithCurrentVersion("Обновление временно недоступно. Запускаем лаунчер.");
            return true;
        }
    }

    private bool ApplyPreparedUpdate(UpdateManager manager, VelopackAsset update, LauncherUpdateConfig config)
    {
        if (!config.InstallOnStartup)
        {
            ReportReady($"Обновление {update.Version} готово, автоустановка выключена.");
            return true;
        }

        ReportStatus(
            "Применяем обновление...",
            "Лаунчер перезапустится после установки пакета.",
            100,
            false,
            "Установка...");
        _logInfo($"Applying Velopack update {update.Version}.");
        manager.ApplyUpdatesAndRestart(update);

        // Velopack starts a helper process, but old launcher builds could keep the
        // Photino/WPF host alive long enough to block replacing the current folder.
        // Exiting here makes the update atomic: install helper owns the restart.
        Environment.Exit(0);
        return false;
    }

    private void ContinueWithCurrentVersion(string message)
    {
        ReportReady(message);
        if (Interlocked.Exchange(ref _fallbackLaunchRequested, 1) == 0)
        {
            FallbackLaunchRequested?.Invoke();
        }
    }

    private static UpdateManager CreateUpdateManager(LauncherUpdateConfig config)
    {
        var options = new UpdateOptions
        {
            ExplicitChannel = string.IsNullOrWhiteSpace(config.Channel) ? null : config.Channel.Trim(),
            AllowVersionDowngrade = false,
            MaximumDeltasBeforeFallback = Math.Max(0, config.MaximumDeltasBeforeFallback)
        };

        var sourceUrl = config.SourceUrl?.Trim();
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new InvalidOperationException("Velopack SourceUrl is empty in update-config.json.");
        }

        if (config.SourceType.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var githubUri) ||
                !string.Equals(githubUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("GitHub update source must be an HTTPS repository URL.");
            }

            return new UpdateManager(new GithubSource(sourceUrl, accessToken: string.Empty, prerelease: config.IncludePrerelease), options);
        }

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Velopack update SourceUrl must use HTTPS or a local file path.");
        }

        return new UpdateManager(sourceUrl, options);
    }

    private void ReportReady(string detail)
    {
        ReportStatus("Запускаем лаунчер...", detail, 100, false, "Готово");
        _logInfo(detail);
    }

    private void ReportStatus(
        string message,
        string? detailMessage = null,
        int? progressPercent = null,
        bool isIndeterminate = true,
        string? progressText = null)
    {
        UiStateChanged?.Invoke(new LauncherUpdateUiState
        {
            Message = message,
            DetailMessage = detailMessage,
            ProgressPercent = progressPercent,
            IsIndeterminate = isIndeterminate,
            ProgressText = progressText
        });
    }

    private static TimeSpan BuildStartupDecisionTimeout(LauncherUpdateConfig config)
    {
        var seconds = config.StartupDecisionTimeoutSeconds <= 0
            ? 15
            : config.StartupDecisionTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan BuildDownloadTimeout(LauncherUpdateConfig config)
    {
        var seconds = config.DownloadTimeoutSeconds <= 0
            ? 600
            : config.DownloadTimeoutSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private static LauncherUpdateConfig? LoadConfig()
    {
        foreach (var path in GetConfigCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LauncherUpdateConfig>(json, JsonOptions);
        }

        return null;
    }

    private static string[] GetConfigCandidatePaths() =>
    [
        Path.Combine(AppContext.BaseDirectory, "update-config.json"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "update-config.json"))
    ];

    private static bool HasEnoughFreeSpace(string directoryPath, long minimumBytes, out long freeBytes)
    {
        freeBytes = 0;

        try
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var rootPath = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return true;
            }

            var driveInfo = new DriveInfo(rootPath);
            freeBytes = driveInfo.AvailableFreeSpace;
            return freeBytes >= minimumBytes;
        }
        catch
        {
            return true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
    }

    private sealed class LauncherUpdateConfig
    {
        public bool Enabled { get; init; } = true;
        public bool CheckOnStartup { get; init; } = true;
        public bool InstallOnStartup { get; init; } = true;
        public string SourceType { get; init; } = "GitHub";
        public string? SourceUrl { get; init; }
        public string Channel { get; init; } = string.Empty;
        public bool IncludePrerelease { get; init; }
        public int StartupDecisionTimeoutSeconds { get; init; } = 15;
        public int DownloadTimeoutSeconds { get; init; } = 600;
        public int MaximumDeltasBeforeFallback { get; init; } = 3;
    }
}

