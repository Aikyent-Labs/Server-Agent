using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.ControlAgent.Commands;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// workspace.ui.restart — CloudOrc support asks the panel in one session (or every session) to
/// restart cleanly. The agent never kills the user's processes: the panel picks the request up on
/// its next heartbeat (≤15 s) and restarts itself; the Windows session and apps are untouched.
/// </summary>
public sealed class WorkspaceUiRestartHandler(WorkspaceHeartbeats heartbeats, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => WorkspaceCommandTypes.UiRestart;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<WorkspaceCommandParameters>() ?? new WorkspaceCommandParameters();
        if (p.SessionId is { } s && (s < 1 || s > 65535))
        {
            return Task.FromResult(TypedCommandDispatcher.Rejected("INVALID_SESSION", "sessionId must be a Windows user session."));
        }

        var targets = heartbeats.Queue(WorkspacePanelCommands.Restart, p.SessionId, time.GetUtcNow());
        return Task.FromResult(new TypedCommandAcceptance
        {
            Accepted = true,
            Message = targets == 0
                ? "No panel is running right now; the request expires in 10 minutes."
                : $"Restart requested for {targets} panel(s); applied within 15 seconds."
        });
    }
}

/// <summary>workspace.policy.refresh — panels reload policy, apps and brand now (after an admin change).</summary>
public sealed class WorkspacePolicyRefreshHandler(WorkspaceHeartbeats heartbeats, TimeProvider time) : ITypedCommandHandler
{
    public string CommandType => WorkspaceCommandTypes.PolicyRefresh;

    public Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<WorkspaceCommandParameters>() ?? new WorkspaceCommandParameters();
        var targets = heartbeats.Queue(WorkspacePanelCommands.Refresh, p.SessionId, time.GetUtcNow());
        return Task.FromResult(new TypedCommandAcceptance { Accepted = true, Message = $"Policy refresh queued for {targets} panel(s)." });
    }
}
