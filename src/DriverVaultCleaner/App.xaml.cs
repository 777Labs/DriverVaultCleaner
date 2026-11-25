using System.Text;
using System.Windows;
using System.Windows.Threading;
using DriverVaultCleaner.Services;
using DriverVaultCleaner.ViewModels;
using DriverVaultCleaner.Views;

namespace DriverVaultCleaner;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private ThemeService? _themeService;
    private SingleInstanceManager? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        base.OnStartup(e);

        bool isElevatedRestart = e.Args.Any(a =>
            string.Equals(a, "--elevated-restart", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstanceManager();
        if (!_singleInstance.TryAcquire(waitBriefly: isElevatedRestart))
        {
            _singleInstance.NotifyExistingInstanceAndActivate();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        var settingsService = new AppSettingsService();
        _themeService = new ThemeService(settingsService);
        _themeService.InitializeApplicationTheme();

        var commandRunner = new PnpCommandRunner();
        var inventoryService = new DriverInventoryService(commandRunner);
        var backupService = new DriverBackupService(commandRunner);
        var elevationService = new AdminElevationService();
        var cleanupService = new DriverCleanupService(commandRunner, backupService, elevationService.IsAdministrator);

        _mainViewModel = new MainViewModel(
            inventoryService,
            cleanupService,
            elevationService,
            _themeService,
            _singleInstance);

        var mainWindow = new MainWindow(
            _mainViewModel,
            _themeService,
            settingsService,
            _singleInstance,
            enableWindowPersistence: true);

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _themeService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _mainViewModel?.AppendLog($"[FATAL] 未処理エラー: {e.Exception.Message}");
        MessageBox.Show(
            $"予期しないエラーが発生しました:\n{e.Exception.Message}",
            "DriverVault Cleaner",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
