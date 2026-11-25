using System.Net;
using System.Text;
using System.Windows;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Tests;

public class WindowGroupingTests
{
    private static readonly List<PublishedApp> Apps =
    [
        new() { Id = "tally", Name = "TallyPrime", ExePath = @"C:\Program Files\TallyPrime\tally.exe" },
        new() { Id = "excel", Name = "Excel", ExePath = @"C:\Office\EXCEL.EXE" },
        new() { Id = "data", Name = "Company Data", Type = "folder", StartupPath = @"D:\Data" },
    ];

    [Fact]
    public void Groups_multiple_instances_of_one_published_app_with_a_count()
    {
        var windows = new[]
        {
            new TopWindow(1, 10, @"C:\Program Files\TallyPrime\tally.exe", "ABC Traders - TallyPrime", WindowState2.Active),
            new TopWindow(2, 11, @"c:\program files\tallyprime\TALLY.EXE", "XYZ Pvt Ltd - TallyPrime", WindowState2.Minimized),
            new TopWindow(3, 12, @"C:\Program Files\TallyPrime\tally.exe", "Demo Company - TallyPrime", WindowState2.Running),
            new TopWindow(4, 20, @"C:\Office\EXCEL.EXE", "Report.xlsx - Excel", WindowState2.Running),
            new TopWindow(5, 30, @"C:\Windows\notepad.exe", "notes - Notepad", WindowState2.Running),
        };

        var groups = WindowGrouping.Group(windows, Apps);

        Assert.Equal(3, groups.Count);
        var tally = Assert.Single(groups, g => g.PublishedAppId == "tally");
        Assert.Equal(3, tally.Count);
        Assert.True(tally.AnyActive);
        Assert.Equal("Excel", groups[0].Name); // published apps first, alphabetical
        Assert.Equal("TallyPrime", groups[1].Name);
        Assert.False(groups[2].IsPublished); // notepad is shown, but after published apps
        Assert.Equal("Notepad", groups[2].Name);
    }

    [Fact]
    public void Same_exe_name_in_another_folder_still_maps_to_the_published_app()
    {
        var groups = WindowGrouping.Group([new TopWindow(1, 1, @"D:\Tally2\tally.exe", "Copy", WindowState2.Running)], Apps);
        Assert.Equal("tally", Assert.Single(groups).PublishedAppId);
    }

    [Fact]
    public void Title_is_display_only_never_identity()
    {
        var groups = WindowGrouping.Group([new TopWindow(1, 1, @"C:\Windows\notepad.exe", "TallyPrime", WindowState2.Running)], Apps);
        Assert.Null(Assert.Single(groups).PublishedAppId);
    }

    [Theory]
    [InlineData("ABC Traders - TallyPrime", "TallyPrime", "ABC Traders")]
    [InlineData("TallyPrime", "TallyPrime", "TallyPrime")]
    [InlineData("Report.xlsx | Excel", "Excel", "Report.xlsx")]
    [InlineData("", "Excel", "Excel")]
    public void Instance_title_strips_the_app_suffix(string title, string app, string expected) =>
        Assert.Equal(expected, WindowGrouping.InstanceTitle(title, app));
}

public class PanelLayoutTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040); // 40px taskbar at the bottom

    [Fact]
    public void Handle_sits_on_the_right_edge_inside_the_work_area()
    {
        var r = PanelLayout.Compute(WorkArea, PanelMode.Handle, leftSide: false, railItemCount: 9);
        Assert.Equal(1920 - PanelLayout.HandleWidth, r.Left);
        Assert.True(r.Bottom <= WorkArea.Bottom);
        Assert.True(r.Top >= WorkArea.Top);
    }

    [Fact]
    public void Expanded_panel_never_covers_the_taskbar()
    {
        var small = new Rect(0, 0, 1280, 680);
        var r = PanelLayout.Compute(small, PanelMode.Expanded, leftSide: false, railItemCount: 9);
        Assert.True(r.Bottom <= small.Bottom);
        Assert.True(r.Top >= small.Top);
        Assert.Equal(PanelLayout.RailWidth + PanelLayout.PanelWidth, r.Width);
    }

    [Fact]
    public void Left_side_preference_anchors_to_the_left()
    {
        var r = PanelLayout.Compute(new Rect(100, 0, 1820, 1040), PanelMode.Rail, leftSide: true, railItemCount: 9);
        Assert.Equal(100, r.Left);
    }
}

