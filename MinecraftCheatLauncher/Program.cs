using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Photino.NET;
using VesperLauncher.Core;
using VesperLauncher.PhotinoHost;
using VesperLauncher.Platform;
using Velopack;

namespace VesperLauncher;

internal static class Program
{
    private const int LauncherWidth = 1100;
    private const int LauncherHeight = 720;
    private const int LauncherMinWidth = 960;
    private const int LauncherMinHeight = 620;
    private const int LauncherCornerRadius = 14;
    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly Logger HostLogger = new("photino-shell");
    private static readonly IPlatformService PlatformService = PlatformServiceFactory.CreateCurrent();

    [STAThread]
    private static void Main(string[] args)
    {
        if (PlatformService.Features.SupportsVelopackAutoUpdate)
        {
            VelopackApp.Build().Run();
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                TryWriteLog($"[{DateTime.Now:O}] FATAL{Environment.NewLine}{exception}{Environment.NewLine}");
            }
        };
        HostLogger.Info(AppDiagnostics.Capture().ToLogText());

        using var backendHost = LauncherBackendHostFactory.CreateCurrent();
        using var shutdown = new CancellationTokenSource();
        var bridgeLock = new SemaphoreSlim(1, 1);
        var snapshotTransport = new SnapshotTransportState();

        // Start backend updates check in background
        backendHost.Start();

        // Start local static server immediately to allow background preloading
        using var staticServer = LocalStaticFileServer.Start();

        // Create the window offscreen initially to prevent flickering/raw UI loading view
        var window = CreateWindow(staticServer.BaseUrl, startOffscreen: PlatformService.Features.SupportsNativeWindowShaping);
        staticServer.BridgeMessageHandler = rawMessage =>
            HandleHttpBridgeMessageAsync(window, rawMessage, backendHost, bridgeLock, snapshotTransport, shutdown.Token);
        window.RegisterWebMessageReceivedHandler((sender, message) =>
        {
            if (sender is not PhotinoWindow photinoWindow)
            {
                return;
            }

            snapshotTransport.MarkClientReady();
            _ = HandleIncomingMessageAsync(photinoWindow, message, backendHost, bridgeLock, snapshotTransport, shutdown.Token);
        });

        var snapshotLoop = Task.CompletedTask;

        try
        {
            // Start preloading the HTML/JS offscreen
            window.Load(staticServer.BaseUrl);

            // Run a background task to wait for updates and client readiness asynchronously,
            // so we do not block the main STA thread which is needed for the message pump.
            _ = Task.Run(async () =>
            {
                try
                {
                    // 1. Wait for updates check (WPF splash) to finish
                    var launcherReady = await backendHost.WaitForLauncherReadyAsync().ConfigureAwait(false);
                    if (!launcherReady)
                    {
                        window.Invoke(() => window.Close());
                        return;
                    }

                    // 2. Keep Photino offscreen while React preloads heavy UI work.
                    // The frontend sends client.uiReady only after liquid glass has initialized.
                    await WaitForLauncherUiReadyAsync(snapshotTransport, shutdown.Token).ConfigureAwait(false);

                    // 3. Apply size/shape while still offscreen. Do not activate or move onscreen yet.
                    window.Invoke(() =>
                    {
                        if (!PlatformService.Features.SupportsNativeWindowShaping)
                        {
                            window.Center();
                            return;
                        }
                        TryApplyLauncherWindowBounds(window, updatePosition: false);
                    });

                    // 4. The update window owns the screen until the launcher is fully preloaded.
                    await backendHost.ExecuteCommandAsync("host.closeSplash", default).ConfigureAwait(false);

                    // 5. Only after closing the splash do we place and activate the launcher window.
                    await Task.Delay(80, shutdown.Token).ConfigureAwait(false);
                    window.Invoke(() =>
                    {
                        if (!PlatformService.Features.SupportsNativeWindowShaping)
                        {
                            window.Center();
                            return;
                        }
                        TryApplyLauncherWindowBounds(window, updatePosition: true);
                    });

                    // 6. Start normal execution loops
                    snapshotLoop = RunSnapshotLoopAsync(window, backendHost, bridgeLock, snapshotTransport, shutdown.Token);
                    ScheduleLauncherWindowBounds(window, shutdown.Token);
                }
                catch (Exception ex)
                {
                    TryWriteLog($"[{DateTime.Now:O}] PRELOAD ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
                }
            });

            window.WaitForClose();
        }
        finally
        {
            shutdown.Cancel();
            TryWriteLog($"[{DateTime.Now:O}] Photino shell closed.{Environment.NewLine}");
            backendHost.Dispose();
            staticServer.Dispose();
            try
            {
                snapshotLoop.GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore shutdown races.
            }
        }
    }

