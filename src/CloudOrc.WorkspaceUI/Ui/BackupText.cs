using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// Customer-facing backup wording - the same words as the customer portal
/// (shared/portal/modules/customer/backup/protection/protectionUi.ts, RestoreActivity.tsx).
/// </summary>
public static class BackupText
{
    public static readonly HashSet<string> Terminal = ["SUCCEEDED", "PARTIAL", "FAILED", "CANCELLED"];

    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADMISSION_DEFERRED"] = "The backup service was busy, so the backup waited a little.",
        ["ADMISSION_DENIED"] = "Backup is not allowed for this server right now. Please check your plan or contact support.",
        ["CONTROL_PLANE_UNREACHABLE"] = "Your server could not reach CloudOrc. It will try again automatically.",
        ["AUTHORIZATION_REJECTED"] = "The backup agent on your server needs to be reconnected. Please contact support.",
        ["AUTHORIZATION_REVOKED"] = "The backup agent on your server was disconnected. Please contact support.",
        ["BACKUP_STORAGE_NOT_READY"] = "Backup storage is not set up yet, so backups cannot run. Our support team has been notified.",
        ["STORAGE_GRANT_FAILED"] = "We could not open your backup storage. It will try again automatically.",
        ["STORAGE_CREDENTIAL_EXPIRED"] = "Access to your backup storage expired. It will be renewed automatically.",
        ["BACKUP_QUOTA_EXCEEDED"] = "Your data is larger than the backup storage in your plan. Buy more backup storage (or remove folders) and the next backup runs normally.",
        ["STORAGE_FULL"] = "Your backup storage is full. Free up space or contact support to get more storage.",
        ["STORAGE_THROTTLED"] = "Your backup storage was busy. The backup will try again automatically.",
        ["STORAGE_UNAVAILABLE"] = "Your backup storage could not be reached. It will try again automatically.",
        ["REPOSITORY_CORRUPT"] = "There is a problem with the stored backups. Our support team has been notified.",
        ["SNAPSHOT_FAILED"] = "Windows could not take a snapshot of your files. It will try again automatically.",
        ["SNAPSHOT_STORAGE_INSUFFICIENT"] = "There was not enough free disk space on the server to take a snapshot.",
        ["APPLICATION_NOT_QUIESCED"] = "Tally was open during the backup, so Tally data may not be fully up to date. Close Tally before the next backup.",
        ["TALLY_OPEN"] = "Tally was open during the backup. Close Tally before the next backup for a complete copy.",
        ["SOURCE_NOT_FOUND"] = "A folder chosen for backup was not found on the server.",
        ["SOURCE_READ_ERRORS"] = "Some files could not be read (for example because they were open) and were skipped.",
        ["ENGINE_BINARY_TAMPERED"] = "The backup agent failed its safety check. Our support team needs to reinstall it.",
        ["ENGINE_FAILED"] = "The backup stopped unexpectedly. It will try again automatically.",
        ["WORKER_CRASHED"] = "The backup stopped unexpectedly. It will try again automatically.",
        ["AGENT_RESTARTED"] = "The server restarted during the backup. It will try again automatically.",
        ["AGENT_OFFLINE"] = "The backup could not run at the scheduled time. It will run again automatically.",
        ["NO_AGENT"] = "The backup could not start. Please try again later or contact support.",
        ["RETRIES_EXHAUSTED"] = "The backup failed after several attempts. Please try again or contact support.",
        ["CANCELLED"] = "The backup was cancelled.",
        ["SUPERSEDED"] = "A newer backup replaced this one.",
        ["STALLED"] = "We lost contact with this backup before it finished. The next backup runs normally.",
        ["POLICY_NOT_FOUND"] = "Your backup settings have not reached the server yet. Please try again in a minute.",
        ["VERIFICATION_FAILED"] = "The backup check found a problem. A new backup will be taken.",
        ["RESTORE_TARGET_INVALID"] = "The restore location is not valid.",
        ["RESTORE_PATH_UNSAFE"] = "The restore was stopped because the target folder is not safe to write to.",
        ["DISPATCH_FAILED"] = "The restore could not be sent to your server. Please request it again.",
        ["NO_SOURCES"] = "No folders are selected for backup yet.",
        ["AGENT_NOT_BACKUP_CAPABLE"] = "The backup agent on your server needs an update before it can back up.",
        ["BACKUP_NOT_IN_PLAN"] = "Backup is not part of your plan. Add Backup to your plan in the CloudOrc portal.",
        ["AGENT_NOT_RESTORE_CAPABLE"] = "The backup agent on your server needs an update before it can restore files.",
    };

    public static string? Reason(string? code, string? fallback)
    {
        var key = (code ?? string.Empty).Trim();
        if (key.Length > 0 && Reasons.TryGetValue(key, out var text)) return text;
        if (!string.IsNullOrWhiteSpace(fallback) && !System.Text.RegularExpressions.Regex.IsMatch(fallback, "kopia|lease|fenc|grant|rclone|repository", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return fallback;
        return key.Length > 0 ? "The backup did not complete. Please try again or contact support." : null;
    }

    public static string JobState(string? state) => (state ?? string.Empty).ToUpperInvariant() switch
    {
        "SUCCEEDED" => "Done",
        "PARTIAL" => "Done with warnings",
        "FAILED" => "Failed",
        "CANCELLED" => "Cancelled",
        "QUEUED" or "RETRYING" => "Preparing",
        "SNAPSHOTTING" => "Taking snapshot",
        "SCANNING" => "Checking files",
        "TRANSFERRING" => "Uploading",
        "VERIFYING" => "Verifying",
        _ => "Running"
    };

    /// <summary>ok | warn | bad | run | muted (the panel's tones).</summary>
    public static string JobTone(string? state) => (state ?? string.Empty).ToUpperInvariant() switch
    {
        "SUCCEEDED" => "ok",
        "PARTIAL" => "warn",
        "FAILED" => "bad",
        "CANCELLED" => "muted",
        _ => "run"
    };

    public static (string Label, string Tone) RestoreStatus(RestoreRow r)
    {
        if (r.ApprovalState == "pending") return ("Waiting for approval", "warn");
        return (r.State ?? string.Empty).ToUpperInvariant() switch
        {
            "SUCCEEDED" => ("Completed", "ok"),
            "PARTIAL" => ("Completed with warnings", "warn"),
            "CANCELLED" => (r.ApprovalState == "rejected" ? "Not approved" : "Cancelled", "muted"),
            "FAILED" => ("Failed", "bad"),
            _ => ("In progress", "run")
        };
    }

    public static string Bytes(double? bytes)
    {
        if (bytes is not { } b || b <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {units[i]}" : $"{b:0.#} {units[i]}";
    }

    public static string When(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("dd MMM yyyy, HH:mm") : "-";

    /// <summary>"D:\ORG\CUST-1\CloudDrive" -> "CloudDrive (D:\ORG\CUST-1)" style: last folder first, readable in a narrow panel.</summary>
    public static string FolderLabel(string path)
    {
        var p = path.TrimEnd('\\', '/');
        var i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return i <= 0 || i == p.Length - 1 ? p : p[(i + 1)..];
    }

    /// <summary>
    /// Next scheduled backup (server local time) for a daily / interval schedule; null when manual or unknown.
    /// daysOfWeek holds full day names ("Sunday"); empty = every day.
    /// </summary>
    public static DateTime? NextRun(System.Text.Json.JsonElement s, DateTime now, DateTimeOffset? lastBackup)
    {
        if (s.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var kind = s.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (kind == "interval" && s.TryGetProperty("intervalHours", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number && h.GetDouble() > 0)
        {
            var next = (lastBackup?.LocalDateTime ?? now).AddHours(h.GetDouble());
            return next < now ? now : next;
        }

        if (kind != "daily") return null;
        var times = s.TryGetProperty("timesOfDay", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Array
            ? t.EnumerateArray().Select(x => TimeSpan.TryParse(x.GetString(), out var ts) ? ts : (TimeSpan?)null).Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList()
            : [];
        if (times.Count == 0) return null;
        var days = s.TryGetProperty("daysOfWeek", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Array
            ? d.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        for (var day = 0; day < 8; day++)
        {
            var date = now.Date.AddDays(day);
            if (days.Count > 0 && !days.Contains(date.DayOfWeek.ToString())) continue;
            foreach (var time in times)
            {
                var at = date + time;
                if (at > now) return at;
            }
        }

        return null;
    }

    /// <summary>"Today 22:00" / "Tomorrow 01:00" / "Mon 01:00".</summary>
    public static string Friendly(DateTime at, DateTime now) =>
        at.Date == now.Date ? $"Today {at:HH:mm}" : at.Date == now.Date.AddDays(1) ? $"Tomorrow {at:HH:mm}" : at.ToString("ddd dd MMM, HH:mm");

    public static string Schedule(System.Text.Json.JsonElement s)
    {
        if (s.ValueKind != System.Text.Json.JsonValueKind.Object) return "Not scheduled";
        var kind = s.TryGetProperty("kind", out var k) ? k.GetString() : null;
        string Times() => s.TryGetProperty("timesOfDay", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Array
            ? string.Join(", ", t.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)))
            : string.Empty;
        string Days() => s.TryGetProperty("daysOfWeek", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Array && d.GetArrayLength() is > 0 and < 7
            ? " on " + string.Join(", ", d.EnumerateArray().Select(x => x.GetString()))
            : string.Empty;
        return kind switch
        {
            "daily" => $"Every day at {Times()}{Days()}".Replace(" at  ", " "),
            "interval" => s.TryGetProperty("intervalHours", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Number ? $"Every {h.GetDouble():0.#} hours" : "At regular intervals",
            "monthly" => $"Monthly at {Times()}",
            "manual" => "Only when you start it",
            _ => "Scheduled by your provider"
        };
    }
}
