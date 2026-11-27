using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.WatchdogAgent.Workspace;

public enum WorkspaceUiAction
{
    None,
    Wait,
    Start,
    MarkDegraded,
    /// <summary>The panel stopped responding: end it, restore the TSPlus panel; it is restarted with backoff.</summary>
    KillHung,
    /// <summary>A fallback switch is on and the panel did not exit by itself: end it and restore the TSPlus panel.</summary>
    StopForFallback
}

public sealed record WorkspaceUiDecision(WorkspaceUiAction Action, string Status, string? ReasonCode, string? Note);

/// <summary>What the supervisor knows about one interactive Windows session this tick.</summary>
public sealed record SessionObservation(
    int SessionId,
    string Username,
    DateTimeOffset? LogonTime,
    bool UiRunning,
    WorkspaceSessionState? StateFile,
    bool HasDesktopShell = true,
    WorkspaceFallbackDecision? Fallback = null,
    SessionProcess? UiProcess = null,
    bool HangCheckAllowed = true);

/// <summary>
/// Pure decision logic for one session (unit-tested). Handoff §31.1 / §48:
/// bounded restart with backoff, crash-loop breaker -> DEGRADED (never an endless loop),
/// respect a panel that chose not to run (disabled / user exit).
/// </summary>
public sealed class WorkspaceUiRecoveryPolicy(WorkspaceUiOptions options)
{
    public sealed class Tracker
    {
        public DateTimeOffset FirstSeenAt { get; init; }
        public bool SeenRunning { get; set; }
        public Queue<DateTimeOffset> Restarts { get; } = new();
        public DateTimeOffset? NextAttemptAt { get; set; }
        public DateTimeOffset? DegradedUntil { get; set; }
        public DateTimeOffset? RunningSince { get; set; }
        public string? LastRecoveryAction { get; set; }
        public int TotalRestarts { get; set; }
        /// <summary>When the current panel-off fallback switch was first seen for this session.</summary>
        public DateTimeOffset? FallbackSince { get; set; }
        public int HangKills { get; set; }
        /// <summary>Last time a hung panel was ended: reported as WORKSPACE_UI_HUNG for a while so CloudOrc sees it.</summary>
        public DateTimeOffset? LastHangAt { get; set; }
    }

