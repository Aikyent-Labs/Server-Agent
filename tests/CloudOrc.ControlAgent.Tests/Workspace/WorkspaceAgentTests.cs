using System.Net;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Transport;
using CloudOrc.ControlAgent.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Workspace;

public class WorkspaceTokenBrokerTests
{
    private sealed class FakeIssuer(Func<string, int, ApiResponse> respond, Uri? baseAddress = null) : IWorkspaceTokenIssuer
    {
        public List<(string User, int Session)> Calls { get; } = [];
        public Uri? BaseAddress { get; } = baseAddress ?? new Uri("https://api.example.test/");

        public Task<ApiResponse> RequestAsync(string windowsUsername, int sessionId, CancellationToken cancellationToken)
        {
            Calls.Add((windowsUsername, sessionId));
            return Task.FromResult(respond(windowsUsername, sessionId));
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private static readonly PipeClientIdentity Ravi = new(100, 3, @"VPS01\ravi", "S-1-5-21-1-2-3-1001");

    private static ApiResponse Ok(DateTimeOffset expires, bool seat = true) =>
        new(HttpStatusCode.OK, $$"""{"token":"tok","expires_at":"{{expires:O}}","correlation_id":"c1","seat_found":{{(seat ? "true" : "false")}}}""", null, null);

    [Fact]
    public async Task Requests_a_token_for_the_OS_identity_and_caches_it()
    {
        var time = new FixedTime(T0);
        var issuer = new FakeIssuer((_, _) => Ok(T0.AddMinutes(15)));
        var broker = new WorkspaceTokenBroker(issuer, time, NullLogger<WorkspaceTokenBroker>.Instance);

        var a = await broker.GetAsync(Ravi, forceRefresh: false, CancellationToken.None);
        var b = await broker.GetAsync(Ravi, forceRefresh: false, CancellationToken.None);

        Assert.True(a.Ok);
        Assert.Equal("tok", a.Token);
        Assert.Equal("https://api.example.test", a.ApiBaseUrl);
        Assert.True(a.SeatFound);
        Assert.Same(a, b);
        Assert.Equal([("ravi", 3)], issuer.Calls); // bare username, OS session id
    }

    [Fact]
    public async Task Refreshes_close_to_expiry_and_on_force()
    {
        var time = new FixedTime(T0);
        var issuer = new FakeIssuer((_, _) => Ok(time.Now.AddMinutes(15)));
        var broker = new WorkspaceTokenBroker(issuer, time, NullLogger<WorkspaceTokenBroker>.Instance);

        await broker.GetAsync(Ravi, false, CancellationToken.None);
        time.Now = T0.AddMinutes(13); // 2 min left < 3 min margin
        await broker.GetAsync(Ravi, false, CancellationToken.None);
        await broker.GetAsync(Ravi, true, CancellationToken.None);

        Assert.Equal(3, issuer.Calls.Count);
    }

    [Fact]
    public async Task Different_sessions_get_different_tokens()
    {
        var issuer = new FakeIssuer((_, _) => Ok(T0.AddMinutes(15)));
        var broker = new WorkspaceTokenBroker(issuer, new FixedTime(T0), NullLogger<WorkspaceTokenBroker>.Instance);

        await broker.GetAsync(Ravi, false, CancellationToken.None);
        await broker.GetAsync(Ravi with { SessionId = 4 }, false, CancellationToken.None);

        Assert.Equal(2, issuer.Calls.Count);
    }

    [Fact]
    public async Task Backend_failure_is_reported_and_not_cached()
    {
        var issuer = new FakeIssuer((_, _) => new ApiResponse(null, null, null, "connection refused"));
        var broker = new WorkspaceTokenBroker(issuer, new FixedTime(T0), NullLogger<WorkspaceTokenBroker>.Instance);

        var r = await broker.GetAsync(Ravi, false, CancellationToken.None);
        await broker.GetAsync(Ravi, false, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(WorkspaceReasonCodes.BackendUnavailable, r.ReasonCode);
        Assert.Equal(2, issuer.Calls.Count);
    }

    [Fact]
    public async Task Not_enrolled_agent_cannot_issue_tokens()
    {
        var issuer = new FakeIssuer((_, _) => throw new InvalidOperationException("must not be called"), baseAddress: null);
        var broker = new WorkspaceTokenBroker(new NoBaseIssuer(), new FixedTime(T0), NullLogger<WorkspaceTokenBroker>.Instance);

        var r = await broker.GetAsync(Ravi, false, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Equal(WorkspaceReasonCodes.AgentNotEnrolled, r.ReasonCode);
        Assert.Empty(issuer.Calls);
    }

    private sealed class NoBaseIssuer : IWorkspaceTokenIssuer
    {
        public Uri? BaseAddress => null;
        public Task<ApiResponse> RequestAsync(string windowsUsername, int sessionId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("must not be called");
    }
}

public class PipeClientIdentityTests
{
    [Theory]
    [InlineData(@"VPS01\ravi", "ravi")]
    [InlineData("ravi", "ravi")]
    public void Bare_user_name(string account, string bare) =>
        Assert.Equal(bare, new PipeClientIdentity(1, 2, account, "S-1-5-21-1").BareUserName);

    [Theory]
    [InlineData(0, "S-1-5-21-1", false)]   // session 0 (services)
    [InlineData(2, "S-1-5-18", false)]     // LocalSystem
    [InlineData(2, "S-1-5-20", false)]     // NetworkService
    [InlineData(2, "S-1-5-21-1-1001", true)]
    public void Only_interactive_users_are_served(int session, string sid, bool expected) =>
        Assert.Equal(expected, new PipeClientIdentity(1, session, "x", sid).IsInteractiveUser);
}

public class WorkspaceHealthReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Supervisor_restart_counts_win_and_heartbeats_fill_gaps()
    {
        var report = new WorkspaceSupervisorReport
        {
            GeneratedAt = T0,
            Sessions =
            [
                new() { SessionId = 2, Username = "ravi", Status = WorkspaceComponentStatus.Healthy, RestartCount = 1, LastRecoveryAction = "restarted" },
                new() { SessionId = 5, Username = "asha", Status = WorkspaceComponentStatus.Degraded, ReasonCode = WorkspaceReasonCodes.CrashLoop, RestartCount = 3 },
            ]
        };
        var live = new List<WorkspaceSessionHealth>
        {
            new() { SessionId = 2, Username = "ravi", Version = "1.2.0", Status = WorkspaceComponentStatus.Healthy, HeartbeatAt = T0 },
            new() { SessionId = 7, Username = "dev", Version = "1.2.0", Status = WorkspaceComponentStatus.Healthy, HeartbeatAt = T0 },
        };

        var rows = WorkspaceHealthReporter.BuildComponents(report, live);

        Assert.Equal(["2", "5", "7"], rows.Select(r => r.SessionId));
        Assert.All(rows, r => Assert.Equal(WorkspaceComponents.WorkspaceUi, r.Component));
        var s2 = rows[0];
        Assert.Equal("1.2.0", s2.Version);
        Assert.Equal(1, s2.RestartCount);
        Assert.Equal("restarted", s2.LastRecoveryAction);
        Assert.Equal(WorkspaceComponentStatus.Degraded, rows[1].Status);
        Assert.Equal(WorkspaceReasonCodes.CrashLoop, rows[1].ReasonCode);
    }

    [Fact]
    public void Works_without_a_supervisor_report()
    {
        var rows = WorkspaceHealthReporter.BuildComponents(null, [new() { SessionId = 3, Status = WorkspaceComponentStatus.Healthy }]);
        Assert.Single(rows);
    }
}

public class WorkspaceHeartbeatsTests
{
    [Fact]
    public void Stale_heartbeat_becomes_unknown()
    {
        var hb = new WorkspaceHeartbeats();
        var t0 = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        hb.Record(new PipeClientIdentity(1, 2, "ravi", "S-1-5-21-1"), "1.0.0", WorkspaceSessionState.StateRunning, t0);

        Assert.Equal(WorkspaceComponentStatus.Healthy, Assert.Single(hb.Snapshot(t0.AddSeconds(30), TimeSpan.FromSeconds(90))).Status);
        Assert.Equal(WorkspaceComponentStatus.Unknown, Assert.Single(hb.Snapshot(t0.AddMinutes(5), TimeSpan.FromSeconds(90))).Status);
    }
}

public class WorkspacePipeRequestParsingTests
{
    [Theory]
    [InlineData("""{"action":"token","forceRefresh":true}""", "token", true)]
    [InlineData("""{"action":"hello","uiVersion":"1.0.0"}""", "hello", false)]
    public void Parses_requests(string json, string action, bool force)
    {
        var r = WorkspacePipeServer.Parse(json);
        Assert.NotNull(r);
        Assert.Equal(action, r!.Action);
        Assert.Equal(force, r.ForceRefresh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData(null)]
    public void Garbage_is_rejected(string? line) => Assert.Null(WorkspacePipeServer.Parse(line));
}

public class WorkspaceCommandQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private static PipeClientIdentity S(int session) => new(1, session, "u" + session, "S-1-5-21-" + session);

    [Fact]
    public void Broadcast_refresh_is_delivered_once_to_every_panel()
    {
        var hb = new WorkspaceHeartbeats();
        hb.Record(S(2), "1", WorkspaceSessionState.StateRunning, T0);
        hb.Record(S(3), "1", WorkspaceSessionState.StateRunning, T0);

        Assert.Equal(2, hb.Queue(WorkspacePanelCommands.Refresh, null, T0));
        Assert.Equal(WorkspacePanelCommands.Refresh, hb.TakeCommand(2, T0));
        Assert.Null(hb.TakeCommand(2, T0));
        Assert.Equal(WorkspacePanelCommands.Refresh, hb.TakeCommand(3, T0));
    }

    [Fact]
    public void Targeted_restart_reaches_only_that_session_and_supersedes_older_commands()
    {
        var hb = new WorkspaceHeartbeats();
        hb.Record(S(2), "1", WorkspaceSessionState.StateRunning, T0);
        hb.Record(S(3), "1", WorkspaceSessionState.StateRunning, T0);
        hb.Queue(WorkspacePanelCommands.Refresh, 3, T0);
        Assert.Equal(1, hb.Queue(WorkspacePanelCommands.Restart, 3, T0));

        Assert.Null(hb.TakeCommand(2, T0));
        Assert.Equal(WorkspacePanelCommands.Refresh, hb.TakeCommand(3, T0));
        Assert.Equal(WorkspacePanelCommands.Restart, hb.TakeCommand(3, T0));
        Assert.Null(hb.TakeCommand(3, T0));
    }

    [Fact]
    public void Commands_expire_and_new_panels_skip_old_ones()
    {
        var hb = new WorkspaceHeartbeats();
        hb.Queue(WorkspacePanelCommands.Restart, null, T0);
        Assert.Null(hb.TakeCommand(4, T0.AddMinutes(11)));          // expired

        hb.Queue(WorkspacePanelCommands.Restart, null, T0);
        hb.SkipPending(5);                                           // panel just started (hello)
        Assert.Null(hb.TakeCommand(5, T0));
    }

    [Theory]
    [InlineData("workspace.ui.restart", true)]
    [InlineData("workspace.policy.refresh", true)]
    [InlineData("workspace.anything", false)]
    [InlineData(null, false)]
    public void Only_known_workspace_commands_are_routed(string? type, bool expected) =>
        Assert.Equal(expected, WorkspaceCommandTypes.IsWorkspace(type));
}

public class SecureAccessBrokerTests
{
    private sealed class FakeSource(Func<SecureAccessRequest, ApiResponse> respond, bool configured = true) : ISecureAccessDecisionSource
    {
        public bool IsConfigured { get; } = configured;
        public List<SecureAccessRequest> Calls { get; } = [];
        public Func<SecureAccessRequest, ApiResponse> Respond { get; set; } = respond;
        public Task<ApiResponse> DecideAsync(SecureAccessRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static ApiResponse Ok(string decision, bool allow) =>
        new(HttpStatusCode.OK, $$"""{"decision":"{{decision}}","allow":{{(allow ? "true" : "false")}},"reason_code":"X","mfa_required":{{(decision == "MFA_REQUIRED" ? "true" : "false")}},"policy_version":3,"correlation_id":"c"}""", null, null);
    private static readonly ApiResponse Down = new(null, null, null, "connection refused");
    private static SecureAccessRequest Req(string u = @"VPS\ravi") => new() { Username = u, SessionId = 3 };

    [Fact]
    public async Task Passes_the_bare_username_and_returns_the_backend_decision()
    {
        var src = new FakeSource(_ => Ok("MFA_REQUIRED", false));
        var b = new SecureAccessBroker(src, new Clock(T0), NullLogger<SecureAccessBroker>.Instance);
        var d = await b.DecideAsync(Req(), CancellationToken.None);
        Assert.Equal("ravi", src.Calls[0].Username);
        Assert.False(d.Allow);
        Assert.True(d.MfaRequired);
        Assert.Equal(3, d.PolicyVersion);
    }

    [Fact]
    public async Task Offline_fails_closed_for_a_known_managed_seat_when_grace_is_off()
    {
        var src = new FakeSource(_ => Ok("ALLOW", true));
        var b = new SecureAccessBroker(src, new Clock(T0), NullLogger<SecureAccessBroker>.Instance);
        await b.DecideAsync(Req(), CancellationToken.None);
        src.Respond = _ => Down;
        var d = await b.DecideAsync(Req(), CancellationToken.None);
        Assert.False(d.Allow);
        Assert.Equal(SecureAccessDecisions.Unavailable, d.Decision);
    }

    [Fact]
    public async Task Offline_grace_allows_a_recent_allow_then_expires()
    {
        var clock = new Clock(T0);
        var src = new FakeSource(_ => Ok("ALLOW", true));
        var b = new SecureAccessBroker(src, clock, NullLogger<SecureAccessBroker>.Instance) { OfflineGrace = TimeSpan.FromMinutes(30) };
        await b.DecideAsync(Req(), CancellationToken.None);
        src.Respond = _ => Down;
        clock.Now = T0.AddMinutes(10);
        var d = await b.DecideAsync(Req(), CancellationToken.None);
        Assert.True(d.Allow);
        Assert.True(d.FromCache);
        clock.Now = T0.AddMinutes(40);
        Assert.False((await b.DecideAsync(Req(), CancellationToken.None)).Allow);
    }

    [Fact]
    public async Task An_mfa_or_deny_result_clears_the_cached_allow()
    {
        var src = new FakeSource(_ => Ok("ALLOW", true));
        var b = new SecureAccessBroker(src, new Clock(T0), NullLogger<SecureAccessBroker>.Instance) { OfflineGrace = TimeSpan.FromMinutes(30) };
        await b.DecideAsync(Req(), CancellationToken.None);
        src.Respond = _ => Ok("DENY", false);
        await b.DecideAsync(Req(), CancellationToken.None);
        src.Respond = _ => Down;
        Assert.False((await b.DecideAsync(Req(), CancellationToken.None)).Allow);
    }

    [Fact]
    public async Task Unknown_account_is_never_locked_out_during_an_outage()
    {
        var b = new SecureAccessBroker(new FakeSource(_ => Down), new Clock(T0), NullLogger<SecureAccessBroker>.Instance);
        var d = await b.DecideAsync(Req("Administrator"), CancellationToken.None);
        Assert.True(d.Allow);
        Assert.Equal(SecureAccessDecisions.NotManaged, d.Decision);
    }

    [Fact]
    public async Task Not_managed_accounts_from_the_backend_are_not_remembered_as_managed()
    {
        var src = new FakeSource(_ => Ok("NOT_MANAGED", true));
        var b = new SecureAccessBroker(src, new Clock(T0), NullLogger<SecureAccessBroker>.Instance);
        await b.DecideAsync(Req("Administrator"), CancellationToken.None);
        src.Respond = _ => Down;
        Assert.True((await b.DecideAsync(Req("Administrator"), CancellationToken.None)).Allow);
    }
}

public class SessionFeedReporterTests
{
    [Fact]
    public void Logon_time_is_utc_iso_truncated_to_the_minute_like_quser()
    {
        var t = new DateTime(2026, 11, 25, 4, 39, 47, DateTimeKind.Utc).ToFileTimeUtc();
        Assert.Equal("2026-11-25T04:39:00.0000000Z", SessionFeedReporter.FormatLogon(t));
        Assert.Null(SessionFeedReporter.FormatLogon(0));
    }

    [Fact]
    public void Signature_changes_on_reconnect_and_ignores_order()
    {
        var a = new SessionRow("ravi", "RDP-Tcp#3", "3", "Active", "x");
        var b = new SessionRow("asha", "", "4", "Disc", "y");
        Assert.Equal(SessionFeedReporter.Signature([a, b]), SessionFeedReporter.Signature([b, a]));
        Assert.NotEqual(SessionFeedReporter.Signature([a]), SessionFeedReporter.Signature([a with { SessionName = "RDP-Tcp#9" }]));
        Assert.NotEqual(SessionFeedReporter.Signature([a]), SessionFeedReporter.Signature([a with { State = "Disc", SessionName = "" }]));
    }

    [Fact]
    public void Enumerate_returns_the_current_session_on_this_machine()
    {
        var rows = SessionFeedReporter.Enumerate();
        Assert.All(rows, r => Assert.NotEqual("0", r.SessionId));
    }
}
