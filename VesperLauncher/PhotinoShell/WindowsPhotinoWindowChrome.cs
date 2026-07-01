using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Photino.NET;

namespace VesperLauncher.PhotinoShell;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPhotinoWindowChrome : IPhotinoWindowChrome
{
    private const int LauncherCornerRadius = 20;
    private const int SplashWidth = 440;
    private const int SplashHeight = 240;
    private const int SplashCornerRadius = 24;
    private const int WmNclButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const int HtLeft = 0x000A;
    private const int HtRight = 0x000B;
    private const int HtTop = 0x000C;
    private const int HtTopLeft = 0x000D;
    private const int HtTopRight = 0x000E;
    private const int HtBottom = 0x000F;
    private const int HtBottomLeft = 0x0010;
    private const int HtBottomRight = 0x0011;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public bool SupportsNativeWindowShaping => true;

    public void ScheduleLauncherWindowBounds(PhotinoWindow window, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            var appliedInitialBounds = false;
            var lastWidth = 0;
            var lastHeight = 0;
            for (var attempt = 0; attempt < 60 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                if (TryApplyLauncherWindowBounds(window, updatePosition: true))
                {
                    appliedInitialBounds = true;
                    break;
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(appliedInitialBounds ? 350 : 100, cancellationToken).ConfigureAwait(false);
                var handle = ResolveWindowHandle(window);
                if (handle == IntPtr.Zero ||
                    !TryGetWindowSize(handle, out var width, out var height) ||
                    (width == lastWidth && height == lastHeight))
                {
                    continue;
                }

                lastWidth = width;
                lastHeight = height;
                ApplyRoundedWindowShapeForCurrentBounds(handle, width, height, LauncherCornerRadius);
            }
        }, cancellationToken);
    }

    public void ScheduleRestoreBoundsGuard(PhotinoWindow window, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            var wasMinimized = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                var isMinimized = window.Minimized;
                if (isMinimized)
                {
                    wasMinimized = true;
                    continue;
                }

                if (!wasMinimized)
                {
                    continue;
                }

                wasMinimized = false;
                for (var attempt = 0; attempt < 8 && !cancellationToken.IsCancellationRequested; attempt++)
                {
                    window.Invoke(() =>
                    {
                        TryApplyLauncherWindowBounds(window, updatePosition: false);
                        ApplyWindowBackdrop(window, 0);
                    });
                    await Task.Delay(35, cancellationToken).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }

    public bool TryApplyLauncherWindowBounds(PhotinoWindow window, bool updatePosition)
    {
        var handle = ResolveWindowHandle(window);
        var windowBounds = LauncherWindowState.Load();

        window.MinWidth = LauncherWindowState.MinWidth;
        window.MinHeight = LauncherWindowState.MinHeight;
        window.SetSize(windowBounds.Width, windowBounds.Height);
        if (updatePosition)
        {
            window.Center();
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (updatePosition)
        {
            try
            {
                SetForegroundWindow(handle);
            }
            catch
            {
            }
        }

        ApplyRoundedWindowShapeForCurrentBounds(handle, windowBounds.Width, windowBounds.Height, LauncherCornerRadius);

        return true;
    }

    public void ApplySplashWindowBounds(PhotinoWindow window)
    {
        window.MinWidth = SplashWidth;
        window.MinHeight = SplashHeight;
        window.SetSize(SplashWidth, SplashHeight);
        window.Center();

        var handle = ResolveWindowHandle(window);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ApplyRoundedWindowShapeForCurrentBounds(handle, SplashWidth, SplashHeight, SplashCornerRadius);
    }

    public void SetWindowVisibility(PhotinoWindow window, bool visible)
    {
        var handle = window.WindowHandle;
        if (handle != IntPtr.Zero)
        {
            if (visible)
            {
                var windowBounds = LauncherWindowState.Load();
                ApplyRoundedWindowShapeForCurrentBounds(handle, windowBounds.Width, windowBounds.Height, LauncherCornerRadius);
                ShowWindow(handle, 1); // SW_SHOWNORMAL = 1
                try { SetForegroundWindow(handle); } catch { }
            }
            else
            {
                ShowWindow(handle, 0); // SW_HIDE = 0
            }
        }
    }

    public void MinimizeWindow(PhotinoWindow window)
    {
        var handle = window.WindowHandle;
        if (handle == IntPtr.Zero)
        {
            window.Minimized = true;
            return;
        }

        TryApplyLauncherWindowBounds(window, updatePosition: false);
        ApplyWindowBackdrop(window, 0);
        ApplyRoundedWindowShapeForCurrentBounds(handle, window.Width, window.Height, LauncherCornerRadius);
        ShowWindow(handle, 6); // SW_MINIMIZE = 6
    }

    public void ApplyWindowBackdrop(PhotinoWindow window, int backdropType)
    {
        var handle = window.WindowHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var value = backdropType;
                _ = DwmSetWindowAttribute(
                    handle,
                    38, // DWMWA_SYSTEMBACKDROP_TYPE
                    ref value,
                    Marshal.SizeOf<int>());
            }
            else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                var accent = new AccentPolicy();
                
                if (backdropType == 3 || backdropType == 2)
                {
                    accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                }
                else
                {
                    accent.AccentState = AccentState.ACCENT_DISABLED;
                }
                
                var accentStructSize = Marshal.SizeOf(accent);
                var accentPtr = Marshal.AllocHGlobal(accentStructSize);
                try
                {
                    Marshal.StructureToPtr(accent, accentPtr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                        SizeOfData = accentStructSize,
                        Data = accentPtr
                    };
                    SetWindowCompositionAttribute(handle, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(accentPtr);
                }
            }
        }
        catch
        {
        }
    }

    public void StartWindowDrag(PhotinoWindow window)
    {
        var handle = window.WindowHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        bool useRegion = !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (useRegion)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
        }

        ReleaseCapture();
        SendMessage(handle, WmNclButtonDown, HtCaption, 0);

        if (useRegion)
        {
            ApplyRoundedWindowShapeForCurrentBounds(handle, window.Width, window.Height, LauncherCornerRadius);
        }
    }

    public void StartWindowResize(PhotinoWindow window, string direction)
    {
        var hitTest = direction.Trim().ToLowerInvariant() switch
        {
            "left" => HtLeft,
            "right" => HtRight,
            "top" => HtTop,
            "top-left" => HtTopLeft,
            "top-right" => HtTopRight,
            "bottom" => HtBottom,
            "bottom-left" => HtBottomLeft,
            "bottom-right" => HtBottomRight,
            _ => 0
        };
        if (hitTest == 0)
        {
            return;
        }

        var handle = window.WindowHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        bool useRegion = !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        if (useRegion)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
        }

        ReleaseCapture();
        SendMessage(handle, WmNclButtonDown, hitTest, 0);

        if (useRegion)
        {
            ApplyRoundedWindowShapeForCurrentBounds(handle, window.Width, window.Height, LauncherCornerRadius);
        }
    }

    private static void ApplyRoundedWindowShape(IntPtr handle, int width, int height, double scale, int cornerRadius)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
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

        ApplyRoundedWindowRegion(handle, width, height, scale, cornerRadius);
    }

    private static void ApplyRoundedWindowShapeForCurrentBounds(
        IntPtr handle,
        int fallbackWidth,
        int fallbackHeight,
        int cornerRadius)
    {
        var width = fallbackWidth;
        var height = fallbackHeight;
        if (TryGetWindowSize(handle, out var actualWidth, out var actualHeight))
        {
            width = actualWidth;
            height = actualHeight;
        }

        ApplyRoundedWindowShape(handle, width, height, GetPrimaryMonitorScale(handle), cornerRadius);
    }

    private static void ApplyRoundedWindowRegion(IntPtr handle, int width, int height, double scale, int cornerRadius)
    {
        var radius = Math.Max(1, ScaleWindowSize(cornerRadius, scale));
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
        }

        var registryScale = TryReadAppliedDpiScale();
        return registryScale ?? 1d;
    }

    private static IntPtr ResolveWindowHandle(PhotinoWindow window)
    {
        return window.WindowHandle != IntPtr.Zero
            ? window.WindowHandle
            : FindPhotinoWindowHandle();
    }

    private static double? TryReadAppliedDpiScale()
    {
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
            // Registry DPI is best-effort; API fallbacks above keep startup safe.
        }

        return null;
    }

    private static bool TryGetWindowSize(IntPtr handle, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            if (GetWindowRect(handle, out var rect))
            {
                width = Math.Max(1, rect.Width);
                height = Math.Max(1, rect.Height);
                return true;
            }
        }
        catch
        {
        }

        return false;
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

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

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
    
    [DllImport("user32.dll")]
    internal static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    internal enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    internal enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }
}
