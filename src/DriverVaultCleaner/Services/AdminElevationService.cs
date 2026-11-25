using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DriverVaultCleaner.Services;

/// <summary>
/// UAC 管理者権限の判定および昇格再起動を管理するサービス。
/// </summary>
public sealed class AdminElevationService
{
    public bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 管理者権限（runas）で自分自身を再起動する。
    /// 二重起動防止 Mutex を安全に引き継ぎ、UAC キャンセル（1223）時は再取得して継続する。
    /// </summary>
    public bool TryRestartAsAdministrator(out string statusMessage, SingleInstanceManager? singleInstance = null)
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            statusMessage = "実行ファイルパスを取得できなかったため、管理者として再起動できません。";
            return false;
        }

        singleInstance?.ReleaseForRestart();

        var psi = new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = "--elevated-restart",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(psi);
            statusMessage = "管理者権限で再起動しています...";
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            singleInstance?.TryReacquireAfterCancelledRestart();
            statusMessage = "管理者権限への昇格がキャンセルされました。引き続き閲覧・ドライランモードで利用できます。";
            return false;
        }
        catch (Exception ex)
        {
            singleInstance?.TryReacquireAfterCancelledRestart();
            statusMessage = $"管理者としての再起動に失敗しました: {ex.Message}";
            return false;
        }
    }
}
