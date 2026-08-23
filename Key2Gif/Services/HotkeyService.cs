using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Key2Gif.Services;

/// <summary>
/// Uses a low-level keyboard hook (WH_KEYBOARD_LL) to capture hotkeys.
/// Primary: LeftCtrl+Shift+RightCtrl
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private Dispatcher? _dispatcher;

    // Only RCtrl itself is tracked, solely to suppress auto-repeat.
    // A keydown arriving long after the previous RCtrl event is always a fresh
    // physical press (auto-repeat fires every ~30ms), so a dropped keyup can
    // never wedge the hotkey for more than one press.
    private bool _rCtrlDown;
    private long _lastRCtrlEvent;
    private const long RepeatTimeoutMs = 500;

    public event Action? HotkeyPressed;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Register()
    {
        _dispatcher = Application.Current.Dispatcher;

        _hookProc = HookCallback;
        // WH_KEYBOARD_LL ignores hMod; pass current module handle for safety.
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);

        if (_hookId != IntPtr.Zero)
        {
            Log.Info($"Low-level keyboard hook installed (handle=0x{_hookId.ToInt64():X})");
        }
        else
        {
            Log.Error("Failed to install low-level keyboard hook!");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            int msg = wParam.ToInt32();
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // Track modifier states via live key state — immune to stuck state
            // caused by keyup events the hook never saw (focus switches, timeouts).
            // Only RCtrl is edge-detected; LCtrl/Shift are sampled at press time.
            if (vkCode == VK_RCONTROL)
            {
                var now = Environment.TickCount64;
                bool isFresh = now - _lastRCtrlEvent > RepeatTimeoutMs;
                _lastRCtrlEvent = now;

                if (isDown && (isFresh || !_rCtrlDown))
                {
                    _rCtrlDown = true;

                    if (IsDown(VK_LCONTROL) && (IsDown(VK_LSHIFT) || IsDown(VK_RSHIFT)))
                    {
                        Log.Info("LCtrl+Shift+RCtrl intercepted!");
                        _dispatcher?.BeginInvoke(() => HotkeyPressed?.Invoke());
                        return (IntPtr)1; // Consume the key
                    }
                }
                else if (isUp)
                {
                    _rCtrlDown = false;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        Log.Info("Hotkey service disposed");
    }
}
