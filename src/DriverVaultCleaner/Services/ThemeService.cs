using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DriverVaultCleaner.Services;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// システム設定・ユーザー選択に応じたライト／ダークテーマ切替と Windows 11 DWM タイトルバー適用を行うサービス。
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    private readonly AppSettingsService _settingsService;
    private bool _disposed;

    public ThemeService(AppSettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? new AppSettingsService();
        CurrentMode = _settingsService.Load().Theme;
        IsDarkActive = ResolveIsDark(CurrentMode);
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
    }

    public event EventHandler? ThemeChanged;

    public AppThemeMode CurrentMode { get; private set; }

    public bool IsDarkActive { get; private set; }

    public void InitializeApplicationTheme()
    {
        ApplyThemeCore(CurrentMode, persist: false);
    }

    public void SetTheme(AppThemeMode mode)
    {
        ApplyThemeCore(mode, persist: true);
    }

    public static bool IsSystemDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, writable: false);
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int intVal && intVal == 0;
        }
        catch
        {
            return false;
        }
    }

    public void ApplyWindowChrome(Window window)
    {
        if (window is null)
        {
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int useDark = IsDarkActive ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int captionColor = IsDarkActive ? ToColorRef(0x18, 0x1B, 0x22) : DWMWA_COLOR_DEFAULT;
                int textColor = IsDarkActive ? ToColorRef(0xF1, 0xF5, 0xF9) : DWMWA_COLOR_DEFAULT;
                int borderColor = IsDarkActive ? ToColorRef(0x2B, 0x32, 0x42) : DWMWA_COLOR_DEFAULT;

                _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
        }
        catch
        {
            // Older Windows builds may ignore DWMWA_* caption attributes safely
        }
    }

    private void ApplyThemeCore(AppThemeMode mode, bool persist)
    {
        CurrentMode = mode;
        IsDarkActive = ResolveIsDark(mode);

        if (Application.Current is Application app)
        {
            var paletteUri = new Uri(
                IsDarkActive
                    ? "pack://application:,,,/Themes/DarkPalette.xaml"
                    : "pack://application:,,,/Themes/LightPalette.xaml",
                UriKind.Absolute);

            var newPalette = new ResourceDictionary { Source = paletteUri };
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count > 0)
            {
                merged[0] = newPalette;
            }
            else
            {
                merged.Add(newPalette);
            }

            foreach (Window win in app.Windows)
            {
                ApplyWindowChrome(win);
            }
        }

        if (persist)
        {
            _settingsService.Update(s => s.Theme = mode);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool ResolveIsDark(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => IsSystemDarkMode()
    };

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (CurrentMode != AppThemeMode.System)
        {
            return;
        }

        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyThemeCore(AppThemeMode.System, persist: false));
        }
    }

    private static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
    }
}
