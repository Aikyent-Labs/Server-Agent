using System.Text.Json;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.WatchdogAgent.Tests.Recovery;
using CloudOrc.WatchdogAgent.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.WatchdogAgent.Tests.Workspace;

public class WorkspaceUiRecoveryPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static WorkspaceUiOptions Options() => new()
    {
        StartGraceSeconds = 60,
        CrashLoopThreshold = 3,
        CrashLoopWindowMinutes = 10,
        RestartBackoffSeconds = 5,
        MaxRestartBackoffSeconds = 120,
        DegradedCooldownMinutes = 30
    };

    private static SessionObservation Obs(bool running, WorkspaceSessionState? state = null) =>
        new(2, "ravi", T0.AddMinutes(-5), running, state);

    [Fact]
    public void Running_panel_is_healthy()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0 };
        var d = p.Decide(Obs(true), t, T0);
        Assert.Equal(WorkspaceUiAction.None, d.Action);
        Assert.Equal(WorkspaceComponentStatus.Healthy, d.Status);
    }

    [Fact]
    public void Waits_for_the_logon_Run_key_before_starting_it_itself()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0 };
        Assert.Equal(WorkspaceUiAction.Wait, p.Decide(Obs(false), t, T0.AddSeconds(10)).Action);
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false), t, T0.AddSeconds(61)).Action);
    }

    [Fact]
    public void Starts_at_once_in_a_session_without_a_desktop_shell()
    {
        // TSPlus .connect Remote App / HTML5 web / Floating Panel: no explorer.exe, so no Run key fires.
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0 };
        var noDesktop = new SessionObservation(2, "ravi", T0.AddMinutes(-1), false, null, HasDesktopShell: false);
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(noDesktop, t, T0.AddSeconds(1)).Action);
    }

    [Fact]
    public void Restarts_a_crashed_panel_immediately_then_backs_off()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0 };
        p.Decide(Obs(true), t, T0);                       // seen running
        var d = p.Decide(Obs(false), t, T0.AddSeconds(1)); // crashed
        Assert.Equal(WorkspaceUiAction.Start, d.Action);
        p.RecordStart(t, T0.AddSeconds(1), succeeded: true);

        // crashed again 2 s later: inside the 5 s backoff -> wait
        Assert.Equal(WorkspaceUiAction.Wait, p.Decide(Obs(false), t, T0.AddSeconds(3)).Action);
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false), t, T0.AddSeconds(7)).Action);
    }

    [Fact]
    public void Crash_loop_stops_restarting_and_marks_degraded()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        var now = T0;
        for (var i = 0; i < 3; i++)
        {
            now = now.AddMinutes(1);
            Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false), t, now).Action);
            p.RecordStart(t, now, true);
        }

        now = now.AddMinutes(1);
        var d = p.Decide(Obs(false), t, now);
        Assert.Equal(WorkspaceUiAction.MarkDegraded, d.Action);
        Assert.Equal(WorkspaceReasonCodes.CrashLoop, d.ReasonCode);

        // Stays degraded (no endless loop) during the cooldown.
        var later = p.Decide(Obs(false), t, now.AddMinutes(5));
        Assert.Equal(WorkspaceUiAction.None, later.Action);
        Assert.Equal(WorkspaceComponentStatus.Degraded, later.Status);

        // After the cooldown exactly one more attempt is allowed.
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false), t, now.AddMinutes(31)).Action);
    }

    [Fact]
    public void Old_restarts_leave_the_window()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        p.RecordStart(t, T0, true);
        p.RecordStart(t, T0.AddMinutes(1), true);
        p.RecordStart(t, T0.AddMinutes(2), true);
        // 15 minutes later the three restarts are outside the 10 minute window.
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false), t, T0.AddMinutes(15)).Action);
    }

    [Theory]
    [InlineData(WorkspaceSessionState.StateDisabled)]
    [InlineData(WorkspaceSessionState.StateUserExit)]
    public void Respects_a_panel_that_chose_not_to_run(string state)
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        var file = new WorkspaceSessionState { SessionId = 2, State = state, HeartbeatAt = T0 };
        var d = p.Decide(Obs(false, file), t, T0.AddMinutes(5));
        Assert.Equal(WorkspaceUiAction.None, d.Action);
        Assert.Equal(WorkspaceComponentStatus.Stopped, d.Status);
    }

    [Fact]
    public void A_disabled_panel_is_rechecked_later_so_re_enabling_works_without_a_new_logon()
    {
        var o = Options();
        o.DisabledRecheckMinutes = 15;
        var p = new WorkspaceUiRecoveryPolicy(o);
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        var file = new WorkspaceSessionState { SessionId = 2, State = WorkspaceSessionState.StateDisabled, HeartbeatAt = T0 };
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(false, file), t, T0.AddMinutes(5)).Action);
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false, file), t, T0.AddMinutes(16)).Action);
    }

    [Fact]
    public void A_requested_self_restart_is_not_treated_as_a_crash()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        var file = new WorkspaceSessionState { SessionId = 2, State = WorkspaceSessionState.StateRestarting, HeartbeatAt = T0 };
        Assert.Equal(WorkspaceUiAction.Wait, p.Decide(Obs(false, file), t, T0.AddSeconds(5)).Action);
        Assert.Empty(t.Restarts);
        // If the new instance never came up, the Watch recovers it after the grace period.
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false, file), t, T0.AddSeconds(40)).Action);
    }

    [Fact]
    public void A_user_exit_is_respected_for_the_whole_logon()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        var file = new WorkspaceSessionState { SessionId = 2, State = WorkspaceSessionState.StateUserExit, HeartbeatAt = T0 };
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(false, file), t, T0.AddHours(6)).Action);
    }

    [Fact]
    public void A_state_file_from_a_previous_logon_is_ignored()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0, SeenRunning = true };
        var stale = new WorkspaceSessionState { SessionId = 2, State = WorkspaceSessionState.StateUserExit, HeartbeatAt = T0.AddDays(-1) };
        Assert.Equal(WorkspaceUiAction.Start, p.Decide(Obs(false, stale), t, T0.AddMinutes(5)).Action);
    }
}