public class AutoHidePolicyTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(4);

    [Fact]
    public void Collapses_after_idle_delay() =>
        Assert.True(AutoHidePolicy.ShouldCollapse(false, false, false, false, false, false, TimeSpan.FromSeconds(5), Delay));

    [Theory]
    [InlineData(true, false, false, false, false, false)]  // pinned
    [InlineData(false, true, false, false, false, false)]  // mouse over
    [InlineData(false, false, true, false, false, false)]  // typing
    [InlineData(false, false, false, true, false, false)]  // confirm dialog
    [InlineData(false, false, false, false, true, false)]  // critical alert
    [InlineData(false, false, false, false, false, true)]  // remote assistance
    public void Never_collapses_while_something_important_is_open(bool pinned, bool mouse, bool focus, bool modal, bool critical, bool assistance) =>
        Assert.False(AutoHidePolicy.ShouldCollapse(pinned, mouse, focus, modal, critical, assistance, TimeSpan.FromMinutes(5), Delay));

    [Fact]
    public void Does_not_collapse_before_the_delay() =>
        Assert.False(AutoHidePolicy.ShouldCollapse(false, false, false, false, false, false, TimeSpan.FromSeconds(1), Delay));
}

public class WorkspaceApiTests
{
    private sealed class FakePipe(params WorkspacePipeResponse?[] responses) : IAgentPipe
    {
        private int _i;
        public int Calls => _i;
        public List<bool> ForceFlags { get; } = [];

