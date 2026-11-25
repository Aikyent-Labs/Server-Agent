namespace CloudOrc.WatchdogAgent.Workspace;

/// <summary>
/// "WorkspaceUi" section: how the Watch supervises the per-user Remote Workspace panel
/// (handoff v1.2 §31, §48). The supervisor does nothing on servers where the panel is not
/// installed (no executable), so it is safe to ship enabled.
/// </summary>
public sealed class WorkspaceUiOptions
{
    public const string SectionName = "WorkspaceUi";

    public bool Enabled { get; set; } = true;

    /// <summary>Full path to CloudOrc.WorkspaceUI.exe. Empty = {install}\WorkspaceUI\CloudOrc.WorkspaceUI.exe next to the Watch folder.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    public string ProcessName { get; set; } = "CloudOrc.WorkspaceUI";

    /// <summary>Where the supervisor report is written. Empty = %ProgramData%\CloudOrc\Workspace (read by the Control Agent).</summary>
    public string ReportDirectory { get; set; } = string.Empty;

    public int CheckIntervalSeconds { get; set; } = 10;

    /// <summary>After a session is first seen, wait this long for the logon Run key to start the panel before starting it ourselves.</summary>
    public int StartGraceSeconds { get; set; } = 60;

    /// <summary>Crash-loop breaker: this many restarts inside <see cref="CrashLoopWindowMinutes"/> marks the session DEGRADED.</summary>
    public int CrashLoopThreshold { get; set; } = 3;

    public int CrashLoopWindowMinutes { get; set; } = 10;

    /// <summary>First restart delay; doubles per restart in the window (bounded backoff).</summary>
    public int RestartBackoffSeconds { get; set; } = 5;

    public int MaxRestartBackoffSeconds { get; set; } = 120;

    /// <summary>
    /// A panel that exited because the workspace is disabled is started again after this long, so
    /// re-enabling the workspace in CloudOrc takes effect without a new logon (it exits again quietly
    /// if still disabled). A user's own "hide for this session" is respected for the whole logon.
    /// </summary>
    public int DisabledRecheckMinutes { get; set; } = 15;

    /// <summary>How long a DEGRADED session is left alone before one more attempt.</summary>
    public int DegradedCooldownMinutes { get; set; } = 30;

    /// <summary>Keep a last-known-good copy of the panel and roll back to it on a crash loop after an update.</summary>
    public bool AutoRollback { get; set; } = true;

    /// <summary>A panel version counts as "good" after running this long without a crash.</summary>
    public int LastKnownGoodAfterMinutes { get; set; } = 10;

    public string ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(ExecutablePath))
        {
            return ExecutablePath;
        }

        // Install layout: {app}\WatchdogAgent\CloudOrc.WatchdogAgent.exe and {app}\WorkspaceUI\CloudOrc.WorkspaceUI.exe
        var watchDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var appDir = Path.GetDirectoryName(watchDir) ?? watchDir;
        return Path.Combine(appDir, "WorkspaceUI", ProcessName + ".exe");
    }
}