    private static async Task WaitForLauncherUiReadyAsync(
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25);
        while (!snapshotTransport.IsUiReady && !cancellationToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= timeoutAt)
            {
                TryWriteLog($"[{DateTime.Now:O}] WARNING: UI preload timed out. Showing launcher after fallback delay.{Environment.NewLine}");
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static PhotinoWindow CreateWindow(string startUrl, bool startOffscreen)
    {
        var iconPath = ResolveIconPath();
        var window = new PhotinoWindow
        {
            Title = "Vesper Launcher",
            Chromeless = true,
            Resizable = true,
            ContextMenuEnabled = false,
            DevToolsEnabled = false,
            Centered = !startOffscreen,
            UseOsDefaultLocation = false,
            UseOsDefaultSize = false,
            Width = LauncherWidth,
            Height = LauncherHeight,
            MinWidth = LauncherMinWidth,
            MinHeight = LauncherMinHeight,
            Transparent = !OperatingSystem.IsWindows()
        };

        if (startOffscreen)
        {
            window.Left = -32000;
            window.Top = -32000;
        }

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            window.IconFile = iconPath;
        }

        return window;
    }

    private static void ScheduleLauncherWindowBounds(PhotinoWindow window, CancellationToken cancellationToken)
    {
        if (!PlatformService.Features.SupportsNativeWindowShaping)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 60 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                if (TryApplyLauncherWindowBounds(window, updatePosition: true))
                {
                    break;
                }
            }
        }, cancellationToken);
    }

    private static bool TryApplyLauncherWindowBounds(PhotinoWindow window, bool updatePosition)
    {
        if (!PlatformService.Features.SupportsNativeWindowShaping)
        {
            return false;
        }

        var handle = FindPhotinoWindowHandle();
        if (handle == IntPtr.Zero)
        {
            try
            {
                handle = window.WindowHandle;
            }
            catch (ApplicationException)
            {
                return false;
            }
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var left = 0;
        var top = 0;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorScale = GetPrimaryMonitorScale(handle);
        var targetWidth = ScaleWindowSize(LauncherWidth, monitorScale);
        var targetHeight = ScaleWindowSize(LauncherHeight, monitorScale);
        if (updatePosition)
        {
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var monitorInfo = MonitorInfo.Create();
            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return false;
            }

            var workArea = monitorInfo.WorkArea;
            left = workArea.Left + (workArea.Width - targetWidth) / 2;
            top = workArea.Top + (workArea.Height - targetHeight) / 2;
        }

        uint flags = 0;
        if (updatePosition)
        {
            try
            {
                SetForegroundWindow(handle);
            }
            catch { }
        }
        else
        {
            flags |= SwpNoZOrder | SwpNoActivate;
            flags |= SwpNoMove;
        }

        var boundsApplied = SetWindowPos(handle, IntPtr.Zero, left, top, targetWidth, targetHeight, flags);
        if (boundsApplied)
        {
            ApplyRoundedWindowShape(handle, targetWidth, targetHeight, monitorScale);
        }

        return boundsApplied;
    }

    private static void ApplyRoundedWindowShape(IntPtr handle, int width, int height, double scale)
    {
        if (!PlatformService.Features.SupportsNativeWindowShaping)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                _ = SetWindowRgn(handle, IntPtr.Zero, true);
                var cornerPreference = DwmWindowCornerPreferenceRound;
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaWindowCornerPreference,
                    ref cornerPreference,
                    Marshal.SizeOf<int>());
                return;
            }
        }
        catch
        {
            // DWM rounded-corner preference is only available on newer Windows.
        }

        var radius = Math.Max(1, ScaleWindowSize(LauncherCornerRadius, scale));
        var regionHandle = CreateRoundRectRgn(
            nLeftRect: 0,
            nTopRect: 0,
            nRightRect: width,
            nBottomRect: height,
            nWidthEllipse: radius * 2,
            nHeightEllipse: radius * 2);

        if (regionHandle == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(handle, regionHandle, true) == 0)
        {
            DeleteObject(regionHandle);
        }
    }

    private static int ScaleWindowSize(int size, double scale)
    {
        return Math.Max(1, (int)Math.Round(size * scale, MidpointRounding.AwayFromZero));
    }

    private static double GetPrimaryMonitorScale(IntPtr windowHandle)
    {
        if (!PlatformService.Features.SupportsNativeWindowShaping)
        {
            return 1d;
        }

        try
        {
            var windowScale = GetWindowDpiScale(windowHandle);
            if (windowScale >= 1d)
            {
                return windowScale;
            }
        }
        catch
        {
            // Fall through
        }

        try
        {
            var primaryMonitor = MonitorFromPoint(new NativePoint(1, 1), MonitorDefaultToNearest);
            if (primaryMonitor != IntPtr.Zero &&
                GetScaleFactorForMonitor(primaryMonitor, out var scalePercent) == 0 &&
                scalePercent >= 100)
            {
                return scalePercent / 100d;
            }
        }
        catch
        {
            // Fall through to the DPI fallback below.
        }

        var registryScale = TryReadAppliedDpiScale();
        if (registryScale is not null)
        {
            return registryScale.Value;
        }

        return 1d;
    }

    private static double? TryReadAppliedDpiScale()
    {
        if (!OperatingSystem.IsWindows() || !PlatformService.Features.SupportsNativeWindowShaping)
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            if (key?.GetValue("AppliedDPI") is int appliedDpi && appliedDpi >= 96)
            {
                return appliedDpi / 96d;
            }
        }
        catch
        {
            // Registry DPI is best-effort; API fallbacks below keep startup safe.
        }

        return null;
    }

    private static double GetWindowDpiScale(IntPtr windowHandle)
    {
        try
        {
            var dpi = GetDpiForWindow(windowHandle);
            if (dpi > 0)
            {
                return dpi / 96d;
            }
        }
        catch
        {
            // Keep the launcher usable on older Windows builds.
        }

        return 1d;
    }

    private static IntPtr FindPhotinoWindowHandle()
    {
        if (!PlatformService.Features.SupportsNativeWindowShaping)
        {
            return IntPtr.Zero;
        }

        var currentProcessId = (uint)Environment.ProcessId;
        var result = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (processId != currentProcessId || !IsWindowVisible(handle))
            {
                return true;
            }

            var className = new StringBuilder(128);
            GetClassName(handle, className, className.Capacity);
            if (!string.Equals(className.ToString(), "Photino", StringComparison.Ordinal))
            {
                return true;
            }

            result = handle;
            return false;
        }, IntPtr.Zero);

        return result;
    }

    private static void StartWindowDrag(PhotinoWindow window)
    {
        if (!PlatformService.Features.SupportsNativeWindowShaping)
        {
            return;
        }

        window.Invoke(() =>
        {
            var handle = window.WindowHandle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            ReleaseCapture();
            SendMessage(handle, WmNclButtonDown, HtCaption, 0);
        });
    }

    private static async Task HandleIncomingMessageAsync(
        PhotinoWindow window,
        string rawMessage,
        ILauncherBackendHost backendHost,
        SemaphoreSlim bridgeLock,
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            var command = root.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
                ? commandElement.GetString()
                : root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : default;

            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            await bridgeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var shouldPublishSnapshot = true;
                switch (command.Trim().ToLowerInvariant())
                {
                    case "host.requestsnapshot":
                    case "bridge.requestsnapshot":
                        break;

                    case "client.uiready":
                        snapshotTransport.MarkUiReady();
                        break;

                    case "host.minimize":
                        window.Invoke(() => window.Minimized = true);
                        break;

                    case "host.togglemaximize":
                        window.Invoke(() => window.Maximized = !window.Maximized);
                        break;

                    case "host.startdrag":
                        shouldPublishSnapshot = false;
                        window.Invoke(() => StartWindowDrag(window));
                        break;

                    case "host.close":
                        shouldPublishSnapshot = false;
                        window.Invoke(() => window.Close());
                        break;

                    default:
                        await backendHost.ExecuteCommandAsync(command, payload).ConfigureAwait(false);
                        break;
                }

                if (shouldPublishSnapshot)
                {
                    var forcePublish = command.Equals("host.requestSnapshot", StringComparison.OrdinalIgnoreCase) ||
                                       command.Equals("bridge.requestSnapshot", StringComparison.OrdinalIgnoreCase);
                    await PublishSnapshotCoreAsync(window, backendHost, snapshotTransport, cancellationToken, forcePublish).ConfigureAwait(false);
                }
            }
            finally
            {
                bridgeLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore shutdown.
        }
        catch (Exception ex)
        {
            TryWriteLog($"[{DateTime.Now:O}] MESSAGE ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
            await SendTransportMessageAsync(window, new
            {
                type = "error",
                message = ex.Message
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> HandleHttpBridgeMessageAsync(
        PhotinoWindow window,
        string rawMessage,
        ILauncherBackendHost backendHost,
        SemaphoreSlim bridgeLock,
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            var command = root.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String
                ? commandElement.GetString()
                : root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : default;

            snapshotTransport.MarkClientReady();
            if (!string.IsNullOrWhiteSpace(command))
            {
                await bridgeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    switch (command.Trim().ToLowerInvariant())
                    {
                        case "host.requestsnapshot":
                        case "bridge.requestsnapshot":
                            break;

                        case "client.uiready":
                            snapshotTransport.MarkUiReady();
                            break;

                        case "host.minimize":
                            window.Invoke(() => window.Minimized = true);
                            break;

                        case "host.togglemaximize":
                            window.Invoke(() => window.Maximized = !window.Maximized);
                            break;

                        case "host.startdrag":
                            StartWindowDrag(window);
                            break;

                        case "host.close":
                            window.Invoke(() => window.Close());
                            break;

                        default:
                            await backendHost.ExecuteCommandAsync(command, payload).ConfigureAwait(false);
                            break;
                    }
                }
                finally
                {
                    bridgeLock.Release();
                }
            }

            return await BuildSnapshotEnvelopeJsonAsync(backendHost).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryWriteLog($"[{DateTime.Now:O}] HTTP BRIDGE ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
            return JsonSerializer.Serialize(new
            {
                type = "error",
                message = ex.Message
            }, JsonOptions);
        }
    }

    private static async Task RunSnapshotLoopAsync(
        PhotinoWindow window,
        ILauncherBackendHost backendHost,
        SemaphoreSlim bridgeLock,
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken)
    {
        await WaitForClientReadyAsync(snapshotTransport, cancellationToken).ConfigureAwait(false);
        await TrySynchronizeBoundsAsync(window, backendHost, bridgeLock, cancellationToken).ConfigureAwait(false);
        await PublishSnapshotAsync(window, backendHost, bridgeLock, snapshotTransport, cancellationToken, requireLock: false, forcePublish: true).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await TrySynchronizeBoundsAsync(window, backendHost, bridgeLock, cancellationToken).ConfigureAwait(false);
            await PublishSnapshotAsync(window, backendHost, bridgeLock, snapshotTransport, cancellationToken, requireLock: false, forcePublish: false).ConfigureAwait(false);
        }
    }

    private static async Task PublishSnapshotAsync(
        PhotinoWindow window,
        ILauncherBackendHost backendHost,
        SemaphoreSlim bridgeLock,
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken,
        bool requireLock,
        bool forcePublish)
    {
        if (!snapshotTransport.IsClientReady)
        {
            return;
        }

        var lockTaken = false;
        try
        {
            if (requireLock)
            {
                await bridgeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockTaken = true;
            }
            else
            {
                lockTaken = await bridgeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                if (!lockTaken)
                {
                    return;
                }
            }

            await PublishSnapshotCoreAsync(window, backendHost, snapshotTransport, cancellationToken, forcePublish).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore shutdown.
        }
        finally
        {
            if (lockTaken)
            {
                bridgeLock.Release();
            }
        }
    }

    private static async Task PublishSnapshotCoreAsync(
        PhotinoWindow window,
        ILauncherBackendHost backendHost,
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken,
        bool forcePublish)
    {
        if (!snapshotTransport.IsClientReady)
        {
            return;
        }

        var json = await BuildSnapshotEnvelopeJsonAsync(backendHost).ConfigureAwait(false);

        if (!forcePublish && string.Equals(snapshotTransport.LastSnapshotJson, json, StringComparison.Ordinal))
        {
            return;
        }

        if (await SendTransportJsonAsync(window, json, cancellationToken).ConfigureAwait(false))
        {
            snapshotTransport.LastSnapshotJson = json;
        }
    }

    private static async Task<string> BuildSnapshotEnvelopeJsonAsync(ILauncherBackendHost backendHost)
    {
        var snapshot = await backendHost.GetSnapshotAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            type = "snapshot",
            data = snapshot
        }, JsonOptions);
    }

    private static async Task WaitForClientReadyAsync(
        SnapshotTransportState snapshotTransport,
        CancellationToken cancellationToken)
    {
        while (!snapshotTransport.IsClientReady)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task TrySynchronizeBoundsAsync(
        PhotinoWindow window,
        ILauncherBackendHost backendHost,
        SemaphoreSlim bridgeLock,
        CancellationToken cancellationToken)
    {
        var lockTaken = false;
        try
        {
            lockTaken = await bridgeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!lockTaken)
            {
                return;
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                left = window.Left,
                top = window.Top,
                width = window.Width,
                height = window.Height,
                maximized = window.Maximized
            }, JsonOptions);

            using var payloadDocument = JsonDocument.Parse(payloadJson);
            await backendHost.ExecuteCommandAsync("host.syncBounds", payloadDocument.RootElement.Clone()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore shutdown.
        }
        catch (Exception ex)
        {
            TryWriteLog($"[{DateTime.Now:O}] BOUNDS ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        finally
        {
            if (lockTaken)
            {
                bridgeLock.Release();
            }
        }
    }

    private static async Task SendTransportMessageAsync(PhotinoWindow window, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await SendTransportJsonAsync(window, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SendTransportJsonAsync(PhotinoWindow window, string json, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Invoke(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        return;
                    }

                    window.SendWebMessage(json);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    TryWriteLog($"[{DateTime.Now:O}] SEND ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
                    completion.TrySetResult(false);
                }
            });

            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryWriteLog($"[{DateTime.Now:O}] SEND ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
            return false;
        }
    }

    private static string? ResolveIconPath()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "vesper-app.ico")),
            Path.Combine(AppContext.BaseDirectory, "Assets", "vesper-app.ico")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void TryWriteLog(string entry)
    {
        HostLogger.WriteRaw(entry);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(IntPtr monitor, out uint scale);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int nLeftRect,
        int nTopRect,
        int nRightRect,
        int nBottomRect,
        int nWidthEllipse,
        int nHeightEllipse);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr extraData);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;

        public static MonitorInfo Create()
        {
            return new MonitorInfo
            {
                Size = Marshal.SizeOf<MonitorInfo>()
            };
        }
    }

    private sealed class SnapshotTransportState
    {
        private int _clientReady;
        private int _uiReady;

        public string? LastSnapshotJson { get; set; }

        public bool IsClientReady => Volatile.Read(ref _clientReady) == 1;

        public void MarkClientReady()
        {
            Volatile.Write(ref _clientReady, 1);
        }

        public bool IsUiReady => Volatile.Read(ref _uiReady) == 1;

        public void MarkUiReady()
        {
            Volatile.Write(ref _uiReady, 1);
        }
    }
}






