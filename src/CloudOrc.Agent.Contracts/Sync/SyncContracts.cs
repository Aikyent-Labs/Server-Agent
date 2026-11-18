using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Sync;

/// <summary>
/// AikySpace R3 one-way selective sync (AikySpace spec v1.0 §08). Wire strings MUST match the CloudOrc
/// backend: backupV2/bv2.constants.js (COMMAND_TYPES.SYNC_*, CAPABILITIES.SYNC_V1) and
/// modules/aikyspace/sync/sync.routes.js (agent control-plane routes).
/// </summary>
public static class SyncCommandTypes
{
    public const string PolicyApply = "sync.policy.apply";
    public const string PolicyRemove = "sync.policy.remove";
    public const string Pause = "sync.pause";
    public const string Resume = "sync.resume";
    public const string Rescan = "sync.rescan";
    public const string Revoke = "sync.revoke";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PolicyApply, PolicyRemove, Pause, Resume, Rescan, Revoke
    };

    public static bool IsSync(string? commandType) => commandType is not null && All.Contains(commandType);
}

public static class SyncCapabilities
{
    public const string SyncV1 = "sync.v1";
}

/// <summary>Per-root sync state machine (§08): DISCONNECTED → ENROLLED → IDLE → SCANNING → UPLOADING → VERIFYING → CHECKPOINTED → IDLE.</summary>
public static class SyncStates
{
    public const string Idle = "IDLE";
    public const string Scanning = "SCANNING";
    public const string Uploading = "UPLOADING";
    public const string Verifying = "VERIFYING";
    public const string Checkpointed = "CHECKPOINTED";

    // recoverable
    public const string PausedPolicy = "PAUSED_POLICY";
    public const string WaitingNetwork = "WAITING_NETWORK";
    public const string RetryBackoff = "RETRY_BACKOFF";
    public const string FileLocked = "FILE_LOCKED";
    public const string QuotaBlocked = "QUOTA_BLOCKED";

    // terminal / security
    public const string Revoked = "REVOKED";
    public const string InvalidRoot = "INVALID_ROOT";
    public const string Compromised = "COMPROMISED";
}

public static class SyncReasonCodes
{
    public const string PolicyInvalid = "SYNC_POLICY_INVALID";
    public const string PathNotAllowed = "SYNC_PATH_NOT_ALLOWED";
    public const string NotConfigured = "SYNC_NOT_CONFIGURED";
    public const string RootMissing = "SYNC_ROOT_MISSING";
    public const string ChangedDuringRead = "SYNC_CHANGED_DURING_READ";
    public const string Locked = "SYNC_FILE_LOCKED";
    public const string Quota = "AKYS_QUOTA";
    public const string Revoked = "AKYS_SYNC_REVOKED";
}

public static class SyncRoutes
{
    public const string Manifests = "api/v1/sync/manifests";
    public const string Uploads = "api/v1/sync/uploads";
    public const string Checkpoints = "api/v1/sync/checkpoints";

    public static string Upload(string sessionId, string rootId) =>
        $"api/v1/sync/uploads/{Uri.EscapeDataString(sessionId)}?root_id={Uri.EscapeDataString(rootId)}";

    public static string Complete(string sessionId) => $"api/v1/sync/uploads/{Uri.EscapeDataString(sessionId)}/complete";
}

// ---------------------------------------------------------------- typed command parameters (camelCase, WebSocket)

public sealed class SyncPolicyDocument
{
    public string ConnectionId { get; init; } = "";
    public string State { get; init; } = "ACTIVE";
    public string Direction { get; init; } = "UPLOAD_ONLY";
    public List<SyncRootSpec> Roots { get; init; } = [];

    /// <summary>Shared servers: the customer's own folders. Empty = any local folder (dedicated server).</summary>
    public List<string> AllowedRoots { get; init; } = [];
    public int RescanMinutes { get; init; } = 60;
    public string ResourceProfile { get; init; } = "eco";
    public double? MaxUploadMbps { get; init; }

