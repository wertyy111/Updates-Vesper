using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using VesperLauncher.Core;
using VesperLauncher.Platform;

namespace VesperLauncher;

public partial class App : Application
{
    public static bool IsPhotinoBackendHost { get; set; }

    private readonly Logger _appLogger = new("app");
    private LauncherAutoUpdateService? _autoUpdateService;
    private bool _mainWindowShown;
    private StartupUpdateWindow? _startupUpdateWindow;
    private bool _closingStartupUpdateWindowInternally;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += AppDomain_OnUnhandledException;
        base.OnStartup(e);
        _appLogger.Info(AppDiagnostics.Capture().ToLogText());
        TrySetShutdownMode(ShutdownMode.OnExplicitShutdown);

        if (IsPhotinoBackendHost)
        {
            return;
        }

        var platformService = PlatformServiceFactory.CreateCurrent();
        if (platformService.Features.SupportsWindowsInstaller)
        {
            WindowsUninstallRegistration.EnsureRegistered(TryWriteInfoToLog, TryWriteErrorToLog);
        }

        ShowStartupUpdateWindow();
        await Dispatcher.Yield(DispatcherPriority.Render);

        _autoUpdateService = new LauncherAutoUpdateService(TryWriteErrorToLog, TryWriteInfoToLog);
        _autoUpdateService.FallbackLaunchRequested += AutoUpdateService_OnFallbackLaunchRequested;
        _autoUpdateService.UiStateChanged += AutoUpdateService_OnUiStateChanged;

        var shouldLaunchMainWindow = await _autoUpdateService.RunBeforeLaunchAsync();
        if (!shouldLaunchMainWindow)
        {
            return;
        }

        ShowMainWindowIfNeeded();
    }

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsNonFatalBitmapDiskSpaceException(e.Exception))
        {
            TryWriteErrorToLog(e.Exception, "Нефатальная ошибка загрузки изображения");
            e.Handled = true;
            return;
        }

        if (e.Exception is InvalidOperationException &&
            (e.Exception.Message.Contains("VerifyNotClosing", StringComparison.OrdinalIgnoreCase) ||
             e.Exception.Message.Contains("Visibility", StringComparison.OrdinalIgnoreCase) ||
             e.Exception.Message.Contains("Window is closed", StringComparison.OrdinalIgnoreCase)))
        {
            TryWriteErrorToLog(e.Exception, "Нефатальная ошибка при закрытии окна во время завершения работы");
            e.Handled = true;
            return;
        }

        TryWriteErrorToLog(e.Exception, "Необработанная ошибка UI");
        MessageBox.Show(
            e.Exception.Message,
            "Ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void AppDomain_OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            TryWriteErrorToLog(ex, "Критическая ошибка");
        }
    }

    private void AutoUpdateService_OnFallbackLaunchRequested()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            TryWriteInfoToLog("Игнорируем fallback-запуск: приложение уже завершает работу.");
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowMainWindowIfNeeded));
    }

    private void AutoUpdateService_OnUiStateChanged(LauncherUpdateUiState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action<LauncherUpdateUiState>(AutoUpdateService_OnUiStateChanged), state);
            return;
        }

        _startupUpdateWindow?.UpdateState(state);
    }

    private void ShowMainWindowIfNeeded()
    {
        if (_mainWindowShown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_startupUpdateWindow is not null)
        {
            _startupUpdateWindow.Hide();
            CloseStartupUpdateWindow();
        }

        var window = new MainWindow();
        window.Closed += MainWindow_OnClosed;
        MainWindow = window;
        _mainWindowShown = true;
        window.Show();
    }

    private void ShowStartupUpdateWindow()
    {
        if (_startupUpdateWindow is not null)
        {
            return;
        }

        _startupUpdateWindow = new StartupUpdateWindow();
        _startupUpdateWindow.Closed += StartupUpdateWindow_OnClosed;
        _startupUpdateWindow.UpdateState(new LauncherUpdateUiState
        {
            Message = "Проверяем обновления...",
            DetailMessage = "Подключаемся к серверу обновлений...",
            IsIndeterminate = true,
            ProgressText = "Проверка..."
        });
        _startupUpdateWindow.Show();
    }

    private void CloseStartupUpdateWindow()
    {
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

        if (_mainWindowShown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        TryWriteInfoToLog("Стартовое окно обновления закрыто пользователем.");

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Shutdown();
                }
                catch (InvalidOperationException)
                {
                    // The app is already closing.
                }
            }), DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            // The app is already closing.
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        CloseStartupUpdateWindow();

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Shutdown();
                }
                catch (InvalidOperationException)
                {
                    // The app is already closing.
                }
            }), DispatcherPriority.Normal);
        }
        catch (InvalidOperationException)
        {
            // The app is already closing.
        }
    }

    private void TrySetShutdownMode(ShutdownMode shutdownMode)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            ShutdownMode = shutdownMode;
        }
        catch (InvalidOperationException)
        {
            // The app is already closing (for example during silent auto-update).
        }
    }

    private static bool IsNonFatalBitmapDiskSpaceException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            var isDiskSpaceException =
                current is IOException &&
                current.Message.Contains("Недостаточно места на диске", StringComparison.OrdinalIgnoreCase);
            var isBitmapDownloadException =
                (current.StackTrace?.Contains("BitmapDownload", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (current.TargetSite?.DeclaringType?.FullName?.Contains("BitmapDownload", StringComparison.OrdinalIgnoreCase) ?? false);

            if (isDiskSpaceException || (current is IOException && isBitmapDownloadException))
            {
                return true;
            }
        }

        return false;
    }

    private void TryWriteErrorToLog(Exception ex, string title)
    {
        _appLogger.Error(ex, title);
    }

    private void TryWriteInfoToLog(string message)
    {
        _appLogger.Info(message);
    }
}

