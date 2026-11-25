using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Versioning;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Enrollment;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// Local IPC for the per-user WorkspaceUI (handoff §47): one JSON line in, one JSON line out.
/// Local authenticated users only, network denied. The caller's identity and session come from
/// the OS (<see cref="PipeClientIdentity"/>); a request can never name another user.
/// The pipe hands out read/display context and a short-lived backend token only — no privileged
/// Windows action is reachable through it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspacePipeServer(
    EnrolledStateStore store,
    BackendLinkState link,
    IServiceProvider services,
    WorkspaceHeartbeats heartbeats,
    TimeProvider time,
    ILogger<WorkspacePipeServer> logger) : BackgroundService
{
    private readonly string _agentVersion = AgentVersionInfo.GetVersion(typeof(WorkspacePipeServer).Assembly);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Workspace pipe listening on '{Pipe}'.", WorkspaceProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    WorkspaceProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    CreateSecurity());

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                var connection = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connection, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Workspace pipe error; recreating.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));

                var line = await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
                WorkspacePipeResponse response;
                PipeClientIdentity? caller = null;
                try
                {
                    caller = PipeClientIdentity.Resolve(pipe);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Workspace pipe: could not identify the client.");
                }

                if (caller is null || !caller.IsInteractiveUser)
                {
                    response = new WorkspacePipeResponse
                    {
                        Ok = false,
                        ReasonCode = WorkspaceReasonCodes.IdentityMismatch,
                        Error = "The workspace panel must run inside a signed-in Windows user session."
                    };
                }
                else
                {
                    response = await HandleAsync(Parse(line), caller, timeout.Token).ConfigureAwait(false);
                }

                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, WorkspaceProtocol.JsonOptions) + "\n");
                await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "Workspace client disconnected.");
            }
        }
    }

    internal async Task<WorkspacePipeResponse> HandleAsync(WorkspacePipeRequest? request, PipeClientIdentity caller, CancellationToken cancellationToken)
    {
        switch (request?.Action)
        {
            case WorkspaceProtocol.ActionHello:
                // A fresh panel starts with current policy: drop anything queued before it started.
                heartbeats.Record(caller, request.UiVersion, request.State ?? WorkspaceSessionState.StateRunning, time.GetUtcNow());
                heartbeats.SkipPending(caller.SessionId);
                return Hello(caller);

            case WorkspaceProtocol.ActionHeartbeat:
                heartbeats.Record(caller, request.UiVersion, request.State ?? WorkspaceSessionState.StateRunning, time.GetUtcNow());
                return Hello(caller, heartbeats.TakeCommand(caller.SessionId, time.GetUtcNow()));

            case WorkspaceProtocol.ActionToken:
                var broker = services.GetService(typeof(WorkspaceTokenBroker)) as WorkspaceTokenBroker;
                if (broker is null)
                {
                    return new WorkspacePipeResponse
                    {
                        Ok = false,
                        ReasonCode = WorkspaceReasonCodes.AgentNotEnrolled,
                        Error = "This server is not connected to CloudOrc."
                    };
                }

                var result = await broker.GetAsync(caller, request.ForceRefresh, cancellationToken).ConfigureAwait(false);
                return new WorkspacePipeResponse
                {
                    Ok = result.Ok,
                    Error = result.Error,
                    ReasonCode = result.ReasonCode,
                    Token = result.Token,
                    ExpiresAt = result.ExpiresAt,
                    ApiBaseUrl = result.ApiBaseUrl,
                    CorrelationId = result.CorrelationId,
                    SeatFound = result.SeatFound,
                    AgentVersion = _agentVersion,
                    AgentConnected = link.IsConnected,
                    WindowsUser = caller.BareUserName,
                    SessionId = caller.SessionId
                };

            default:
                return new WorkspacePipeResponse { Ok = false, Error = "Unknown request." };
        }
    }

    private WorkspacePipeResponse Hello(PipeClientIdentity caller, string? command = null) => new()
    {
        Ok = true,
        Command = command,
        AgentVersion = _agentVersion,
        AgentConnected = link.IsConnected,
        AgentEnrolled = store.TryLoad() is not null,
        LastContactAt = link.LastContactAt,
        WindowsUser = caller.BareUserName,
        SessionId = caller.SessionId
    };

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    internal static WorkspacePipeRequest? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkspacePipeRequest>(line, WorkspaceProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[WorkspaceProtocol.MaxMessageBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer[total] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer, 0, total);
            }

            total += read;
        }

        return total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}

