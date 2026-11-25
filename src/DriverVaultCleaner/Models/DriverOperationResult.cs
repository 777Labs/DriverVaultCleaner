namespace DriverVaultCleaner.Models;

/// <summary>
/// 1ドライバーパッケージに対するシミュレーション／バックアップ／削除の処理結果。
/// </summary>
public sealed class DriverOperationResult
{
    public string PublishedName { get; init; } = string.Empty;
    public string OriginalName { get; init; } = string.Empty;
    public bool IsDryRun { get; init; }
    public bool BackupAttempted { get; init; }
    public bool BackupSucceeded { get; init; }
    public string? BackupPath { get; init; }
    public bool DeleteAttempted { get; init; }
    public bool DeleteSucceeded { get; init; }
    public string? SkippedReason { get; init; }
    public List<string> ExecutedCommands { get; init; } = [];
    public string OutputLog { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public bool RequiresReboot { get; init; }
}
