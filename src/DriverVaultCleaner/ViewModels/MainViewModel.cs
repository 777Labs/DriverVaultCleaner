using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using DriverVaultCleaner.Models;
using DriverVaultCleaner.Services;
using Microsoft.Win32;

namespace DriverVaultCleaner.ViewModels;

/// <summary>
/// メインウィンドウ全体の状態・フィルタ・テーマ・安全モード・クリーンアップ実行・ログを統括する ViewModel。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public const string AllClassesOption = "すべて (All)";

    private readonly DriverInventoryService _inventoryService;
    private readonly DriverCleanupService _cleanupService;
    private readonly AdminElevationService _elevationService;
    private readonly ThemeService _themeService;
    private readonly SingleInstanceManager? _singleInstance;
    private readonly StringBuilder _logBuffer = new();

    private DriverItemViewModel? _selectedDriver;
    private string _searchText = string.Empty;
    private string _selectedClass = AllClassesOption;
    private bool _onlyUnused;
    private bool _onlyRemnants;
    private bool _excludeMicrosoft;
    private bool _onlyChecked;
    private bool _isBusy;
    private bool _isLiveMode;
    private bool _enableBackup = true;
    private string _backupRootDir = ExecutionOptions.DefaultBackupDirectory;
    private bool _forceUninstall;
    private string _statusMessage = "準備完了";
    private string _lastParserFormat = "—";
    private int _visibleCount;
    private int _checkedCount;
    private int _inUseCount;
    private int _remnantCount;

    public MainViewModel(
        DriverInventoryService inventoryService,
        DriverCleanupService cleanupService,
        AdminElevationService elevationService,
        ThemeService themeService,
        SingleInstanceManager? singleInstance = null)
    {
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _elevationService = elevationService ?? throw new ArgumentNullException(nameof(elevationService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _singleInstance = singleInstance;
        _themeService.ThemeChanged += (_, _) => RaiseThemePropertiesChanged();

        IsAdministrator = _elevationService.IsAdministrator();
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriverPredicate;
        DriversView.SortDescriptions.Add(
            new SortDescription(nameof(DriverItemViewModel.OemNumber), ListSortDirection.Ascending));

        AvailableClasses.Add(AllClassesOption);

        RefreshCommand = new AsyncRelayCommand(RefreshDriversAsync, () => !IsBusy);
        ExecuteCleanupCommand = new AsyncRelayCommand(ExecuteCleanupAsync, () => !IsBusy && CheckedCount > 0);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdministrator, () => !IsAdministrator && !IsBusy);
        SelectOldRemnantsCommand = new RelayCommand(SelectOldRemnants, () => !IsBusy && VisibleCount > 0);
        SelectVisibleUnusedCommand = new RelayCommand(SelectVisibleUnused, () => !IsBusy && VisibleCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !IsBusy && CheckedCount > 0);
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrEmpty(SearchText));
        BrowseBackupFolderCommand = new RelayCommand(BrowseBackupFolder, () => !IsBusy);
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);

        SetThemeSystemCommand = new RelayCommand(() => SwitchTheme(AppThemeMode.System));
        SetThemeLightCommand = new RelayCommand(() => SwitchTheme(AppThemeMode.Light));
        SetThemeDarkCommand = new RelayCommand(() => SwitchTheme(AppThemeMode.Dark));

        string themeDesc = _themeService.IsDarkActive ? "ダーク" : "ライト";
        AppendLog($"DriverVault Cleaner 起動（権限: {PrivilegeBadgeText} / テーマ: {_themeService.CurrentMode} [{themeDesc}] / モード: ドライラン安全モード）");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Func<string, string, bool> ConfirmWarningHandler { get; set; } = (_, _) => false;
    public Func<string, string, bool> ConfirmYesNoHandler { get; set; } = (_, _) => false;
    public Action<string, string>? ShowNoticeHandler { get; set; }

    public ObservableCollection<DriverItemViewModel> Drivers { get; } = [];
    public ICollectionView DriversView { get; }
    public ObservableCollection<string> AvailableClasses { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExecuteCleanupCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand SelectOldRemnantsCommand { get; }
    public RelayCommand SelectVisibleUnusedCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand ResetFiltersCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand BrowseBackupFolderCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand SetThemeSystemCommand { get; }
    public RelayCommand SetThemeLightCommand { get; }
    public RelayCommand SetThemeDarkCommand { get; }

    public AppThemeMode CurrentThemeMode => _themeService.CurrentMode;
    public bool IsThemeSystem => _themeService.CurrentMode == AppThemeMode.System;
    public bool IsThemeLight => _themeService.CurrentMode == AppThemeMode.Light;
    public bool IsThemeDark => _themeService.CurrentMode == AppThemeMode.Dark;
    public bool IsDarkThemeActive => _themeService.IsDarkActive;

    public bool IsAdministrator { get; }

    public string PrivilegeBadgeText =>
        IsAdministrator ? "管理者権限で実行中" : "標準ユーザー（閲覧・Dry-Run専用）";

    public bool CanElevate => !IsAdministrator;

    public DriverItemViewModel? SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (ReferenceEquals(_selectedDriver, value))
            {
                return;
            }

            _selectedDriver = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedDriver));
            OnPropertyChanged(nameof(HasNoSelectedDriver));
        }
    }

    public bool HasSelectedDriver => SelectedDriver is not null;
    public bool HasNoSelectedDriver => SelectedDriver is null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public string SelectedClass
    {
        get => _selectedClass;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? AllClassesOption : value;
            if (_selectedClass == normalized)
            {
                return;
            }

            _selectedClass = normalized;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public bool OnlyUnused
    {
        get => _onlyUnused;
        set
        {
            if (_onlyUnused == value)
            {
                return;
            }

            _onlyUnused = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public bool OnlyRemnants
    {
        get => _onlyRemnants;
        set
        {
            if (_onlyRemnants == value)
            {
                return;
            }

            _onlyRemnants = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public bool ExcludeMicrosoft
    {
        get => _excludeMicrosoft;
        set
        {
            if (_excludeMicrosoft == value)
            {
                return;
            }

            _excludeMicrosoft = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public bool OnlyChecked
    {
        get => _onlyChecked;
        set
        {
            if (_onlyChecked == value)
            {
                return;
            }

            _onlyChecked = value;
            OnPropertyChanged();
            ApplyFilters();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            ExecuteCleanupCommand.RaiseCanExecuteChanged();
            RestartAsAdminCommand.RaiseCanExecuteChanged();
            SelectOldRemnantsCommand.RaiseCanExecuteChanged();
            SelectVisibleUnusedCommand.RaiseCanExecuteChanged();
            ClearSelectionCommand.RaiseCanExecuteChanged();
            BrowseBackupFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLiveMode
    {
        get => _isLiveMode;
        set
        {
            if (_isLiveMode == value)
            {
                return;
            }

            if (value)
            {
                bool confirmed = ConfirmWarningHandler(
                    "本番実行モード (Live Mode) への切り替え確認",
                    "ドライラン（シミュレーション）を解除し、本番実行モードを有効にしようとしています。\n\n" +
                    "本番モードでは、実行ボタン押下後（最終確認ダイアログ承認後）に\n" +
                    "選択したドライバーの実際のバックアップおよび削除コマンドが実行されます。\n\n" +
                    "本当に「本番実行モード」へ切り替えますか？");

                if (!confirmed)
                {
                    AppendLog("本番実行モードへの切り替えをキャンセルしました（ドライラン維持）。");
                    StatusMessage = "ドライランモードを維持しました。";
                    SnapBackProperty(nameof(IsLiveMode));
                    return;
                }

                AppendLog("[WARN] 本番実行モード (Live) が有効化されました。操作前に必ず対象とバックアップ先を確認してください。");
                StatusMessage = "⚠️ 本番実行モードが有効です（実行時は最終確認ダイアログが表示されます）。";
            }
            else
            {
                AppendLog("ドライラン（シミュレーション）モードへ戻しました。");
                StatusMessage = "🛡️ ドライランモード（安全保護）に戻しました。";
            }

            _isLiveMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SafetyBannerTitle));
            OnPropertyChanged(nameof(SafetyBannerDescription));
            OnPropertyChanged(nameof(PrimaryActionButtonText));
        }
    }

    public string SafetyBannerTitle =>
        IsLiveMode
            ? "⚠️ 本番実行モード有効（実削除可能）"
            : "🛡️ ドライランモード有効（安全保護）";

    public string SafetyBannerDescription =>
        IsLiveMode
            ? "選択したドライバーが実際にバックアップ・削除されます。実行前に削除対象・使用中警告を十分に確認してください。"
            : "実環境への変更・削除コマンドは一切実行されません（シミュレーションログ出力のみ）。";

    public string PrimaryActionButtonText =>
        IsLiveMode
            ? (EnableBackup
                ? $"バックアップして削除を実行 ({CheckedCount}件)"
                : $"削除を実行 [バックアップなし] ({CheckedCount}件)")
            : $"シミュレーションを実行 ({CheckedCount}件)";

    public bool EnableBackup
    {
        get => _enableBackup;
        set
        {
            if (_enableBackup == value)
            {
                return;
            }

            if (!value)
            {
                bool confirmed = ConfirmWarningHandler(
                    "削除前バックアップ無効化の確認",
                    "「削除前にバックアップを作成」を無効にすると、削除したドライバーを復元できなくなる恐れがあります。\n\n" +
                    "本当に削除前バックアップを無効にしますか？");

                if (!confirmed)
                {
                    SnapBackProperty(nameof(EnableBackup));
                    return;
                }

                AppendLog("[WARN] 削除前バックアップが無効化されました。");
            }
            else
            {
                AppendLog("削除前バックアップを有効化しました（推奨）。");
            }

            _enableBackup = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PrimaryActionButtonText));
        }
    }

    public string BackupRootDir
    {
        get => _backupRootDir;
        set
        {
            if (_backupRootDir == value)
            {
                return;
            }

            _backupRootDir = value;
            OnPropertyChanged();
        }
    }

    public bool ForceUninstall
    {
        get => _forceUninstall;
        set
        {
            if (_forceUninstall == value)
            {
                return;
            }

            if (value)
            {
                bool confirmed = ConfirmWarningHandler(
                    "強制削除 (/uninstall /force) 有効化の確認",
                    "「強制削除」を有効にすると、現在デバイスで使用中のドライバーも強制的にアンインストール・削除されます。\n" +
                    "システムが不安定になったり、接続機器が動作しなくなる可能性があります。\n\n" +
                    "本当に強制削除オプションを有効にしますか？");

                if (!confirmed)
                {
                    SnapBackProperty(nameof(ForceUninstall));
                    return;
                }

                AppendLog("[WARN] 強制削除オプション (/uninstall /force) が有効化されました。");
            }
            else
            {
                AppendLog("強制削除オプションを無効化しました（標準削除）。");
            }

            _forceUninstall = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string LastParserFormat
    {
        get => _lastParserFormat;
        private set
        {
            if (_lastParserFormat == value)
            {
                return;
            }

            _lastParserFormat = value;
            OnPropertyChanged();
        }
    }

    public int TotalCount => Drivers.Count;

    public int VisibleCount
    {
        get => _visibleCount;
        private set
        {
            if (_visibleCount == value)
            {
                return;
            }

            _visibleCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryStatusText));
        }
    }

    public int CheckedCount
    {
        get => _checkedCount;
        private set
        {
            if (_checkedCount == value)
            {
                return;
            }

            _checkedCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryStatusText));
            OnPropertyChanged(nameof(PrimaryActionButtonText));
            ClearSelectionCommand.RaiseCanExecuteChanged();
            ExecuteCleanupCommand.RaiseCanExecuteChanged();
        }
    }

    public int InUseCount
    {
        get => _inUseCount;
        private set
        {
            if (_inUseCount == value)
            {
                return;
            }

            _inUseCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummaryStatusText));
        }
    }

    public int RemnantCount
    {
        get => _remnantCount;
        private set
        {
            if (_remnantCount == value)
            {
                return;
            }

            _remnantCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectRemnantsButtonText));
            OnPropertyChanged(nameof(SummaryStatusText));
        }
    }

    public string SelectRemnantsButtonText => $"🧹 旧版残骸を一括選択 ({RemnantCount}件)";

    public string SummaryStatusText =>
        $"全 {TotalCount} 件 / 表示中 {VisibleCount} 件 / 選択中 {CheckedCount} 件 / 使用中 {InUseCount} 件 / 旧版残骸 {RemnantCount} 件";

    public string CombinedLogText => _logBuffer.ToString();

    public Task InitializeAsync() => RefreshDriversAsync();

    public async Task RefreshDriversAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "pnputil.exe からドライバー一覧を読み込んでいます...";
        AppendLog("ドライバー一覧のスキャンを開始します...");

        try
        {
            InventoryLoadResult result = await _inventoryService.LoadDriversAsync();

            string? previouslySelectedPublished = SelectedDriver?.PublishedName;
            string previousClass = SelectedClass;

            Drivers.Clear();
            foreach (DriverPackageInfo info in result.Drivers)
            {
                Drivers.Add(new DriverItemViewModel(info, OnItemSelectionChanged));
            }

            RebuildClassList(result.Drivers, previousClass);
            LastParserFormat = result.ParserFormat;

            ApplyFilters();

            if (!string.IsNullOrEmpty(previouslySelectedPublished))
            {
                SelectedDriver = DriversView.Cast<DriverItemViewModel>()
                    .FirstOrDefault(d => string.Equals(d.PublishedName, previouslySelectedPublished, StringComparison.OrdinalIgnoreCase));
            }

            SelectedDriver ??= DriversView.Cast<DriverItemViewModel>().FirstOrDefault();

            StatusMessage = $"読み込み完了: {result.Drivers.Count} 件 ({result.ParserFormat}, {result.ElapsedMilliseconds} ms / 旧版残骸 {RemnantCount} 件)";
            AppendLog($"取得完了: {result.Drivers.Count} 件（うち旧版残骸候補: {RemnantCount} 件）/ 形式: {result.ParserFormat} / 所要時間: {result.ElapsedMilliseconds} ms");
            AppendLog($"実行コマンド: {result.ExecutedCommand}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"一覧取得エラー: {ex.Message}";
            AppendLog($"[ERROR] 一覧取得に失敗しました: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExecuteCleanupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        List<DriverPackageInfo> targets = Drivers
            .Where(d => d.IsSelected)
            .Select(d => d.Model)
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "対象ドライバーが選択されていません。";
            return;
        }

        if (IsLiveMode)
        {
            if (!IsAdministrator)
            {
                AppendLog("[BLOCK] 標準ユーザー権限のため本番実行をブロックしました。");
                bool elevateNow = ConfirmYesNoHandler(
                    "管理者権限が必要です",
                    "実ドライバーのバックアップおよび削除を実行するには管理者権限が必要です。\n\n" +
                    "今すぐ「管理者として再起動」しますか？");

                if (elevateNow)
                {
                    RestartAsAdministrator();
                }

                return;
            }

            if (!ConfirmFinalLiveExecution(targets))
            {
                AppendLog("本番実行の最終確認でキャンセルされました。");
                StatusMessage = "本番実行をキャンセルしました。";
                return;
            }
        }

        var options = new ExecutionOptions
        {
            IsLiveMode = IsLiveMode,
            EnableBackup = EnableBackup,
            BackupRootDir = string.IsNullOrWhiteSpace(BackupRootDir)
                ? ExecutionOptions.DefaultBackupDirectory
                : BackupRootDir.Trim(),
            ForceUninstall = ForceUninstall
        };

        IsBusy = true;
        StatusMessage = options.IsLiveMode
            ? $"本番処理を実行中 ({targets.Count} 件)..."
            : $"ドライラン・シミュレーションを実行中 ({targets.Count} 件)...";

        CleanupBatchReport? report = null;
        try
        {
            report = await _cleanupService.ExecuteAsync(targets, options, AppendLog);

            if (report.IsDryRun)
            {
                StatusMessage = $"シミュレーション完了: {targets.Count} 件の実行予定コマンドをログへ出力しました（実環境への変更なし）。";
            }
            else
            {
                string rebootText = report.AnyRebootRequired ? "（※PCの再起動が必要です）" : string.Empty;
                StatusMessage =
                    $"実行完了: 成功 {report.SuccessCount} 件 / スキップ {report.SkippedCount} 件 / 失敗 {report.FailedCount} 件 {rebootText}".Trim();

                ShowNoticeHandler?.Invoke(
                    "処理結果サマリー",
                    $"対象: {targets.Count} 件\n" +
                    $"削除成功: {report.SuccessCount} 件\n" +
                    $"バックアップ失敗等によるスキップ: {report.SkippedCount} 件\n" +
                    $"削除失敗: {report.FailedCount} 件\n" +
                    (string.IsNullOrEmpty(report.BackupSessionDirectory)
                        ? string.Empty
                        : $"\nバックアップ保存先:\n{report.BackupSessionDirectory}\n") +
                    (report.AnyRebootRequired ? "\n※ 一部のドライバーで Windows の再起動が要求されました。" : string.Empty));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"実行エラー: {ex.Message}";
            AppendLog($"[ERROR] 実行中断: {ex.Message}");
            ShowNoticeHandler?.Invoke("実行エラー", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (report is not null && !report.IsDryRun && report.SuccessCount > 0)
        {
            await RefreshDriversAsync();
        }
    }

    private bool ConfirmFinalLiveExecution(IReadOnlyList<DriverPackageInfo> targets)
    {
        var previewLines = targets
            .Take(10)
            .Select(d =>
            {
                string warn = d.IsInUse ? $" [★使用中: {d.ConnectedDeviceCount}件]" : string.Empty;
                return $"  • {d.PublishedName} ({d.OriginalName}) - {d.ProviderName}{warn}";
            });

        var sb = new StringBuilder();
        sb.AppendLine($"以下の {targets.Count} 件のドライバーに対して本番処理を実行します。");
        sb.AppendLine();
        foreach (string line in previewLines)
        {
            sb.AppendLine(line);
        }

        if (targets.Count > 10)
        {
            sb.AppendLine($"  ... ほか {targets.Count - 10} 件");
        }

        int inUseTargetCount = targets.Count(d => d.IsInUse);
        if (inUseTargetCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ 警告: 選択対象に「現在使用中」のドライバーが {inUseTargetCount} 件含まれています！");
        }

        sb.AppendLine();
        sb.AppendLine(EnableBackup
            ? $"・削除前バックアップ: 有効 ({BackupRootDir})"
            : "・削除前バックアップ: 無効（復元不可）");
        sb.AppendLine(ForceUninstall
            ? "・削除方式: 強制削除 (/delete-driver ... /uninstall /force)"
            : "・削除方式: 通常削除 (/delete-driver)");
        sb.AppendLine();
        sb.Append("本当に実行してよろしいですか？");

        return ConfirmWarningHandler("最終実行確認 (Live Mode)", sb.ToString());
    }

    private void SnapBackProperty(string propertyName)
    {
        OnPropertyChanged(propertyName);
        Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(propertyName));
    }

    private void SwitchTheme(AppThemeMode mode)
    {
        _themeService.SetTheme(mode);
        string activeLabel = _themeService.IsDarkActive ? "ダーク" : "ライト";
        StatusMessage = $"テーマを「{mode} ({activeLabel})」へ切り替えました。";
        AppendLog($"テーマ変更: {mode} (適用: {activeLabel})");
    }

    private void RaiseThemePropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentThemeMode));
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsDarkThemeActive));
    }

    private void RebuildClassList(IReadOnlyList<DriverPackageInfo> drivers, string previousClass)
    {
        AvailableClasses.Clear();
        AvailableClasses.Add(AllClassesOption);

        foreach (string className in drivers
                     .Select(d => d.ClassName)
                     .Where(c => !string.IsNullOrWhiteSpace(c))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            AvailableClasses.Add(className);
        }

        _selectedClass = AvailableClasses.Contains(previousClass)
            ? previousClass
            : AllClassesOption;
        OnPropertyChanged(nameof(SelectedClass));
    }

    private bool FilterDriverPredicate(object obj)
    {
        if (obj is not DriverItemViewModel item)
        {
            return false;
        }

        return item.MatchesFilter(SearchText, SelectedClass, OnlyUnused, OnlyRemnants, ExcludeMicrosoft, OnlyChecked);
    }

    private void ApplyFilters()
    {
        DriversView.Refresh();
        UpdateCounts();

        if (SelectedDriver is not null && !FilterDriverPredicate(SelectedDriver))
        {
            SelectedDriver = DriversView.Cast<DriverItemViewModel>().FirstOrDefault();
        }
    }

    private void OnItemSelectionChanged()
    {
        UpdateCounts();
        if (OnlyChecked)
        {
            DriversView.Refresh();
            UpdateCounts();
        }
    }

    private void UpdateCounts()
    {
        VisibleCount = DriversView.Cast<object>().Count();
        CheckedCount = Drivers.Count(d => d.IsSelected);
        InUseCount = Drivers.Count(d => d.IsInUse);
        RemnantCount = Drivers.Count(d => d.IsOldVersionRemnant);
        OnPropertyChanged(nameof(TotalCount));
        SelectOldRemnantsCommand.RaiseCanExecuteChanged();
        SelectVisibleUnusedCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        ExecuteCleanupCommand.RaiseCanExecuteChanged();
    }

    private void SelectOldRemnants()
    {
        int selectedNow = 0;
        foreach (DriverItemViewModel item in DriversView.Cast<DriverItemViewModel>())
        {
            if (item.IsOldVersionRemnant && !item.IsInUse && !item.IsSelected)
            {
                item.SetSelectedSilent(true);
                selectedNow++;
            }
        }

        UpdateCounts();
        if (selectedNow > 0)
        {
            StatusMessage = $"表示中の旧バージョン残骸 {selectedNow} 件を追加選択しました。";
            AppendLog($"一括選択: 旧バージョン残骸 +{selectedNow} 件（同一プロバイダー/クラス/INF名で新版あり・未使用のみ）");
        }
        else
        {
            StatusMessage = "現在表示中のドライバーに追加選択できる旧バージョンの残骸はありません（0件）。";
            AppendLog("一括選択（旧版残骸）: 該当ドライバーは 0 件でした。");
        }
    }

    private void SelectVisibleUnused()
    {
        int selectedNow = 0;
        foreach (DriverItemViewModel item in DriversView.Cast<DriverItemViewModel>())
        {
            if (!item.IsInUse && !item.IsSelected)
            {
                item.SetSelectedSilent(true);
                selectedNow++;
            }
        }

        UpdateCounts();
        StatusMessage = $"表示中の未使用ドライバー {selectedNow} 件を追加選択しました（使用中ドライバーは除外）。";
        AppendLog($"一括選択: 表示中の未使用ドライバー +{selectedNow} 件（使用中は安全のため除外）");
    }

    private void ClearSelection()
    {
        foreach (DriverItemViewModel item in Drivers)
        {
            item.SetSelectedSilent(false);
        }

        if (OnlyChecked)
        {
            DriversView.Refresh();
        }

        UpdateCounts();
        StatusMessage = "選択をすべて解除しました。";
    }

    private void ResetFilters()
    {
        _searchText = string.Empty;
        _selectedClass = AllClassesOption;
        _onlyUnused = false;
        _onlyRemnants = false;
        _excludeMicrosoft = false;
        _onlyChecked = false;

        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(SelectedClass));
        OnPropertyChanged(nameof(OnlyUnused));
        OnPropertyChanged(nameof(OnlyRemnants));
        OnPropertyChanged(nameof(ExcludeMicrosoft));
        OnPropertyChanged(nameof(OnlyChecked));

        ApplyFilters();
        StatusMessage = "検索条件とフィルタをリセットしました。";
    }

    private void RestartAsAdministrator()
    {
        bool started = _elevationService.TryRestartAsAdministrator(out string message, _singleInstance);
        StatusMessage = message;
        AppendLog(message);

        if (started)
        {
            Application.Current?.Shutdown();
        }
    }

    private void BrowseBackupFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "バックアップ保存先フォルダを選択",
            InitialDirectory = Directory.Exists(BackupRootDir)
                ? BackupRootDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            BackupRootDir = dialog.FolderName;
            AppendLog($"バックアップ保存先を変更: {BackupRootDir}");
        }
    }

    private void OpenBackupFolder()
    {
        try
        {
            if (!Directory.Exists(BackupRootDir))
            {
                Directory.CreateDirectory(BackupRootDir);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = BackupRootDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"フォルダを開けませんでした: {ex.Message}";
            AppendLog($"[WARN] バックアップフォルダを開けませんでした: {ex.Message}");
        }
    }

    public void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logBuffer.AppendLine(line);
        OnPropertyChanged(nameof(CombinedLogText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
