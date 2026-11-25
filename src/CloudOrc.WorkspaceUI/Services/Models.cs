using System.Text.Json.Serialization;

namespace CloudOrc.WorkspaceUI.Services;

// DTOs of GET/POST /api/v1/remote-workspace/* (Cloude_Website/backend/src/modules/remoteWorkspace).

public sealed class WorkspaceContext
{
    [JsonPropertyName("workspaceEnabled")] public bool WorkspaceEnabled { get; set; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; set; }
    [JsonPropertyName("brand")] public BrandInfo Brand { get; set; } = new();
    [JsonPropertyName("server")] public ServerInfo? Server { get; set; }
    [JsonPropertyName("user")] public UserInfo User { get; set; } = new();
    [JsonPropertyName("customer")] public CustomerInfo? Customer { get; set; }
    [JsonPropertyName("launchMode")] public string LaunchMode { get; set; } = "MINIMAL_PANEL";
    [JsonPropertyName("allowedModes")] public List<string> AllowedModes { get; set; } = [];
    [JsonPropertyName("railItems")] public List<string> RailItems { get; set; } = [];
    [JsonPropertyName("permittedActions")] public List<string> PermittedActions { get; set; } = [];
    [JsonPropertyName("preferences")] public Preferences Preferences { get; set; } = new();
    [JsonPropertyName("hideTsplusPanel")] public bool HideTsplusPanel { get; set; }
    [JsonPropertyName("portalUsersUrl")] public string? PortalUsersUrl { get; set; }
    [JsonPropertyName("policyVersion")] public int PolicyVersion { get; set; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }

    public bool Can(string action) => PermittedActions.Contains(action, StringComparer.Ordinal);
}

public sealed class BrandInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "CloudOrc";
    [JsonPropertyName("productName")] public string ProductName { get; set; } = "Remote Workspace";
    [JsonPropertyName("logoUrl")] public string? LogoUrl { get; set; }
    [JsonPropertyName("supportOrganization")] public string SupportOrganization { get; set; } = "CloudOrc Support";
    [JsonPropertyName("supportEmail")] public string? SupportEmail { get; set; }
    [JsonPropertyName("supportPhone")] public string? SupportPhone { get; set; }
    [JsonPropertyName("supportUrl")] public string? SupportUrl { get; set; }
    [JsonPropertyName("whiteLabel")] public bool WhiteLabel { get; set; }
}

public sealed class ServerInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class UserInfo
{
    [JsonPropertyName("windowsUserId")] public string? WindowsUserId { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("isCustomerAdmin")] public bool IsCustomerAdmin { get; set; }
    [JsonPropertyName("twoFactorEnabled")] public bool TwoFactorEnabled { get; set; }
}

public sealed class CustomerInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class Preferences
{
    [JsonPropertyName("launchMode")] public string? LaunchMode { get; set; }
    [JsonPropertyName("panelPinned")] public bool PanelPinned { get; set; }
    [JsonPropertyName("panelSide")] public string PanelSide { get; set; } = "right";
    [JsonPropertyName("autoHideSeconds")] public int AutoHideSeconds { get; set; } = 4;
}

public sealed class AppsResponse
{
    [JsonPropertyName("apps")] public List<PublishedApp> Apps { get; set; } = [];
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; set; }
}

public sealed class PublishedApp
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "application";
    [JsonPropertyName("exePath")] public string ExePath { get; set; } = string.Empty;
    [JsonPropertyName("startupPath")] public string StartupPath { get; set; } = string.Empty;
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = string.Empty;
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("maxInstances")] public int? MaxInstances { get; set; }

    [JsonIgnore] public bool IsFolder => string.Equals(Type, "folder", StringComparison.OrdinalIgnoreCase);
}

public sealed class Capabilities
{
    [JsonPropertyName("clipboard")] public ClipboardCapability Clipboard { get; set; } = new();
    [JsonPropertyName("files")] public FilesCapability Files { get; set; } = new();
    [JsonPropertyName("print")] public PrintCapability Print { get; set; } = new();
}

