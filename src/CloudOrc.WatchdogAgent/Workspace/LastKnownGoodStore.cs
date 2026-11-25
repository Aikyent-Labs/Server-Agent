using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CloudOrc.WatchdogAgent.Workspace;

/// <summary>
/// Last-known-good copy of the WorkspaceUI folder (handoff §31: "rollback last-known-good build;
/// quarantine bad version"). A version is promoted once it has run cleanly for a while; on a crash
/// loop with a different current version, the bad folder is quarantined and the LKG copy restored.
/// Files in use by another session's panel make the swap fail safely (nothing is half-replaced:
/// the current folder is renamed first, and restored if the copy fails).
/// </summary>
public sealed class LastKnownGoodStore(ILogger<LastKnownGoodStore> logger)
{
    private const string VersionFile = "lkg-version.txt";

    public static string? ReadExeVersion(string exePath)
    {
        try
        {
            return File.Exists(exePath) ? FileVersionInfo.GetVersionInfo(exePath).ProductVersion : null;
        }
        catch
        {
            return null;
        }
    }

    public static string LkgDirFor(string uiDir) => uiDir.TrimEnd(Path.DirectorySeparatorChar) + ".lkg";

    public string? LkgVersion(string uiDir)
    {
        var f = Path.Combine(LkgDirFor(uiDir), VersionFile);
        try
        {
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copies the current folder to .lkg when its version is not already saved there.</summary>
    public bool TryPromote(string exePath)
    {
        var uiDir = Path.GetDirectoryName(exePath)!;
        var version = ReadExeVersion(exePath);
        if (version is null || string.Equals(version, LkgVersion(uiDir), StringComparison.Ordinal))
        {
            return false;
        }

        var lkg = LkgDirFor(uiDir);
        var staging = lkg + ".tmp";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            CopyDirectory(uiDir, staging);
            File.WriteAllText(Path.Combine(staging, VersionFile), version);
            if (Directory.Exists(lkg)) Directory.Delete(lkg, recursive: true);
            Directory.Move(staging, lkg);
            logger.LogInformation("WorkspaceUI {Version} saved as last-known-good.", version);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save WorkspaceUI {Version} as last-known-good.", version);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>Quarantines the current (crashing) folder and restores the LKG copy. Returns the restored version.</summary>
    public string? TryRollback(string exePath, out string? error)
    {
        error = null;
        var uiDir = Path.GetDirectoryName(exePath)!;
        var current = ReadExeVersion(exePath);
        var lkgVersion = LkgVersion(uiDir);
        var lkg = LkgDirFor(uiDir);
        if (lkgVersion is null || !Directory.Exists(lkg))
        {
            error = "no last-known-good copy";
            return null;
        }

        if (string.Equals(current, lkgVersion, StringComparison.Ordinal))
        {
            error = "current version is the last-known-good version (crash is not caused by an update)";
            return null;
        }

        var quarantine = $"{uiDir.TrimEnd(Path.DirectorySeparatorChar)}.bad-{Sanitize(current)}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            Directory.Move(uiDir, quarantine);
        }
        catch (Exception ex)
        {
            error = $"current version is in use and could not be quarantined: {ex.Message}";
            return null;
        }

        try
        {
            CopyDirectory(lkg, uiDir);
            File.Delete(Path.Combine(uiDir, VersionFile));
            logger.LogWarning("WorkspaceUI rolled back from {Bad} to last-known-good {Good}; bad build kept at {Quarantine}.", current, lkgVersion, quarantine);
            return lkgVersion;
        }
        catch (Exception ex)
        {
            error = $"restore failed: {ex.Message}";
            try
            {
                if (Directory.Exists(uiDir)) Directory.Delete(uiDir, recursive: true);
                Directory.Move(quarantine, uiDir);
            }
            catch (Exception restoreEx)
            {
                logger.LogError(restoreEx, "WorkspaceUI rollback could not restore the original folder from {Quarantine}.", quarantine);
            }

            return null;
        }
    }

    private static string Sanitize(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "unknown" : new string(v.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target, StringComparison.OrdinalIgnoreCase), overwrite: true);
        }
    }
}
