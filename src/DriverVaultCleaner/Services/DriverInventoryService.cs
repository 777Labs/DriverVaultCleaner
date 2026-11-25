using System.Diagnostics;
using System.Text;
using DriverVaultCleaner.Models;

namespace DriverVaultCleaner.Services;

public sealed record InventoryLoadResult(
    IReadOnlyList<DriverPackageInfo> Drivers,
    string ParserFormat,
    string ExecutedCommand,
    long ElapsedMilliseconds);

/// <summary>
/// Driver Store 内のサードパーティ製ドライバー一覧を XML 優先 + CP932テキストフォールバックで取得するサービス。
/// </summary>
public sealed class DriverInventoryService
{
    private readonly IPnpCommandRunner _runner;

    public DriverInventoryService(IPnpCommandRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<InventoryLoadResult> LoadDriversAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Primary: Windows 11 以降対応の UTF-8 XML 出力
        PnpCommandResult xmlResult = await _runner
            .RunAsync("/enum-drivers /devices /format xml", Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        if (xmlResult.ExitCode == 0 &&
            xmlResult.StandardOutput.Contains("<PnpUtil", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                IReadOnlyList<DriverPackageInfo> drivers = PnpUtilParser.ParseXml(xmlResult.StandardOutput);
                sw.Stop();
                return new InventoryLoadResult(
                    drivers,
                    "XML (UTF-8)",
                    xmlResult.ExecutedCommandLine,
                    sw.ElapsedMilliseconds);
            }
            catch
            {
                // XML 破損・非互換時はテキストパーサーへ安全にフォールバック
            }
        }

        // 2. Fallback: Windows 10 等向け OEM / CP932 テキスト出力
        Encoding oemEncoding = PnpCommandRunner.ResolveSystemOemEncoding();
        PnpCommandResult textResult = await _runner
            .RunAsync("/enum-drivers /devices", oemEncoding, cancellationToken)
            .ConfigureAwait(false);

        if (textResult.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(textResult.StandardError)
                ? textResult.StandardOutput
                : textResult.StandardError;
            throw new InvalidOperationException(
                $"pnputil /enum-drivers の実行に失敗しました (ExitCode={textResult.ExitCode}): {details}");
        }

        IReadOnlyList<DriverPackageInfo> parsedTextDrivers = PnpUtilParser.ParseText(textResult.StandardOutput);
        sw.Stop();

        return new InventoryLoadResult(
            parsedTextDrivers,
            $"Text ({oemEncoding.WebName.ToUpperInvariant()}, CP{oemEncoding.CodePage})",
            textResult.ExecutedCommandLine,
            sw.ElapsedMilliseconds);
    }
}
