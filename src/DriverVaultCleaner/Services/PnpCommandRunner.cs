using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace DriverVaultCleaner.Services;

/// <summary>
/// pnputil.exe をコンソール非表示（CreateNoWindow = true）かつデッドロックなしで実行するランナー。
/// </summary>
public sealed class PnpCommandRunner : IPnpCommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public string PnpUtilPath { get; } = Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    /// <summary>
    /// 環境変数 DVC_FORBID_DESTRUCTIVE=1 および危険フラグのハードガード検証。
    /// </summary>
    internal static void ValidateSafetyGuards(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            throw new ArgumentException("pnputil arguments cannot be empty.", nameof(arguments));
        }

        if (arguments.Contains("/reboot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Safety guard: /reboot flag is strictly forbidden.");
        }

        bool forbidDestructive = string.Equals(
            Environment.GetEnvironmentVariable("DVC_FORBID_DESTRUCTIVE"),
            "1",
            StringComparison.Ordinal);

        if (forbidDestructive &&
            (arguments.Contains("/delete-driver", StringComparison.OrdinalIgnoreCase) ||
             arguments.Contains("/remove-device", StringComparison.OrdinalIgnoreCase) ||
             arguments.Contains("/disable-device", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Safety guard: destructive pnputil operation is blocked by DVC_FORBID_DESTRUCTIVE=1.");
        }
    }

    public static Encoding ResolveSystemOemEncoding()
    {
        try
        {
            int oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (oemCodePage > 0)
            {
                return Encoding.GetEncoding(oemCodePage);
            }
        }
        catch
        {
            // Fall back below
        }

        try
        {
            return Encoding.GetEncoding(932);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public async Task<PnpCommandResult> RunAsync(
        string arguments,
        Encoding? outputEncoding = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSafetyGuards(arguments);

        if (!File.Exists(PnpUtilPath))
        {
            throw new FileNotFoundException($"pnputil.exe が見つかりません: {PnpUtilPath}", PnpUtilPath);
        }

        Encoding resolvedEncoding = outputEncoding ?? ResolveSystemOemEncoding();
        string fullCommandLine = $"\"{PnpUtilPath}\" {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = PnpUtilPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = resolvedEncoding,
            StandardErrorEncoding = resolvedEncoding
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"pnputil.exe の起動に失敗しました: {fullCommandLine}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill errors during cancellation/timeout cleanup
            }

            throw;
        }

        return new PnpCommandResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            fullCommandLine);
    }
}