    /// <summary>Time-of-day upload limits (spec 08 "bandwidth windows"), evaluated in the server's local time.</summary>
    public List<SyncBandwidthWindow> BandwidthWindows { get; init; } = [];
    public string PolicyVersion { get; init; } = "";
    public string EffectiveHash { get; init; } = "";
}

/// <summary>e.g. { days:["Mon","Tue"], start:"09:00", end:"18:00", maxUploadMbps: 5 } (0 = pause uploads in that window).</summary>
public sealed class SyncBandwidthWindow
{
    public List<string> Days { get; init; } = [];
    public string Start { get; init; } = "00:00";
    public string End { get; init; } = "24:00";
    public double MaxUploadMbps { get; init; }
}

public sealed class SyncRootSpec
{
    public string RootId { get; init; } = "";
    public string Path { get; init; } = "";
    public List<string> Excludes { get; init; } = [];
}

public sealed class SyncPolicyApplyParameters
{
    public SyncPolicyDocument? Policy { get; init; }
}

public sealed class SyncConnectionParameters
{
    public string? ConnectionId { get; init; }
}

// ---------------------------------------------------------------- HTTPS control plane (snake_case)

public sealed class SyncManifestEntry
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("mtime")] public DateTimeOffset? ModifiedAt { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
}

public sealed class SyncManifestRequest
{
    [JsonPropertyName("root_id")] public required string RootId { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("complete")] public bool Complete { get; init; }
    [JsonPropertyName("entries")] public List<SyncManifestEntry> Entries { get; init; } = [];
}

public sealed class SyncUploadInstruction
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    [JsonPropertyName("document_id")] public string? DocumentId { get; init; }
}

public sealed class SyncManifestResponse
{
    [JsonPropertyName("root_id")] public string? RootId { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("upload")] public List<SyncUploadInstruction> Upload { get; init; } = [];
    [JsonPropertyName("unchanged")] public int Unchanged { get; init; }
    [JsonPropertyName("tombstoned")] public int Tombstoned { get; init; }
}

public sealed class SyncUploadRequest
{
    [JsonPropertyName("root_id")] public required string RootId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    [JsonPropertyName("mime")] public string? Mime { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
}

public sealed class SyncPartGrant
{
    [JsonPropertyName("part")] public int Part { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";
}

public sealed class SyncUploadResponse
{
    [JsonPropertyName("skipped")] public bool Skipped { get; init; }
    [JsonPropertyName("document_id")] public string? DocumentId { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("part_size")] public long PartSize { get; init; }
    [JsonPropertyName("total_parts")] public int TotalParts { get; init; }
    [JsonPropertyName("parts")] public List<SyncPartGrant> Parts { get; init; } = [];
    [JsonPropertyName("parts_remaining")] public int PartsRemaining { get; init; }
}

public sealed class SyncCompleteRequest
{
    [JsonPropertyName("root_id")] public required string RootId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("mtime")] public DateTimeOffset? ModifiedAt { get; init; }
    [JsonPropertyName("file_id")] public string? FileId { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
}

public sealed class SyncCompleteResponse
{
    [JsonPropertyName("document_id")] public string? DocumentId { get; init; }
    [JsonPropertyName("version_id")] public string? VersionId { get; init; }
    [JsonPropertyName("seq")] public int? Seq { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
}

public sealed class SyncCheckpointRequest
{
    [JsonPropertyName("root_id")] public required string RootId { get; init; }
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("failure_code")] public string? FailureCode { get; init; }
    [JsonPropertyName("stats")] public Dictionary<string, object?> Stats { get; init; } = [];
}

/// <summary>
/// CloudOrc error body: { success:false, error:{ code, message } }. The agent decides on the HTTP status
/// (413 quota/too large, 410 revoked, 404 unknown root, 409 paused/conflict, 5xx/network transient);
/// the code is only a hint (CloudOrc may normalise codes).
/// </summary>
public sealed class SyncProblem
{
    [JsonPropertyName("error")] public SyncProblemError? Error { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }

    public string? EffectiveCode => Error?.Code ?? Code;
    public string? EffectiveMessage => Error?.Message ?? Message;
}

public sealed class SyncProblemError
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}
