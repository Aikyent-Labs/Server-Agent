using System.Text.Json;
using CloudOrc.WatchdogAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.WatchdogAgent.ControlAgentManagement;

/// <summary>
/// Writes the watchdog's latest resource sample of the agent it watches (and of itself) to
/// <c>&lt;DataDirectory&gt;/agent-metrics.json</c> every monitoring cycle. CloudOrc reads this file
/// for Admin → server → Agent monitoring (live CPU / RAM, stored as history). Best effort: a
/// failed write is logged once and never affects the watchdog's real job.
/// </summary>
public sealed class AgentMetricsFile(IOptions<WatchdogOptions> options, ILogger<AgentMetricsFile> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private bool _warned;

    public string FilePath => Path.Combine(options.Value.DataDirectory, "agent-metrics.json");

    public void Write(string serviceState, string? health, ProcessResourceSnapshot agent, ProcessResourceSnapshot self)
    {
        try
        {
            var doc = new
            {
                agent = "serverAgent",
                measuredAt = DateTimeOffset.UtcNow,
                intervalSeconds = options.Value.HealthCheckIntervalSeconds,
                serviceState,
                health,
                agentProcess = Shape(agent),
                watchdog = Shape(self),
            };
            Directory.CreateDirectory(options.Value.DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                logger.LogWarning(ex, "Could not write {Path}; agent monitoring falls back to a direct process read.", FilePath);
            }
        }
    }

    private static object Shape(ProcessResourceSnapshot s) => new
    {
        running = s.IsRunning,
        pid = s.ProcessId,
        cpuPercent = s.CpuPercent,
        workingSetBytes = s.WorkingSetBytes,
        privateBytes = s.PrivateMemoryBytes,
    };
}
