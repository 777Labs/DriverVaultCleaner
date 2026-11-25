using System.Globalization;
using DriverVaultCleaner.Models;

namespace DriverVaultCleaner.Services;

/// <summary>
/// 同一 (ProviderName, ClassName, OriginalName) グループ内で、より新しい版が別に存在する
/// 未使用の「旧バージョン残骸ドライバー」を安全に検出・注釈付けするアナライザー。
/// </summary>
public static class SupersededDriverAnalyzer
{
    public static void AnnotateSupersededRemnants(IReadOnlyList<DriverPackageInfo> drivers)
    {
        if (drivers is null || drivers.Count == 0)
        {
            return;
        }

        foreach (DriverPackageInfo driver in drivers)
        {
            driver.IsOldVersionRemnant = false;
            driver.SupersededByPublishedName = null;
            driver.SupersededByVersionDisplay = null;
        }

        var groups = drivers
            .Where(d =>
                !string.IsNullOrWhiteSpace(d.ProviderName) &&
                !string.IsNullOrWhiteSpace(d.ClassName) &&
                !string.IsNullOrWhiteSpace(d.OriginalName))
            .GroupBy(d => new DriverGroupKey(
                d.ProviderName.Trim().ToUpperInvariant(),
                d.ClassName.Trim().ToUpperInvariant(),
                d.OriginalName.Trim().ToUpperInvariant()));

        foreach (IGrouping<DriverGroupKey, DriverPackageInfo> group in groups)
        {
            List<DriverPackageInfo> members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            DriverPackageInfo newest = members[0];
            for (int i = 1; i < members.Count; i++)
            {
                if (CompareVersionThenDate(members[i], newest) > 0)
                {
                    newest = members[i];
                }
            }

            foreach (DriverPackageInfo candidate in members)
            {
                if (ReferenceEquals(candidate, newest))
                {
                    continue;
                }

                // 使用中デバイスに紐付くものは絶対に旧版残骸（削除推奨）としてマークしない
                if (!candidate.IsInUse && CompareVersionThenDate(candidate, newest) < 0)
                {
                    candidate.IsOldVersionRemnant = true;
                    candidate.SupersededByPublishedName = newest.PublishedName;
                    candidate.SupersededByVersionDisplay = newest.VersionDisplay;
                }
            }
        }
    }

    /// <summary>
    /// 数値バージョン（例: 32.0.15.7200）を優先比較し、同値または比較不能な場合のみ日付（DriverDate）で比較する。
    /// </summary>
    public static int CompareVersionThenDate(DriverPackageInfo left, DriverPackageInfo right)
    {
        if (TryParseNumericVersion(left.DriverVersionNumber, out ulong[]? leftVer) &&
            TryParseNumericVersion(right.DriverVersionNumber, out ulong[]? rightVer))
        {
            int len = Math.Max(leftVer!.Length, rightVer!.Length);
            for (int i = 0; i < len; i++)
            {
                ulong a = i < leftVer.Length ? leftVer[i] : 0;
                ulong b = i < rightVer.Length ? rightVer[i] : 0;
                int cmp = a.CompareTo(b);
                if (cmp != 0)
                {
                    return cmp;
                }
            }
        }

        if (TryParseDriverDate(left.DriverDate, out DateOnly leftDate) &&
            TryParseDriverDate(right.DriverDate, out DateOnly rightDate))
        {
            int dateCmp = leftDate.CompareTo(rightDate);
            if (dateCmp != 0)
            {
                return dateCmp;
            }
        }

        return 0;
    }

    private static bool TryParseNumericVersion(string? rawVersion, out ulong[]? segments)
    {
        segments = null;
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string[] parts = rawVersion.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var parsed = new ulong[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        segments = parsed;
        return true;
    }

    private static bool TryParseDriverDate(string? rawDate, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(rawDate))
        {
            return false;
        }

        string trimmed = rawDate.Trim();
        return DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) ||
               DateOnly.TryParseExact(trimmed, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private readonly record struct DriverGroupKey(string Provider, string Class, string OriginalName);
}
