namespace DriverVaultCleaner.Models;

/// <summary>
/// Driver Store に登録されているサードパーティ製ドライバーパッケージ情報。
/// </summary>
public sealed class DriverPackageInfo
{
    public string PublishedName { get; init; } = string.Empty;
    public int OemNumber { get; init; } = int.MaxValue;
    public string OriginalName { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string ClassGuid { get; init; } = string.Empty;
    public string ClassVersion { get; init; } = string.Empty;
    public string DriverVersionRaw { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string DriverVersionNumber { get; init; } = string.Empty;
    public string SignerName { get; init; } = string.Empty;
    public string Attributes { get; init; } = string.Empty;
    public string WhcpVersion { get; init; } = string.Empty;
    public IReadOnlyList<AssociatedDeviceInfo> Devices { get; init; } = [];

    /// <summary>
    /// 同一プロバイダー・同一クラス・同一元の名前の中で、より新しい版が別に存在する未使用の旧バージョン残骸かどうか。
    /// </summary>
    public bool IsOldVersionRemnant { get; internal set; }

    /// <summary>
    /// より新しい版の公開名（例: oem15.inf）。
    /// </summary>
    public string? SupersededByPublishedName { get; internal set; }

    /// <summary>
    /// より新しい版のバージョン／日付表示。
    /// </summary>
    public string? SupersededByVersionDisplay { get; internal set; }

    /// <summary>
    /// 1台以上のデバイスに現在紐付いている（使用中）かどうか。
    /// </summary>
    public bool IsInUse => Devices.Count > 0;

    /// <summary>
    /// 開始（Started）状態のデバイスが含まれるかどうか。
    /// </summary>
    public bool HasStartedDevice => Devices.Any(d => d.IsStarted);

    /// <summary>
    /// 紐付くデバイスの総数。
    /// </summary>
    public int ConnectedDeviceCount => Devices.Count;

    /// <summary>
    /// Microsoft 提供プロバイダーかどうか。
    /// </summary>
    public bool IsMicrosoftProvided =>
        string.Equals(ProviderName.Trim(), "Microsoft", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ProviderName.Trim(), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// UI表示用のバージョン・日付文字列。
    /// </summary>
    public string VersionDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DriverVersionNumber) && !string.IsNullOrWhiteSpace(DriverDate))
            {
                return $"{DriverVersionNumber}  ({DriverDate})";
            }

            if (!string.IsNullOrWhiteSpace(DriverVersionNumber))
            {
                return DriverVersionNumber;
            }

            return DriverVersionRaw;
        }
    }
}
