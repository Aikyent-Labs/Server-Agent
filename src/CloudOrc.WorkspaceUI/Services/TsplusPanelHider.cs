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

    /// <summary>Process names of the TSPlus floating panel (FloatingPanel.exe in TSplus\UserDesktop\files).</summary>
    private static readonly string[] ProcessNames = ["floatingpanel"];

    private sealed record Original(long ExStyle, bool WasLayered, byte Alpha, uint Key, uint Flags);

    private readonly Dictionary<IntPtr, Original> _cloaked = [];
    private readonly object _sync = new();
    private readonly int _session = Process.GetCurrentProcess().SessionId;

    public bool Enabled { get; private set; }

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
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.SessionId != _session) continue;
                    foreach (var h in TopLevelWindows(p.Id))
                    {
                        lock (_sync)
                        {
                            if (_cloaked.ContainsKey(h) || !IsWindowVisible(h)) continue;
                        }

                        Cloak(h);
                    }
                }
            }
        }
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
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.SessionId != _session) continue;
                    foreach (var h in TopLevelWindows(p.Id))
                    {
                        lock (_sync)
                        {
                            if (_cloaked.ContainsKey(h)) continue;
                        }

                        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
                        if ((ex & WS_EX_LAYERED) == 0 || (ex & WS_EX_TRANSPARENT) == 0) continue;
                        if (!GetLayeredWindowAttributes(h, out _, out var alpha, out var flags) || alpha != 0 || (flags & LWA_ALPHA) == 0) continue;
                        SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
                        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT)));
                        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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

        lock (_sync) _cloaked[h] = new Original(ex, wasLayered, alpha, key, flags);
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));
        if (!SetLayeredWindowAttributes(h, 0, 0, LWA_ALPHA))
        {
            // Could not make it transparent (e.g. higher-integrity window): undo and leave it alone.
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
            lock (_sync) _cloaked.Remove(h);
            Log.Write("Could not hide a TSPlus floating panel window; left unchanged.");
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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);
}
