using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Alt+Tab opens the CloudOrc app switcher instead of the Windows one (owner decision 26 Nov 2026,
/// overriding handoff §42 "do not hook Alt+Tab").
///
/// Only keys that reach THIS Windows session are seen: a full-desktop RDP client sending Windows key
/// combinations to the remote computer. In a RemoteApp (.connect) or browser (HTML5) session the user's
/// own PC handles Alt+Tab and nothing on the server can see it.
///
/// Fail-safe (never leave the user without Alt+Tab):
///  - the hook runs on its OWN thread with its own message loop, so a busy or hung panel UI never delays
///    keyboard input;
///  - Tab is swallowed only while the panel UI is alive: if the previous Alt+Tab was not handled by the UI
///    within <see cref="UiDeadline"/>, keys go to Windows and the Windows switcher opens as usual;
///  - <see cref="Failed"/> is raised then, so the panel can switch the hook off for the session;
///  - a fallback switch (CloudOrc team / server admin) disposes it (see FallbackGuard).
/// </summary>
public sealed class AltTabHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_QUIT = 0x0012;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int LLKHF_ALTDOWN = 0x20;

    /// <summary>How long the UI may take to show the switcher before Alt+Tab goes back to Windows.</summary>
    public static readonly TimeSpan UiDeadline = TimeSpan.FromMilliseconds(1500);

    private readonly HookProc _proc; // keep the delegate alive for the lifetime of the hook
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hook;
    private uint _threadId;
    private bool _inGesture;
    private long _pendingSince; // Stopwatch ticks of the oldest UI action not yet run (0 = none)
    private volatile bool _uiDead;

    /// <summary>Alt+Tab (forward = true) or Alt+Shift+Tab (forward = false). Raised on the UI thread.</summary>
    public event Action<bool>? Step;

    /// <summary>Alt released after at least one Alt+Tab: switch to the selected window. Raised on the UI thread.</summary>
    public event Action? Commit;

    /// <summary>Esc pressed during the gesture. Raised on the UI thread.</summary>
    public event Action? Cancel;

    /// <summary>The UI did not answer in time; Alt+Tab went to Windows. Raised on the UI thread once it recovers.</summary>
    public event Action? Failed;

    public AltTabHook(System.Windows.Threading.Dispatcher ui)
    {
        _ui = ui;
        _proc = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = "CloudOrc Alt+Tab hook" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool Installed => _hook != IntPtr.Zero;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        using (var module = Process.GetCurrentProcess().MainModule)
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        }

        if (_hook == IntPtr.Zero) Log.Write($"Alt+Tab hook could not be installed (error {Marshal.GetLastWin32Error()}); Windows Alt+Tab stays.");
        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        // A low-level hook is called through the installing thread's message loop.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && !_uiDead)
            {
                var msg = wParam.ToInt32();
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                var up = msg is WM_KEYUP or WM_SYSKEYUP;
                var altHeld = (info.flags & LLKHF_ALTDOWN) != 0;

                if (down && info.vkCode == VK_TAB && altHeld)
                {
                    if (UiIsLate())
                    {
                        // The panel UI is not answering: give Alt+Tab back to Windows right now.
                        _uiDead = true;
                        _inGesture = false;
                        Log.Write("Alt+Tab: the panel did not respond in time; Windows Alt+Tab is used.");
                        _ui.BeginInvoke(() => Failed?.Invoke());
                        return CallNextHookEx(_hook, nCode, wParam, lParam);
                    }

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
        }
        catch
        {
            // Never break the keyboard: on any error the key goes on to Windows.
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool UiIsLate()
    {
        var since = Interlocked.Read(ref _pendingSince);
        return since != 0 && Stopwatch.GetElapsedTime(since) > UiDeadline;
    }

    /// <summary>Runs <paramref name="a"/> on the UI thread; tracks whether the UI is keeping up.</summary>
    private void Raise(Action a)
    {
        Interlocked.CompareExchange(ref _pendingSince, Stopwatch.GetTimestamp(), 0);
        _ui.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pendingSince, 0);
            a();
        });
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _threadId = 0;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
