using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriverVaultCleaner.Services;

public sealed class SavedWindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

public sealed class AppUserSettings
{
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;
    public SavedWindowPlacement? WindowPlacement { get; set; }
}

/// <summary>
/// テーマ設定およびウィンドウ配置を %LocalAppData%\DriverVaultCleaner\settings.json へ安全に読み書きするサービス。
/// 安全ルールに従い、本番実行モード（IsLiveMode）は絶対に保存しない。
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _syncLock = new();
    private readonly string _settingsFilePath;

    public AppSettingsService(string? customSettingsFilePath = null)
    {
        _settingsFilePath = string.IsNullOrWhiteSpace(customSettingsFilePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DriverVaultCleaner",
                "settings.json")
            : customSettingsFilePath;
    }

    public AppUserSettings Load()
    {
        lock (_syncLock)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new AppUserSettings();
                }

                string json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppUserSettings>(json, JsonOptions) ?? new AppUserSettings();
            }
            catch
            {
                return new AppUserSettings();
            }
        }
    }

    public void Update(Action<AppUserSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_syncLock)
        {
            try
            {
                AppUserSettings current = LoadCoreUnsafe();
                mutate(current);
                SaveCoreUnsafe(current);
            }
            catch
            {
                // 設定保存の失敗でアプリ本体操作を妨げない
            }
        }
    }

    private AppUserSettings LoadCoreUnsafe()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppUserSettings();
        }

        string json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
        return JsonSerializer.Deserialize<AppUserSettings>(json, JsonOptions) ?? new AppUserSettings();
    }

    private void SaveCoreUnsafe(AppUserSettings settings)
    {
        string? dir = Path.GetDirectoryName(_settingsFilePath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        string tmpPath = _settingsFilePath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tmpPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmpPath, _settingsFilePath, overwrite: true);
    }
}
