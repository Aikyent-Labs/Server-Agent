using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Sync;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using CloudOrc.ControlAgent.Sync;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Commands;

/// <summary>
/// AikySpace sync typed commands (spec §08). Like every typed command they arrive signed and
/// allow-listed through <see cref="TypedCommandDispatcher"/>; none of them can run arbitrary code -
/// they only change which folders are read and uploaded.
/// </summary>
public sealed class SyncPolicyApplyHandler(SyncStore store, SyncCoordinator coordinator, IOptions<ControlAgentOptions> agentOptions, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => SyncCommandTypes.PolicyApply;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var policy = context.Parameters<SyncPolicyApplyParameters>()?.Policy;
        var errors = SyncPathPolicy.Validate(policy, agentOptions.Value.DataDirectory);
        if (errors.Count > 0)
        {
            var code = errors.Any(e => e.Contains("folder", StringComparison.OrdinalIgnoreCase)) ? SyncReasonCodes.PathNotAllowed : SyncReasonCodes.PolicyInvalid;
            return Task.FromResult(TypedCommandDispatcher.Rejected(code, string.Join(" ", errors.Take(5))));
        }

        // store canonical paths only - what is executed is exactly what was validated
        var canonical = new SyncPolicyDocument
        {
            ConnectionId = policy!.ConnectionId,
            State = policy.State,
            Direction = policy.Direction,
            AllowedRoots = policy.AllowedRoots,
            RescanMinutes = policy.RescanMinutes,
            ResourceProfile = policy.ResourceProfile,
            MaxUploadMbps = policy.MaxUploadMbps,
            BandwidthWindows = policy.BandwidthWindows,
            PolicyVersion = policy.PolicyVersion,
            EffectiveHash = policy.EffectiveHash,
            Roots = policy.Roots.Select(r => new SyncRootSpec { RootId = r.RootId, Path = SyncPathPolicy.Canonical(r.Path)!, Excludes = r.Excludes }).ToList()
        };
        store.UpsertPolicy(canonical, time.GetUtcNow());
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = $"Sync policy {canonical.PolicyVersion} applied ({canonical.Roots.Count} folder(s))." });
    }
}

public sealed class SyncPolicyRemoveHandler(SyncStore store, SyncCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => SyncCommandTypes.PolicyRemove;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var id = context.Parameters<SyncConnectionParameters>()?.ConnectionId;
        if (!BackupPolicyValidator.IsSafeId(id))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(SyncReasonCodes.PolicyInvalid, "connectionId is required."));
        }

        var removed = store.RemoveConnection(id!);
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = removed ? "Sync stopped; files already in AikyDrive stay there." : "Nothing to remove." });
    }
}

public sealed class SyncPauseHandler(SyncStore store, SyncCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => SyncCommandTypes.Pause;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken) =>
        SyncStateHandlers.Toggle(context, store, coordinator, paused: true);
}

public sealed class SyncResumeHandler(SyncStore store, SyncCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => SyncCommandTypes.Resume;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken) =>
        SyncStateHandlers.Toggle(context, store, coordinator, paused: false);
}

public sealed class SyncRescanHandler(SyncStore store, SyncCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => SyncCommandTypes.Rescan;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var id = context.Parameters<SyncConnectionParameters>()?.ConnectionId;
        store.RequestRescan(BackupPolicyValidator.IsSafeId(id) ? id : null);
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = "Rescan requested." });
    }
}

/// <summary>sync.revoke - stop immediately and forget local sync state for the connection.</summary>
public sealed class SyncRevokeHandler(SyncStore store, SyncCoordinator coordinator) : ITypedCommandHandler
{
    public string CommandType => SyncCommandTypes.Revoke;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var id = context.Parameters<SyncConnectionParameters>()?.ConnectionId;
        if (!BackupPolicyValidator.IsSafeId(id))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(SyncReasonCodes.PolicyInvalid, "connectionId is required."));
        }

        store.RemoveConnection(id!);
        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = "Sync revoked." });
    }
}

internal static class SyncStateHandlers
{
    public static Task<TypedCommandAcceptance> Toggle(TypedCommandContext context, SyncStore store, SyncCoordinator coordinator, bool paused)
    {
        var id = context.Parameters<SyncConnectionParameters>()?.ConnectionId;
        if (!BackupPolicyValidator.IsSafeId(id))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(SyncReasonCodes.PolicyInvalid, "connectionId is required."));
        }

        if (!store.SetPaused(id!, paused))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected(SyncReasonCodes.NotConfigured, "No sync policy for this connection."));
        }

        if (!paused)
        {
            store.RequestRescan(id);
        }

        coordinator.Wake();
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = paused ? "Sync paused." : "Sync resumed." });
    }
}
