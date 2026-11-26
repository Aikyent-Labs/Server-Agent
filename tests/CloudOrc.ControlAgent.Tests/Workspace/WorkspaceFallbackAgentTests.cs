using System.Net;
using System.Net.Sockets;
using System.Text;
using CloudOrc.Agent.Contracts.Identity;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Transport;
using CloudOrc.ControlAgent.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Workspace;

/// <summary>Fallback switches on the agent: desired-state sync from CloudOrc (sticky offline) and the local admin CLI.</summary>
public sealed class WorkspaceFallbackAgentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "co-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _listener = new();
    private readonly BackendApiClient _api;
    private int _status = 200;
    private string _body = "{}";

    public WorkspaceFallbackAgentTests()
    {
        Directory.CreateDirectory(_dir);
        var port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);

        var controlOptions = Options.Create(new ControlAgentOptions { DataDirectory = _dir });
        var store = new EnrolledStateStore(controlOptions, NullLogger<EnrolledStateStore>.Instance);
        var identity = new AgentIdentity { AgentId = "a", ServerId = "s", MachineId = "m", MachineName = "n", AgentVersion = "1", Credential = "test-credential" };
        _api = new BackendApiClient(
            Options.Create(new Configuration.BackupOptions()),
            Options.Create(new BackendConnectionOptions { Url = $"ws://localhost:{port}/agent", DevelopmentAllowInsecureWs = true }),
            store,
            new AgentCredentialProvider(identity, store),
            TimeProvider.System,
            NullLogger<BackendApiClient>.Instance);
    }

    public void Dispose()
    {
        _listener.Stop();
        _api.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string TeamPath => Path.Combine(_dir, "fallback-team.json");
    private string LocalPath => Path.Combine(_dir, "fallback-local.json");

    private WorkspaceFallbackSync Sync() => new(_api, TimeProvider.System, NullLogger<WorkspaceFallbackSync>.Instance) { TeamPath = TeamPath };

    [Fact]
    public async Task A_team_switch_from_CloudOrc_is_written_and_applies_to_the_user()
    {
        _body = """{"revision":"r1","rules":[{"id":"x","scope":"USER","username":"ravi","panelOff":true,"reason":"bad release"}]}""";
        Assert.True(await Sync().SyncOnceAsync(CancellationToken.None));

        var doc = WorkspaceFallback.Load(TeamPath);
        Assert.NotNull(doc);
        var d = WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, doc));
        Assert.True(d.PanelOff);
        Assert.Equal("bad release", d.Reason);
        Assert.False(WorkspaceFallback.Resolve("sita", (WorkspaceFallbackSources.Team, doc)).Any);
    }

    [Fact]
    public async Task Switches_stay_in_force_when_CloudOrc_cannot_be_reached_and_clear_when_it_says_so()
    {
        var sync = Sync();
        _body = """{"revision":"r1","rules":[{"scope":"GLOBAL","panelOff":true,"reason":"incident"}]}""";
        await sync.SyncOnceAsync(CancellationToken.None);

        _status = 503;
        Assert.False(await sync.SyncOnceAsync(CancellationToken.None));
        Assert.True(WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Team, WorkspaceFallback.Load(TeamPath))).PanelOff);

        _status = 404; // old backend without the route: never clears
        Assert.False(await sync.SyncOnceAsync(CancellationToken.None));
        Assert.True(File.Exists(TeamPath));

        _status = 200;
        _body = """{"revision":"r2","rules":[]}""";
        Assert.True(await sync.SyncOnceAsync(CancellationToken.None));
        Assert.False(File.Exists(TeamPath));
    }

    [Fact]
    public void Local_cli_turns_the_panel_off_and_on_for_one_user_or_the_server()
    {
        Assert.Equal(0, Cli("fallback", "on", "--user", "ravi", "--reason", "agent offline, runbook R9"));
        Assert.Equal(0, Cli("fallback", "on", "--alt-tab-only", "--reason", "alt-tab issue"));

        var local = WorkspaceFallback.Load(LocalPath);
        var ravi = WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Local, local));
        var sita = WorkspaceFallback.Resolve("sita", (WorkspaceFallbackSources.Local, local));
        Assert.True(ravi.PanelOff);
        Assert.Equal(WorkspaceFallbackSources.Local, ravi.Source);
        Assert.False(sita.PanelOff);
        Assert.True(sita.AltTabOff);

        Assert.Equal(0, Cli("fallback", "off", "--user", "ravi"));
        Assert.False(WorkspaceFallback.Resolve("ravi", (WorkspaceFallbackSources.Local, WorkspaceFallback.Load(LocalPath))).PanelOff);

        Assert.Equal(0, Cli("fallback", "off", "--all"));
        Assert.False(File.Exists(LocalPath));
    }

    [Fact]
    public void Local_cli_needs_an_administrator_and_a_reason()
    {
        Assert.Equal(5, WorkspaceFallbackCli.Run(["fallback", "on", "--reason", "some reason"], LocalPath, TeamPath, () => false));
        Assert.Equal(2, Cli("fallback", "on"));
        Assert.Equal(2, Cli("fallback", "on", "--reason", "x"));
        Assert.False(File.Exists(LocalPath));
        Assert.Equal(0, Cli("fallback", "status", "--user", "ravi"));
        Assert.Equal(1, Cli("nonsense"));
    }

    private int Cli(params string[] args) => WorkspaceFallbackCli.Run(args, LocalPath, TeamPath, () => true);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(_status == 200 ? _body : "{\"error\":\"x\"}");
            ctx.Response.StatusCode = _status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
