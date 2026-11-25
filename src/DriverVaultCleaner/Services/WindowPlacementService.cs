using System.Windows;

namespace DriverVaultCleaner.Services;

/// <summary>
/// ウィンドウの表示位置・サイズ・最大化状態の復元およびマルチモニター対応の画面外クランプ処理。
/// </summary>
public static class WindowPlacementService
{
    public static SavedWindowPlacement? NormalizePlacement(
        SavedWindowPlacement? saved,
        double minWidth,
        double minHeight,
        Rect virtualScreen)
    {
        if (saved is null)
        {
            return null;
        }

        if (double.IsNaN(saved.Left) || double.IsInfinity(saved.Left) ||
            double.IsNaN(saved.Top) || double.IsInfinity(saved.Top) ||
            double.IsNaN(saved.Width) || double.IsInfinity(saved.Width) ||
            double.IsNaN(saved.Height) || double.IsInfinity(saved.Height))
        {
            return null;
        }

        if (saved.Width <= 0 || saved.Height <= 0 || virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            return null;
        }

        double effectiveMinW = Math.Min(Math.Max(minWidth, 200), virtualScreen.Width);
        double effectiveMinH = Math.Min(Math.Max(minHeight, 200), virtualScreen.Height);

        double width = Math.Clamp(saved.Width, effectiveMinW, virtualScreen.Width);
        double height = Math.Clamp(saved.Height, effectiveMinH, virtualScreen.Height);

        double left = saved.Left;
        double top = saved.Top;

        double requiredVisibleW = Math.Min(120, width);
        double requiredVisibleH = Math.Min(80, height);

        bool horizontallyVisible =
            left + width >= virtualScreen.Left + requiredVisibleW &&
            left <= virtualScreen.Right - requiredVisibleW;

        bool verticallyVisible =
            top + height >= virtualScreen.Top + requiredVisibleH &&
            top <= virtualScreen.Bottom - requiredVisibleH;

        if (!horizontallyVisible)
        {
            left = Math.Clamp(left, virtualScreen.Left, Math.Max(virtualScreen.Left, virtualScreen.Right - width));
        }

        if (!verticallyVisible)
        {
            top = Math.Clamp(top, virtualScreen.Top, Math.Max(virtualScreen.Top, virtualScreen.Bottom - height));
        }

        return new SavedWindowPlacement
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            IsMaximized = saved.IsMaximized
        };
    }

    public static void ApplyPlacement(Window window, SavedWindowPlacement? saved)
    {
        ArgumentNullException.ThrowIfNull(window);

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        SavedWindowPlacement? normalized = NormalizePlacement(
            saved,
            window.MinWidth > 0 ? window.MinWidth : 980,
            window.MinHeight > 0 ? window.MinHeight : 640,
            virtualScreen);

        if (normalized is null)
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = normalized.Left;
        window.Top = normalized.Top;
        window.Width = normalized.Width;
        window.Height = normalized.Height;

        if (normalized.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public static SavedWindowPlacement? CapturePlacement(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        if (bounds.IsEmpty ||
            double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top) ||
            bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.Left < -25000 || bounds.Top < -25000)
        {
            return null;
        }

        return new SavedWindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = window.WindowState == WindowState.Maximized
        };
    }
}