/// <summary>Latest panel heartbeat per Windows session (in memory; reported by <see cref="WorkspaceHealthReporter"/>).</summary>
public sealed class WorkspaceHeartbeats
{
    /// <summary>A queued command is delivered to panels that heartbeat within this time, then dropped.</summary>
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<int, WorkspaceSessionHealth> _bySession = new();
    private readonly object _commandSync = new();
    private readonly List<(long Seq, string Command, int? Session, DateTimeOffset Expires)> _commands = [];
    private readonly Dictionary<int, long> _delivered = [];
    private long _seq;

    /// <summary>Queues a panel command for one session (or all). Returns how many live panels it targets now.</summary>
    public int Queue(string command, int? sessionId, DateTimeOffset now)
    {
        lock (_commandSync)
        {
            _commands.RemoveAll(c => c.Expires <= now);
            _commands.Add((++_seq, command, sessionId, now + CommandLifetime));
        }

        return _bySession.Values.Count(h => sessionId is null || h.SessionId == sessionId);
    }

    /// <summary>Marks everything queued so far as delivered for this session (a new panel already has fresh state).</summary>
    public void SkipPending(int sessionId)
    {
        lock (_commandSync)
        {
            _delivered[sessionId] = _seq;
        }
    }

    /// <summary>Next undelivered command for this session (each command is delivered once per session).</summary>
    public string? TakeCommand(int sessionId, DateTimeOffset now)
    {
        lock (_commandSync)
        {
            _commands.RemoveAll(c => c.Expires <= now);
            var last = _delivered.GetValueOrDefault(sessionId);
            var next = _commands.FirstOrDefault(c => c.Seq > last && (c.Session is null || c.Session == sessionId));
            if (next.Command is null)
            {
                return null;
            }

            // A restart makes every other queued command for this session pointless (the new panel loads fresh state).
            _delivered[sessionId] = next.Command == WorkspacePanelCommands.Restart
                ? _commands.Where(c => c.Session is null || c.Session == sessionId).Max(c => c.Seq)
                : next.Seq;
            return next.Command;
        }
    }

    public void Record(PipeClientIdentity caller, string? version, string state, DateTimeOffset at)
    {
        _bySession[caller.SessionId] = new WorkspaceSessionHealth
        {
            SessionId = caller.SessionId,
            Username = caller.BareUserName,
            Version = version,
            Status = state == WorkspaceSessionState.StateRunning ? WorkspaceComponentStatus.Healthy : WorkspaceComponentStatus.Stopped,
            HeartbeatAt = at
        };
    }

    public IReadOnlyList<WorkspaceSessionHealth> Snapshot(DateTimeOffset now, TimeSpan maxAge)
    {
        foreach (var stale in _bySession.Where(kv => kv.Value.HeartbeatAt is { } h && now - h > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
        {
            _bySession.TryRemove(stale, out _);
        }

        return _bySession.Values
            .Select(h => h.HeartbeatAt is { } at && now - at > maxAge
                ? new WorkspaceSessionHealth
                {
                    SessionId = h.SessionId,
                    Username = h.Username,
                    Version = h.Version,
                    Status = WorkspaceComponentStatus.Unknown,
                    HeartbeatAt = h.HeartbeatAt
                }
                : h)
            .ToList();
    }
}
