using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Pilot switch "hide the TSPlus floating panel" (handoff §37 phase 6, §38.1): while the CloudOrc
/// panel is healthy it makes TSPlus FloatingPanel windows in THIS session invisible and
/// click-through. Nothing in the TSPlus configuration changes — publication and sessions keep working.
///
/// The window is NOT hidden (SW_HIDE): hiding a modal/top-level window can make its owner close it,
/// after which it could not be brought back. Instead it stays "visible" to its process but is drawn
/// fully transparent and ignores the mouse; the original window style is restored when the policy is
/// switched off, when the panel exits, or when it crashes.
/// </summary>
public sealed class TsplusPanelHider
{
    public static TsplusPanelHider Instance { get; } = new();

    /// <summary>
    /// TSPlus panel windows: the desktop Floating Panel (FloatingPanel.exe, any window) and the RemoteApp
    /// panel of ".connect" Remote App clients, which TSPlus draws from svcr.exe with the title "Remote App".
    /// svcr.exe shows other TSPlus windows too, so for it only that exact title is matched.
    /// </summary>
    private static readonly (string Process, string? Title)[] Targets = [("floatingpanel", null), ("svcr", "Remote App")];

    private static IEnumerable<(Process Process, string? Title)> TargetProcesses()
    {
        foreach (var (name, title) in Targets)
        {
            foreach (var p in Process.GetProcessesByName(name)) yield return (p, title);
        }
    }

    private static bool TitleMatches(IntPtr h, string? title)
    {
        if (title is null) return true;
        var sb = new System.Text.StringBuilder(128);
        GetWindowText(h, sb, sb.Capacity);
        return string.Equals(sb.ToString(), title, StringComparison.Ordinal);
    }

    private sealed record Original(long ExStyle, bool WasLayered, byte Alpha, uint Key, uint Flags, int Width, int Height, bool Shrunk);

    // Seamless (RemoteApp) sessions draw each window on the user's own PC, where layered transparency and an
    // empty window region are not honoured (the window shows as a black box) and off-screen positions are
    // clamped back. There the window is also shrunk to 1x1 px: it stays "open", so TSPlus keeps it and the
    // session stays alive, but nothing is left to see or click. Size is restored with the rest.

    private readonly Dictionary<IntPtr, Original> _cloaked = [];
    private readonly object _sync = new();
    private bool? _seamlessCache;

    /// <summary>RemoteApp session? Cached: a session never changes kind, and each check walks every process on the server.</summary>
    private bool Seamless => _seamlessCache ??= SessionActions.IsSeamlessSession();
    /// <summary>One cloak / restore pass at a time (the instant-hide hook runs on its own thread).</summary>
    private readonly object _gate = new();
    private readonly int _session = Process.GetCurrentProcess().SessionId;

    public bool Enabled { get; private set; }

    private int _lastSeen = -1;

