using System.Text.Json.Serialization;

namespace CloudOrc.Agent.Contracts.Workspace;

// Secure-Access Broker contract (handoff v1.2 §29–30). The Windows Credential Provider shim (native,
// runs inside LogonUI as SYSTEM) asks the broker over this local pipe whether an interactive logon may
// proceed. Windows still validates the password; CloudOrc adds the second gate. No secret ever crosses
// this pipe: the request carries the user name, session and client address only.

public static class SecureAccessProtocol
{
    /// <summary>SYSTEM + Administrators only (LogonUI's Credential Provider runs as SYSTEM).</summary>
    public const string PipeName = "CloudOrc.SecureAccess";
    public const string ActionDecide = "decide";
    public const int MaxMessageBytes = 4096;
}

public static class SecureAccessDecisions
{
    public const string Allow = "ALLOW";
    public const string MfaRequired = "MFA_REQUIRED";
    public const string Deny = "DENY";

    /// <summary>The account is not a CloudOrc-managed seat (e.g. the server's own local administrator): Windows decides.</summary>
    public const string NotManaged = "NOT_MANAGED";

    /// <summary>CloudOrc could not be asked and no valid cached decision exists.</summary>
    public const string Unavailable = "UNAVAILABLE";
}

public sealed class SecureAccessRequest
{
    [JsonPropertyName("action")] public string Action { get; init; } = SecureAccessProtocol.ActionDecide;
    [JsonPropertyName("username")] public required string Username { get; init; }
    [JsonPropertyName("sessionId")] public int? SessionId { get; init; }
    [JsonPropertyName("clientIp")] public string? ClientIp { get; init; }
}

public sealed class SecureAccessDecision
{
    [JsonPropertyName("allow")] public bool Allow { get; init; }
    [JsonPropertyName("decision")] public string Decision { get; init; } = SecureAccessDecisions.Unavailable;
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; init; }
    [JsonPropertyName("mfaRequired")] public bool MfaRequired { get; init; }
    [JsonPropertyName("policyVersion")] public int? PolicyVersion { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("correlationId")] public string? CorrelationId { get; init; }
    [JsonPropertyName("fromCache")] public bool FromCache { get; init; }
}
