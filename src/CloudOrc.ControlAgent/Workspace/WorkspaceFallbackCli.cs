using System.Security.Principal;
using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// Local fallback switch for a server administrator (runbook: CloudOrc cannot reach the server, or the
/// team asks the customer's admin to act on the server). Writes %ProgramData%\CloudOrc\Workspace\fallback-local.json;
/// the panel applies it within 5 s and the Watch within 10 s - no agent, internet or restart needed.
///
///   CloudOrc.ControlAgent.exe workspace fallback status [--user NAME]
///   CloudOrc.ControlAgent.exe workspace fallback on  --reason "TEXT" [--user NAME] [--alt-tab-only | --keep-tsplus-only]
///   CloudOrc.ControlAgent.exe workspace fallback off [--user NAME] [--all]
/// </summary>
public static class WorkspaceFallbackCli
{
    public static int Run(string[] args, string? localPath = null, string? teamPath = null, Func<bool>? isAdmin = null)
    {
        localPath ??= WorkspaceFallbackDocument.LocalPath;
        teamPath ??= WorkspaceFallbackDocument.TeamPath;
        isAdmin ??= IsElevatedAdministrator;

        if (args.Length < 2 || !args[0].Equals("fallback", StringComparison.OrdinalIgnoreCase))
        {
            return Usage();
        }

        var verb = args[1].ToLowerInvariant();
        var user = Option(args, "--user");
        var reason = Option(args, "--reason");
        var all = Flag(args, "--all");
        var altTabOnly = Flag(args, "--alt-tab-only");
        var keepTsplusOnly = Flag(args, "--keep-tsplus-only");

        switch (verb)
        {
            case "status":
                return Status(localPath, teamPath, user);

            case "on":
            {
                if (!isAdmin()) return NotAdmin();
                if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
                {
                    Console.Error.WriteLine("A reason is required (at least 5 characters): --reason \"why\".");
                    return 2;
                }

                if (altTabOnly && keepTsplusOnly)
                {
                    Console.Error.WriteLine("Use either --alt-tab-only or --keep-tsplus-only, not both.");
                    return 2;
                }

                var doc = WorkspaceFallback.Load(localPath) ?? new WorkspaceFallbackDocument();
                var rules = doc.Rules.Where(r => !SameTarget(r.Username, user)).ToList();
                rules.Add(new WorkspaceFallbackRule
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Scope = string.IsNullOrWhiteSpace(user) ? WorkspaceFallbackScopes.Server : WorkspaceFallbackScopes.User,
                    Username = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
                    PanelOff = !altTabOnly && !keepTsplusOnly,
                    AltTabOff = altTabOnly,
                    HideTsplusOff = keepTsplusOnly,
                    Reason = reason.Trim()[..Math.Min(300, reason.Trim().Length)]
                });
                Save(localPath, rules);
                var what = altTabOnly ? "Alt+Tab is the Windows switcher" : keepTsplusOnly ? "the TSPlus panel is never hidden" : "the CloudOrc panel is off (default Windows / TSPlus)";
                Console.WriteLine($"Local fallback ON for {(user is null ? "every user on this server" : user)}: {what}. Applied within 10 seconds.");
                return 0;
            }

            case "off":
            {
                if (!isAdmin()) return NotAdmin();
                var doc = WorkspaceFallback.Load(localPath) ?? new WorkspaceFallbackDocument();
                var keep = all ? [] : doc.Rules.Where(r => !SameTarget(r.Username, user)).ToList();
                var removed = doc.Rules.Count - keep.Count;
                Save(localPath, keep);
                Console.WriteLine(removed == 0
                    ? "No matching local fallback switch was on."
                    : $"Local fallback OFF ({removed} switch(es) removed). The CloudOrc panel comes back within a minute unless a CloudOrc team switch is still on.");
                return 0;
            }

            default:
                return Usage();
        }
    }

    private static int Status(string localPath, string teamPath, string? user)
    {
        var team = WorkspaceFallback.Load(teamPath);
        var local = WorkspaceFallback.Load(localPath);
        Console.WriteLine("CloudOrc Remote Workspace - fallback switches on this server");
        Print("CloudOrc team (synced)", team);
        Print("Local (server administrator)", local);
        if (!string.IsNullOrWhiteSpace(user))
        {
            var d = WorkspaceFallback.Resolve(user, (WorkspaceFallbackSources.Team, team), (WorkspaceFallbackSources.Local, local));
            Console.WriteLine(d.Any
                ? $"Effective for {user}: panelOff={d.PanelOff} altTabOff={d.AltTabOff} keepTsplus={d.HideTsplusOff} (source {d.Source}: {d.Reason})"
                : $"Effective for {user}: normal (CloudOrc panel on)");
        }

        return 0;
    }

    private static void Print(string title, WorkspaceFallbackDocument? doc)
    {
        Console.WriteLine($"  {title}: {(doc is null || doc.Rules.Count == 0 ? "none" : $"{doc.Rules.Count} switch(es), updated {doc.UpdatedAt:u}")}");
        foreach (var r in doc?.Rules ?? [])
        {
            var what = r.PanelOff ? "panel off" : string.Join(", ", new[] { r.AltTabOff ? "Alt+Tab off" : null, r.HideTsplusOff ? "TSPlus panel kept" : null }.Where(x => x is not null));
            Console.WriteLine($"    - {r.Scope ?? "?"} {(r.Username is null ? "all users" : r.Username)}: {what} - {r.Reason}");
        }
    }

    private static void Save(string path, List<WorkspaceFallbackRule> rules) =>
        WorkspaceFallback.Save(path, new WorkspaceFallbackDocument
        {
            Revision = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = Environment.UserName,
            Rules = rules
        });

    private static bool SameTarget(string? ruleUser, string? user) =>
        string.IsNullOrWhiteSpace(user)
            ? string.IsNullOrWhiteSpace(ruleUser)
            : string.Equals(WorkspaceFallback.BareUser(ruleUser), WorkspaceFallback.BareUser(user), StringComparison.OrdinalIgnoreCase);

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Flag(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsElevatedAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var me = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int NotAdmin()
    {
        Console.Error.WriteLine("Run this from an elevated (Administrator) command prompt on the server.");
        return 5;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Usage:
              CloudOrc.ControlAgent.exe workspace fallback status [--user NAME]
              CloudOrc.ControlAgent.exe workspace fallback on --reason "TEXT" [--user NAME] [--alt-tab-only | --keep-tsplus-only]
              CloudOrc.ControlAgent.exe workspace fallback off [--user NAME] [--all]

            on   : default Windows / TSPlus experience (CloudOrc panel off) for every user, or one user.
                   --alt-tab-only      only give Alt+Tab back to Windows (panel stays)
                   --keep-tsplus-only  only stop hiding the TSPlus floating panel (panel stays)
            off  : remove the local switch for every user (no --user), one user, or all local switches (--all).
            CloudOrc team switches (admin panel) are separate and are shown by status.
            """);
        return 1;
    }
}