    public WorkspaceUiDecision Decide(SessionObservation s, Tracker t, DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, options.CrashLoopWindowMinutes));
        while (t.Restarts.Count > 0 && now - t.Restarts.Peek() > window)
        {
            t.Restarts.Dequeue();
        }

        // Fallback switch (CloudOrc team / server admin): the panel must not run for this user.
        if (s.Fallback is { PanelOff: true } fb)
        {
            t.FallbackSince ??= now;
            if (s.UiRunning && now - t.FallbackSince.Value >= TimeSpan.FromSeconds(Math.Max(5, options.FallbackGraceSeconds)))
            {
                return new WorkspaceUiDecision(WorkspaceUiAction.StopForFallback, WorkspaceComponentStatus.Fallback, fb.ReasonCode, "panel did not exit for the fallback switch");
            }

            return new WorkspaceUiDecision(WorkspaceUiAction.None, WorkspaceComponentStatus.Fallback, fb.ReasonCode,
                s.UiRunning ? "waiting for the panel to exit" : fb.Reason);
        }

        if (t.FallbackSince is not null)
        {
            // Switch cleared: give the panel a fresh start (no crash history from the fallback period).
            t.FallbackSince = null;
            t.Restarts.Clear();
            t.NextAttemptAt = null;
            t.DegradedUntil = null;
            t.LastRecoveryAction = "fallback switch cleared";
        }

        if (s.UiRunning && IsHung(s, now))
        {
            return new WorkspaceUiDecision(WorkspaceUiAction.KillHung, WorkspaceComponentStatus.Recovering, WorkspaceReasonCodes.Hung, "panel not responding");
        }

        if (s.UiRunning)
        {
            t.SeenRunning = true;
            t.RunningSince ??= now;
            if (t.DegradedUntil is not null)
            {
                // The panel came back (user relaunched or logon Run key) — leave DEGRADED.
                t.DegradedUntil = null;
                t.LastRecoveryAction = "recovered";
            }

            return new WorkspaceUiDecision(WorkspaceUiAction.None, WorkspaceComponentStatus.Healthy, null, null);
        }

        t.RunningSince = null;

        // A state file written in THIS logon that says the panel decided not to run.
        if (s.StateFile is { } f && IsCurrentLogon(f, s))
        {
            // Requested self-restart in progress: give the new process time; it is not a crash.
            if (f.State == WorkspaceSessionState.StateRestarting && now - f.HeartbeatAt < TimeSpan.FromSeconds(30))
            {
                return new WorkspaceUiDecision(WorkspaceUiAction.Wait, WorkspaceComponentStatus.Recovering, null, "panel restarting on request");
            }

            if (f.State == WorkspaceSessionState.StateUserExit)
            {
                return new WorkspaceUiDecision(WorkspaceUiAction.None, WorkspaceComponentStatus.Stopped, null, f.State);
            }

            if (f.State == WorkspaceSessionState.StateDisabled)
            {
                // Periodically let the panel re-check (it asks CloudOrc and exits again if still disabled).
                var recheck = TimeSpan.FromMinutes(Math.Max(1, options.DisabledRecheckMinutes));
                return now - f.HeartbeatAt < recheck
                    ? new WorkspaceUiDecision(WorkspaceUiAction.None, WorkspaceComponentStatus.Stopped, null, f.State)
                    : new WorkspaceUiDecision(WorkspaceUiAction.Start, WorkspaceComponentStatus.Stopped, null, "re-check disabled workspace");
            }
        }

        if (t.DegradedUntil is { } until)
        {
            if (now < until)
            {
                return new WorkspaceUiDecision(WorkspaceUiAction.None, WorkspaceComponentStatus.Degraded, WorkspaceReasonCodes.CrashLoop, "cooldown");
            }

            // Cooldown over: allow exactly one more attempt with a fresh window.
            t.DegradedUntil = null;
            t.Restarts.Clear();
        }

        // Grace only where the HKLM Run entry will start the panel: a desktop session (explorer.exe). TSPlus
        // .connect Remote App, HTML5 web and Floating Panel sessions have no explorer, so nothing else starts
        // the panel there - start it at once, before the user reaches for the TSPlus panel.
        if (s.HasDesktopShell && !t.SeenRunning && now - t.FirstSeenAt < TimeSpan.FromSeconds(Math.Max(0, options.StartGraceSeconds)))
        {
            return new WorkspaceUiDecision(WorkspaceUiAction.Wait, WorkspaceComponentStatus.Unknown, null, "waiting for logon start");
        }

        if (t.Restarts.Count >= Math.Max(1, options.CrashLoopThreshold))
        {
            t.DegradedUntil = now.AddMinutes(Math.Max(1, options.DegradedCooldownMinutes));
            t.LastRecoveryAction = "crash-loop: restart stopped";
            return new WorkspaceUiDecision(WorkspaceUiAction.MarkDegraded, WorkspaceComponentStatus.Degraded, WorkspaceReasonCodes.CrashLoop, null);
        }

        if (t.NextAttemptAt is { } next && now < next)
        {
            return new WorkspaceUiDecision(WorkspaceUiAction.Wait, WorkspaceComponentStatus.Recovering, null, "backoff");
        }

        return new WorkspaceUiDecision(WorkspaceUiAction.Start, WorkspaceComponentStatus.Recovering, null, t.SeenRunning ? "restart after exit" : "start (not started at logon)");
    }

    /// <summary>Call after a start attempt; schedules the next allowed attempt (exponential, bounded).</summary>
    public void RecordStart(Tracker t, DateTimeOffset now, bool succeeded)
    {
        if (!succeeded && !t.SeenRunning)
        {
            // Right after sign-in Windows may not hand out the user's token yet: not a crash, just try again soon.
            t.NextAttemptAt = now.AddSeconds(Math.Max(1, options.RestartBackoffSeconds));
            t.LastRecoveryAction = "start failed (will retry)";
            return;
        }

        t.Restarts.Enqueue(now);
        t.TotalRestarts++;
        var exp = Math.Min(10, t.Restarts.Count - 1);
        var delay = Math.Min(options.MaxRestartBackoffSeconds, options.RestartBackoffSeconds * Math.Pow(2, exp));
        t.NextAttemptAt = now.AddSeconds(Math.Max(1, delay));
        t.LastRecoveryAction = succeeded ? (t.SeenRunning ? "restarted" : "started") : "start failed";
    }

    /// <summary>Records that a hung panel was ended (the restart after it is counted by <see cref="RecordStart"/>).</summary>
    public static void RecordHangKill(Tracker t, bool killed, DateTimeOffset now)
    {
        t.HangKills++;
        t.LastHangAt = now;
        t.RunningSince = null;
        t.LastRecoveryAction = killed ? "ended a panel that stopped responding" : "could not end a panel that stopped responding";
    }

    /// <summary>
    /// The panel writes its heartbeat from its UI thread every 15 s. Stale heartbeat of THIS process = hung UI.
    /// A process that never wrote one for twice the limit is hung too. Unknown / untrusted state = never judged.
    /// </summary>
    private bool IsHung(SessionObservation s, DateTimeOffset now)
    {
        if (options.HangSeconds <= 0 || !s.HangCheckAllowed || s.UiProcess is not { } proc || s.StateFile is not { } f)
        {
            return false;
        }

        var limit = TimeSpan.FromSeconds(Math.Max(30, options.HangSeconds));
        if (f.ProcessId == proc.ProcessId)
        {
            return f.State == WorkspaceSessionState.StateRunning && now - f.HeartbeatAt > limit;
        }

        return proc.StartedAt is { } started && now - started > limit * 2;
    }

    private static bool IsCurrentLogon(WorkspaceSessionState f, SessionObservation s) =>
        s.LogonTime is null || f.HeartbeatAt >= s.LogonTime.Value.AddMinutes(-1);
}
