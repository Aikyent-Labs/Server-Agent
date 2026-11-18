using System.Text;
using System.Text.RegularExpressions;
using CloudOrc.Agent.Contracts.Sync;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Commands;

namespace CloudOrc.ControlAgent.Sync;

/// <summary>
/// Local re-validation of a sync policy (§08 "Root selection: authorized administrator selects explicit
/// allowed folder roots; reject system/secret paths by policy; canonicalize path and block
/// traversal/junction escapes"). The backend validated it too - the agent is the last line of defence.
/// </summary>
public static class SyncPathPolicy
{
    private static readonly Regex[] ForbiddenPatterns =
    [
        new(@"^[a-z]:\\windows(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]:\\program files( \(x86\))?(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]:\\programdata(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]:\\users\\[^\\]+\\appdata(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]:\\\$recycle\.bin(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]:\\system volume information(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^[a-z]:\\recovery(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\\cloudorc(\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>Errors for a policy (empty = valid). Paths are canonicalized in place by the caller via <see cref="Canonical"/>.</summary>
    public static IReadOnlyList<string> Validate(SyncPolicyDocument? policy, string? agentDataDirectory = null)
    {
        var errors = new List<string>();
        if (policy is null)
        {
            return ["policy is required."];
        }

        if (!BackupPolicyValidator.IsSafeId(policy.ConnectionId))
        {
            errors.Add("connectionId must be 1-128 safe characters.");
        }

        if (policy.Direction != "UPLOAD_ONLY")
        {
            errors.Add("Only one-way UPLOAD_ONLY sync is supported.");
        }

        if (policy.State is not ("ACTIVE" or "PAUSED"))
        {
            errors.Add($"Unknown state '{policy.State}'.");
        }

        if (policy.Roots.Count is 0 or > 64)
        {
            errors.Add("1-64 roots are required.");
        }

        if (policy.RescanMinutes is < 1 or > 7 * 24 * 60)
        {
            errors.Add("rescanMinutes must be 1-10080.");
        }

        var allowed = DiscoveryBrowseHandler.NormalizeRoots(policy.AllowedRoots);
        var seen = new List<string>();
        foreach (var root in policy.Roots)
        {
            if (!BackupPolicyValidator.IsSafeId(root.RootId))
            {
                errors.Add("rootId is invalid.");
                continue;
            }

            var why = RejectReason(root.Path, allowed, agentDataDirectory);
            if (why is not null)
            {
                errors.Add($"{Trim(root.Path)}: {why}");
                continue;
            }

            var canonical = Canonical(root.Path)!;
            if (seen.Any(s => DiscoveryBrowseHandler.IsSameOrUnder(canonical, s) || DiscoveryBrowseHandler.IsSameOrUnder(s, canonical)))
            {
                errors.Add($"{Trim(root.Path)}: overlaps another synced folder.");
            }

            seen.Add(canonical);
            if (root.Excludes.Count > 256 || root.Excludes.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 260 || e.Contains("..", StringComparison.Ordinal)))
            {
                errors.Add($"{Trim(root.Path)}: invalid excludes.");
            }
        }

        return errors;
    }

    /// <summary>Canonical absolute path ("D:\Tally\Data") or null when it is not a safe local folder path.</summary>
    public static string? Canonical(string? path)
    {
        if (!BackupPolicyValidator.IsAllowedLocalPath(path) || !DiscoveryBrowseHandler.TryNormalizeLocalPath(path, out var full))
        {
            return null;
        }

        return full;
    }

    /// <summary>Why a root may not be synced (null = allowed). Checks existence and link escapes too.</summary>
    public static string? RejectReason(string? path, IReadOnlyList<string> allowedRoots, string? agentDataDirectory)
    {
        var p = Canonical(path);
        if (p is null)
        {
            return "must be an absolute local folder path (no UNC, device, relative or '..' paths)";
        }

        if (p.Length <= 3)
        {
            return "a whole drive cannot be synced - choose a folder";
        }

        if (ForbiddenPatterns.Any(r => r.IsMatch(p)))
        {
            return "system, program and agent folders cannot be synced";
        }

        if (agentDataDirectory is not null && DiscoveryBrowseHandler.TryNormalizeLocalPath(agentDataDirectory, out var data)
            && (DiscoveryBrowseHandler.IsSameOrUnder(p, data) || DiscoveryBrowseHandler.IsSameOrUnder(data, p)))
        {
            return "the agent's own data folder cannot be synced";
        }

        if (allowedRoots.Count > 0 && !allowedRoots.Any(r => DiscoveryBrowseHandler.IsSameOrUnder(p, r)))
        {
            return "outside the folders this customer may use on this server";
        }

        if (Directory.Exists(p) && DiscoveryBrowseHandler.PassesThroughLink(p))
        {
            return "linked folders (junctions/symbolic links) cannot be synced";
        }

        return null;
    }

    private static string Trim(string? v) => v is null ? "" : v.Length > 80 ? v[..80] + "..." : v;
}

/// <summary>
/// Exclusion globs relative to the root (case-insensitive): <c>*</c>, <c>?</c>, <c>**</c>; a pattern without
/// a slash matches a name at any depth ("*.tmp", "Backup"). Same semantics as the backup worker.
/// </summary>
public sealed class SyncGlob
{
    private readonly List<Regex> _patterns = [];

    public SyncGlob(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw.Replace('\\', '/').Trim().Trim('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            var anyDepth = !pattern.Contains('/', StringComparison.Ordinal);
            var sb = new StringBuilder(anyDepth ? "(^|.*/)" : "^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (ch == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                    }
                }
                else if (ch == '*')
                {
                    sb.Append("[^/]*");
                }
                else if (ch == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(ch.ToString()));
                }
            }

            sb.Append("(/.*)?$");
            _patterns.Add(new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        }
    }

    /// <summary>Always skipped: Office/OS temp and lock files are never meaningful to sync.</summary>
    public static readonly string[] BuiltIn = ["~$*", "*.tmp", "Thumbs.db", "desktop.ini", ".~lock.*"];

    public bool IsMatch(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        foreach (var r in _patterns)
        {
            try
            {
                if (r.IsMatch(path))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        return false;
    }
}
