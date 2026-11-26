using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.WatchdogAgent.Workspace;

namespace CloudOrc.WatchdogAgent.Tests.Workspace;

/// <summary>Fallback switches ("default Windows / TSPlus experience") and hang detection.</summary>
public class WorkspaceFallbackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private static WorkspaceUiOptions Options() => new()
    {
        StartGraceSeconds = 60,
        CrashLoopThreshold = 3,
        CrashLoopWindowMinutes = 10,
        RestartBackoffSeconds = 5,
        MaxRestartBackoffSeconds = 120,
        DegradedCooldownMinutes = 30,
        HangSeconds = 90,
        FallbackGraceSeconds = 30
    };

    private static WorkspaceFallbackDocument Doc(params WorkspaceFallbackRule[] rules) => new() { Rules = [.. rules] };

    private static readonly WorkspaceFallbackDecision PanelOffTeam =
        new(true, true, true, WorkspaceFallbackSources.Team, "bad release");

    // ------------------------------------------------------------ resolve

    [Fact]
    public void No_switch_means_the_panel_runs_normally()
    {
        var d = WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, null), (WorkspaceFallbackSources.Local, Doc()));
        Assert.Same(WorkspaceFallbackDecision.None, d);
        Assert.False(d.Any);
    }

    [Fact]
    public void A_server_wide_switch_applies_to_every_user_and_panel_off_implies_the_rest()
    {
        var d = WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, Doc(new WorkspaceFallbackRule { PanelOff = true, Reason = "incident" })));
        Assert.True(d.PanelOff);
        Assert.True(d.AltTabOff);
        Assert.True(d.HideTsplusOff);
        Assert.Equal(WorkspaceFallbackSources.Team, d.Source);
        Assert.Equal(WorkspaceReasonCodes.FallbackTeam, d.ReasonCode);
    }

    [Fact]
    public void A_user_switch_matches_only_that_user_ignoring_domain_and_case()
    {
        var doc = Doc(new WorkspaceFallbackRule { Username = @"VPS\Ravi", AltTabOff = true });
        Assert.True(WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, doc)).AltTabOff);
        Assert.True(WorkspaceFallback.Resolve("RAVI@vps.local", (WorkspaceFallbackSources.Team, doc)).AltTabOff);
        Assert.False(WorkspaceFallback.Resolve("sita", (WorkspaceFallbackSources.Team, doc)).Any);
    }

    [Fact]
    public void Team_and_local_switches_add_up_and_the_panel_off_source_names_the_decision()
    {
        var team = Doc(new WorkspaceFallbackRule { AltTabOff = true, Reason = "alt-tab bug" });
        var local = Doc(new WorkspaceFallbackRule { PanelOff = true, Reason = "server admin" });
        var d = WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, team), (WorkspaceFallbackSources.Local, local));
        Assert.True(d.PanelOff);
        Assert.Equal(WorkspaceFallbackSources.Local, d.Source);
        Assert.Equal("server admin", d.Reason);
        Assert.Equal(WorkspaceReasonCodes.FallbackLocal, d.ReasonCode);
    }

    [Fact]
    public void Only_tsplus_hiding_off_keeps_the_panel_and_alt_tab()
    {
        var d = WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, Doc(new WorkspaceFallbackRule { HideTsplusOff = true })));
        Assert.False(d.PanelOff);
        Assert.False(d.AltTabOff);
        Assert.True(d.HideTsplusOff);
    }

    [Fact]
    public void Save_then_load_round_trips_and_empty_rules_delete_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "co-fb-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            WorkspaceFallback.Save(path, Doc(new WorkspaceFallbackRule { Username = "ravi", PanelOff = true, Reason = "test" }));
            var loaded = WorkspaceFallback.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("ravi", Assert.Single(loaded!.Rules).Username);

            WorkspaceFallback.Save(path, Doc());
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_switch_file_a_normal_user_could_have_written_is_ignored()
    {
        // Simulate an untrusted owner: the current user's own SID (tests do not run as SYSTEM; when run
        // elevated the owner would be Administrators, so only assert when the owner really is the user).
        var path = Path.Combine(Path.GetTempPath(), "co-fb-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"rules\":[{\"panelOff\":true}]}");
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            try
            {
                sec.SetOwner(me.User!);
                fi.SetAccessControl(sec);
            }
            catch (Exception)
            {
                return; // cannot set owner here: nothing to assert
            }

            var owner = fi.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier))?.Value;
            if (owner is "S-1-5-18" or "S-1-5-32-544") return;
            Assert.Null(WorkspaceFallback.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------ Watch policy

    private static SessionObservation Obs(bool running, WorkspaceSessionState? state = null, WorkspaceFallbackDecision? fb = null,
        SessionProcess? proc = null, bool hangCheck = true) =>
        new(2, "ravi", T0.AddMinutes(-30), running, state, true, fb, proc, hangCheck);

    [Fact]
    public void Panel_off_switch_never_starts_the_panel_and_reports_fallback()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1) };
        var d = p.Decide(Obs(false, fb: PanelOffTeam), t, T0);
        Assert.Equal(WorkspaceUiAction.None, d.Action);
        Assert.Equal(WorkspaceComponentStatus.Fallback, d.Status);
        Assert.Equal(WorkspaceReasonCodes.FallbackTeam, d.ReasonCode);
    }

    [Fact]
    public void A_panel_that_ignores_the_switch_is_ended_after_the_grace_period()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1) };
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(true, fb: PanelOffTeam), t, T0).Action);
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(true, fb: PanelOffTeam), t, T0.AddSeconds(20)).Action);
        Assert.Equal(WorkspaceUiAction.StopForFallback, p.Decide(Obs(true, fb: PanelOffTeam), t, T0.AddSeconds(31)).Action);
    }

    [Fact]
    public void Clearing_the_switch_starts_the_panel_again_with_a_clean_history()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1), SeenRunning = true };
        t.Restarts.Enqueue(T0.AddMinutes(-1));
        t.Restarts.Enqueue(T0.AddMinutes(-1));
        p.Decide(Obs(false, fb: PanelOffTeam), t, T0);
        var state = new WorkspaceSessionState { SessionId = 2, ProcessId = 50, State = WorkspaceSessionState.StateFallback, HeartbeatAt = T0 };
        var d = p.Decide(Obs(false, state), t, T0.AddMinutes(1));
        Assert.Equal(WorkspaceUiAction.Start, d.Action);
        Assert.Empty(t.Restarts);
    }

    [Fact]
    public void A_panel_whose_heartbeat_stopped_is_hung()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1) };
        var proc = new SessionProcess(2, 77, T0.AddMinutes(-20));
        var fresh = new WorkspaceSessionState { SessionId = 2, ProcessId = 77, State = WorkspaceSessionState.StateRunning, HeartbeatAt = T0.AddSeconds(-20) };
        var stale = new WorkspaceSessionState { SessionId = 2, ProcessId = 77, State = WorkspaceSessionState.StateRunning, HeartbeatAt = T0.AddSeconds(-120) };

        Assert.Equal(WorkspaceComponentStatus.Healthy, p.Decide(Obs(true, fresh, proc: proc), t, T0).Status);
        var d = p.Decide(Obs(true, stale, proc: proc), t, T0);
        Assert.Equal(WorkspaceUiAction.KillHung, d.Action);
        Assert.Equal(WorkspaceReasonCodes.Hung, d.ReasonCode);
    }

    [Fact]
    public void Hang_detection_never_judges_on_low_disk_or_while_restarting()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1) };
        var proc = new SessionProcess(2, 77, T0.AddMinutes(-20));
        var stale = new WorkspaceSessionState { SessionId = 2, ProcessId = 77, State = WorkspaceSessionState.StateRunning, HeartbeatAt = T0.AddMinutes(-5) };
        var restarting = new WorkspaceSessionState { SessionId = 2, ProcessId = 77, State = WorkspaceSessionState.StateRestarting, HeartbeatAt = T0.AddMinutes(-5) };

        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(true, stale, proc: proc, hangCheck: false), t, T0).Action);
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(true, restarting, proc: proc), t, T0).Action);
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(true, null, proc: proc), t, T0).Action);
    }

    [Fact]
    public void A_new_panel_that_never_wrote_a_heartbeat_gets_twice_the_limit()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1) };
        var oldState = new WorkspaceSessionState { SessionId = 2, ProcessId = 10, State = WorkspaceSessionState.StateRunning, HeartbeatAt = T0.AddMinutes(-30) };
        Assert.Equal(WorkspaceUiAction.None, p.Decide(Obs(true, oldState, proc: new SessionProcess(2, 99, T0.AddSeconds(-100))), t, T0).Action);
        Assert.Equal(WorkspaceUiAction.KillHung, p.Decide(Obs(true, oldState, proc: new SessionProcess(2, 99, T0.AddSeconds(-200))), t, T0).Action);
    }

    [Fact]
    public void Repeated_hangs_end_in_the_crash_loop_breaker()
    {
        var p = new WorkspaceUiRecoveryPolicy(Options());
        var t = new WorkspaceUiRecoveryPolicy.Tracker { FirstSeenAt = T0.AddHours(-1), SeenRunning = true };
        var now = T0;
        for (var i = 0; i < 3; i++)
        {
            WorkspaceUiRecoveryPolicy.RecordHangKill(t, killed: true, now);
            var d = p.Decide(Obs(false), t, now);
            Assert.Equal(WorkspaceUiAction.Start, d.Action);
            p.RecordStart(t, now, succeeded: true);
            now = now.AddMinutes(3);
        }

        Assert.Equal(WorkspaceUiAction.MarkDegraded, p.Decide(Obs(false), t, now).Action);
        Assert.Equal(3, t.HangKills);
    }
}
