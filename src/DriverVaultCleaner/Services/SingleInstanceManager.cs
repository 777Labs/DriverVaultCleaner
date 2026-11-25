using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DriverVaultCleaner.Services;

/// <summary>
/// 同一ユーザーセッション内での二重起動を防止し、既存ウィンドウを復元・最前面化するマネージャー。
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = @"Local\DriverVaultCleaner_SingleInstance_Mutex_v1";
    private const string ActivateMessageName = "DriverVaultCleaner.ActivateMainWindow.v1";

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const int SwRestore = 9;
    private const int AsfwAny = -1;
    private const uint MsgfltAllow = 1;

    private readonly uint _activateMessageId;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private Window? _attachedWindow;
    private HwndSource? _hwndSource;
    private bool _disposed;

    public SingleInstanceManager()
    {
        _activateMessageId = RegisterWindowMessage(ActivateMessageName);
    }

    public bool TryAcquire(bool waitBriefly = false)
    {
        if (_ownsMutex)
        {
            return true;
        }

        int maxAttempts = waitBriefly ? 30 : 1;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var candidate = new Mutex(initiallyOwned: true, name: MutexName, out bool createdNew);
                if (createdNew)
                {
                    _mutex = candidate;
                    _ownsMutex = true;
                    return true;
                }

                // 前回プロセス終了直後（UAC昇格再起動など）の猶予待機
                if (waitBriefly && candidate.WaitOne(100))
                {
                    _mutex = candidate;
                    _ownsMutex = true;
                    return true;
                }

                candidate.Dispose();
            }
            catch (AbandonedMutexException)
            {
                // 前回プロセスが異常終了した後の放棄ミューテックスを安全に引き継ぐ
                _ownsMutex = true;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 既に管理者権限インスタンスが Mutex を保持している場合など
                if (!waitBriefly || attempt == maxAttempts - 1)
                {
                    return false;
                }
            }

            if (waitBriefly && attempt < maxAttempts - 1)
            {
                Thread.Sleep(100);
            }
        }

        return false;
    }

    public void ReleaseForRestart()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
            }
        }
        catch
        {
            _ownsMutex = false;
        }
        finally
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public void TryReacquireAfterCancelledRestart()
    {
        _ = TryAcquire(waitBriefly: false);
    }

    public void NotifyExistingInstanceAndActivate()
    {
        if (_activateMessageId == 0)
        {
            return;
        }

        _ = AllowSetForegroundWindow(AsfwAny);
        _ = PostMessage(HwndBroadcast, _activateMessageId, IntPtr.Zero, IntPtr.Zero);
    }

    public void AttachWindowActivationHook(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _attachedWindow = window;
        if (_activateMessageId != 0)
        {
            // 標準ユーザープロセスからのブロードキャストを管理者ウィンドウでも受信できるよう許可
            _ = ChangeWindowMessageFilterEx(hwnd, _activateMessageId, MsgfltAllow, IntPtr.Zero);
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_activateMessageId != 0 && (uint)msg == _activateMessageId && _attachedWindow is not null)
        {
            handled = true;
            RestoreAndFocusWindow(_attachedWindow, hwnd);
        }

        return IntPtr.Zero;
    }

    private static void RestoreAndFocusWindow(Window window, IntPtr hwnd)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (IsIconic(hwnd) || window.WindowState == WindowState.Minimized)
        {
            _ = ShowWindow(hwnd, SwRestore);
            window.WindowState = WindowState.Normal;
        }

        _ = SetForegroundWindow(hwnd);
        window.Activate();
        window.Focus();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _attachedWindow = null;
        ReleaseForRestart();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);
}
