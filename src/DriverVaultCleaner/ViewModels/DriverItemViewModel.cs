using System.ComponentModel;
using System.Runtime.CompilerServices;
using DriverVaultCleaner.Models;

namespace DriverVaultCleaner.ViewModels;

/// <summary>
/// DataGrid 表示および選択状態管理用のドライバー項目 ViewModel。
/// </summary>
public sealed class DriverItemViewModel : INotifyPropertyChanged
{
    private readonly Action? _onSelectionChanged;
    private bool _isSelected;

    public DriverItemViewModel(DriverPackageInfo model, Action? onSelectionChanged = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _onSelectionChanged = onSelectionChanged;
    }

    public DriverPackageInfo Model { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            _onSelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// 一括更新時に毎回カウンタ再計算を走らせない内部設定用。
    /// </summary>
    internal void SetSelectedSilent(bool selected)
    {
        if (_isSelected == selected)
        {
            return;
        }

        _isSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }

    public string PublishedName => Model.PublishedName;
    public int OemNumber => Model.OemNumber;
    public string OriginalName => Model.OriginalName;
    public string ProviderName => Model.ProviderName;
    public string ClassName => Model.ClassName;
    public string ClassGuid => Model.ClassGuid;
    public string ClassVersion => Model.ClassVersion;
    public string DriverVersionRaw => Model.DriverVersionRaw;
    public string DriverDate => Model.DriverDate;
    public string DriverVersionNumber => Model.DriverVersionNumber;
    public string VersionDisplay => Model.VersionDisplay;
    public string SignerName => Model.SignerName;
    public string Attributes => Model.Attributes;
    public string WhcpVersion => Model.WhcpVersion;
    public IReadOnlyList<AssociatedDeviceInfo> Devices => Model.Devices;
    public bool IsInUse => Model.IsInUse;
    public bool HasStartedDevice => Model.HasStartedDevice;
    public int ConnectedDeviceCount => Model.ConnectedDeviceCount;
    public bool IsMicrosoftProvided => Model.IsMicrosoftProvided;
    public bool IsOldVersionRemnant => Model.IsOldVersionRemnant;
    public string? SupersededByPublishedName => Model.SupersededByPublishedName;
    public string? SupersededByVersionDisplay => Model.SupersededByVersionDisplay;

    public string UsageBadgeText =>
        IsInUse
            ? $"使用中 ({ConnectedDeviceCount}件)"
            : (IsOldVersionRemnant ? "旧版残骸" : "未使用");

    public string RemnantDetailText =>
        IsOldVersionRemnant
            ? $"同一プロバイダー・クラス・INF名に新しい版（{SupersededByPublishedName} : {SupersededByVersionDisplay}）が存在する未使用の旧バージョン残骸です。"
            : string.Empty;

    public string PrimaryDeviceSummary
    {
        get
        {
            if (Devices.Count == 0)
            {
                return "—";
            }

            string first = Devices[0].DisplayName;
            return Devices.Count > 1 ? $"{first} (+{Devices.Count - 1})" : first;
        }
    }

    public bool MatchesFilter(
        string? searchText,
        string? selectedClass,
        bool onlyUnused,
        bool onlyRemnants,
        bool excludeMicrosoft,
        bool onlyChecked)
    {
        if (onlyChecked && !IsSelected)
        {
            return false;
        }

        if (onlyUnused && IsInUse)
        {
            return false;
        }

        if (onlyRemnants && !IsOldVersionRemnant)
        {
            return false;
        }

        if (excludeMicrosoft && IsMicrosoftProvided)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(selectedClass) &&
            !string.Equals(selectedClass, MainViewModel.AllClassesOption, StringComparison.Ordinal) &&
            !string.Equals(ClassName, selectedClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            string q = searchText.Trim();
            bool matched =
                PublishedName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                OriginalName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                ProviderName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                ClassName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                SignerName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                Devices.Any(d =>
                    d.DeviceDescription.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    d.InstanceId.Contains(q, StringComparison.OrdinalIgnoreCase));

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
