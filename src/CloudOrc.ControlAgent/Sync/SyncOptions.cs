namespace CloudOrc.ControlAgent.Sync;

/// <summary>"Sync" section of appsettings.json (AikySpace R3 one-way selective sync).</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Advertise sync.v1 and run the sync coordinator.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Main loop tick; file-change notifications only mark a root dirty (they are hints, not truth).</summary>
    public int TickSeconds { get; set; } = 15;

    /// <summary>Quiet period after the last change notification before a root is rescanned.</summary>
    public int DebounceSeconds { get; set; } = 30;

    /// <summary>Lower bound for the policy's rescanMinutes (full reconciliation scan).</summary>
    public int MinRescanMinutes { get; set; } = 10;

    /// <summary>Entries per manifest call (CloudOrc/AikySpace accept up to 5000).</summary>
    public int ManifestBatchSize { get; set; } = 2000;

    /// <summary>Hashing read budget (MB/s) so a scan never saturates the disk of a busy server.</summary>
    public double HashReadMbPerSecond { get; set; } = 40;

    /// <summary>Default upload budget when the policy does not set maxUploadMbps (megabits/s). 0 = unlimited.</summary>
    public double DefaultMaxUploadMbps { get; set; } = 20;

    /// <summary>While one of these processes runs (e.g. Tally) the read/upload budgets are quartered.</summary>
    public List<string> ProtectedProcessNames { get; set; } = ["tally"];

    public int RetryBaseSeconds { get; set; } = 60;
    public int RetryMaxMinutes { get; set; } = 60;

    /// <summary>Allow http:// part URLs (local development only).</summary>
    public bool DevelopmentAllowInsecureStorage { get; set; }

    public int PartTimeoutSeconds { get; set; } = 300;
}
