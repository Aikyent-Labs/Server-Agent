using System.Runtime.InteropServices;
using System.Text;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Reads and acts on top-level windows of THIS interactive session using normal user-session
/// Windows APIs (handoff §44). No global keyboard hooks; Alt+Tab is never touched. Foreground
/// changes come from a WinEvent hook so state follows real window events.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private readonly int _ownPid = Environment.ProcessId;
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private IntPtr _lastForeign;

    public WindowTracker()
    {
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var fg = GetForegroundWindow();
        if (!IsOwn(fg)) _lastForeign = fg;
    }

    /// <summary>Raised (on the UI thread that created the tracker) when another app becomes foreground.</summary>
    public event Action? WindowsChanged;

    /// <summary>The last foreground window that was not the panel itself — that is the user's "active" app.</summary>
    public IntPtr LastActiveWindow => _lastForeign;

    public IReadOnlyList<TopWindow> Enumerate()
    {
        var list = new List<TopWindow>();
        var active = _lastForeign;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsCandidate(h)) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid == _ownPid) return true;
                var title = GetTitle(h);
                if (string.IsNullOrWhiteSpace(title)) return true;
                var state = IsIconic(h) ? WindowState2.Minimized : h == active ? WindowState2.Active : WindowState2.Running;
                list.Add(new TopWindow(h, (int)pid, GetProcessPath(pid), title, state));
            }
            catch
            {
                // a window that disappears mid-enumeration is simply skipped
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>User gesture: restore if minimized and bring to front.</summary>
    public static bool Focus(IntPtr h)
    {
        if (!IsWindow(h)) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        return SetForegroundWindow(h);
    }

    public static bool Minimize(IntPtr h) => IsWindow(h) && ShowWindow(h, SW_MINIMIZE);

    /// <summary>Standard close request (the app may ask to save). Never kills the process (§44).</summary>
    public static bool RequestClose(IntPtr h) => IsWindow(h) && PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static bool Exists(IntPtr h) => IsWindow(h);

    private bool IsOwn(IntPtr h)
    {
        if (h == IntPtr.Zero) return true;
        GetWindowThreadProcessId(h, out var pid);
        return pid == _ownPid;
    }

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || IsOwn(hwnd) || !IsCandidate(hwnd)) return;
        _lastForeign = hwnd;
        WindowsChanged?.Invoke();
    }

    private static bool IsCandidate(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        var cls = GetClass(h);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow");
    }

    private static string GetTitle(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string? GetProcessPath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- interop

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const uint WM_CLOSE = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
