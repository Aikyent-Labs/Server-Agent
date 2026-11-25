using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>Abstraction over the backend decision call so the broker policy is unit-testable.</summary>
public interface ISecureAccessDecisionSource
{
    bool IsConfigured { get; }

    Task<ApiResponse> DecideAsync(SecureAccessRequest request, CancellationToken cancellationToken);
}

public sealed class BackendSecureAccessDecisionSource(BackendApiClient api) : ISecureAccessDecisionSource
{
    public const string Route = "api/v1/remote-workspace/agent/secure-access/decision";

    public bool IsConfigured => api.IsConfigured;

    public Task<ApiResponse> DecideAsync(SecureAccessRequest request, CancellationToken cancellationToken) =>
        api.PostJsonAsync(Route, new Body(request.Username, request.SessionId, request.ClientIp), cancellationToken);

    private sealed record Body(
        [property: JsonPropertyName("windows_username")] string WindowsUsername,
        [property: JsonPropertyName("session_id")] int? SessionId,
        [property: JsonPropertyName("client_ip")] string? ClientIp);
}

/// <summary>
/// Secure-Access Broker (handoff v1.2 §29–30): answers "may this Windows user start an interactive
/// session?" for the native Credential Provider shim. Policy is CloudOrc's (backend); the broker only
/// asks, caches and applies the offline rules:
///  - managed seat + CloudOrc reachable  → backend decision (ALLOW / MFA_REQUIRED / DENY);
///  - CloudOrc unreachable               → a cached ALLOW within the configured offline grace (default off),
///                                         otherwise fail closed for a known managed seat (§29.3);
///  - account CloudOrc never manages     → NOT_MANAGED (Windows decides) — a CloudOrc outage can never lock
///                                         the server's own administrator out.
/// </summary>
public sealed class SecureAccessBroker(ISecureAccessDecisionSource source, TimeProvider time, ILogger<SecureAccessBroker> logger)
{
    private readonly ConcurrentDictionary<string, (SecureAccessDecision Decision, DateTimeOffset At)> _lastAllow = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _managed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offline grace for a previously allowed, low-risk user. 0 = disabled (fail closed).</summary>
    public TimeSpan OfflineGrace { get; init; } = TimeSpan.Zero;

    public async Task<SecureAccessDecision> DecideAsync(SecureAccessRequest request, CancellationToken cancellationToken)
    {
        var user = Bare(request.Username);
        if (user.Length == 0)
        {
            return new SecureAccessDecision { Allow = false, Decision = SecureAccessDecisions.Deny, ReasonCode = "INVALID_REQUEST" };
        }

        if (source.IsConfigured)
        {
            ApiResponse response;
            try
            {
                response = await source.DecideAsync(new SecureAccessRequest { Username = user, SessionId = request.SessionId, ClientIp = request.ClientIp }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Secure-access decision call failed.");
                response = new ApiResponse(null, null, null, ex.Message);
            }

            if (response.IsSuccess && Parse(response.Body) is { } decision)
            {
                if (decision.Decision != SecureAccessDecisions.NotManaged) _managed[user] = 1;
                if (decision.Allow && decision.Decision == SecureAccessDecisions.Allow) _lastAllow[user] = (decision, time.GetUtcNow());
                else _lastAllow.TryRemove(user, out _);
                return decision;
            }

            logger.LogWarning("Secure-access decision unavailable for a logon ({Outcome}).", response.Describe());
        }

        return Offline(user);
    }

    internal SecureAccessDecision Offline(string user)
    {
        var now = time.GetUtcNow();
        if (OfflineGrace > TimeSpan.Zero && _lastAllow.TryGetValue(user, out var cached) && now - cached.At <= OfflineGrace)
        {
            return new SecureAccessDecision
            {
                Allow = true,
                Decision = SecureAccessDecisions.Allow,
                ReasonCode = "OFFLINE_GRACE",
                PolicyVersion = cached.Decision.PolicyVersion,
                ExpiresAt = cached.At + OfflineGrace,
                CorrelationId = cached.Decision.CorrelationId,
                FromCache = true
            };
        }

        if (_managed.ContainsKey(user))
        {
            return new SecureAccessDecision { Allow = false, Decision = SecureAccessDecisions.Unavailable, ReasonCode = "SECURE_ACCESS_UNAVAILABLE" };
        }

        // Never seen as a CloudOrc seat: do not block Windows' own accounts during an outage.
        return new SecureAccessDecision { Allow = true, Decision = SecureAccessDecisions.NotManaged, ReasonCode = "NOT_MANAGED_OFFLINE" };
    }

    internal static SecureAccessDecision? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var decision = r.TryGetProperty("decision", out var d) ? d.GetString() : null;
            if (decision is null) return null;
            return new SecureAccessDecision
            {
                Allow = r.TryGetProperty("allow", out var a) && a.ValueKind == JsonValueKind.True,
                Decision = decision,
                ReasonCode = r.TryGetProperty("reason_code", out var rc) ? rc.GetString() : null,
                MfaRequired = r.TryGetProperty("mfa_required", out var m) && m.ValueKind == JsonValueKind.True,
                PolicyVersion = r.TryGetProperty("policy_version", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt32() : null,
                ExpiresAt = r.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), out var exp) ? exp : null,
                CorrelationId = r.TryGetProperty("correlation_id", out var c) ? c.GetString() : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Bare(string? name)
    {
        var s = (name ?? string.Empty).Trim();
        var i = s.LastIndexOf('\\');
        return i >= 0 ? s[(i + 1)..] : s;
    }
}

/// <summary>Local pipe for the Credential Provider shim. SYSTEM (LogonUI) and Administrators only; network denied.</summary>
[SupportedOSPlatform("windows")]
public sealed class SecureAccessPipeServer(SecureAccessBroker broker, ILogger<SecureAccessPipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Secure-access broker pipe listening on '{Pipe}'.", SecureAccessProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                server = NamedPipeServerStreamAcl.Create(SecureAccessProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
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
                logger.LogWarning(ex, "Secure-access pipe error; recreating.");
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
                // LogonUI must never wait long: answer within 20 s (CloudOrc call has its own shorter timeout).
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var buffer = new byte[SecureAccessProtocol.MaxMessageBytes];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await pipe.ReadAsync(buffer.AsMemory(total, 1), timeout.Token).ConfigureAwait(false);
                    if (read == 0 || buffer[total] == (byte)'\n') break;
                    total += read;
                }

                SecureAccessDecision decision;
                SecureAccessRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<SecureAccessRequest>(Encoding.UTF8.GetString(buffer, 0, total), WorkspaceProtocol.JsonOptions);
                }
                catch (JsonException)
                {
                    request = null;
                }

                decision = request is null
                    ? new SecureAccessDecision { Allow = false, Decision = SecureAccessDecisions.Deny, ReasonCode = "INVALID_REQUEST" }
                    : await broker.DecideAsync(request, timeout.Token).ConfigureAwait(false);

                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(decision, WorkspaceProtocol.JsonOptions) + "\n");
                await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "Secure-access client disconnected.");
            }
        }
    }
}
