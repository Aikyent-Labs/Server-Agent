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
            Apply();
        }
        else
        {
            RestoreAll();
            RecoverOrphans();
        }
    }

    /// <summary>Cloaks any (new) TSPlus panel window of this session. Cheap; called periodically.</summary>
    public void Apply()
    {
        if (!Enabled) return;
        var seamless = SessionActions.IsSeamlessSession();
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

        var shrink = SessionActions.IsSeamlessSession();
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