public sealed class ClipboardCapability
{
    [JsonPropertyName("state")] public string State { get; set; } = "TEXT_ONLY";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class FilesCapability
{
    [JsonPropertyName("state")] public string State { get; set; } = "BLOCKED";
    [JsonPropertyName("driveRedirection")] public bool DriveRedirection { get; set; }
    [JsonPropertyName("fileCopyMode")] public string? FileCopyMode { get; set; }
    [JsonPropertyName("allowedExtensions")] public List<string> AllowedExtensions { get; set; } = [];
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class PrintCapability
{
    [JsonPropertyName("state")] public string State { get; set; } = "READY";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class BackupStatus
{
    [JsonPropertyName("state")] public string State { get; set; } = "NOT_CONFIGURED";
    [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
    [JsonPropertyName("lastVerifiedAt")] public DateTimeOffset? LastVerifiedAt { get; set; }
    [JsonPropertyName("ageHours")] public double? AgeHours { get; set; }
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("canRun")] public bool CanRun { get; set; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; set; }
    [JsonPropertyName("history")] public List<BackupPoint> History { get; set; } = [];
}

public sealed class BackupPoint
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
    [JsonPropertyName("verification")] public string? Verification { get; set; }
    [JsonPropertyName("trust")] public string? Trust { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class AlertsResponse
{
    [JsonPropertyName("items")] public List<AlertItem> Items { get; set; } = [];
    [JsonPropertyName("unread")] public int Unread { get; set; }
}

public sealed class AlertItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("severity")] public string Severity { get; set; } = "info";
    [JsonPropertyName("read")] public bool Read { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }

    [JsonIgnore] public bool IsCritical => string.Equals(Severity, "critical", StringComparison.OrdinalIgnoreCase);
}

public sealed class TicketResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("ticketId")] public string? TicketId { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class AiAnswer
{
    [JsonPropertyName("answer")] public string Answer { get; set; } = string.Empty;
    [JsonPropertyName("suggestions")] public List<string> Suggestions { get; set; } = [];
}

public sealed class ApiError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("error")] public object? Error { get; set; }
}

public sealed class SupportCategories
{
    [JsonPropertyName("categories")] public List<SupportCategory> Categories { get; set; } = [];
    [JsonPropertyName("defaultCategory")] public string? DefaultCategory { get; set; }
}

public sealed class SupportCategory
{
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("subcategories")] public List<SupportCategory> Subcategories { get; set; } = [];

    public override string ToString() => Name;
}

// ---------------------------------------------------------------- Phase 2

public sealed class SecurityStatus
{
    [JsonPropertyName("managed")] public bool Managed { get; set; }
    [JsonPropertyName("mfa")] public MfaStatus Mfa { get; set; } = new();
    [JsonPropertyName("ipPolicy")] public IpPolicyStatus? IpPolicy { get; set; }
    [JsonPropertyName("signedInAt")] public DateTimeOffset? SignedInAt { get; set; }
}

public sealed class MfaStatus
{
    [JsonPropertyName("required")] public bool Required { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    [JsonPropertyName("verifiedAt")] public DateTimeOffset? VerifiedAt { get; set; }
    [JsonPropertyName("rememberUntil")] public DateTimeOffset? RememberUntil { get; set; }
}

public sealed class IpPolicyStatus
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("allowedNetworks")] public int AllowedNetworks { get; set; }
}

public sealed class WorkspaceUsersResponse
{
    [JsonPropertyName("users")] public List<WorkspaceUserRow> Users { get; set; } = [];
}

public sealed class WorkspaceUserRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("operationStatus")] public string? OperationStatus { get; set; }
    [JsonPropertyName("sessionStatus")] public string? SessionStatus { get; set; }
    [JsonPropertyName("twoFactorEnabled")] public bool TwoFactorEnabled { get; set; }
    [JsonPropertyName("isOwnerLogin")] public bool IsOwnerLogin { get; set; }
    [JsonPropertyName("isYou")] public bool IsYou { get; set; }

    [JsonIgnore] public bool IsSuspended => string.Equals(Status, "suspended", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string Summary => $"{(IsSuspended ? "Suspended" : (Status ?? "-"))}{(TwoFactorEnabled ? " · 2FA on" : "")}{(string.IsNullOrWhiteSpace(SessionStatus) ? "" : " · " + SessionStatus)}{(IsYou ? " · you" : "")}";
    public override string ToString() => $"{DisplayName ?? Username} ({Username}), {Summary}";
}

public sealed class ActionResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class FileSendResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("sentTo")] public string? SentTo { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class AssistanceEnvelope
{
    [JsonPropertyName("request")] public AssistanceRequest? Request { get; set; }
}

public sealed class AssistanceRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("sessionId")] public int SessionId { get; set; }
    [JsonPropertyName("requesterName")] public string RequesterName { get; set; } = string.Empty;
    [JsonPropertyName("requesterOrg")] public string RequesterOrg { get; set; } = string.Empty;
    [JsonPropertyName("ticketRef")] public string? TicketRef { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("providerLabel")] public string? ProviderLabel { get; set; }
    [JsonPropertyName("providerCapabilities")] public List<string> ProviderCapabilities { get; set; } = [];
    [JsonPropertyName("requestedCapabilities")] public List<string> RequestedCapabilities { get; set; } = [];
    [JsonPropertyName("grantedCapabilities")] public List<string>? GrantedCapabilities { get; set; }
    [JsonPropertyName("requestExpiresAt")] public DateTimeOffset? RequestExpiresAt { get; set; }
    [JsonPropertyName("leaseExpiresAt")] public DateTimeOffset? LeaseExpiresAt { get; set; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }

    [JsonIgnore] public bool IsPending => Status == "PENDING";
    [JsonIgnore] public bool IsGranted => Status is "GRANTED" or "ACTIVE";
}
