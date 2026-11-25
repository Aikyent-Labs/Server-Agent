using System.Runtime.InteropServices;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Same-session, same-user command to the running panel ("open:apps", "close", "switcher") via
/// WM_COPYDATA to its window. Used by a Start/desktop shortcut and by accessibility tools. Window
/// messages cannot cross Windows sessions, so another user can never drive this panel.
/// </summary>
public static class PanelCommand
{
    public const string WindowTitle = "CloudOrc Remote Workspace";
    public const int WM_COPYDATA = 0x004A;
    private static readonly IntPtr Signature = new(0xC10D0C);

    public static bool SendToRunningPanel(string command)
    {
        var hwnd = FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var data = Marshal.StringToHGlobalUni(command);
        try
        {
            var cds = new COPYDATASTRUCT { dwData = Signature, cbData = (command.Length + 1) * 2, lpData = data };
            SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds, 0x0002 /* SMTO_ABORTIFHUNG */, 3000, out _);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Decodes a WM_COPYDATA lParam sent by <see cref="SendToRunningPanel"/>; null for anything else.</summary>
    public static string? Decode(IntPtr lParam)
    {
        try
        {
            var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
            if (cds.dwData != Signature || cds.cbData <= 0 || cds.cbData > 512) return null;
            return Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2)?.TrimEnd('\0');
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeout, out IntPtr result);
}
