namespace DriverVaultCleaner.Models;

/// <summary>
/// ドライバーパッケージに紐付いているデバイス情報。
/// </summary>
public sealed record AssociatedDeviceInfo(
    string InstanceId,
    string DeviceDescription,
    string Status)
{
    /// <summary>
    /// デバイスが現在稼働状態（Started / 開始）かどうか。
    /// </summary>
    public bool IsStarted =>
        Status.Equals("Started", StringComparison.OrdinalIgnoreCase) ||
        Status.Equals("開始", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 表示用タイトル。説明が空の場合は InstanceId を返す。
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(DeviceDescription) ? InstanceId : DeviceDescription;
}
