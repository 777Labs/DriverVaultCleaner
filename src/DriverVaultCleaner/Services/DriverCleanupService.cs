using System.IO;
using DriverVaultCleaner.Models;

namespace DriverVaultCleaner.Services;

public sealed record CleanupBatchReport(
    bool IsDryRun,
    string? BackupSessionDirectory,
    IReadOnlyList<DriverOperationResult> Results,
    int SuccessCount,
    int SkippedCount,
    int FailedCount,
    bool AnyRebootRequired);

/// <summary>
/// Dry-Run シミュレーションおよび Live 本番削除（バックアップ検証付き）の実行オーケストレーター。
/// </summary>
public sealed class DriverCleanupService
{
    private const int ExitCodeRebootRequired = 3010;

    private readonly IPnpCommandRunner _runner;
    private readonly DriverBackupService _backupService;
    private readonly Func<bool> _isAdministratorCheck;

    public DriverCleanupService(
        IPnpCommandRunner runner,
        DriverBackupService backupService,
        Func<bool>? isAdministratorCheck = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _isAdministratorCheck = isAdministratorCheck ?? (() => new AdminElevationService().IsAdministrator());
    }

    public static string BuildDeleteArguments(string publishedName, bool forceUninstall)
    {
        string validName = PnpUtilParser.EnsureValidPublishedInfName(publishedName);
        return forceUninstall
            ? $"/delete-driver {validName} /uninstall /force"
            : $"/delete-driver {validName}";
    }

