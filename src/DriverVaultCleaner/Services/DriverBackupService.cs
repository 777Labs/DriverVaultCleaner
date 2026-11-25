using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DriverVaultCleaner.Models;

namespace DriverVaultCleaner.Services;

public sealed record DriverExportResult(
    bool Succeeded,
    string TargetSubDir,
    string ExecutedCommandLine,
    int ExitCode,
    string Output,
    string? ErrorMessage);

/// <summary>
/// 削除前のドライバーエクスポート（/export-driver）と復元スクリプト・マニフェスト生成を担当するサービス。
/// </summary>
public sealed class DriverBackupService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IPnpCommandRunner _runner;
    private readonly Func<string, bool> _exportedInfVerifier;

    public DriverBackupService(
        IPnpCommandRunner runner,
        Func<string, bool>? exportedInfVerifier = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _exportedInfVerifier = exportedInfVerifier ?? DefaultExportedInfVerifier;
    }

    public static bool DefaultExportedInfVerifier(string directoryPath) =>
        Directory.Exists(directoryPath) &&
        Directory.EnumerateFiles(directoryPath, "*.inf", SearchOption.AllDirectories).Any();

    public static string BuildSessionDirectoryPath(string backupRootDir, DateTime localTimestamp)
    {
        string root = string.IsNullOrWhiteSpace(backupRootDir)
            ? ExecutionOptions.DefaultBackupDirectory
            : backupRootDir.Trim();
        return Path.Combine(root, $"Backup_{localTimestamp:yyyyMMdd_HHmmss}");
    }

    public static string BuildDriverSubFolderName(DriverPackageInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        string publishedName = PnpUtilParser.EnsureValidPublishedInfName(driver.PublishedName);

        string baseOriginal = Path.GetFileNameWithoutExtension(driver.OriginalName ?? string.Empty);
        string sanitized = SanitizePathSegment(baseOriginal);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "driver";
        }

        return $"{publishedName}_{sanitized}";
    }

    public static string BuildExportArguments(string publishedName, string targetSubDir)
    {
        string validName = PnpUtilParser.EnsureValidPublishedInfName(publishedName);
        return $"/export-driver {validName} \"{targetSubDir}\"";
    }

    public async Task<DriverExportResult> ExportDriverAsync(
        DriverPackageInfo driver,
        string sessionDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        string publishedName = PnpUtilParser.EnsureValidPublishedInfName(driver.PublishedName);
        string subFolder = BuildDriverSubFolderName(driver);
        string targetSubDir = Path.Combine(sessionDir, subFolder);

        Directory.CreateDirectory(targetSubDir);

        string arguments = BuildExportArguments(publishedName, targetSubDir);
        PnpCommandResult result = await _runner
            .RunAsync(arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string combinedOutput = CombineCommandOutput(result);
        if (result.ExitCode != 0)
        {
            return new DriverExportResult(
                Succeeded: false,
                TargetSubDir: targetSubDir,
                ExecutedCommandLine: result.ExecutedCommandLine,
                ExitCode: result.ExitCode,
                Output: combinedOutput,
                ErrorMessage: $"pnputil /export-driver が終了コード {result.ExitCode} で失敗しました。");
        }

        if (!_exportedInfVerifier(targetSubDir))
        {
            return new DriverExportResult(
                Succeeded: false,
                TargetSubDir: targetSubDir,
                ExecutedCommandLine: result.ExecutedCommandLine,
                ExitCode: result.ExitCode,
                Output: combinedOutput,
                ErrorMessage: $"エクスポート先フォルダに .inf ファイルが出力されませんでした: {targetSubDir}");
        }

        return new DriverExportResult(
            Succeeded: true,
            TargetSubDir: targetSubDir,
            ExecutedCommandLine: result.ExecutedCommandLine,
            ExitCode: result.ExitCode,
            Output: combinedOutput,
            ErrorMessage: null);
    }

    public async Task WriteSessionArtifactsAsync(
        string sessionDir,
        IReadOnlyList<DriverPackageInfo> drivers,
        IReadOnlyList<DriverOperationResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(sessionDir);

        var resultMap = results.ToDictionary(r => r.PublishedName, StringComparer.OrdinalIgnoreCase);
        var entries = new List<BackupManifestEntry>(drivers.Count);

        foreach (DriverPackageInfo driver in drivers)
        {
            resultMap.TryGetValue(driver.PublishedName, out DriverOperationResult? op);
            entries.Add(new BackupManifestEntry
            {
                PublishedName = driver.PublishedName,
                OriginalName = driver.OriginalName,
                ProviderName = driver.ProviderName,
                ClassName = driver.ClassName,
                ClassGuid = driver.ClassGuid,
                DriverVersionRaw = driver.DriverVersionRaw,
                SignerName = driver.SignerName,
                ExportSubFolder = BuildDriverSubFolderName(driver),
                BackupSucceeded = op?.BackupSucceeded ?? false,
                DeleteAttempted = op?.DeleteAttempted ?? false,
                DeleteSucceeded = op?.DeleteSucceeded ?? false,
                Notes = op?.SkippedReason
            });
        }

        var manifest = new BackupManifest
        {
            CreatedAtUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            SessionFolder = sessionDir,
            Drivers = entries
        };

        string manifestPath = Path.Combine(sessionDir, "manifest.json");
        string manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await File.WriteAllTextAsync(manifestPath, manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        string restoreBatPath = Path.Combine(sessionDir, "restore_drivers.bat");
        string restoreBatContent = BuildRestoreBatchScript();
        await File.WriteAllTextAsync(restoreBatPath, restoreBatContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        string guidePath = Path.Combine(sessionDir, "RESTORE_GUIDE.txt");
        string guideContent = BuildRestoreGuideText(sessionDir, entries);
        await File.WriteAllTextAsync(guidePath, guideContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildRestoreBatchScript()
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 > nul\r\n");
        sb.Append("setlocal\r\n");
        sb.Append("cd /d \"%~dp0\"\r\n\r\n");
        sb.Append("net session >nul 2>&1\r\n");
        sb.Append("if errorlevel 1 (\r\n");
        sb.Append("    echo [エラー] 管理者権限が必要です。右クリックして「管理者として実行」してください。\r\n");
        sb.Append("    pause\r\n");
        sb.Append("    exit /b 1\r\n");
        sb.Append(")\r\n\r\n");
        sb.Append("echo バックアップされたドライバーを一括復元しています...\r\n");
        sb.Append("pnputil.exe /add-driver \"%~dp0*.inf\" /subdirs /install\r\n");
        sb.Append("if errorlevel 1 (\r\n");
        sb.Append("    echo [注意] 一部のドライバー復元で警告またはエラーが発生しました。出力を確認してください。\r\n");
        sb.Append("    pause\r\n");
        sb.Append("    exit /b 1\r\n");
        sb.Append(")\r\n\r\n");
        sb.Append("echo 復元処理が完了しました。必要に応じて PC を再起動してください。\r\n");
        sb.Append("pause\r\n");
        sb.Append("exit /b 0\r\n");
        return sb.ToString();
    }

    private static string BuildRestoreGuideText(string sessionDir, IReadOnlyList<BackupManifestEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("============================================================");
        sb.AppendLine(" DriverVault Cleaner - ドライバー復元ガイド");
        sb.AppendLine("============================================================");
        sb.AppendLine($"セッションフォルダ: {sessionDir}");
        sb.AppendLine($"作成日時 (ローカル): {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("[方法 1] 一括復元バッチファイルを使用（推奨）");
        sb.AppendLine("  1. 同フォルダ内の restore_drivers.bat を右クリックし「管理者として実行」します。");
        sb.AppendLine("  2. 画面の案内に従って処理完了を待ちます。");
        sb.AppendLine();
        sb.AppendLine("[方法 2] 特定のドライバーのみコマンドで手動復元");
        sb.AppendLine("  管理者権限のコマンド プロンプトまたは PowerShell で以下を実行します:");
        sb.AppendLine("    pnputil /add-driver \"<サブフォルダ>\\<元のINF名>\" /install");
        sb.AppendLine();
        sb.AppendLine("[方法 3] デバイスマネージャーから復元");
        sb.AppendLine("  1. デバイスマネージャーで対象デバイスを右クリック -> [ドライバーの更新]");
        sb.AppendLine("  2. [コンピューターを参照してドライバーを検索] -> このセッションフォルダを指定");
        sb.AppendLine("  3. [サブフォルダーも検索する] にチェックを入れて次へ進みます。");
        sb.AppendLine();
        sb.AppendLine("------------------------------------------------------------");
        sb.AppendLine(" 退避対象ドライバー一覧");
        sb.AppendLine("------------------------------------------------------------");
        foreach (BackupManifestEntry entry in entries)
        {
            string status = entry.BackupSucceeded ? "退避OK" : "退避失敗";
            sb.AppendLine($"- {entry.PublishedName} ({entry.OriginalName}) | {entry.ProviderName} | {status} -> {entry.ExportSubFolder}");
        }
        return sb.ToString();
    }

    private static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char ch in input.Trim())
        {
            sb.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        return sb.ToString();
    }

    private static string CombineCommandOutput(PnpCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardOutput.Trim();
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return result.StandardError.Trim();
        }

        return $"{result.StandardOutput.Trim()}\n[STDERR] {result.StandardError.Trim()}";
    }
}
