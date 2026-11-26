using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Workspace;

// Remote Workspace fallback ("go back to the default Windows / TSPlus experience").
//
// Self-contained on purpose: CloudOrc.WorkspaceUI links this file as source (like WorkspaceProtocol.cs).
//
// Two switch files in %ProgramData%\CloudOrc\Workspace (both optional):
//   fallback-team.json   written by the Control Agent from CloudOrc (admin panel kill switch, synced)
//   fallback-local.json  written on the server by an administrator (`CloudOrc.ControlAgent.exe workspace ...`)
//                        for when CloudOrc cannot reach the server
// A file is trusted only when it is owned by SYSTEM or the Administrators group: a Windows user cannot
// switch their own panel off by dropping a file. Each file keeps its last value while CloudOrc is
// unreachable (sticky), so a switch the team set stays in force offline.

/// <summary>Where a fallback decision came from.</summary>
public static class WorkspaceFallbackSources
{
    /// <summary>CloudOrc team (admin panel switch: global / partner / server / user).</summary>
    public const string Team = "TEAM";

    /// <summary>An administrator on the server itself (local switch).</summary>
    public const string Local = "LOCAL";
}

/// <summary>Scopes of a team switch (information only on the server: the backend already resolved them).</summary>
public static class WorkspaceFallbackScopes
{
    public const string Global = "GLOBAL";
    public const string Partner = "PARTNER";
    public const string Server = "SERVER";
    public const string User = "USER";
}

/// <summary>One switch. <see cref="Username"/> null = every Windows user on this server.</summary>
public sealed class WorkspaceFallbackRule
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }

    /// <summary>The CloudOrc panel does not run at all: Windows desktop / TSPlus panel as without CloudOrc.</summary>
    [JsonPropertyName("panelOff")] public bool PanelOff { get; init; }

    /// <summary>Alt+Tab is the Windows switcher again (the panel keeps running).</summary>
    [JsonPropertyName("altTabOff")] public bool AltTabOff { get; init; }

    /// <summary>The TSPlus floating panel is never hidden (the panel keeps running).</summary>
    [JsonPropertyName("hideTsplusOff")] public bool HideTsplusOff { get; init; }

    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public sealed class WorkspaceFallbackDocument
{
    public const string TeamFileName = "fallback-team.json";
    public const string LocalFileName = "fallback-local.json";

    [JsonPropertyName("revision")] public string? Revision { get; init; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("updatedBy")] public string? UpdatedBy { get; init; }
    [JsonPropertyName("rules")] public List<WorkspaceFallbackRule> Rules { get; init; } = [];

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudOrc", "Workspace");

    public static string TeamPath => Path.Combine(Directory, TeamFileName);
    public static string LocalPath => Path.Combine(Directory, LocalFileName);
}

/// <summary>Effective fallback for one Windows user. <see cref="None"/> = the CloudOrc panel runs normally.</summary>
public sealed record WorkspaceFallbackDecision(bool PanelOff, bool AltTabOff, bool HideTsplusOff, string? Source, string? Reason)
{
    public static readonly WorkspaceFallbackDecision None = new(false, false, false, null, null);

    public bool Any => PanelOff || AltTabOff || HideTsplusOff;

    /// <summary>Health reason code for a panel that is off because of a switch.</summary>
    public string ReasonCode => Source == WorkspaceFallbackSources.Local ? WorkspaceReasonCodes.FallbackLocal : WorkspaceReasonCodes.FallbackTeam;
}

public static class WorkspaceFallback
{
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>Effective decision for <paramref name="username"/> from the switch files on this server.</summary>
    public static WorkspaceFallbackDecision ForUser(string? username) =>
        Resolve(username, (WorkspaceFallbackSources.Team, Load(WorkspaceFallbackDocument.TeamPath)), (WorkspaceFallbackSources.Local, Load(WorkspaceFallbackDocument.LocalPath)));

    /// <summary>
    /// Pure merge (unit-tested): any matching rule turns a feature off (OR). Panel off implies the other two.
    /// The first source that turns the panel off (else anything off) names the decision.
    /// </summary>
    public static WorkspaceFallbackDecision Resolve(string? username, params (string Source, WorkspaceFallbackDocument? Doc)[] docs)
    {
        var user = BareUser(username);
        bool panel = false, altTab = false, hide = false;
        string? source = null, reason = null;
        var sourceIsPanel = false;
        foreach (var (src, doc) in docs)
        {
            foreach (var r in doc?.Rules ?? [])
            {
                if (r is null || !(r.PanelOff || r.AltTabOff || r.HideTsplusOff))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(r.Username) && !string.Equals(BareUser(r.Username), user, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                panel |= r.PanelOff;
                altTab |= r.AltTabOff || r.PanelOff;
                hide |= r.HideTsplusOff || r.PanelOff;
                if (source is null || (r.PanelOff && !sourceIsPanel))
                {
                    source = src;
                    reason = r.Reason;
                    sourceIsPanel = r.PanelOff;
                }
            }
        }

        return source is null ? WorkspaceFallbackDecision.None : new WorkspaceFallbackDecision(panel, altTab, hide, source, reason);
    }

    /// <summary>"DOMAIN\user" / "user@domain" -> "user".</summary>
    public static string BareUser(string? username)
    {
        var u = (username ?? string.Empty).Trim();
        var slash = u.LastIndexOf('\\');
        if (slash >= 0) u = u[(slash + 1)..];
        var at = u.IndexOf('@');
        if (at > 0) u = u[..at];
        return u;
    }

    /// <summary>Reads a switch file; null when missing, unreadable, malformed or not owned by SYSTEM / Administrators.</summary>
    public static WorkspaceFallbackDocument? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsTrustedOwner(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WorkspaceFallbackDocument>(File.ReadAllText(path), WorkspaceProtocol.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically writes a switch file readable by everyone, writable only by SYSTEM / Administrators.
    /// Caller must be SYSTEM or an elevated administrator. Empty rules delete the file.
    /// </summary>
    public static void Save(string path, WorkspaceFallbackDocument doc)
    {
        var dir = Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(dir);
        if (doc.Rules.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, WorkspaceProtocol.JsonOptions));
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Read, AccessControlType.Allow));
        try
        {
            sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            new FileInfo(tmp).SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            // Owner change needs elevation: keep the ACL (owner is then the elevated writer, still trusted when SYSTEM/admin group).
            var noOwner = new FileSecurity();
            noOwner.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, false, typeof(SecurityIdentifier))) noOwner.AddAccessRule(rule);
            new FileInfo(tmp).SetAccessControl(noOwner);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static bool IsTrustedOwner(string path)
    {
        var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
        return owner is SystemSid or AdministratorsSid;
    }
}
