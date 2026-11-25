using System.IO;

namespace DriverVaultCleaner.Models;

/// <summary>
/// バックアップおよび削除処理の実行オプション。安全のため既定値は Dry-Run・バックアップ有効。
/// </summary>
public sealed class ExecutionOptions
{
    public static string DefaultBackupDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DriverVaultBackups");

    /// <summary>
    /// 本番実行モードかどうか。既定値は必ず false（Dry-Run シミュレーション）。
    /// </summary>
    public bool IsLiveMode { get; init; } = false;

    /// <summary>
    /// 削除前に /export-driver でバックアップを取得するかどうか。
    /// </summary>
    public bool EnableBackup { get; init; } = true;

    /// <summary>
    /// バックアップの保存先ルートディレクトリ。
    /// </summary>
    public string BackupRootDir { get; init; } = DefaultBackupDirectory;

    /// <summary>
    /// 使用中デバイスからもアンインストールして強制削除（/uninstall /force）するかどうか。
    /// </summary>
    public bool ForceUninstall { get; init; } = false;
}