    public int HiddenCount { get { lock (_sync) return _cloaked.Count; } }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Log.Write($"TSPlus floating panel hiding {(enabled ? "enabled" : "disabled")} by policy.");
        if (enabled)
        {
            InstallShowHook();
            Apply();
        }
        else
        {
            RemoveShowHook();
            RestoreAll();
            RecoverOrphans();
        }
    }

    // ---- Instant cloak: a TSPlus panel window is hidden the moment Windows shows it (no timer wait), so the
    // user never sees it flash at sign-in. Installed on the UI thread (it has the message loop it needs).

    private const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_NAMECHANGE = 0x800C, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private WinEventProc? _showProc;
    private Thread? _hookThread;
    private uint _hookThreadId;
    /// <summary>pid -> matching target (process, title rule) or null when it is not a TSPlus panel process.</summary>
    private readonly Dictionary<uint, (string Process, string? Title)?> _targetPid = [];

    /// <summary>
    /// The hook gets its own thread and message loop: at sign-in the panel's UI thread is busy building the
    /// window, and a hook on it was delivered ~0.5 s late - long enough to see the TSPlus panel flash.
    /// </summary>
    private void InstallShowHook()
    {
        if (_hookThread is not null) return;
        _showProc = OnObjectShown;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            // CREATE..SHOW: the TSPlus panel is made invisible as soon as its window exists, before it is first shown.
            var show = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _showProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            // TSPlus shows its .connect panel first and sets the "Remote App" title right after: catch that too.
            var name = SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _showProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (show != IntPtr.Zero) UnhookWinEvent(show);
            if (name != IntPtr.Zero) UnhookWinEvent(name);
        }) { IsBackground = true, Name = "CloudOrc TSPlus hide hook" };
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
    }

    private void RemoveShowHook()
    {
        if (_hookThread is null) return;
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _hookThread = null;
        _hookThreadId = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private void OnObjectShown(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (!Enabled || evt == EVENT_OBJECT_DESTROY || idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!_targetPid.TryGetValue(pid, out var target))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    target = p.SessionId == _session
                        ? Targets.Cast<(string Process, string? Title)?>().FirstOrDefault(t => string.Equals(t!.Value.Process, p.ProcessName, StringComparison.OrdinalIgnoreCase))
                        : null;
                }
                catch (ArgumentException) { target = null; }
                if (_targetPid.Count > 500) _targetPid.Clear();
                _targetPid[pid] = target;
            }

            // Fast path: hide exactly this window now (a full Apply walks every process on the server: ~0.5 s).
            if (target is { } t && IsTopLevel(hwnd) && TitleMatches(hwnd, t.Title) && (evt == EVENT_OBJECT_CREATE || IsWindowVisible(hwnd)))
            {
                lock (_gate)
                {
                    bool known;
                    lock (_sync) known = _cloaked.ContainsKey(hwnd);
                    if (Enabled && !known) Cloak(hwnd);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("tsplus show hook", ex);
        }
    }

    private static bool IsTopLevel(IntPtr h) => GetAncestor(h, 1 /* GA_PARENT */) == GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>Cloaks any (new) TSPlus panel window of this session. Cheap; called periodically.</summary>
    public void Apply()
    {
        lock (_gate) ApplyCore();
    }

    private void ApplyCore()
    {
        if (!Enabled) return;
        var seamless = Seamless;
        var seen = 0;
        {
            foreach (var (p, title) in TargetProcesses())
            {
                using (p)
                {
                    if (p.SessionId != _session) continue;
                    foreach (var h in TopLevelWindows(p.Id))
                    {
                        if (!TitleMatches(h, title)) continue;
                        seen++;
                        bool known;
                        lock (_sync) known = _cloaked.ContainsKey(h);
                        if (known)
                        {
                            // TSPlus may redraw or re-create the window shape: keep its visible area empty.
                            var test = CreateRectRgn(0, 0, 0, 0);
                            var kind = GetWindowRgn(h, test);
                            DeleteObject(test);
                            if (kind != NULLREGION)
                            {
                                var empty = CreateRectRgn(0, 0, 0, 0);
                                if (!SetWindowRgn(h, empty, true)) DeleteObject(empty);
                            }

                            if (seamless && GetWindowRect(h, out var cur) && (cur.Right - cur.Left > 2 || cur.Bottom - cur.Top > 2))
                            {
                                SetWindowPos(h, IntPtr.Zero, 0, 0, 1, 1, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                            }

                            continue;
                        }

                        if (!IsWindowVisible(h)) continue;
                        Cloak(h);
                    }
                }
            }
        }
        if (seen != _lastSeen)
        {
            _lastSeen = seen;
            Log.Write($"TSPlus panel windows in this session: {seen} (seamless={seamless}).");
        }
    }

    /// <summary>
    /// A TSPlus panel (FloatingPanel, or the "Remote App" panel of .connect / HTML5 sessions) exists in this
    /// session — visible or already hidden by us. Means the user works from a launcher panel.
    /// </summary>
    public bool HasPanelWindow()
    {
        lock (_sync)
        {
            if (_cloaked.Keys.Any(IsWindow)) return true;
        }

        foreach (var (p, title) in TargetProcesses())
        {
            using (p)
            {
                if (p.SessionId != _session) continue;
                if (TopLevelWindows(p.Id).Any(h => IsWindowVisible(h) && TitleMatches(h, title))) return true;
            }
        }

        return false;
    }

    /// <summary>Restores every window this panel cloaked (policy off / exit / crash).</summary>
    public void RestoreAll()
    {
        lock (_gate) RestoreAllCore();
    }

    private void RestoreAllCore()
    {
        KeyValuePair<IntPtr, Original>[] items;
        lock (_sync)
        {
            items = [.. _cloaked];
            _cloaked.Clear();
        }

        foreach (var (h, o) in items)
        {
            if (!IsWindow(h)) continue;
            if (o.WasLayered)
            {
                SetLayeredWindowAttributes(h, o.Key, o.Alpha, o.Flags);
            }

            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(o.ExStyle));
            SetWindowRgn(h, IntPtr.Zero, true);
            if (o.Shrunk)
            {
                SetWindowPos(h, IntPtr.Zero, 0, 0, o.Width, o.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            else
            {
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME);
        }

        if (items.Length > 0) Log.Write($"Restored {items.Length} TSPlus floating panel window(s).");
    }

    /// <summary>
    /// Restores TSPlus panel windows left transparent by a previous panel process that was killed
    /// (a hard kill cannot run <see cref="RestoreAll"/>). Called at start-up and when the policy is off.
    /// </summary>
    public int RecoverOrphans()
    {
        lock (_gate) return RecoverOrphansCore();
    }

    private int RecoverOrphansCore()
    {
        var fixedCount = 0;
        {
            foreach (var (p, title) in TargetProcesses())
            {
                using (p)
                {
                    if (p.SessionId != _session) continue;
                    foreach (var h in TopLevelWindows(p.Id))
                    {
                        if (!TitleMatches(h, title)) continue;
                        lock (_sync)
                        {
                            if (_cloaked.ContainsKey(h)) continue;
                        }

                        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
                        if ((ex & WS_EX_LAYERED) == 0 || (ex & WS_EX_TRANSPARENT) == 0) continue;
                        if (!GetLayeredWindowAttributes(h, out _, out var alpha, out var flags) || alpha != 0 || (flags & LWA_ALPHA) == 0) continue;
                        SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
                        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT)));
                        SetWindowRgn(h, IntPtr.Zero, true);
                        // A window shrunk by a killed panel lost its size: give it the TSPlus panel's usual size.
                        var tiny = GetWindowRect(h, out var r) && r.Right - r.Left <= 2 && r.Bottom - r.Top <= 2;
                        SetWindowPos(h, IntPtr.Zero, 0, 0, tiny ? 200 : 0, tiny ? 210 : 0, (tiny ? 0 : SWP_NOSIZE) | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                        RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME);
                        fixedCount++;
                    }
                }
            }
        }

        if (fixedCount > 0) Log.Write($"Recovered {fixedCount} TSPlus floating panel window(s) left hidden by a previous panel.");
        return fixedCount;
    }

    private void Cloak(IntPtr h)
    {
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        var wasLayered = (ex & WS_EX_LAYERED) != 0;
        byte alpha = 255;
        uint key = 0, flags = 0;
        if (wasLayered)
        {
            GetLayeredWindowAttributes(h, out key, out alpha, out flags);
        }

        var shrink = Seamless;
        GetWindowRect(h, out var rect);
        lock (_sync) _cloaked[h] = new Original(ex, wasLayered, alpha, key, flags, rect.Right - rect.Left, rect.Bottom - rect.Top, shrink);
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));
        if (!SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA))
        {
            // Could not make it transparent (e.g. higher-integrity window): undo and leave it alone.
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
            lock (_sync) _cloaked.Remove(h);
            Log.Write("Could not hide a TSPlus floating panel window; left unchanged.");
            return;
        }

        // Empty region on top of transparency: needed in seamless sessions (see above), harmless on a desktop.
        // The system owns the region handle after a successful SetWindowRgn.
        var rgn = CreateRectRgn(0, 0, 0, 0);
        if (!SetWindowRgn(h, rgn, true)) DeleteObject(rgn);
        if (shrink)
        {
            SetWindowPos(h, IntPtr.Zero, 0, 0, 1, 1, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            Log.Write($"Seamless: TSPlus panel window shrunk (was {rect.Right - rect.Left}x{rect.Bottom - rect.Top}).");
        }
    }

    private static List<IntPtr> TopLevelWindows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private const uint RDW_INVALIDATE = 0x1, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const int NULLREGION = 1;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hWnd, IntPtr rgn);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);
}
