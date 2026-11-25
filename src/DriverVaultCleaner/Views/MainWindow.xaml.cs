using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DriverVaultCleaner.Services;
using DriverVaultCleaner.ViewModels;

namespace DriverVaultCleaner.Views;

/// <summary>
/// DriverVault Cleaner メインウィンドウ。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeService _themeService;
    private readonly AppSettingsService _settingsService;
    private readonly SingleInstanceManager? _singleInstance;
    private readonly bool _enableWindowPersistence;
    private readonly DispatcherTimer? _placementSaveTimer;
    private bool _initialized;

    public MainWindow(
        MainViewModel viewModel,
        ThemeService themeService,
        AppSettingsService settingsService,
        SingleInstanceManager? singleInstance = null,
        bool enableWindowPersistence = true)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _singleInstance = singleInstance;
        _enableWindowPersistence = enableWindowPersistence;
        DataContext = _viewModel;

        if (_enableWindowPersistence)
        {
            WindowPlacementService.ApplyPlacement(this, _settingsService.Load().WindowPlacement);

            _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _placementSaveTimer.Tick += (_, _) =>
            {
                _placementSaveTimer.Stop();
                SaveWindowPlacement();
            };

            LocationChanged += (_, _) => SchedulePlacementSave();
            SizeChanged += (_, _) => SchedulePlacementSave();
            StateChanged += (_, _) => SchedulePlacementSave();
        }

        _viewModel.ConfirmWarningHandler = (title, message) =>
            MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;

        _viewModel.ConfirmYesNoHandler = (title, message) =>
            MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;

        _viewModel.ShowNoticeHandler = (title, message) =>
            MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        _themeService.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => _themeService.ThemeChanged -= OnThemeChanged;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _themeService.ApplyWindowChrome(this);
        _singleInstance?.AttachWindowActivationHook(this);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _placementSaveTimer?.Stop();
        SaveWindowPlacement();
    }

    private void SchedulePlacementSave()
    {
        if (!_enableWindowPersistence || !_initialized || _placementSaveTimer is null)
        {
            return;
        }

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SaveWindowPlacement()
    {
        if (!_enableWindowPersistence)
        {
            return;
        }

        SavedWindowPlacement? captured = WindowPlacementService.CapturePlacement(this);
        if (captured is not null)
        {
            _settingsService.Update(s => s.WindowPlacement = captured);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _themeService.ApplyWindowChrome(this);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SaveWindowPlacement();
        await _viewModel.InitializeAsync();
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.ScrollToEnd();
    }
}
