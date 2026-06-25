using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BlurredBackgroundEffect = BlurredBackground.WPF.BlurredBackground;

namespace VesperLauncher;

public partial class MainWindow : Window
{
    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEnterSizeMove:
                _isInSizeMove = true;
                break;
            case WmExitSizeMove:
                if (_isInSizeMove)
                {
                    _isInSizeMove = false;
                    ResumeResizePerformanceMode();
                    UpdateWindowClip();
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void SuspendResizePerformanceMode()
    {
        if (_resizePerformanceModeActive)
        {
            return;
        }

        _resizePerformanceModeActive = true;

        if (BackgroundPhoto is not null)
        {
            _backgroundPhotoScalingMode = RenderOptions.GetBitmapScalingMode(BackgroundPhoto);
            RenderOptions.SetBitmapScalingMode(BackgroundPhoto, BitmapScalingMode.LowQuality);
        }

        if (LeftControlSurfaceBorder is not null)
        {
            BlurredBackgroundEffect.SetEnableBlur(LeftControlSurfaceBorder, false);
        }

        ClearWindowClips();
    }

    private void ResumeResizePerformanceMode()
    {
        if (!_resizePerformanceModeActive)
        {
            return;
        }

        _resizePerformanceModeActive = false;

        if (BackgroundPhoto is not null)
        {
            RenderOptions.SetBitmapScalingMode(BackgroundPhoto, _backgroundPhotoScalingMode);
        }

        if (LeftControlSurfaceBorder is not null)
        {
            BlurredBackgroundEffect.SetEnableBlur(LeftControlSurfaceBorder, true);
        }
    }

    private void UpdateWindowClip()
    {
        UpdateWindowChromeLayout();
        UpdateWindowClipGeometry();
        UpdateWindowRegion();
    }

    private void UpdateWindowChromeLayout()
    {
        if (RootChromeBorder is null || ContentHostBorder is null || TitleBarBorder is null)
        {
            return;
        }

        var outerCornerRadius = GetOuterCornerRadius();
        var innerCornerRadius = GetInnerCornerRadius();
        var isMaximized = outerCornerRadius <= 0d;
        RootChromeBorder.CornerRadius = new CornerRadius(outerCornerRadius);
        ContentHostBorder.CornerRadius = new CornerRadius(innerCornerRadius);
        TitleBarBorder.CornerRadius = isMaximized
            ? new CornerRadius(0d)
            : new CornerRadius(innerCornerRadius, innerCornerRadius, 0d, 0d);
        ContentHostBorder.Margin = isMaximized ? MaximizedWindowContentMargin : RoundedWindowContentMargin;
    }

    private void UpdateWindowClipGeometry()
    {
        if (RootChromeBorder is null || ContentHostBorder is null)
        {
            return;
        }

        var outerCornerRadius = GetOuterCornerRadius();
        var innerCornerRadius = GetInnerCornerRadius();

        var outerWidth = Math.Max(0d, RootChromeBorder.ActualWidth);
        var outerHeight = Math.Max(0d, RootChromeBorder.ActualHeight);
        var outerRect = new Rect(0, 0, outerWidth, outerHeight);
        RootChromeBorder.Clip = new RectangleGeometry(
            outerRect,
            outerCornerRadius,
            outerCornerRadius);

        var innerWidth = Math.Max(0d, ContentHostBorder.ActualWidth);
        var innerHeight = Math.Max(0d, ContentHostBorder.ActualHeight);
        var innerRect = new Rect(0, 0, innerWidth, innerHeight);
        ContentHostBorder.Clip = new RectangleGeometry(
            innerRect,
            innerCornerRadius,
            innerCornerRadius);
    }

    private void ClearWindowClips()
    {
        if (RootChromeBorder is not null)
        {
            RootChromeBorder.Clip = null;
        }

        if (ContentHostBorder is not null)
        {
            ContentHostBorder.Clip = null;
        }
    }

    private void UpdateWindowRegion()
    {
        if (_isInSizeMove)
        {
            return;
        }

        ApplyWindowRegion(GetOuterCornerRadius());
    }

    private double GetOuterCornerRadius() => WindowState == WindowState.Maximized ? 0d : DefaultOuterCornerRadius;

    private double GetInnerCornerRadius() => WindowState == WindowState.Maximized ? 0d : DefaultInnerCornerRadius;

    private void ClearWindowRegion()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle != IntPtr.Zero)
        {
            SetWindowRgn(windowHandle, IntPtr.Zero, true);
        }
    }

    private void ApplyWindowRegion(double cornerRadius)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (WindowState == WindowState.Maximized || ActualWidth <= 0 || ActualHeight <= 0 || cornerRadius <= 0)
        {
            SetWindowRgn(windowHandle, IntPtr.Zero, true);
            return;
        }

        var dpiScaleX = 1d;
        var dpiScaleY = 1d;
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget is not null)
        {
            var transform = source.CompositionTarget.TransformToDevice;
            dpiScaleX = transform.M11;
            dpiScaleY = transform.M22;
        }

        var widthInPixels = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpiScaleX));
        var heightInPixels = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpiScaleY));
        var radiusX = Math.Max(1, (int)Math.Ceiling(cornerRadius * dpiScaleX));
        var radiusY = Math.Max(1, (int)Math.Ceiling(cornerRadius * dpiScaleY));

        var regionHandle = CreateRoundRectRgn(
            nLeftRect: 0,
            nTopRect: 0,
            nRightRect: widthInPixels + 1,
            nBottomRect: heightInPixels + 1,
            nWidthEllipse: radiusX * 2,
            nHeightEllipse: radiusY * 2);

        if (regionHandle == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(windowHandle, regionHandle, true) == 0)
        {
            DeleteObject(regionHandle);
        }
    }

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
}

