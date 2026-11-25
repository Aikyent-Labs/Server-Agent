using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// The user's TSPlus-published applications and folders, read from the server's own TSPlus
/// publication (AppControl.ini) — the exact list the TSPlus floating panel shows (handoff §43:
/// "published applications are driven by the existing CloudOrc/TSPlus publishing configuration; the
/// workspace must not create a second application catalog").
///
/// An entry applies to the user when its users= line names them, its groups= line names one of their
/// Windows groups, or all_users=yes. TSPlus' own shell entries (FloatingPanel, Application Panel, Remote
/// Taskbar, Remote Desktop) are left out. Folders are TSPlus folder-launcher entries (folder.exe /
/// CloudeOpenVirtualFolder) and are opened through that same launcher — never explorer.exe, which the
/// Floating Panel lockdown blocks.
/// </summary>
public static class TsplusAppSource
{
    public const string IdPrefix = "tsplus:";

    private static readonly string[] IniCandidates =
    [
        @"C:\Program Files (x86)\TSplus\UserDesktop\files\AppControl.ini",
        @"C:\Program Files\TSplus\UserDesktop\files\AppControl.ini",
    ];

    public static List<PublishedApp> ForCurrentUser()
    {
        try
        {
            var ini = IniCandidates.FirstOrDefault(File.Exists);
            if (ini is null) return [];
            var groups = CurrentUserGroupNames();
            return ForUser(File.ReadAllLines(ini), Environment.UserName, groups);
        }
        catch (Exception ex)
        {
            Log.Write($"TSPlus app list could not be read: {ex.Message}");
            return [];
        }
    }

    /// <summary>Pure: parse AppControl.ini lines and keep the entries this user gets.</summary>
    internal static List<PublishedApp> ForUser(IEnumerable<string> lines, string userName, IReadOnlyCollection<string> userGroups)
    {
        var me = Bare(userName);
        var groups = new HashSet<string>(userGroups.Select(Bare), StringComparer.OrdinalIgnoreCase);
        var result = new List<PublishedApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (section, props) in Sections(lines))
        {
            if (!section.StartsWith("App", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Get(props, "appname");
            var path = Unquote(Get(props, "path"));
            var startup = Unquote(Get(props, "startup"));
            var cmdline = Get(props, "cmdline");
            if (string.IsNullOrWhiteSpace(path) || IsTsplusShell(name, path)) continue;

            var applies = IsYes(Get(props, "all_users"))
                || SplitNames(Get(props, "users")).Any(u => string.Equals(Bare(u), me, StringComparison.OrdinalIgnoreCase))
                || SplitNames(Get(props, "groups")).Any(g => groups.Contains(Bare(g)));
            if (!applies) continue;

            var isFolder = IsFolderLauncher(path);
            if (isFolder && string.IsNullOrWhiteSpace(Unquote(cmdline)) && string.IsNullOrWhiteSpace(startup)) continue; // no folder to open
            if (!seen.Add($"{path}|{cmdline}")) continue;

            result.Add(new PublishedApp
            {
                Id = IdPrefix + section,
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim(),
                Type = isFolder ? "folder" : "application",
                ExePath = path,
                StartupPath = startup,
                Arguments = cmdline,
            });
        }

        return result;
    }

    internal static bool IsFolderLauncher(string path)
    {
        var file = Path.GetFileName(path).ToLowerInvariant();
        return file is "folder.exe" or "cloudeopenvirtualfolder.lnk" or "cloudeopenvirtualfolder.cmd" or "cloudeopenvirtualfolder.exe";
    }

    private static bool IsTsplusShell(string name, string path)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("floating") && n.Contains("panel")) return true;
        if (n.Contains("application panel") || n.Contains("remote taskbar") || n.Contains("remote desktop") || n.Contains("full desktop")) return true;
        var file = Path.GetFileName(path).ToLowerInvariant();
        return file is "floatingpanel.exe" or "applicationpanel.exe" or "remotetaskbar.exe" or "mstsc.exe"
            || file.Contains("cloudefloatingpanel") || file == "floatingpanel.cmd";
    }

    private static IEnumerable<(string Section, Dictionary<string, string> Props)> Sections(IEnumerable<string> lines)
    {
        string? current = null;
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (current is not null) yield return (current, props);
                current = line[1..^1].Trim();
                props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (current is null || eq <= 0) continue;
            props[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        if (current is not null) yield return (current, props);
    }

    private static string Get(Dictionary<string, string> p, string key) => p.TryGetValue(key, out var v) ? v : string.Empty;

    private static string Unquote(string v) => v.Trim().Trim('"').Trim();

    private static bool IsYes(string v) => v.Trim().ToLowerInvariant() is "yes" or "true" or "1";

    private static IEnumerable<string> SplitNames(string line) =>
        line.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Bare(string name)
    {
        var n = (name ?? string.Empty).Trim();
        var i = n.LastIndexOf('\\');
        return (i >= 0 ? n[(i + 1)..] : n).Trim();
    }

    /// <summary>Names of the user's Windows groups (local and domain), including deny-only ones.</summary>
    private static List<string> CurrentUserGroupNames()
    {
        var names = new List<string>();
        using var identity = WindowsIdentity.GetCurrent();
        GetTokenInformation(identity.Token, 2 /* TokenGroups */, IntPtr.Zero, 0, out var size);
        if (size <= 0) return names;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.Token, 2, buffer, size, out _)) return names;
            var count = Marshal.ReadInt32(buffer);
            var entrySize = IntPtr.Size * 2;
            var first = buffer + IntPtr.Size;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var sid = new SecurityIdentifier(Marshal.ReadIntPtr(first + i * entrySize));
                    names.Add(((NTAccount)sid.Translate(typeof(NTAccount))).Value);
                }
                catch
                {
                    // unresolvable SID (logon SID etc.): not a group name TSPlus can list
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return names;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);
}
