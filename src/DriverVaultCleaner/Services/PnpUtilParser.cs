using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriverVaultCleaner.Models;

namespace DriverVaultCleaner.Services;

/// <summary>
/// pnputil /enum-drivers /devices の XML および日英テキスト出力パーサーと oem#.inf バリデータ。
/// </summary>
public static partial class PnpUtilParser
{
    [GeneratedRegex(@"^oem(\d+)\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OemInfRegex();

    public static bool IsValidPublishedInfName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && OemInfRegex().IsMatch(value.Trim());
    }

    public static string EnsureValidPublishedInfName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("公開 INF 名が空です。", nameof(value));
        }

        string trimmed = value.Trim();
        if (!OemInfRegex().IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"不正なドライバー公開名です（oem#.inf 形式のみ許可）: '{value}'",
                nameof(value));
        }

        return trimmed.ToLowerInvariant();
    }

    public static int ExtractOemNumber(string? publishedName)
    {
        if (string.IsNullOrWhiteSpace(publishedName))
        {
            return int.MaxValue;
        }

        Match match = OemInfRegex().Match(publishedName.Trim());
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return number;
        }

        return int.MaxValue;
    }

    public static (string Date, string Version) SplitDriverVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, string.Empty);
        }

        string trimmed = raw.Trim();
        string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string dateToken = parts[0];
            string versionToken = parts[1];
            string formattedDate = DateTime.TryParseExact(
                dateToken,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsedDate)
                ? parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dateToken;

            return (formattedDate, versionToken);
        }

        return (string.Empty, trimmed);
    }

    public static IReadOnlyList<DriverPackageInfo> ParseXml(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return [];
        }

        XDocument doc = XDocument.Parse(xmlContent);
        XElement? root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "PnpUtil", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("pnputil XML ルート要素 <PnpUtil> が見つかりません。");
        }

        var result = new List<DriverPackageInfo>();
        foreach (XElement driverElem in root.Elements("Driver"))
        {
            string publishedName = (string?)driverElem.Attribute("DriverName") ?? string.Empty;
            if (!IsValidPublishedInfName(publishedName))
            {
                continue;
            }

            string normalizedPublished = publishedName.Trim().ToLowerInvariant();
            string driverVersionRaw = ((string?)driverElem.Element("DriverVersion") ?? string.Empty).Trim();
            (string driverDate, string driverVersionNumber) = SplitDriverVersion(driverVersionRaw);

            IEnumerable<string> attrs = driverElem
                .Element("CatalogAttributes")?
                .Elements("Attribute")
                .Select(a => a.Value.Trim())
                .Where(s => !string.IsNullOrEmpty(s)) ?? [];

            var devices = new List<AssociatedDeviceInfo>();
            XElement? devicesElem = driverElem.Element("Devices");
            if (devicesElem is not null)
            {
                foreach (XElement devElem in devicesElem.Elements("Device"))
                {
                    string instanceId = ((string?)devElem.Attribute("InstanceId") ?? string.Empty).Trim();
                    string description = ((string?)devElem.Element("DeviceDescription") ?? string.Empty).Trim();
                    string status = ((string?)devElem.Element("Status") ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(instanceId) || !string.IsNullOrEmpty(description))
                    {
                        devices.Add(new AssociatedDeviceInfo(instanceId, description, status));
                    }
                }
            }

            result.Add(new DriverPackageInfo
            {
                PublishedName = normalizedPublished,
                OemNumber = ExtractOemNumber(normalizedPublished),
                OriginalName = ((string?)driverElem.Element("OriginalName") ?? string.Empty).Trim(),
                ProviderName = ((string?)driverElem.Element("ProviderName") ?? string.Empty).Trim(),
                ClassName = ((string?)driverElem.Element("ClassName") ?? string.Empty).Trim(),
                ClassGuid = ((string?)driverElem.Element("ClassGuid") ?? string.Empty).Trim(),
                ClassVersion = ((string?)driverElem.Element("ClassVersion") ?? string.Empty).Trim(),
                DriverVersionRaw = driverVersionRaw,
                DriverDate = driverDate,
                DriverVersionNumber = driverVersionNumber,
                SignerName = ((string?)driverElem.Element("SignerName") ?? string.Empty).Trim(),
                Attributes = string.Join(", ", attrs),
                WhcpVersion = ((string?)driverElem.Element("WhcpVersion") ?? string.Empty).Trim(),
                Devices = devices
            });
        }

        List<DriverPackageInfo> ordered = result
            .OrderBy(d => d.OemNumber)
            .ThenBy(d => d.PublishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SupersededDriverAnalyzer.AnnotateSupersededRemnants(ordered);
        return ordered;
    }

    public static IReadOnlyList<DriverPackageInfo> ParseText(string textContent)
    {
        if (string.IsNullOrWhiteSpace(textContent))
        {
            return [];
        }

        var drivers = new List<DriverPackageInfo>();
        DriverBuilder? currentDriver = null;
        DeviceBuilder? currentDevice = null;
        bool inDevicesSection = false;

        void FlushDevice()
        {
            if (currentDriver is not null && currentDevice is not null && currentDevice.HasAnyValue)
            {
                currentDriver.Devices.Add(currentDevice.Build());
            }
            currentDevice = null;
        }

        void FlushDriver()
        {
            FlushDevice();
            if (currentDriver is not null && IsValidPublishedInfName(currentDriver.PublishedName))
            {
                drivers.Add(currentDriver.Build());
            }
            currentDriver = null;
            inDevicesSection = false;
        }

        using var reader = new StringReader(textContent);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            int colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            string key = trimmed[..colonIndex].Trim();
            string value = trimmed[(colonIndex + 1)..].Trim();

            if (IsPublishedNameKey(key))
            {
                FlushDriver();
                currentDriver = new DriverBuilder
                {
                    PublishedName = value
                };
                inDevicesSection = false;
                continue;
            }

            if (currentDriver is null)
            {
                continue;
            }

            if (IsDevicesSectionKey(key))
            {
                inDevicesSection = true;
                continue;
            }

            if (inDevicesSection)
            {
                if (IsInstanceIdKey(key))
                {
                    FlushDevice();
                    currentDevice = new DeviceBuilder { InstanceId = value };
                    continue;
                }

                if (IsDeviceDescriptionKey(key))
                {
                    currentDevice ??= new DeviceBuilder();
                    currentDevice.DeviceDescription = value;
                    continue;
                }

                if (IsDeviceStatusKey(key))
                {
                    currentDevice ??= new DeviceBuilder();
                    currentDevice.Status = value;
                    continue;
                }
            }

            switch (NormalizeDriverField(key))
            {
                case DriverField.OriginalName:
                    inDevicesSection = false;
                    currentDriver.OriginalName = value;
                    break;
                case DriverField.ProviderName:
                    inDevicesSection = false;
                    currentDriver.ProviderName = value;
                    break;
                case DriverField.ClassName:
                    inDevicesSection = false;
                    currentDriver.ClassName = value;
                    break;
                case DriverField.ClassGuid:
                    inDevicesSection = false;
                    currentDriver.ClassGuid = value;
                    break;
                case DriverField.ClassVersion:
                    inDevicesSection = false;
                    currentDriver.ClassVersion = value;
                    break;
                case DriverField.DriverVersion:
                    inDevicesSection = false;
                    currentDriver.DriverVersionRaw = value;
                    break;
                case DriverField.SignerName:
                    inDevicesSection = false;
                    currentDriver.SignerName = value;
                    break;
                case DriverField.Attributes:
                    inDevicesSection = false;
                    currentDriver.Attributes = value;
                    break;
                case DriverField.WhcpVersion:
                    inDevicesSection = false;
                    currentDriver.WhcpVersion = value;
                    break;
            }
        }

        FlushDriver();

        List<DriverPackageInfo> ordered = drivers
            .OrderBy(d => d.OemNumber)
            .ThenBy(d => d.PublishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SupersededDriverAnalyzer.AnnotateSupersededRemnants(ordered);
        return ordered;
    }

    private static bool IsPublishedNameKey(string key) =>
        key.Equals("公開名", StringComparison.Ordinal) ||
        key.Equals("Published Name", StringComparison.OrdinalIgnoreCase);

    private static bool IsDevicesSectionKey(string key) =>
        key.Equals("デバイス", StringComparison.Ordinal) ||
        key.Equals("Devices", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstanceIdKey(string key) =>
        key.Equals("インスタンス ID", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Instance ID", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeviceDescriptionKey(string key) =>
        key.Equals("デバイスの説明", StringComparison.Ordinal) ||
        key.Equals("Device Description", StringComparison.OrdinalIgnoreCase);

    private static bool IsDeviceStatusKey(string key) =>
        key.Equals("状態", StringComparison.Ordinal) ||
        key.Equals("Status", StringComparison.OrdinalIgnoreCase);

    private enum DriverField
    {
        Unknown,
        OriginalName,
        ProviderName,
        ClassName,
        ClassGuid,
        ClassVersion,
        DriverVersion,
        SignerName,
        Attributes,
        WhcpVersion
    }

    private static DriverField NormalizeDriverField(string key)
    {
        if (key.Equals("元の名前", StringComparison.Ordinal) ||
            key.Equals("Original Name", StringComparison.OrdinalIgnoreCase))
            return DriverField.OriginalName;

        if (key.Equals("プロバイダー名", StringComparison.Ordinal) ||
            key.Equals("Provider Name", StringComparison.OrdinalIgnoreCase))
            return DriverField.ProviderName;

        if (key.Equals("クラス名", StringComparison.Ordinal) ||
            key.Equals("Class Name", StringComparison.OrdinalIgnoreCase))
            return DriverField.ClassName;

        if (key.Equals("クラス GUID", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Class GUID", StringComparison.OrdinalIgnoreCase))
            return DriverField.ClassGuid;

        if (key.Equals("クラス バージョン", StringComparison.Ordinal) ||
            key.Equals("Class Version", StringComparison.OrdinalIgnoreCase))
            return DriverField.ClassVersion;

        if (key.Equals("ドライバー バージョン", StringComparison.Ordinal) ||
            key.Equals("Driver Version", StringComparison.OrdinalIgnoreCase))
            return DriverField.DriverVersion;

        if (key.Equals("署名者名", StringComparison.Ordinal) ||
            key.Equals("Signer Name", StringComparison.OrdinalIgnoreCase))
            return DriverField.SignerName;

        if (key.Equals("属性", StringComparison.Ordinal) ||
            key.Equals("Attributes", StringComparison.OrdinalIgnoreCase))
            return DriverField.Attributes;

        if (key.Equals("WHCP バージョン", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("WHCP Version", StringComparison.OrdinalIgnoreCase))
            return DriverField.WhcpVersion;

        return DriverField.Unknown;
    }

    private sealed class DeviceBuilder
    {
        public string InstanceId { get; set; } = string.Empty;
        public string DeviceDescription { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        public bool HasAnyValue =>
            !string.IsNullOrWhiteSpace(InstanceId) ||
            !string.IsNullOrWhiteSpace(DeviceDescription) ||
            !string.IsNullOrWhiteSpace(Status);

        public AssociatedDeviceInfo Build() =>
            new(InstanceId.Trim(), DeviceDescription.Trim(), Status.Trim());
    }

    private sealed class DriverBuilder
    {
        public string PublishedName { get; set; } = string.Empty;
        public string OriginalName { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string ClassGuid { get; set; } = string.Empty;
        public string ClassVersion { get; set; } = string.Empty;
        public string DriverVersionRaw { get; set; } = string.Empty;
        public string SignerName { get; set; } = string.Empty;
        public string Attributes { get; set; } = string.Empty;
        public string WhcpVersion { get; set; } = string.Empty;
        public List<AssociatedDeviceInfo> Devices { get; } = [];

        public DriverPackageInfo Build()
        {
            string normalizedPublished = PublishedName.Trim().ToLowerInvariant();
            (string date, string version) = SplitDriverVersion(DriverVersionRaw);
            return new DriverPackageInfo
            {
                PublishedName = normalizedPublished,
                OemNumber = ExtractOemNumber(normalizedPublished),
                OriginalName = OriginalName.Trim(),
                ProviderName = ProviderName.Trim(),
                ClassName = ClassName.Trim(),
                ClassGuid = ClassGuid.Trim(),
                ClassVersion = ClassVersion.Trim(),
                DriverVersionRaw = DriverVersionRaw.Trim(),
                DriverDate = date,
                DriverVersionNumber = version,
                SignerName = SignerName.Trim(),
                Attributes = Attributes.Trim(),
                WhcpVersion = WhcpVersion.Trim(),
                Devices = Devices.ToArray()
            };
        }
    }
}
