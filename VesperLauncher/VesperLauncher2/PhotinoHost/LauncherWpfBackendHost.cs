using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VesperLauncher.PhotinoHost;

internal sealed class LauncherWpfBackendHost : ILauncherBackendHost
{
    private const double HiddenHostLeft = -32000;
    private const double HiddenHostTop = -32000;
    private const int MinimumStartupUpdateWindowVisibleMs = 1200;
    private readonly TaskCompletionSource<object?> _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _launcherReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "photino-host.log");
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _wpfWindowSuppressionTimer;
    private App? _application;
    private MainWindow? _mainWindow;
    private StartupUpdateWindow? _startupUpdateWindow;
    private DateTime _startupUpdateWindowShownAtUtc;
    private bool _closingStartupUpdateWindowInternally;
    private LauncherUpdateUiState _updateState = new()
    {
        Message = "Проверяем обновления...",
        DetailMessage = "Подключаемся к серверу обновлений...",
        IsIndeterminate = true,
        ProgressText = "Проверка..."
    };

    private volatile string _phase = "starting";
    private volatile string? _errorMessage;
    private volatile bool _disposed;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "VesperLauncher.WpfBackendHost"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _startedTcs.Task.GetAwaiter().GetResult();
    }

    public Task<bool> WaitForLauncherReadyAsync()
    {
        return _launcherReadyTcs.Task;
    }

    public Task<object> GetSnapshotAsync()
    {
        return InvokeAsync<object>(() => BuildSnapshot());
    }

    public Task ExecuteCommandAsync(string command, JsonElement payload)
    {
        return InvokeAsync(async () =>
        {
            if (string.Equals(command, "host.closeSplash", StringComparison.OrdinalIgnoreCase))
            {
                CloseStartupUpdateWindow();
                return;
            }

            if (_mainWindow is null)
            {
                if (string.Equals(command, "host.syncBounds", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                throw new InvalidOperationException("Лаунчер еще не готов к командам.");
            }

            if (string.Equals(command, "host.syncBounds", StringComparison.OrdinalIgnoreCase))
            {
                _mainWindow.SyncPhotinoHostBounds(payload);
                SuppressWpfWindows();
                return;
            }

            await _mainWindow.ExecutePhotinoCommandAsync(command, payload).ConfigureAwait(true);
            SuppressWpfWindows();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcherReadyTcs.TrySetResult(false);

        if (_dispatcher is null)
        {
            return;
        }

        try
        {
            _dispatcher.Invoke(() =>
            {
                try
                {
                    _wpfWindowSuppressionTimer?.Stop();
                    _wpfWindowSuppressionTimer = null;
                    _application?.Shutdown();
                }
                catch
                {
                    // Ignore shutdown races.
                }
            });
        }
        catch
        {
            // Ignore shutdown races.
        }
    }

    private void ThreadMain()
    {
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            _dispatcher = Dispatcher.CurrentDispatcher;

            App.IsPhotinoBackendHost = true;
            _application = new App();
            _application.InitializeComponent();
            _application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _application.DispatcherUnhandledException += (s, e) =>
            {
                if (IsNonFatalWindowClosingException(e.Exception))
                {
                    TryWriteLog($"[{DateTime.Now:O}] Suppressed shutdown window closing race: {e.Exception.Message}{Environment.NewLine}");
                    e.Handled = true;
                    return;
                }

                TryWriteLog($"[{DateTime.Now:O}] Unhandled WPF dispatcher exception:{Environment.NewLine}{e.Exception}{Environment.NewLine}");
                e.Handled = true;
            };
            StartWpfWindowSuppression();

            _startedTcs.TrySetResult(null);
            _ = RunStartupPipelineAsync();

            Dispatcher.Run();
            _launcherReadyTcs.TrySetResult(false);
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
            TryWriteLog($"[{DateTime.Now:O}] Fatal host error{Environment.NewLine}{ex}{Environment.NewLine}");
            _startedTcs.TrySetResult(null);
            _launcherReadyTcs.TrySetResult(false);
        }
    }

    private void StartWpfWindowSuppression()
    {
        _wpfWindowSuppressionTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _wpfWindowSuppressionTimer.Tick += (_, _) => SuppressWpfWindows();
        _wpfWindowSuppressionTimer.Start();
    }

    private void SuppressWpfWindows()
    {
        if (_application is null || _disposed)
        {
            return;
        }

        foreach (Window window in _application.Windows)
        {
            try
            {
                if (window is StartupUpdateWindow)
                {
                    continue;
                }

                if (window is MainWindow mainWindow)
                {
                    mainWindow.HidePhotinoHostWindow();
                    continue;
                }

                window.ShowInTaskbar = false;
                window.ShowActivated = false;
                window.IsHitTestVisible = false;
                window.Opacity = 0;

                if (window.WindowState != WindowState.Normal)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = HiddenHostLeft;
                window.Top = HiddenHostTop;
            }
            catch (Exception ex)
            {
                LogError(ex, "Не удалось скрыть WPF-окно backend-а.");
            }
        }
    }

    private async Task RunStartupPipelineAsync()
    {
        try
        {
            _phase = "checking-updates";
            ShowStartupUpdateWindow();

            var autoUpdateService = new LauncherAutoUpdateService(LogError, LogInfo);
            autoUpdateService.UiStateChanged += state =>
            {
                _updateState = new LauncherUpdateUiState
                {
                    Message = state.Message,
                    DetailMessage = state.DetailMessage,
                    ProgressPercent = state.ProgressPercent,
                    IsIndeterminate = state.IsIndeterminate,
                    ProgressText = state.ProgressText
                };
                UpdateStartupUpdateWindow(_updateState);
                _phase = "updating";
            };
            autoUpdateService.FallbackLaunchRequested += () =>
            {
                if (_dispatcher is null)
                {
                    return;
                }

                _ = _dispatcher.BeginInvoke(new Action(async () =>
                {
                    await WaitForStartupUpdateWindowMinimumDurationAsync().ConfigureAwait(true);
                    CreateMainWindowIfNeeded();
                }));
            };

            var shouldLaunchMainWindow = await autoUpdateService.RunBeforeLaunchAsync().ConfigureAwait(true);
            if (shouldLaunchMainWindow)
            {
                await WaitForStartupUpdateWindowMinimumDurationAsync().ConfigureAwait(true);
                CreateMainWindowIfNeeded();
                return;
            }

            _phase = "updating";
            _launcherReadyTcs.TrySetResult(false);
            _application?.Shutdown();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
            LogError(ex, "Ошибка старта Photino-host");
            await WaitForStartupUpdateWindowMinimumDurationAsync().ConfigureAwait(true);
            CreateMainWindowIfNeeded();
        }
    }

    private void ShowStartupUpdateWindow()
    {
        if (_application is null || _startupUpdateWindow is not null || _disposed)
        {
            return;
        }

        _startupUpdateWindow = new StartupUpdateWindow();
        _startupUpdateWindow.Closed += StartupUpdateWindow_OnClosed;
        _startupUpdateWindow.UpdateState(_updateState);
        _startupUpdateWindowShownAtUtc = DateTime.UtcNow;
        _startupUpdateWindow.Show();
        _startupUpdateWindow.Activate();
    }

    private async Task WaitForStartupUpdateWindowMinimumDurationAsync()
    {
        if (_startupUpdateWindow is null || _startupUpdateWindowShownAtUtc == default)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _startupUpdateWindowShownAtUtc;
        var remaining = TimeSpan.FromMilliseconds(MinimumStartupUpdateWindowVisibleMs) - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining).ConfigureAwait(true);
        }
    }

    private void UpdateStartupUpdateWindow(LauncherUpdateUiState state)
    {
        if (_dispatcher is null || _disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            _startupUpdateWindow?.UpdateState(state);
            return;
        }

        _dispatcher.BeginInvoke(new Action(() => _startupUpdateWindow?.UpdateState(state)), DispatcherPriority.Normal);
    }

    private void CloseStartupUpdateWindow()
    {
        if (_dispatcher is null || _disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(CloseStartupUpdateWindow), DispatcherPriority.Normal);
            return;
        }

        if (_startupUpdateWindow is null)
        {
            return;
        }

        var window = _startupUpdateWindow;
        _startupUpdateWindow = null;
        _closingStartupUpdateWindowInternally = true;
        try
        {
            window.Closed -= StartupUpdateWindow_OnClosed;
            window.Hide();
            window.Close();
        }
        finally
        {
            _closingStartupUpdateWindowInternally = false;
        }
    }

    private void StartupUpdateWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_closingStartupUpdateWindowInternally)
        {
            return;
        }

        if (ReferenceEquals(sender, _startupUpdateWindow))
        {
            _startupUpdateWindow = null;
        }

        if (_mainWindow is not null || _disposed)
        {
            return;
        }

        _launcherReadyTcs.TrySetResult(false);
        try
        {
            _dispatcher?.BeginInvoke(new Action(() =>
            {
                try
                {
                    _application?.Shutdown();
                }
                catch
                {
                    // Ignore shutdown races.
                }
            }), DispatcherPriority.Normal);
        }
        catch
        {
            // Ignore shutdown races.
        }
    }

    private void CreateMainWindowIfNeeded()
    {
        if (_dispatcher is null || _disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(CreateMainWindowIfNeeded), DispatcherPriority.Normal);
            return;
        }

        if (_mainWindow is not null)
        {
            MarkLauncherReady();
            return;
        }

        _phase = "initializing-launcher";
        _updateState = new LauncherUpdateUiState
        {
            Message = "Запуск лаунчера...",
            DetailMessage = "Загрузка и подготовка интерфейса...",
            IsIndeterminate = true,
            ProgressText = "Загрузка..."
        };
        UpdateStartupUpdateWindow(_updateState);

        var window = new MainWindow();
        window.PrepareForPhotinoHost();
        window.Loaded += (_, _) =>
        {
            window.HidePhotinoHostWindow();
            MarkLauncherReady();
        };
        window.Closed += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _dispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_disposed)
                        {
                            _application?.Shutdown();
                        }
                    }
                    catch
                    {
                        // Ignore shutdown races.
                    }
                }), DispatcherPriority.Normal);
            }
            catch
            {
                // Ignore shutdown races.
            }
        };

        _application!.MainWindow = window;
        _mainWindow = window;
        window.Show();
        window.HidePhotinoHostWindow();
        SuppressWpfWindows();
        if (window.IsLoaded)
        {
            MarkLauncherReady();
        }
    }

    private void MarkLauncherReady()
    {
        _phase = "ready";
        _launcherReadyTcs.TrySetResult(true);
    }

    private object BuildSnapshot()
    {
        return new
        {
            phase = _phase,
            errorMessage = _errorMessage,
            update = new
            {
                message = _updateState.Message,
                detailMessage = _updateState.DetailMessage,
                progressPercent = _updateState.ProgressPercent,
                isIndeterminate = _updateState.IsIndeterminate,
                progressText = _updateState.ProgressText
            },
            launcher = _mainWindow?.CreatePhotinoSnapshot()
        };
    }

    private Task InvokeAsync(Action action)
    {
        if (_dispatcher is null)
        {
            throw new InvalidOperationException("WPF-host dispatcher is not initialized.");
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (_dispatcher is null)
        {
            throw new InvalidOperationException("WPF-host dispatcher is not initialized.");
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private Task InvokeAsync(Func<Task> action)
    {
        if (_dispatcher is null)
        {
            throw new InvalidOperationException("WPF-host dispatcher is not initialized.");
        }

        return _dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private static bool IsNonFatalWindowClosingException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is InvalidOperationException &&
                (current.Message.Contains("VerifyNotClosing", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("Visibility", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("Window is closed", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private void LogInfo(string message)
    {
        TryWriteLog($"[{DateTime.Now:O}] INFO {message}{Environment.NewLine}");
    }

    private void LogError(Exception exception, string title)
    {
        TryWriteLog($"[{DateTime.Now:O}] ERROR {title}{Environment.NewLine}{exception}{Environment.NewLine}");
    }

    private void TryWriteLog(string entry)
    {
        try
        {
            File.AppendAllText(_logPath, entry);
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}

