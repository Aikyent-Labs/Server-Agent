using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudOrc.WorkspaceUI.Services;

// Backup v2 as the customer portal sees it (/remote-workspace/backup-v2 = the portal's backup API, limited to
// this server). Field names follow the backend (snake_case where the portal API uses it).

public sealed class BackupMe
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("canChangePlan")] public bool CanChangePlan { get; set; }
    [JsonPropertyName("devices")] public int Devices { get; set; }
    [JsonPropertyName("hasBackupPlan")] public bool HasBackupPlan { get; set; } = true;
    [JsonPropertyName("isAdmin")] public bool IsAdmin { get; set; } = true;
    [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; set; }
    /// <summary>The signed-in subscription's own workspace folder on this server (D:\ORG\NAME-1234).</summary>
    [JsonPropertyName("workspaceRoot")] public string? WorkspaceRoot { get; set; }
}

public sealed class BackupEnvironmentsResponse
{
    [JsonPropertyName("environments")] public List<BackupEnvironment> Environments { get; set; } = [];
}

public sealed class BackupEnvironment
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("engine_mode")] public string? EngineMode { get; set; }
    [JsonPropertyName("agent_online")] public bool AgentOnline { get; set; }
    [JsonPropertyName("agent_last_seen_at")] public DateTimeOffset? AgentLastSeenAt { get; set; }
    [JsonPropertyName("policy_state")] public string? PolicyState { get; set; }
    [JsonPropertyName("policy_error")] public string? PolicyError { get; set; }
    [JsonPropertyName("last_recovery_point_at")] public DateTimeOffset? LastRecoveryPointAt { get; set; }
    [JsonPropertyName("last_recovery_status")] public string? LastRecoveryStatus { get; set; }
    [JsonPropertyName("last_verification_state")] public string? LastVerificationState { get; set; }
    [JsonPropertyName("paused_at")] public DateTimeOffset? PausedAt { get; set; }
    [JsonPropertyName("pause_note")] public string? PauseNote { get; set; }
    [JsonPropertyName("subscription_id")] public string? SubscriptionId { get; set; }
    [JsonPropertyName("plan_name")] public string? PlanName { get; set; }
    [JsonPropertyName("protected_bytes")] public JsonElement ProtectedBytes { get; set; }
}

public sealed class BackupQuota
{
    [JsonPropertyName("limitBytes")] public double? LimitBytes { get; set; }
    [JsonPropertyName("usedBytes")] public double UsedBytes { get; set; }
    [JsonPropertyName("percentUsed")] public double? PercentUsed { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("notInPlan")] public bool NotInPlan { get; set; }
    [JsonPropertyName("planName")] public string? PlanName { get; set; }
}

public sealed class BackupPolicyResponse
{
    [JsonPropertyName("effective")] public BackupSettings Effective { get; set; } = new();
    [JsonPropertyName("locked")] public List<string> Locked { get; set; } = [];
}

public sealed class BackupSettings
{
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("sources")] public List<BackupSource>? Sources { get; set; }
    [JsonPropertyName("excludes")] public List<string>? Excludes { get; set; }
    [JsonPropertyName("schedule")] public JsonElement Schedule { get; set; }
    [JsonPropertyName("retentionDays")] public int? RetentionDays { get; set; }
}

public sealed class BackupSource
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

public sealed class BackupJobsResponse
{
    [JsonPropertyName("jobs")] public List<BackupJob> Jobs { get; set; } = [];
}

public sealed class BackupJobDetail
{
    [JsonPropertyName("job")] public BackupJob? Job { get; set; }
    [JsonPropertyName("events")] public List<BackupJobEvent> Events { get; set; } = [];
}

public sealed class BackupJob
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("progress")] public BackupProgress? Progress { get; set; }
    [JsonPropertyName("result")] public JsonElement Result { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("ended_at")] public DateTimeOffset? EndedAt { get; set; }
}

public sealed class BackupProgress
{
    [JsonPropertyName("percent")] public double? Percent { get; set; }
    [JsonPropertyName("files_done")] public long? FilesDone { get; set; }
    [JsonPropertyName("files_total")] public long? FilesTotal { get; set; }
    [JsonPropertyName("bytes_read")] public double? BytesRead { get; set; }
    [JsonPropertyName("bytes_total")] public double? BytesTotal { get; set; }
}

public sealed class BackupJobEvent
{
    [JsonPropertyName("event_type")] public string? EventType { get; set; }
    [JsonPropertyName("stage")] public string? Stage { get; set; }
    [JsonPropertyName("received_at")] public DateTimeOffset? ReceivedAt { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
}

public sealed class RecoveryPointsResponse
{
    [JsonPropertyName("recoveryPoints")] public List<RecoveryPoint> RecoveryPoints { get; set; } = [];
}

public sealed class RecoveryPoint
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("job_id")] public string? JobId { get; set; }
    [JsonPropertyName("source_time")] public DateTimeOffset? SourceTime { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("verification_state")] public string? VerificationState { get; set; }
    [JsonPropertyName("browsable")] public bool Browsable { get; set; }
    [JsonPropertyName("manifest_entries")] public JsonElement ManifestEntries { get; set; }
    [JsonPropertyName("stats")] public RecoveryStats? Stats { get; set; }
    [JsonPropertyName("roots")] public List<string> Roots { get; set; } = [];
    [JsonPropertyName("root_sizes")] public List<RootSize> RootSizes { get; set; } = [];
}

public sealed class RootSize
{
    [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;
    [JsonPropertyName("bytes")] public double Bytes { get; set; }
    [JsonPropertyName("files")] public long Files { get; set; }
}

public sealed class RecoveryStats
{
    [JsonPropertyName("source_bytes")] public double? SourceBytes { get; set; }
    [JsonPropertyName("errors")] public int? Errors { get; set; }
}

public sealed class RecoveryRootsResponse
{
    [JsonPropertyName("roots")] public List<RecoveryRoot> Roots { get; set; } = [];
}

public sealed class RecoveryRoot
{
    [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public long Entries { get; set; }
}

public sealed class RecoveryBrowseResponse
{
    [JsonPropertyName("items")] public List<RecoveryItem> Items { get; set; } = [];
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("nextCursor")] public JsonElement NextCursor { get; set; }
}

public sealed class RecoveryItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("root")] public string Root { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "file";
    [JsonPropertyName("size")] public double? Size { get; set; }
    [JsonPropertyName("mtime")] public DateTimeOffset? Mtime { get; set; }
}

public sealed class RestoresResponse
{
    [JsonPropertyName("restores")] public List<RestoreRow> Restores { get; set; } = [];
}

public sealed class RestoreRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("approval_state")] public string? ApprovalState { get; set; }
    [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("result")] public RestoreResult? Result { get; set; }
    [JsonPropertyName("target_kind")] public string? TargetKind { get; set; }
    [JsonPropertyName("target_path")] public string? TargetPath { get; set; }
    [JsonPropertyName("source_root")] public string? SourceRoot { get; set; }
    [JsonPropertyName("recovery_point_time")] public DateTimeOffset? RecoveryPointTime { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class RestoreResult
{
    [JsonPropertyName("restored_files")] public long? RestoredFiles { get; set; }
    [JsonPropertyName("restored_bytes")] public double? RestoredBytes { get; set; }
    [JsonPropertyName("restore_target")] public string? RestoreTarget { get; set; }
}

public sealed class SourceBrowseResponse
{
    [JsonPropertyName("roots")] public List<SourceRoot> Roots { get; set; } = [];
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("entries")] public List<SourceEntry> Entries { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

public sealed class SourceRoot
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class SourceEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "dir";
}
