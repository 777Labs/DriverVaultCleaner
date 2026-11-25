namespace DriverVaultCleaner.Models;

/// <summary>
/// バックアップセッションフォルダに出力する manifest.json のシリアライズモデル。
/// </summary>
public sealed class BackupManifest
{
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public string MachineName { get; init; } = Environment.MachineName;
    public string OsVersion { get; init; } = Environment.OSVersion.VersionString;
    public string SessionFolder { get; init; } = string.Empty;
    public List<BackupManifestEntry> Drivers { get; init; } = [];
}

public sealed class BackupManifestEntry
{
    public string PublishedName { get; init; } = string.Empty;
    public string OriginalName { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string ClassGuid { get; init; } = string.Empty;
    public string DriverVersionRaw { get; init; } = string.Empty;
    public string SignerName { get; init; } = string.Empty;
    public string ExportSubFolder { get; init; } = string.Empty;
    public bool BackupSucceeded { get; init; }
    public bool DeleteAttempted { get; init; }
    public bool DeleteSucceeded { get; init; }
    public string? Notes { get; init; }
}
