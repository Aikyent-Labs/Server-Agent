using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Workspace;

// CloudOrc Remote Workspace (floating panel) — handoff v1.2 §47, §50.
//
// This file is self-contained on purpose: CloudOrc.WorkspaceUI links it as source
// (<Compile Include=... Link=...>) so the per-user UI process carries no dependency on the
// agent core assemblies — the same rule the tray follows. Keep string values in sync with
// Cloude_Website/backend/src/modules/remoteWorkspace/remoteWorkspace.constants.js.

/// <summary>WorkspaceUI.exe &lt;-&gt; Agent Core local IPC (named pipe, one JSON line each way).</summary>
public static class WorkspaceProtocol
{
    public const string PipeName = "CloudOrc.Workspace";
    public const int Version = 1;

    /// <summary>Agent status + whether the workspace panel is available on this server.</summary>
    public const string ActionHello = "hello";

    /// <summary>Short-lived backend token for the CALLING Windows user/session (never a client-supplied identity).</summary>
    public const string ActionToken = "token";

    /// <summary>Panel heartbeat (version, state); recorded for health reporting.</summary>
    public const string ActionHeartbeat = "heartbeat";

    public const int MaxMessageBytes = 16 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Typed agent commands for the panel (signed + audited like every typed command).</summary>
public static class WorkspaceCommandTypes
{
    /// <summary>Restart the panel in one session (sessionId) or all sessions (no sessionId).</summary>
    public const string UiRestart = "workspace.ui.restart";

    /// <summary>Panels reload their policy/apps/brand now (e.g. after an admin profile change).</summary>
    public const string PolicyRefresh = "workspace.policy.refresh";

    public static bool IsWorkspace(string? commandType) => commandType is UiRestart or PolicyRefresh;
}

/// <summary>Commands the agent hands to a panel on its next heartbeat.</summary>
public static class WorkspacePanelCommands
{
    public const string Refresh = "refresh";
    public const string Restart = "restart";
}

public sealed class WorkspaceCommandParameters
{
    [JsonPropertyName("sessionId")] public int? SessionId { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

public static class WorkspaceLaunchModes
{
    public const string DirectApp = "DIRECT_APP";
    public const string MinimalPanel = "MINIMAL_PANEL";
    public const string WindowsDesktop = "WINDOWS_DESKTOP";
    public const string CloudOrcDesktop = "CLOUDORC_DESKTOP";
    public const string WebRemote = "WEB_REMOTE";
}

public static class WorkspaceComponents
{
    public const string WorkspaceUi = "WORKSPACE_UI";
    public const string ControlAgent = "CONTROL_AGENT";
    public const string Watchdog = "WATCHDOG";
}

public static class WorkspaceComponentStatus
{
    public const string Healthy = "HEALTHY";
    public const string Recovering = "RECOVERING";
    public const string Degraded = "DEGRADED";
    public const string Stopped = "STOPPED";
    public const string Unknown = "UNKNOWN";
}

public static class WorkspaceReasonCodes
{
    public const string AgentOffline = "AGENT_OFFLINE";
    public const string AgentNotEnrolled = "AGENT_NOT_ENROLLED";
    public const string IdentityMismatch = "WORKSPACE_IDENTITY_MISMATCH";
    public const string BackendUnavailable = "BACKEND_UNAVAILABLE";
    public const string WorkspaceUiDegraded = "WORKSPACE_UI_DEGRADED";
    public const string CrashLoop = "WORKSPACE_UI_CRASH_LOOP";
}

public sealed class WorkspacePipeRequest
{
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; } = WorkspaceProtocol.Version;
    [JsonPropertyName("uiVersion")] public string? UiVersion { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("forceRefresh")] public bool ForceRefresh { get; init; }
}

public sealed class WorkspacePipeResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; } = true;
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }

    // hello
    [JsonPropertyName("agentVersion")] public string? AgentVersion { get; init; }
    [JsonPropertyName("agentConnected")] public bool AgentConnected { get; init; }
    [JsonPropertyName("agentEnrolled")] public bool AgentEnrolled { get; init; }
    [JsonPropertyName("lastContactAt")] public DateTimeOffset? LastContactAt { get; init; }
    [JsonPropertyName("windowsUser")] public string? WindowsUser { get; init; }
    [JsonPropertyName("sessionId")] public int? SessionId { get; init; }

    /// <summary>A pending agent command for this panel (<see cref="WorkspacePanelCommands"/>), delivered once.</summary>
    [JsonPropertyName("command")] public string? Command { get; init; }

    // token
    [JsonPropertyName("apiBaseUrl")] public string? ApiBaseUrl { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
    [JsonPropertyName("seatFound")] public bool? SeatFound { get; init; }
}

/// <summary>
/// Per-session panel state written by WorkspaceUI (heartbeat) and read by the Watch supervisor.
/// Lives in %ProgramData%\CloudOrc\Workspace\sessions\session-{id}.json. Holds no secret.
/// </summary>
public sealed class WorkspaceSessionState
{
    public const string StateRunning = "running";
    /// <summary>The panel decided not to show (workspace disabled for this server / user) — do not restart.</summary>
    public const string StateDisabled = "disabled";
    /// <summary>The user closed the panel for this session — do not restart.</summary>
    public const string StateUserExit = "user-exit";
    /// <summary>The panel is restarting itself on request — the Watch should wait, not count a crash.</summary>
    public const string StateRestarting = "restarting";

    [JsonPropertyName("sessionId")] public int SessionId { get; init; }
    [JsonPropertyName("processId")] public int ProcessId { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = StateRunning;
    [JsonPropertyName("heartbeatAt")] public DateTimeOffset HeartbeatAt { get; init; }
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; init; }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudOrc", "Workspace", "sessions");

    public static string PathFor(int sessionId) => Path.Combine(Directory, $"session-{sessionId}.json");
}

/// <summary>Watch -> Control Agent: supervisor view of every per-session WorkspaceUI (no secrets).</summary>
public sealed class WorkspaceSupervisorReport
{
    public const string FileName = "workspace-ui-health.json";

    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; init; }
    [JsonPropertyName("sessions")] public List<WorkspaceSessionHealth> Sessions { get; init; } = [];
}

public sealed class WorkspaceSessionHealth
{
    [JsonPropertyName("sessionId")] public int SessionId { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = WorkspaceComponentStatus.Unknown;
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }
    [JsonPropertyName("restartCount")] public int RestartCount { get; init; }
    [JsonPropertyName("lastRecoveryAction")] public string? LastRecoveryAction { get; init; }
    [JsonPropertyName("heartbeatAt")] public DateTimeOffset? HeartbeatAt { get; init; }
}
