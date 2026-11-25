using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.WatchdogAgent.Workspace;

/// <summary>
/// Watch supervision of the per-user Remote Workspace panel (handoff v1.2 §31, §48, RW-06):
///  - one WorkspaceUI per interactive session; started into the user's session if the logon
///    Run key did not start it, restarted after a crash with bounded backoff;
///  - crash-loop breaker: stop restarting, mark DEGRADED, roll back to last-known-good if an
///    update caused it — the user's Windows session and apps are never touched;
///  - writes a supervisor report the Control Agent forwards to CloudOrc.
/// It never restarts the Control Agent or TSPlus because of a panel problem.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceUiSupervisor(
    IOptions<WorkspaceUiOptions> options,
    ISessionHost sessions,
    LastKnownGoodStore lkg,
    TimeProvider time,
    ILogger<WorkspaceUiSupervisor> logger) : BackgroundService
{
    private readonly WorkspaceUiRecoveryPolicy _policy = new(options.Value);
    private readonly Dictionary<int, WorkspaceUiRecoveryPolicy.Tracker> _trackers = [];
    private readonly Dictionary<int, WorkspaceSessionHealth> _lastHealth = [];
    private bool _loggedMissingExe;

    public static string WorkspaceRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudOrc", "Workspace");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("WorkspaceUI supervision is disabled by configuration.");
            return;
        }

        EnsureSharedDirectories();
        var interval = TimeSpan.FromSeconds(Math.Max(3, options.Value.CheckIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WorkspaceUI supervision cycle failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal void Tick()
    {
        var exe = options.Value.ResolveExecutablePath();
        var now = time.GetUtcNow();
        if (!File.Exists(exe))
        {
            if (!_loggedMissingExe)
            {
                _loggedMissingExe = true;
                logger.LogInformation("WorkspaceUI is not installed on this server ({Path}); nothing to supervise.", exe);
            }

            WriteReport(now, []);
            return;
        }

        _loggedMissingExe = false;
        var live = sessions.GetUserSessions();
        var running = sessions.GetSessionsRunning(options.Value.ProcessName);
        var report = new List<WorkspaceSessionHealth>();

        foreach (var s in live)
        {
            if (!_trackers.TryGetValue(s.SessionId, out var tracker))
            {
                tracker = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = now };
                _trackers[s.SessionId] = tracker;
            }

            // Disconnected sessions keep their panel process; never start one in a disconnected session.
            var uiRunning = running.Contains(s.SessionId);
            if (!s.Active && !uiRunning)
            {
                report.Add(Health(s, tracker, WorkspaceComponentStatus.Stopped, null, null));
                continue;
            }

            var obs = new SessionObservation(s.SessionId, s.Username, s.LogonTime, uiRunning, ReadStateFile(s.SessionId));
            var decision = _policy.Decide(obs, tracker, now);
            switch (decision.Action)
            {
                case WorkspaceUiAction.Start:
                    var ok = sessions.TryStartInSession(s.SessionId, exe, "--started-by=watch", out var error);
                    _policy.RecordStart(tracker, now, ok);
                    if (ok)
                    {
                        logger.LogInformation("WorkspaceUI {Note} in session {Session} ({User}); restart {Count} in window.",
                            decision.Note, s.SessionId, s.Username, tracker.Restarts.Count);
                    }
                    else
                    {
                        logger.LogWarning("WorkspaceUI could not be started in session {Session}: {Error}", s.SessionId, error);
                    }

                    break;

                case WorkspaceUiAction.MarkDegraded:
                    logger.LogWarning("WorkspaceUI crash loop in session {Session} ({User}): {Count} restarts in {Window} min. Restart stopped; the Windows session stays usable.",
                        s.SessionId, s.Username, tracker.Restarts.Count, options.Value.CrashLoopWindowMinutes);
                    TryRollback(exe, tracker);
                    // A seamless (RemoteApp) user has no desktop: if our panel had hidden the TSPlus panel and
                    // was hard-killed, put the TSPlus panel back now (one-shot, no UI) so the user can still
                    // open apps while our panel is stopped.
                    if (!sessions.TryStartInSession(s.SessionId, exe, "--recover-tsplus", out var recoverError))
                    {
                        logger.LogWarning("Could not restore the TSPlus panel in session {Session}: {Error}", s.SessionId, recoverError);
                    }

                    break;
            }

            // Promote a version to last-known-good after it has run cleanly for a while.
            if (uiRunning && options.Value.AutoRollback && tracker.RunningSince is { } since
                && now - since >= TimeSpan.FromMinutes(Math.Max(1, options.Value.LastKnownGoodAfterMinutes))
                && tracker.Restarts.Count == 0)
            {
                lkg.TryPromote(exe);
            }

            report.Add(Health(s, tracker, decision.Status, decision.ReasonCode, obs.StateFile?.Version));
        }

        foreach (var gone in _trackers.Keys.Where(id => live.All(s => s.SessionId != id)).ToList())
        {
            _trackers.Remove(gone);
            _lastHealth.Remove(gone);
        }

        WriteReport(now, report);
    }

    private void TryRollback(string exe, WorkspaceUiRecoveryPolicy.Tracker tracker)
    {
        if (!options.Value.AutoRollback)
        {
            return;
        }

        var restored = lkg.TryRollback(exe, out var error);
        if (restored is not null)
        {
            // Give the restored build one fresh chance in every session.
            foreach (var t in _trackers.Values)
            {
                t.DegradedUntil = null;
                t.Restarts.Clear();
                t.NextAttemptAt = null;
            }

            tracker.LastRecoveryAction = $"rolled back to {restored}";
        }
        else
        {
            logger.LogInformation("WorkspaceUI rollback not performed: {Reason}.", error);
        }
    }

    private WorkspaceSessionHealth Health(InteractiveSession s, WorkspaceUiRecoveryPolicy.Tracker t, string status, string? reason, string? version)
    {
        var h = new WorkspaceSessionHealth
        {
            SessionId = s.SessionId,
            Username = s.Username,
            Version = version ?? LastKnownGoodStore.ReadExeVersion(options.Value.ResolveExecutablePath()),
            Status = status,
            ReasonCode = reason,
            RestartCount = t.Restarts.Count,
            LastRecoveryAction = t.LastRecoveryAction,
            HeartbeatAt = time.GetUtcNow()
        };
        _lastHealth[s.SessionId] = h;
        return h;
    }

    /// <summary>State file written by the panel. Trusted only when it is owned by that session's user (or by an admin/SYSTEM).</summary>
    private WorkspaceSessionState? ReadStateFile(int sessionId)
    {
        var path = WorkspaceSessionState.PathFor(sessionId);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var expected = sessions.GetSessionUserSid(sessionId);
            if (expected is not null)
            {
                var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
                if (owner is not null && owner != expected && owner != "S-1-5-18" && owner != "S-1-5-32-544")
                {
                    logger.LogWarning("Ignoring workspace state file for session {Session}: owned by another account.", sessionId);
                    return null;
                }
            }

            return JsonSerializer.Deserialize<WorkspaceSessionState>(File.ReadAllText(path), WorkspaceProtocol.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteReport(DateTimeOffset now, List<WorkspaceSessionHealth> sessionsHealth)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(options.Value.ReportDirectory) ? WorkspaceRoot : options.Value.ReportDirectory;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, WorkspaceSupervisorReport.FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new WorkspaceSupervisorReport { GeneratedAt = now, Sessions = sessionsHealth }, WorkspaceProtocol.JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write the workspace supervisor report.");
        }
    }

    /// <summary>
    /// %ProgramData%\CloudOrc\Workspace\sessions: users may create files (their own session state)
    /// but only modify what they own (CREATOR OWNER). Applied only when running elevated.
    /// </summary>
    private void EnsureSharedDirectories()
    {
        try
        {
            var dir = new DirectoryInfo(WorkspaceSessionState.Directory);
            dir.Create();
            using var me = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return;
            }

            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.Modify, inherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            dir.SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not prepare the shared workspace state directory.");
        }
    }
}