        public Task<WorkspacePipeResponse?> SendAsync(WorkspacePipeRequest request, CancellationToken cancellationToken)
        {
            ForceFlags.Add(request.ForceRefresh);
            var r = responses[Math.Min(_i, responses.Length - 1)];
            _i++;
            return Task.FromResult(r);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static WorkspacePipeResponse Token(string t) => new()
    {
        Ok = true,
        Token = t,
        ApiBaseUrl = "https://api.example.test",
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        SeatFound = true
    };

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sends_bearer_token_to_the_remote_workspace_route()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"apps":[{"id":"a1","name":"Tally","exePath":"C:\\T\\tally.exe"}]}"""));
        using var api = new WorkspaceApi(new FakePipe(Token("tok-1")), handler);

        var apps = await api.GetAppsAsync(CancellationToken.None);

        Assert.Equal("Tally", Assert.Single(apps.Apps).Name);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://api.example.test/api/v1/remote-workspace/apps", req.RequestUri!.ToString());
        Assert.Equal("tok-1", req.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Refreshes_the_token_once_on_401()
    {
        var calls = 0;
        var handler = new FakeHandler(r => ++calls == 1
            ? Json(HttpStatusCode.Unauthorized, """{"error":{"code":"WORKSPACE_TOKEN_INVALID","message":"expired"}}""")
            : Json(HttpStatusCode.OK, """{"items":[],"unread":0}"""));
        var pipe = new FakePipe(Token("old"), Token("new"));
        using var api = new WorkspaceApi(pipe, handler);

        await api.GetAlertsAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("new", handler.Requests[1].Headers.Authorization!.Parameter);
        Assert.Equal([false, true], pipe.ForceFlags);
    }

    [Fact]
    public async Task Surfaces_backend_error_code_and_message()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Forbidden, """{"error":{"code":"POLICY_BLOCKED","message":"Only your workspace administrator can start a backup"}}"""));
        using var api = new WorkspaceApi(new FakePipe(Token("t")), handler);

        var ex = await Assert.ThrowsAsync<WorkspaceApiException>(() => api.RunBackupAsync(CancellationToken.None));

        Assert.Equal("POLICY_BLOCKED", ex.Code);
        Assert.Contains("administrator", ex.Message);
    }

    [Fact]
    public async Task Agent_offline_is_reported_without_calling_the_backend()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        using var api = new WorkspaceApi(new FakePipe((WorkspacePipeResponse?)null), handler);

        var ex = await Assert.ThrowsAsync<WorkspaceApiException>(() => api.GetContextAsync(CancellationToken.None));

        Assert.Equal(WorkspaceReasonCodes.AgentOffline, ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Reuses_a_valid_token()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var pipe = new FakePipe(Token("t"));
        using var api = new WorkspaceApi(pipe, handler);

        await api.GetCapabilitiesAsync(CancellationToken.None);
        await api.GetCapabilitiesAsync(CancellationToken.None);

        Assert.Equal(1, pipe.Calls);
    }

    [Theory]
    [InlineData("""{"error":{"code":"X","message":"m"}}""", "X", "m")]
    [InlineData("""{"code":"Y","message":"n"}""", "Y", "n")]
    [InlineData("<html>bad gateway</html>", null, null)]
    public void Parses_error_bodies(string body, string? code, string? message) =>
        Assert.Equal((code, message), WorkspaceApi.ParseError(body));
}

public class TimeTextTests
{
    [Fact]
    public void Formats_relative_times()
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("never", TimeText.Ago(null, now));
        Assert.Equal("just now", TimeText.Ago(now.AddSeconds(-20), now));
        Assert.Equal("5 min ago", TimeText.Ago(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", TimeText.Ago(now.AddHours(-3), now));
        Assert.Equal("2 d ago", TimeText.Ago(now.AddDays(-2), now));
    }
}

public class AssistancePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static AssistanceRequest R(string status, int leaseMin = 15) => new() { Id = "a1", Status = status, LeaseExpiresAt = T0.AddMinutes(leaseMin) };

    [Fact]
    public void Pending_request_asks_for_consent() =>
        Assert.Equal(AssistancePolicy.Step.AskConsent, AssistancePolicy.Next(R("PENDING"), false, null, null, T0));

    [Fact]
    public void Granted_without_shadow_shows_the_banner() =>
        Assert.Equal(AssistancePolicy.Step.ShowBanner, AssistancePolicy.Next(R("GRANTED"), false, null, null, T0));

    [Fact]
    public void Shadow_seen_marks_active() =>
        Assert.Equal(AssistancePolicy.Step.MarkActive, AssistancePolicy.Next(R("GRANTED"), true, T0, null, T0));

    [Fact]
    public void Lease_end_stops_access_even_while_shadowing() =>
        Assert.Equal(AssistancePolicy.Step.EndLeaseExpired, AssistancePolicy.Next(R("ACTIVE"), true, T0, null, T0.AddMinutes(16)));

    [Fact]
    public void Shadow_gone_briefly_is_not_ended_but_gone_for_long_is() 
    {
        Assert.Equal(AssistancePolicy.Step.ShowBanner, AssistancePolicy.Next(R("ACTIVE"), false, T0, T0.AddSeconds(30), T0.AddSeconds(33)));
        Assert.Equal(AssistancePolicy.Step.EndShadowGone, AssistancePolicy.Next(R("ACTIVE"), false, T0, T0.AddSeconds(30), T0.AddSeconds(40)));
    }

    [Theory]
    [InlineData("DENIED")]
    [InlineData("ENDED")]
    [InlineData("EXPIRED")]
    public void Closed_requests_do_nothing(string status) =>
        Assert.Equal(AssistancePolicy.Step.None, AssistancePolicy.Next(R(status), false, null, null, T0));
}
