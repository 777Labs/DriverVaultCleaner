using System.Text;

namespace DriverVaultCleaner.Services;

/// <summary>
/// pnputil.exe 実行結果。
/// </summary>
public sealed record PnpCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string ExecutedCommandLine);

/// <summary>
/// pnputil.exe 実行抽象インターフェース（テスト時はモックへ差し替え可能）。
/// </summary>
public interface IPnpCommandRunner
{
    string PnpUtilPath { get; }

    Task<PnpCommandResult> RunAsync(
        string arguments,
        Encoding? outputEncoding = null,
        CancellationToken cancellationToken = default);
}
