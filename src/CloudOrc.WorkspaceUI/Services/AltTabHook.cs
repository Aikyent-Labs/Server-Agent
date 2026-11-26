using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Alt+Tab opens the CloudOrc app switcher instead of the Windows one (owner decision 26 Nov 2026,
/// overriding handoff §42 "do not hook Alt+Tab").
///
/// Only keys that reach THIS Windows session are seen: a full-desktop RDP client sending Windows key
/// combinations to the remote computer (full screen). In a RemoteApp (.connect) or browser (HTML5) session
/// the user's own PC handles Alt+Tab and nothing on the server can see it.
///
/// Low-level keyboard hook on the UI thread: Tab with Alt held is swallowed and reported (Shift = backwards);
/// Alt released ends the gesture; Esc during it cancels. If the panel ever stops responding, Windows drops
/// the hook after its LowLevelHooksTimeout and the normal Windows Alt+Tab works again.
/// </summary>
public sealed class AltTabHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int LLKHF_ALTDOWN = 0x20;

    private readonly HookProc _proc; // keep the delegate alive for the lifetime of the hook
    private IntPtr _hook;
    private bool _inGesture;

    /// <summary>Alt+Tab (forward = true) or Alt+Shift+Tab (forward = false).</summary>
    public event Action<bool>? Step;

    /// <summary>Alt released after at least one Alt+Tab: switch to the selected window.</summary>
    public event Action? Commit;

    /// <summary>Esc pressed during the gesture.</summary>
    public event Action? Cancel;

    public AltTabHook()
    {
        _proc = HookCallback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) Log.Write($"Alt+Tab hook could not be installed (error {Marshal.GetLastWin32Error()}); Windows Alt+Tab stays.");
    }

    public bool Installed => _hook != IntPtr.Zero;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = msg is WM_KEYUP or WM_SYSKEYUP;
            var altHeld = (info.flags & LLKHF_ALTDOWN) != 0;

            if (down && info.vkCode == VK_TAB && altHeld)
            {
                _inGesture = true;
                var forward = (GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0;
                Raise(() => Step?.Invoke(forward));
                return new IntPtr(1); // swallow: the Windows switcher must not open
            }

            if (_inGesture && down && info.vkCode == VK_ESCAPE)
            {
                _inGesture = false;
                Raise(() => Cancel?.Invoke());
                return new IntPtr(1);
            }

            if (_inGesture && up && info.vkCode is VK_MENU or VK_LMENU or VK_RMENU)
            {
                _inGesture = false;
                Raise(() => Commit?.Invoke());
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>The hook must return fast: the switcher UI work runs right after, on the same (UI) thread.</summary>
    private static void Raise(Action a) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
