using System.Text.Json;
using System.Text.Json.Serialization;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// Reports per-session WorkspaceUI health to CloudOrc (handoff §31, §48, RW-06). Sources:
/// the Watch supervisor's report file (authoritative for restarts / crash loops — it keeps
/// working even when this agent is down) merged with live pipe heartbeats. Best effort: a
/// failed report never affects anything else the agent does.
/// </summary>
public sealed class WorkspaceHealthReporter(
    BackendApiClient api,
    WorkspaceHeartbeats heartbeats,
    TimeProvider time,
    ILogger<WorkspaceHealthReporter> logger) : BackgroundService
{
    public const string Route = "api/v1/remote-workspace/agent/health";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromSeconds(90);
    private string? _lastPayload;
    private DateTimeOffset _lastSentAt;

    public static string SupervisorReportPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudOrc", "Workspace", WorkspaceSupervisorReport.FileName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so the panel heartbeats of an already-logged-on user arrive first.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReportOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Workspace health report failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task ReportOnceAsync(CancellationToken cancellationToken)
    {
        if (!api.IsConfigured)
        {
            return;
        }

        var components = BuildComponents(ReadSupervisorReport(), heartbeats.Snapshot(time.GetUtcNow(), HeartbeatMaxAge));
        if (components.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new { components }, BackendApiClient.SnakeCaseJson);
        // Unchanged state is re-sent at most every 5 minutes (keeps observed_at fresh without chatter).
        if (payload == _lastPayload && time.GetUtcNow() - _lastSentAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        var response = await api.PostJsonStringAsync(Route, payload, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            _lastPayload = payload;
            _lastSentAt = time.GetUtcNow();
        }
        else
        {
            logger.LogDebug("Workspace health report not accepted: {Outcome}.", response.Describe());
        }
    }

    /// <summary>Supervisor rows win (restart counts, crash loop); heartbeats fill in sessions the supervisor has not seen.</summary>
    internal static List<HealthComponent> BuildComponents(WorkspaceSupervisorReport? report, IReadOnlyList<WorkspaceSessionHealth> live)
    {
        var bySession = new Dictionary<int, WorkspaceSessionHealth>();
        foreach (var h in live)
        {
            bySession[h.SessionId] = h;
        }

        foreach (var s in report?.Sessions ?? [])
        {
            if (bySession.TryGetValue(s.SessionId, out var hb) && s.Status == WorkspaceComponentStatus.Healthy)
            {
                // Supervisor says process is alive; keep its restart count but the freshest version/heartbeat.
                bySession[s.SessionId] = new WorkspaceSessionHealth
                {
                    SessionId = s.SessionId,
                    Username = hb.Username ?? s.Username,
                    Version = hb.Version ?? s.Version,
                    Status = hb.Status == WorkspaceComponentStatus.Unknown ? s.Status : hb.Status,
                    RestartCount = s.RestartCount,
                    LastRecoveryAction = s.LastRecoveryAction,
                    ReasonCode = s.ReasonCode,
                    HeartbeatAt = hb.HeartbeatAt ?? s.HeartbeatAt
                };
            }
            else
            {
                bySession[s.SessionId] = s;
            }
        }

        return bySession.Values
            .OrderBy(h => h.SessionId)
            .Select(h => new HealthComponent
            {
                Component = WorkspaceComponents.WorkspaceUi,
                SessionId = h.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Username = h.Username,
                Version = h.Version,
                Status = h.Status,
                ReasonCode = h.ReasonCode,
                RestartCount = h.RestartCount,
                LastRecoveryAction = h.LastRecoveryAction,
                HeartbeatAt = h.HeartbeatAt
            })
            .ToList();
    }

    private WorkspaceSupervisorReport? ReadSupervisorReport()
    {
        try
        {
            if (!File.Exists(SupervisorReportPath))
            {
                return null;
            }

            var report = JsonSerializer.Deserialize<WorkspaceSupervisorReport>(File.ReadAllText(SupervisorReportPath), WorkspaceProtocol.JsonOptions);
            // A report older than 5 minutes means the Watch is not running — do not present stale data as live.
            return report is not null && time.GetUtcNow() - report.GeneratedAt < TimeSpan.FromMinutes(5) ? report : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read the workspace supervisor report.");
            return null;
        }
    }

    internal sealed class HealthComponent
    {
        [JsonPropertyName("component")] public required string Component { get; init; }
        [JsonPropertyName("session_id")] public required string SessionId { get; init; }
        [JsonPropertyName("username")] public string? Username { get; init; }
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("status")] public required string Status { get; init; }
        [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }
        [JsonPropertyName("restartCount")] public int RestartCount { get; init; }
        [JsonPropertyName("lastRecoveryAction")] public string? LastRecoveryAction { get; init; }
        [JsonPropertyName("heartbeatAt")] public DateTimeOffset? HeartbeatAt { get; init; }
    }
}
