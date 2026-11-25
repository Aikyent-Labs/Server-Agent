using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.WatchdogAgent.Workspace;

public enum WorkspaceUiAction
{
    None,
    Wait,
    Start,
    MarkDegraded
}

public sealed record WorkspaceUiDecision(WorkspaceUiAction Action, string Status, string? ReasonCode, string? Note);

/// <summary>What the supervisor knows about one interactive Windows session this tick.</summary>
public sealed record SessionObservation(
    int SessionId,
    string Username,
    DateTimeOffset? LogonTime,
    bool UiRunning,
    WorkspaceSessionState? StateFile);

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
    }

    public WorkspaceUiDecision Decide(SessionObservation s, Tracker t, DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(Math.Max(1, options.CrashLoopWindowMinutes));
        while (t.Restarts.Count > 0 && now - t.Restarts.Peek() > window)
        {
            t.Restarts.Dequeue();
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

        if (!t.SeenRunning && now - t.FirstSeenAt < TimeSpan.FromSeconds(Math.Max(0, options.StartGraceSeconds)))
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
        t.Restarts.Enqueue(now);
        t.TotalRestarts++;
        var exp = Math.Min(10, t.Restarts.Count - 1);
        var delay = Math.Min(options.MaxRestartBackoffSeconds, options.RestartBackoffSeconds * Math.Pow(2, exp));
        t.NextAttemptAt = now.AddSeconds(Math.Max(1, delay));
        t.LastRecoveryAction = succeeded ? (t.SeenRunning ? "restarted" : "started") : "start failed";
    }

    private static bool IsCurrentLogon(WorkspaceSessionState f, SessionObservation s) =>
        s.LogonTime is null || f.HeartbeatAt >= s.LogonTime.Value.AddMinutes(-1);
}
