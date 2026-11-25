using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Two non-conflicting panel hotkeys (RW-08): Ctrl+Alt+Shift+C toggles the panel, Ctrl+Alt+Shift+A
/// opens the app switcher. Registered with RegisterHotKey (not a keyboard hook). Alt+Tab and every
/// native RDP shortcut (Ctrl+Alt+Home / End / Break, Alt+PgUp/PgDn) stay untouched (§42).
/// </summary>
public sealed class HotKeys : IDisposable
{
    public const int TogglePanelId = 0xC10;
    public const int SwitcherId = 0xC11;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;

    public HotKeys(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
        var mods = MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT;
        if (!RegisterHotKey(hwnd, TogglePanelId, mods, 0x43 /* C */)) Log.Write("Hotkey Ctrl+Alt+Shift+C is in use by another app.");
        if (!RegisterHotKey(hwnd, SwitcherId, mods, 0x41 /* A */)) Log.Write("Hotkey Ctrl+Alt+Shift+A is in use by another app.");
    }

    public event Action<int>? Pressed;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, TogglePanelId);
        UnregisterHotKey(_hwnd, SwitcherId);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>Window style helpers: tool window (no taskbar / Alt+Tab entry) and no-activate while idle.</summary>
public static class WindowStyles
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;

    public static void MakeToolWindow(IntPtr hwnd)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>While collapsed the panel never takes focus from Tally; expanded it may (user gesture).</summary>
    public static void SetNoActivate(IntPtr hwnd, bool noActivate)
    {
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = noActivate ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
}
