using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

public sealed record WorkspaceTokenResult(
    bool Ok,
    string? Token,
    DateTimeOffset? ExpiresAt,
    string? ApiBaseUrl,
    string? CorrelationId,
    bool SeatFound,
    string? ReasonCode,
    string? Error);

/// <summary>Abstraction over the control-plane call so the broker is unit-testable.</summary>
public interface IWorkspaceTokenIssuer
{
    Uri? BaseAddress { get; }

    Task<ApiResponse> RequestAsync(string windowsUsername, int sessionId, CancellationToken cancellationToken);
}

public sealed class BackendWorkspaceTokenIssuer(BackendApiClient api) : IWorkspaceTokenIssuer
{
    public const string Route = "api/v1/remote-workspace/agent/token";

    public Uri? BaseAddress => api.BaseAddress;

    public Task<ApiResponse> RequestAsync(string windowsUsername, int sessionId, CancellationToken cancellationToken) =>
        api.PostJsonAsync(Route, new TokenRequest(windowsUsername, sessionId), cancellationToken);

    private sealed record TokenRequest(
        [property: JsonPropertyName("windows_username")] string WindowsUsername,
        [property: JsonPropertyName("session_id")] int SessionId);
}

/// <summary>
/// Gets and caches the short-lived WorkspaceUI token per (user SID, Windows session). The agent
/// vouches for the identity it read from the OS; the backend decides what that user may see.
/// </summary>
public sealed class WorkspaceTokenBroker(IWorkspaceTokenIssuer issuer, TimeProvider time, ILogger<WorkspaceTokenBroker> logger)
{
    /// <summary>Refresh when less than this much lifetime is left.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, WorkspaceTokenResult> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WorkspaceTokenResult> GetAsync(PipeClientIdentity caller, bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = $"{caller.UserSid}|{caller.SessionId}";
        if (!forceRefresh && TryCached(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && TryCached(key, out cached))
            {
                return cached;
            }

            var result = await RequestAsync(caller, cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                _cache[key] = result;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops tokens of sessions that no longer exist (called by the pipe server periodically).</summary>
    public void Forget(int sessionId)
    {
        foreach (var key in _cache.Keys.Where(k => k.EndsWith($"|{sessionId}", StringComparison.Ordinal)).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    private bool TryCached(string key, out WorkspaceTokenResult result)
    {
        if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt is { } exp && exp - time.GetUtcNow() > RefreshMargin)
        {
            result = hit;
            return true;
        }

        result = null!;
        return false;
    }

    private async Task<WorkspaceTokenResult> RequestAsync(PipeClientIdentity caller, CancellationToken cancellationToken)
    {
        var baseUrl = issuer.BaseAddress?.ToString().TrimEnd('/');
        if (baseUrl is null)
        {
            return new WorkspaceTokenResult(false, null, null, null, null, false, WorkspaceReasonCodes.AgentNotEnrolled,
                "This server is not connected to CloudOrc yet.");
        }

        var response = await issuer.RequestAsync(caller.BareUserName, caller.SessionId, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            logger.LogWarning("Workspace token request for session {Session} failed: {Outcome}.", caller.SessionId, response.Describe());
            var reason = response.IsNetworkFailure || response.IsTransient
                ? WorkspaceReasonCodes.BackendUnavailable
                : response.StatusCode == HttpStatusCode.Forbidden ? WorkspaceReasonCodes.AgentNotEnrolled : WorkspaceReasonCodes.BackendUnavailable;
            return new WorkspaceTokenResult(false, null, null, baseUrl, null, false, reason, "CloudOrc is not reachable right now.");
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body ?? "{}");
            var root = doc.RootElement;
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
            DateTimeOffset? expires = root.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), out var parsed) ? parsed : null;
            var correlation = root.TryGetProperty("correlation_id", out var c) ? c.GetString() : null;
            var seat = root.TryGetProperty("seat_found", out var s) && s.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(token))
            {
                return new WorkspaceTokenResult(false, null, null, baseUrl, null, false, WorkspaceReasonCodes.BackendUnavailable, "Empty token response.");
            }

            return new WorkspaceTokenResult(true, token, expires ?? time.GetUtcNow().AddMinutes(10), baseUrl, correlation, seat, null, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Workspace token response was not valid JSON.");
            return new WorkspaceTokenResult(false, null, null, baseUrl, null, false, WorkspaceReasonCodes.BackendUnavailable, "Invalid token response.");
        }
    }
}
