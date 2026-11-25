using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>Launches a published app inside this user session (the app list itself is server-authorized).</summary>
public static class AppLauncher
{
    public static bool TryLaunch(PublishedApp app, out string? error)
    {
        error = null;
        try
        {
            ProcessStartInfo psi;
            if (app.IsFolder)
            {
                var folder = string.IsNullOrWhiteSpace(app.StartupPath) ? app.ExePath : app.StartupPath;
                psi = new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true };
            }
            else
            {
                if (!File.Exists(app.ExePath))
                {
                    error = $"{app.Name} is not installed on this server.";
                    return false;
                }

                psi = new ProcessStartInfo(app.ExePath)
                {
                    UseShellExecute = true,
                    Arguments = app.Arguments ?? string.Empty,
                    WorkingDirectory = !string.IsNullOrWhiteSpace(app.StartupPath) && Directory.Exists(app.StartupPath)
                        ? app.StartupPath
                        : Path.GetDirectoryName(app.ExePath) ?? string.Empty
                };
            }

            using var p = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"launch {app.Name}", ex);
            error = $"{app.Name} could not be started.";
            return false;
        }
    }
}

/// <summary>Shell icons for executables / folders (cached; frozen so any thread can use them).</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? path, bool folder = false)
    {
        var key = folder ? "<folder>" : path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }

        ImageSource? img = null;
        try
        {
            var info = new SHFILEINFO();
            var flags = SHGFI_ICON | SHGFI_LARGEICON;
            uint attrs = 0;
            var target = path ?? string.Empty;
            if (folder || !File.Exists(target))
            {
                flags |= SHGFI_USEFILEATTRIBUTES;
                attrs = folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                target = folder ? "folder" : Path.GetFileName(target);
            }

            if (SHGetFileInfo(target, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"icon for {key}: {ex.Message}");
        }

        lock (Cache)
        {
            Cache[key] = img;
        }

        return img;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint attrs, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>Remote session actions available to the user (RW-09: sign-out is separate from disconnect).</summary>
public static class SessionActions
{
    public static int CurrentSessionId => Process.GetCurrentProcess().SessionId;

    /// <summary>
    /// True in a RemoteApp (seamless) session: Windows runs its RemoteApp shell (rdpinit.exe / rdpshell.exe)
    /// in place of a desktop. That is how TSPlus ".connect" Remote App clients, .rdp files in RemoteApp
    /// mode and HTML5 RemoteApp sessions arrive; a full desktop session never has these.
    /// </summary>
    public static bool IsSeamlessSession()
    {
        var session = CurrentSessionId;
        foreach (var name in SeamlessShellProcesses)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.SessionId == session) return true;
                }
            }
        }

        return false;
    }

    private static readonly string[] SeamlessShellProcesses = ["rdpinit", "rdpshell"];

    /// <summary>Disconnect keeps every app running; the user can reconnect later.</summary>
    public static bool Disconnect() => WTSDisconnectSession(IntPtr.Zero, -1, false);

    /// <summary>Sign out closes the user's apps (unsaved work may be lost — the UI warns first).</summary>
    public static bool SignOut() => ExitWindowsEx(0 /* EWX_LOGOFF */, 0);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool wait);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}

/// <summary>
/// Heartbeat/state file the Watch reads (%ProgramData%\CloudOrc\Workspace\sessions\session-N.json).
/// Tells the Watch whether the panel is running, or deliberately not running (disabled / user exit).
/// </summary>
public sealed class SessionStateFile(int sessionId, string username)
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public void Write(string state)
    {
        try
        {
            Directory.CreateDirectory(WorkspaceSessionState.Directory);
            var doc = new WorkspaceSessionState
            {
                SessionId = sessionId,
                ProcessId = Environment.ProcessId,
                Username = username,
                Version = AppInfo.Version,
                State = state,
                HeartbeatAt = DateTimeOffset.UtcNow,
                StartedAt = _startedAt
            };
            // Overwrite in place (users may modify only files they own; no rename needed).
            File.WriteAllText(WorkspaceSessionState.PathFor(sessionId), JsonSerializer.Serialize(doc, WorkspaceProtocol.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write($"state file: {ex.Message}");
        }
    }
}

/// <summary>Non-secret per-user cache so the panel can show apps immediately and while offline.</summary>
public static class LocalCache
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudOrc", "WorkspaceUI", "cache.json");

    public sealed class Snapshot
    {
        public WorkspaceContext? Context { get; set; }
        public List<PublishedApp> Apps { get; set; } = [];
        public DateTimeOffset SavedAt { get; set; }
    }

    public static Snapshot? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(FilePath)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WorkspaceContext? context, List<PublishedApp> apps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Snapshot { Context = context, Apps = apps, SavedAt = DateTimeOffset.UtcNow }));
        }
        catch
        {
            // best effort
        }
    }
}