public class WorkspaceUiSupervisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "co-ws-" + Guid.NewGuid().ToString("N"));

    public WorkspaceUiSupervisorTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "CloudOrc.WorkspaceUI.exe"), "stub");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeHost : ISessionHost
    {
        public List<InteractiveSession> Sessions { get; } = [];
        public HashSet<int> Running { get; } = [];
        public List<int> Started { get; } = [];
        public bool StartSucceeds { get; set; } = true;

        public IReadOnlyList<InteractiveSession> GetUserSessions() => Sessions;
        /// <summary>Sessions with explorer.exe (a desktop). Null = every session has a desktop.</summary>
        public HashSet<int>? Desktops { get; set; }

        public IReadOnlyCollection<int> GetSessionsRunning(string processName) =>
            processName == "explorer" ? (Desktops ?? Sessions.Select(s => s.SessionId).ToHashSet()) : Running;
        public string? GetSessionUserSid(int sessionId) => null;

        public List<int> RecoverStarts { get; } = [];
        public List<int> Killed { get; } = [];

        public IReadOnlyList<SessionProcess> GetProcesses(string processName) =>
            Running.Select(id => new SessionProcess(id, 1000 + id, null)).ToList();

        public bool TryKill(int processId, out string? error)
        {
            Killed.Add(processId);
            Running.Remove(processId - 1000);
            error = null;
            return true;
        }

        public bool TryStartInSession(int sessionId, string exePath, string arguments, out string? error)
        {
            if (arguments == "--recover-tsplus")
            {
                // One-shot "put the TSPlus panel back" run: not a panel start, never leaves a panel running.
                RecoverStarts.Add(sessionId);
                error = null;
                return true;
            }

            Started.Add(sessionId);
            error = StartSucceeds ? null : "denied";
            if (StartSucceeds) Running.Add(sessionId);
            return StartSucceeds;
        }
    }

    [Fact]
    public void Starts_the_panel_on_the_first_tick_in_a_session_without_a_desktop()
    {
        // Real 60 s grace: a desktop session waits for the Run key, a RemoteApp/web session does not.
        var host = new FakeHost { Desktops = [5] };
        host.Sessions.Add(new InteractiveSession(5, "desk", "VPS", null, Active: true));
        host.Sessions.Add(new InteractiveSession(7, "sayjaan1", "VPS", null, Active: true));
        var sup = Create(host, new ManualTimeProvider(DateTimeOffset.UtcNow), graceSeconds: 60);

        sup.Tick();

        Assert.Equal([7], host.Started);
    }

    private WorkspaceUiSupervisor Create(FakeHost host, ManualTimeProvider time, int graceSeconds = 0) =>
        new(Microsoft.Extensions.Options.Options.Create(new WorkspaceUiOptions
        {
            ExecutablePath = Path.Combine(_dir, "CloudOrc.WorkspaceUI.exe"),
            StartGraceSeconds = graceSeconds,
            CrashLoopThreshold = 3,
            CrashLoopWindowMinutes = 10,
            RestartBackoffSeconds = 1,
            AutoRollback = false,
            ReportDirectory = _dir
        }), host, new LastKnownGoodStore(NullLogger<LastKnownGoodStore>.Instance), time, NullLogger<WorkspaceUiSupervisor>.Instance);

    [Fact]
    public void Starts_the_panel_in_an_active_session_and_writes_a_report()
    {
        var host = new FakeHost();
        host.Sessions.Add(new InteractiveSession(3, "ravi", "VPS", null, Active: true));
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sup = Create(host, time);

        sup.Tick();

        Assert.Equal([3], host.Started);
        var report = JsonSerializer.Deserialize<WorkspaceSupervisorReport>(
            File.ReadAllText(Path.Combine(_dir, WorkspaceSupervisorReport.FileName)), WorkspaceProtocol.JsonOptions)!;
        Assert.Contains(report.Sessions, s => s.SessionId == 3 && s.Username == "ravi");
    }

    [Fact]
    public void Never_starts_a_panel_in_a_disconnected_session()
    {
        var host = new FakeHost();
        host.Sessions.Add(new InteractiveSession(4, "asha", "VPS", null, Active: false));
        var sup = Create(host, new ManualTimeProvider(DateTimeOffset.UtcNow));

        sup.Tick();

        Assert.Empty(host.Started);
    }

    [Fact]
    public void Repeated_crashes_stop_at_the_threshold()
    {
        var host = new FakeHost();
        host.Sessions.Add(new InteractiveSession(5, "dev", "VPS", null, Active: true));
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sup = Create(host, time);

        for (var i = 0; i < 10; i++)
        {
            sup.Tick();
            host.Running.Clear(); // the panel "crashes" right after every start
            time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(3, host.Started.Count);
        // On the crash loop the Watch restores the TSPlus panel once (a seamless user is never left without one).
        Assert.Equal([5], host.RecoverStarts);
    }

    [Fact]
    public void Does_nothing_when_the_panel_is_not_installed()
    {
        var host = new FakeHost();
        host.Sessions.Add(new InteractiveSession(6, "x", null, null, Active: true));
        File.Delete(Path.Combine(_dir, "CloudOrc.WorkspaceUI.exe"));
        var sup = Create(host, new ManualTimeProvider(DateTimeOffset.UtcNow));

        sup.Tick();

        Assert.Empty(host.Started);
    }
}
