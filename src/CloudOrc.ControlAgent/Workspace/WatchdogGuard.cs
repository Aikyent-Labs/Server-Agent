using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Workspace;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>
/// The Control Agent watches the Watch (the Watch already watches the Control Agent): mutual supervision.
///  - Watch service stopped (not disabled by an administrator) -> start it again, at most 3 times an hour;
///  - Watch running but its workspace report is older than 3 minutes -> reported NOT_REPORTING (hung);
///  - status is sent to CloudOrc as the WATCHDOG component, so the team is alerted.
/// Windows service recovery (sc failure: restart) remains the first safety net for a crashed process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WatchdogGuard(TimeProvider time, ILogger<WatchdogGuard> logger) : BackgroundService
{
    public const string ServiceName = "CloudOrcWatchdogAgent";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReportMaxAge = TimeSpan.FromMinutes(3);
    private const int MaxStartsPerHour = 3;
    private readonly Queue<DateTimeOffset> _starts = new();

    public string Status { get; private set; } = WorkspaceComponentStatus.Unknown;
    public string? ReasonCode { get; private set; }
    public string? LastAction { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }

    /// <summary>False on servers where the Watch is not installed (dev runs): nothing to report.</summary>
    public bool Installed { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckOnce();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Watch check failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal void CheckOnce()
    {
        var now = time.GetUtcNow();
        CheckedAt = now;
        using var sc = new ServiceController(ServiceName);
        ServiceControllerStatus state;
        ServiceStartMode mode;
        try
        {
            state = sc.Status;
            mode = sc.StartType;
            Installed = true;
        }
        catch (InvalidOperationException)
        {
            Installed = false;
            Status = WorkspaceComponentStatus.Unknown;
            return;
        }

        if (state == ServiceControllerStatus.Running)
        {
            var reportAt = ReadReportTime();
            if (reportAt is { } at && now - at > ReportMaxAge)
            {
                Set(WorkspaceComponentStatus.Degraded, WorkspaceReasonCodes.WatchdogStale);
            }
            else
            {
                Set(WorkspaceComponentStatus.Healthy, null);
            }

            return;
        }

        if (state is ServiceControllerStatus.StartPending)
        {
            Set(WorkspaceComponentStatus.Recovering, null);
            return;
        }

        Set(WorkspaceComponentStatus.Stopped, WorkspaceReasonCodes.WatchdogDown);
        if (mode == ServiceStartMode.Disabled)
        {
            return; // an administrator turned it off on purpose; CloudOrc is told, nothing is forced
        }

        if (!CanStartServices())
        {
            // Console / dev run: never touch the services of an installation this process does not belong to.
            LastAction = "not started (agent is not running as the installed service)";
            return;
        }

        while (_starts.Count > 0 && now - _starts.Peek() > TimeSpan.FromHours(1))
        {
            _starts.Dequeue();
        }

        if (_starts.Count >= MaxStartsPerHour)
        {
            LastAction = "start limit reached (3 an hour)";
            return;
        }

        _starts.Enqueue(now);
        try
        {
            if (state != ServiceControllerStatus.Stopped)
            {
                return; // stopping / paused: look again next minute
            }

            sc.Start();
            LastAction = "started the stopped Watch service";
            logger.LogWarning("The CloudOrc Watch service was stopped; started it again ({Count}/{Max} this hour).", _starts.Count, MaxStartsPerHour);
            Set(WorkspaceComponentStatus.Recovering, WorkspaceReasonCodes.WatchdogDown);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastAction = "could not start the Watch service";
            logger.LogWarning(ex, "Could not start the CloudOrc Watch service.");
        }
    }

    /// <summary>Only the installed Control Agent service (SCM-started, same install as the Watch) restarts the Watch. Tests may override.</summary>
    internal Func<bool> CanStartServices { get; init; } = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService;

    private void Set(string status, string? reason)
    {
        Status = status;
        ReasonCode = reason;
    }

    private static DateTimeOffset? ReadReportTime()
    {
        try
        {
            var path = WorkspaceHealthReporter.SupervisorReportPath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WorkspaceSupervisorReport>(File.ReadAllText(path), WorkspaceProtocol.JsonOptions)?.GeneratedAt;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