    public async Task<CleanupBatchReport> ExecuteAsync(
        IReadOnlyList<DriverPackageInfo> targets,
        ExecutionOptions options,
        Action<string>? progressLog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        if (targets.Count == 0)
        {
            return new CleanupBatchReport(!options.IsLiveMode, null, [], 0, 0, 0, false);
        }

        // 1. 事前バリデーション（Dry-Run / Live 共通で不正な INF 名を遮断）
        foreach (DriverPackageInfo target in targets)
        {
            _ = PnpUtilParser.EnsureValidPublishedInfName(target.PublishedName);
        }

        string? sessionDir = options.EnableBackup
            ? DriverBackupService.BuildSessionDirectoryPath(options.BackupRootDir, DateTime.Now)
            : null;

        // 2. Dry-Run パス（既定：実プロセス起動・実ディスク書込は一切行わない）
        if (!options.IsLiveMode)
        {
            return ExecuteDryRun(targets, options, sessionDir, progressLog);
        }

        // 3. Live 本番パス — 安全ガード検証
        if (string.Equals(Environment.GetEnvironmentVariable("DVC_FORBID_DESTRUCTIVE"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Safety guard: Live cleanup is blocked because DVC_FORBID_DESTRUCTIVE=1 is set.");
        }

        if (!_isAdministratorCheck())
        {
            throw new UnauthorizedAccessException("実ドライバーのバックアップ・削除には管理者権限が必要です。");
        }

        if (options.EnableBackup && !string.IsNullOrWhiteSpace(sessionDir))
        {
            Directory.CreateDirectory(sessionDir);
            progressLog?.Invoke($"バックアップセッションフォルダを作成: {sessionDir}");
        }

        var results = new List<DriverOperationResult>(targets.Count);
        int successCount = 0;
        int skippedCount = 0;
        int failedCount = 0;
        bool anyRebootRequired = false;

        foreach (DriverPackageInfo driver in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string publishedName = PnpUtilParser.EnsureValidPublishedInfName(driver.PublishedName);
            var commands = new List<string>();
            var outputLines = new List<string>();

            bool backupAttempted = false;
            bool backupSucceeded = false;
            string? backupPath = null;

            // フェイルセーフ Step A: 有効時は必ず先にバックアップを実行・検証
            if (options.EnableBackup && !string.IsNullOrWhiteSpace(sessionDir))
            {
                backupAttempted = true;
                progressLog?.Invoke($"バックアップ開始: {publishedName} ({driver.OriginalName})");

                DriverExportResult exportResult = await _backupService
                    .ExportDriverAsync(driver, sessionDir, cancellationToken)
                    .ConfigureAwait(false);

                commands.Add(exportResult.ExecutedCommandLine);
                if (!string.IsNullOrWhiteSpace(exportResult.Output))
                {
                    outputLines.Add(exportResult.Output);
                }

                backupPath = exportResult.TargetSubDir;
                backupSucceeded = exportResult.Succeeded;

                if (!exportResult.Succeeded)
                {
                    string reason = exportResult.ErrorMessage ?? "バックアップに失敗したため削除をスキップしました（フェイルセーフ）。";
                    progressLog?.Invoke($"[ERROR] フェイルセーフ発動 ({publishedName}): {reason} -> /delete-driver をスキップします。");

                    results.Add(new DriverOperationResult
                    {
                        PublishedName = publishedName,
                        OriginalName = driver.OriginalName,
                        IsDryRun = false,
                        BackupAttempted = true,
                        BackupSucceeded = false,
                        BackupPath = backupPath,
                        DeleteAttempted = false,
                        DeleteSucceeded = false,
                        SkippedReason = reason,
                        ExecutedCommands = commands,
                        OutputLog = string.Join(Environment.NewLine, outputLines),
                        ExitCode = exportResult.ExitCode
                    });
                    skippedCount++;
                    continue;
                }

                progressLog?.Invoke($"バックアップ成功 & .inf 検証OK: {publishedName} -> {backupPath}");
            }

            // Step B: 削除実行（/reboot 付与禁止）
            string deleteArgs = BuildDeleteArguments(publishedName, options.ForceUninstall);
            progressLog?.Invoke($"削除コマンド実行: \"{_runner.PnpUtilPath}\" {deleteArgs}");

            PnpCommandResult deleteResult = await _runner
                .RunAsync(deleteArgs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            commands.Add(deleteResult.ExecutedCommandLine);
            if (!string.IsNullOrWhiteSpace(deleteResult.StandardOutput))
            {
                outputLines.Add(deleteResult.StandardOutput.Trim());
            }
            if (!string.IsNullOrWhiteSpace(deleteResult.StandardError))
            {
                outputLines.Add($"[STDERR] {deleteResult.StandardError.Trim()}");
            }

            bool requiresReboot = deleteResult.ExitCode == ExitCodeRebootRequired;
            bool deleteSucceeded = deleteResult.ExitCode == 0 || requiresReboot;
            if (requiresReboot)
            {
                anyRebootRequired = true;
            }

            if (deleteSucceeded)
            {
                successCount++;
                string rebootTag = requiresReboot ? " [再起動が必要 (ExitCode=3010)]" : string.Empty;
                progressLog?.Invoke($"削除完了: {publishedName}{rebootTag}");
            }
            else
            {
                failedCount++;
                progressLog?.Invoke($"[WARN] 削除失敗 ({publishedName}, ExitCode={deleteResult.ExitCode})");
            }

            results.Add(new DriverOperationResult
            {
                PublishedName = publishedName,
                OriginalName = driver.OriginalName,
                IsDryRun = false,
                BackupAttempted = backupAttempted,
                BackupSucceeded = backupSucceeded,
                BackupPath = backupPath,
                DeleteAttempted = true,
                DeleteSucceeded = deleteSucceeded,
                SkippedReason = deleteSucceeded ? null : $"pnputil /delete-driver 終了コード: {deleteResult.ExitCode}",
                ExecutedCommands = commands,
                OutputLog = string.Join(Environment.NewLine, outputLines),
                ExitCode = deleteResult.ExitCode,
                RequiresReboot = requiresReboot
            });
        }

        if (options.EnableBackup && !string.IsNullOrWhiteSpace(sessionDir))
        {
            await _backupService
                .WriteSessionArtifactsAsync(sessionDir, targets, results, cancellationToken)
                .ConfigureAwait(false);
            progressLog?.Invoke($"復元スクリプトとマニフェストを出力: {sessionDir} (manifest.json, restore_drivers.bat, RESTORE_GUIDE.txt)");
        }

        return new CleanupBatchReport(
            IsDryRun: false,
            BackupSessionDirectory: sessionDir,
            Results: results,
            SuccessCount: successCount,
            SkippedCount: skippedCount,
            FailedCount: failedCount,
            AnyRebootRequired: anyRebootRequired);
    }

    private CleanupBatchReport ExecuteDryRun(
        IReadOnlyList<DriverPackageInfo> targets,
        ExecutionOptions options,
        string? sessionDir,
        Action<string>? progressLog)
    {
        progressLog?.Invoke($"[DRY-RUN] シミュレーション開始（対象 {targets.Count} 件 / 実環境への変更・書込なし）");
        if (options.EnableBackup && !string.IsNullOrWhiteSpace(sessionDir))
        {
            progressLog?.Invoke($"[DRY-RUN] 予定バックアップ先: {sessionDir}");
        }
        else
        {
            progressLog?.Invoke("[DRY-RUN] バックアップ設定: 無効（削除のみシミュレーション）");
        }

        var results = new List<DriverOperationResult>(targets.Count);

        foreach (DriverPackageInfo driver in targets)
        {
            string publishedName = PnpUtilParser.EnsureValidPublishedInfName(driver.PublishedName);
            var plannedCommands = new List<string>();
            string? targetSubDir = null;

            if (options.EnableBackup && !string.IsNullOrWhiteSpace(sessionDir))
            {
                targetSubDir = Path.Combine(sessionDir, DriverBackupService.BuildDriverSubFolderName(driver));
                string exportCmd = $"\"{_runner.PnpUtilPath}\" {DriverBackupService.BuildExportArguments(publishedName, targetSubDir)}";
                plannedCommands.Add(exportCmd);
                progressLog?.Invoke($"[DRY-RUN] 実行予定コマンド: {exportCmd}");
            }

            string deleteCmd = $"\"{_runner.PnpUtilPath}\" {BuildDeleteArguments(publishedName, options.ForceUninstall)}";
            plannedCommands.Add(deleteCmd);

            string inUseNote = driver.IsInUse
                ? $" [注意: 現在 {driver.ConnectedDeviceCount} 台のデバイスで使用中]"
                : string.Empty;
            progressLog?.Invoke($"[DRY-RUN] 実行予定コマンド: {deleteCmd}{inUseNote}");

            results.Add(new DriverOperationResult
            {
                PublishedName = publishedName,
                OriginalName = driver.OriginalName,
                IsDryRun = true,
                BackupAttempted = options.EnableBackup,
                BackupSucceeded = options.EnableBackup,
                BackupPath = targetSubDir,
                DeleteAttempted = true,
                DeleteSucceeded = true,
                ExecutedCommands = plannedCommands,
                OutputLog = string.Join(Environment.NewLine, plannedCommands),
                ExitCode = 0
            });
        }

        if (options.EnableBackup && !string.IsNullOrWhiteSpace(sessionDir))
        {
            progressLog?.Invoke("[DRY-RUN] 生成予定ファイル: manifest.json, restore_drivers.bat, RESTORE_GUIDE.txt");
        }

        progressLog?.Invoke($"[DRY-RUN] シミュレーション完了（{targets.Count} 件の実行予定コマンドを出力、実環境未変更）");

        return new CleanupBatchReport(
            IsDryRun: true,
            BackupSessionDirectory: sessionDir,
            Results: results,
            SuccessCount: targets.Count,
            SkippedCount: 0,
            FailedCount: 0,
            AnyRebootRequired: false);
    }
}
