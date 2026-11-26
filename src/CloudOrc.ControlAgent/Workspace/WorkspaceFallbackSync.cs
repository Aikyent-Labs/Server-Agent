using CloudOrc.Agent.Contracts.Backup;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// Keeps %ProgramData%\CloudOrc\Workspace\fallback-team.json equal to the CloudOrc team's fallback
/// switches for this server (admin panel kill switch: global / partner / server / user).
///  - desired-state sync: GET agent/fallback every minute, and at once on workspace.fallback.sync;
///  - sticky: when CloudOrc cannot be reached the file keeps its last value (a switch stays in force);
///  - the panel and the Watch read the file locally, so they act on it with no agent or internet.
/// </summary>
public sealed class WorkspaceFallbackSync(
    BackendApiClient api,
    TimeProvider time,
    ILogger<WorkspaceFallbackSync> logger) : BackgroundService
{
    public const string Route = "api/v1/remote-workspace/agent/fallback";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private string? _lastRevision;

    /// <summary>Path of the team switch file (overridable for tests).</summary>
    public string TeamPath { get; init; } = WorkspaceFallbackDocument.TeamPath;

    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>workspace.fallback.sync: fetch now instead of waiting for the next minute.</summary>
    public void RequestSync()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already requested */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastRevision = WorkspaceFallback.Load(TeamPath)?.Revision;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Workspace fallback sync failed; the last switches stay in force.");
            }

            try
            {
                await _wake.WaitAsync(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<bool> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!api.IsConfigured)
        {
            return false;
        }

        var response = await api.GetAsync(Route, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
        {
            // Offline / old backend (404) / error: never clear switches because CloudOrc is unreachable.
            return false;
        }

        var doc = JsonSerializer.Deserialize<WorkspaceFallbackDocument>(response.Body, WorkspaceProtocol.JsonOptions);
        if (doc is null)
        {
            return false;
        }

        LastSyncedAt = time.GetUtcNow();
        if (doc.Revision is not null && doc.Revision == _lastRevision)
        {
            return true;
        }

        Apply(doc);
        return true;
    }

    internal void Apply(WorkspaceFallbackDocument doc)
    {
        var stored = new WorkspaceFallbackDocument
        {
            Revision = doc.Revision,
            UpdatedAt = time.GetUtcNow(),
            UpdatedBy = "CloudOrc",
            Rules = doc.Rules.Where(r => r is not null && (r.PanelOff || r.AltTabOff || r.HideTsplusOff)).Take(500).ToList()
        };
        WorkspaceFallback.Save(TeamPath, stored);
        _lastRevision = doc.Revision;
        if (stored.Rules.Count == 0)
        {
            logger.LogInformation("Workspace fallback: no CloudOrc switch is on for this server (revision {Revision}).", doc.Revision);
        }
        else
        {
            logger.LogWarning("Workspace fallback: {Count} CloudOrc switch(es) on for this server (revision {Revision}): {Rules}.",
                stored.Rules.Count, doc.Revision,
                string.Join("; ", stored.Rules.Select(r => $"{r.Scope ?? "?"} user={r.Username ?? "*"} panelOff={r.PanelOff} altTabOff={r.AltTabOff} keepTsplus={r.HideTsplusOff}")));
        }
    }
}

/// <summary>workspace.fallback.sync — the CloudOrc team changed a fallback switch: fetch the switches now.</summary>
public sealed class WorkspaceFallbackSyncHandler(WorkspaceFallbackSync sync) : ITypedCommandHandler
{
    public string CommandType => WorkspaceCommandTypes.FallbackSync;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        sync.RequestSync();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = "Fallback switches are being fetched; panels apply them within 10 seconds." });
    }
}
