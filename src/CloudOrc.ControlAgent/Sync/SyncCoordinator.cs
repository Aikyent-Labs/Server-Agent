using System.Collections.Concurrent;
using System.Diagnostics;
using CloudOrc.Agent.Contracts.Sync;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Sync;

/// <summary>
/// AikySpace one-way selective sync engine (spec §08). A hosted service of its own - independent of
/// the backup job coordinator, so a long sync never blocks backups and vice versa.
///
/// Per root: IDLE → SCANNING → UPLOADING → VERIFYING → CHECKPOINTED → IDLE, with recoverable branches
/// PAUSED_POLICY / WAITING_NETWORK / RETRY_BACKOFF / FILE_LOCKED / QUOTA_BLOCKED and terminal
/// REVOKED / INVALID_ROOT. File-system notifications only mark a root dirty; a full reconciliation
/// scan (cheap: unchanged files are not re-hashed) is the source of truth.
/// Safety rules: a scan that could not read every file is never sent as "complete" (so nothing is
/// tombstoned by mistake); a file is checkpointed only after the cloud confirmed the commit.
/// </summary>
public sealed class SyncCoordinator : BackgroundService
{
    private readonly SyncStore _store;
    private readonly SyncTransport _transport;
    private readonly SyncOptions _options;
    private readonly string _dataDirectory;
    private readonly TimeProvider _time;
    private readonly ILogger<SyncCoordinator> _logger;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dirtyAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _live = new(StringComparer.Ordinal);
    private DateTimeOffset _protectedCheckedAt = DateTimeOffset.MinValue;
    private bool _protectedRunning;

    public SyncCoordinator(SyncStore store, SyncTransport transport, IOptions<SyncOptions> options, IOptions<ControlAgentOptions> agentOptions, TimeProvider time, ILogger<SyncCoordinator> logger)
    {
        _store = store;
        _transport = transport;
        _options = options.Value;
        _dataDirectory = agentOptions.Value.DataDirectory;
        _time = time;
        _logger = logger;
    }

    /// <summary>Current state per root (for local status / telemetry).</summary>
    public IReadOnlyDictionary<string, string> LiveStates => _live;

    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync tick failed.");
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, _options.TickSeconds)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var w in _watchers.Values)
        {
            w.Dispose();
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var policies = _store.Policies().ToDictionary(p => p.ConnectionId, StringComparer.Ordinal);
        var roots = _store.Roots();
        UpdateWatchers(policies, roots);
        if (!_transport.IsConfigured)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!policies.TryGetValue(root.ConnectionId, out var policy))
            {
                continue;
            }

            var spec = policy.Roots.FirstOrDefault(r => r.RootId == root.RootId);
            if (spec is null)
            {
                continue;
            }

            if (policy.State == "PAUSED" || _store.IsPaused(root.ConnectionId))
            {
                _live[root.RootId] = SyncStates.PausedPolicy;
                continue;
            }

            if (root.State == SyncStates.Revoked || (root.State == SyncStates.InvalidRoot && !root.RescanRequested))
            {
                _live[root.RootId] = root.State;
                continue;
            }

            if (root.NextAttemptAt is { } next && next > now && !root.RescanRequested)
            {
                continue;
            }

            var rescanEvery = TimeSpan.FromMinutes(Math.Max(_options.MinRescanMinutes, policy.RescanMinutes));
            var dirty = _dirtyAt.TryGetValue(root.RootId, out var at) && now - at >= TimeSpan.FromSeconds(_options.DebounceSeconds);
            var due = root.RescanRequested || root.LastFullScanAt is null || now - root.LastFullScanAt >= rescanEvery || dirty
                || root.State is SyncStates.RetryBackoff or SyncStates.WaitingNetwork or SyncStates.FileLocked or SyncStates.QuotaBlocked;
            if (!due)
            {
                continue;
            }

            _dirtyAt.TryRemove(root.RootId, out _);
            await RunRootAsync(policy, spec, root, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ one root

    private async Task RunRootAsync(SyncPolicyDocument policy, SyncRootSpec spec, SyncRootRecord root, CancellationToken ct)
    {
        var allowed = DiscoveryBrowseHandler.NormalizeRoots(policy.AllowedRoots);
        var path = SyncPathPolicy.Canonical(spec.Path);
        var reject = SyncPathPolicy.RejectReason(spec.Path, allowed, _dataDirectory);
        if (path is null || reject is not null || !Directory.Exists(path))
        {
            var code = path is not null && !Directory.Exists(path) ? SyncReasonCodes.RootMissing : SyncReasonCodes.PathNotAllowed;
            _logger.LogWarning("Sync root {RootId} rejected locally: {Reason}", root.RootId, reject ?? code);
            Enter(root, SyncStates.InvalidRoot, code, null);
            await CheckpointAsync(root, root.Generation, SyncStates.InvalidRoot, code, new(), ct).ConfigureAwait(false);
            return;
        }

        var generation = root.Generation + 1;
        var started = Stopwatch.StartNew();
        Enter(root, SyncStates.Scanning, null, null);
        var cache = _store.Files(root.RootId);
        var scan = await SyncScanner.ScanAsync(path, new SyncGlob(spec.Excludes), cache, new ByteRateLimiter(ReadBudget), ct).ConfigureAwait(false);
        if (scan.RootMissing)
        {
            Enter(root, SyncStates.InvalidRoot, SyncReasonCodes.RootMissing, null);
            await CheckpointAsync(root, root.Generation, SyncStates.InvalidRoot, SyncReasonCodes.RootMissing, new(), ct).ConfigureAwait(false);
            return;
        }

        foreach (var f in scan.Files.Where(f => f.Rehashed))
        {
            var key = SyncStore.KeyOf(f.RelPath);
            var stillSynced = cache.TryGetValue(key, out var old) && old.State == SyncFileStates.Synced && old.Sha256 == f.Sha256;
            _store.UpsertFile(root.RootId, f.RelPath, f.Size, f.MtimeTicks, f.Sha256, stillSynced ? SyncFileStates.Synced : SyncFileStates.Pending);
        }

        // never "complete" a scan that skipped unreadable files: completion tombstones what was not seen
        var complete = scan.Locked == 0 && scan.ChangedDuringRead == 0 && scan.Errors == 0;
        var uploads = new List<SyncUploadInstruction>();
        var batches = scan.Files.Chunk(Math.Clamp(_options.ManifestBatchSize, 100, 5000)).ToList();
        if (batches.Count == 0)
        {
            batches.Add([]);
        }

        for (var i = 0; i < batches.Count; i++)
        {
            var response = await _transport.ManifestAsync(new SyncManifestRequest
            {
                RootId = root.RootId,
                Generation = generation,
                Complete = complete && i == batches.Count - 1,
                Entries = batches[i].Select(f => new SyncManifestEntry { Path = f.RelPath, Size = f.Size, ModifiedAt = f.ModifiedAt, Sha256 = f.Sha256, FileId = f.FileId }).ToList()
            }, ct).ConfigureAwait(false);
            if (response.Value is null)
            {
                await FailAsync(root, response.Failure, response.Detail, response.RetryAfter, ct).ConfigureAwait(false);
                return;
            }

            uploads.AddRange(response.Value.Upload);
        }

        var byKey = scan.Files.ToDictionary(f => SyncStore.KeyOf(f.RelPath), StringComparer.Ordinal);
        int uploaded = 0, rejected = 0;
        if (uploads.Count > 0)
        {
            Enter(root, SyncStates.Uploading, null, null);
            foreach (var instruction in uploads)
            {
                ct.ThrowIfCancellationRequested();
                if (!byKey.TryGetValue(SyncStore.KeyOf(instruction.Path), out var file))
                {
                    continue;
                }

                var result = await UploadOneAsync(root, path, file, generation, policy, ct).ConfigureAwait(false);
                if (result == SyncFailureKind.None)
                {
                    uploaded++;
                    continue;
                }

                if (result == SyncFailureKind.FileRejected)
                {
                    rejected++;
                    _store.UpsertFile(root.RootId, file.RelPath, file.Size, file.MtimeTicks, file.Sha256, SyncFileStates.Failed, "rejected");
                    continue;
                }

                await FailAsync(root, result, $"upload of {file.RelPath} failed", null, ct).ConfigureAwait(false);
                return;
            }
        }

        Enter(root, SyncStates.Verifying, null, null);
        var stats = new Dictionary<string, object?>
        {
            ["files"] = scan.Files.Count,
            ["bytes"] = scan.Bytes,
            ["uploaded"] = uploaded,
            ["rejected"] = rejected,
            ["locked"] = scan.Locked,
            ["changedDuringRead"] = scan.ChangedDuringRead,
            ["errors"] = scan.Errors,
            ["scanMs"] = scan.DurationMs,
            ["totalMs"] = started.ElapsedMilliseconds,
            // telemetry (spec 08): queue depth and lag since the last successful checkpoint
            ["pendingUploads"] = Math.Max(0, uploads.Count - uploaded - rejected),
            ["lagSeconds"] = root.LastFullScanAt is { } last ? (long)(_time.GetUtcNow() - last).TotalSeconds : (long?)null,
            ["uploadBudgetBps"] = UploadBudget(policy)
        };

        if (complete)
        {
            _store.CompleteScan(root.RootId, generation, _time.GetUtcNow());
            Enter(root, SyncStates.Checkpointed, null, null);
            await CheckpointAsync(root, generation, SyncStates.Checkpointed, null, stats, ct).ConfigureAwait(false);
            _failures.TryRemove(root.RootId, out _);
            Enter(root, SyncStates.Idle, null, null);
        }
        else
        {
            // in-use / changing files: everything else is synced; try the rest again soon
            var state = scan.Locked > 0 ? SyncStates.FileLocked : SyncStates.RetryBackoff;
            Enter(root, state, scan.Locked > 0 ? SyncReasonCodes.Locked : SyncReasonCodes.ChangedDuringRead, _time.GetUtcNow().AddMinutes(5));
            await CheckpointAsync(root, root.Generation, state, scan.Locked > 0 ? SyncReasonCodes.Locked : SyncReasonCodes.ChangedDuringRead, stats, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Sync root {RootId}: {Files} files, {Uploaded} uploaded, {Locked} locked, complete={Complete}.", root.RootId, scan.Files.Count, uploaded, scan.Locked, complete);
    }

    private async Task<SyncFailureKind> UploadOneAsync(SyncRootRecord root, string rootPath, ScannedFile file, int generation, SyncPolicyDocument policy, CancellationToken ct)
    {
        var fullPath = Path.Combine(rootPath, file.RelPath.Replace('/', Path.DirectorySeparatorChar));
        if (!IsUnchanged(fullPath, file))
        {
            MarkDirty(root.RootId);
            return SyncFailureKind.FileRejected; // changed since the scan: picked up by the next pass
        }

        var start = await _transport.StartUploadAsync(new SyncUploadRequest
        {
            RootId = root.RootId,
            Path = file.RelPath,
            Size = file.Size,
            Sha256 = file.Sha256,
            Generation = generation
        }, ct).ConfigureAwait(false);
        if (start.Value is null)
        {
            return start.Failure;
        }

        if (start.Value.Skipped)
        {
            _store.UpsertFile(root.RootId, file.RelPath, file.Size, file.MtimeTicks, file.Sha256, SyncFileStates.Synced);
            return SyncFailureKind.None;
        }

        var sessionId = start.Value.SessionId!;
        var partSize = start.Value.PartSize;
        var parts = start.Value.Parts;
        var limiter = new ByteRateLimiter(() => UploadBudget(policy));
        for (var refresh = 0; refresh <= 3; refresh++)
        {
            var needRefresh = false;
            foreach (var part in parts)
            {
                var outcome = SyncFailureKind.Transient;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    outcome = await _transport.PutPartAsync(part.Url, fullPath, (part.Part - 1) * partSize, part.Size, limiter, ct).ConfigureAwait(false);
                    if (outcome is SyncFailureKind.None or SyncFailureKind.GrantExpired or SyncFailureKind.FileRejected)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) + Random.Shared.NextDouble()), _time, ct).ConfigureAwait(false);
                }

                if (outcome == SyncFailureKind.GrantExpired)
                {
                    needRefresh = true;
                    break;
                }

                if (outcome != SyncFailureKind.None)
                {
                    return outcome;
                }
            }

            if (!needRefresh)
            {
                break;
            }

            var fresh = await _transport.RefreshUploadAsync(sessionId, root.RootId, ct).ConfigureAwait(false);
            if (fresh.Value is null)
            {
                return fresh.Failure;
            }

            parts = fresh.Value.Parts; // grants for the parts still missing
            if (refresh == 3 && parts.Count > 0)
            {
                return SyncFailureKind.Transient;
            }
        }

        if (!IsUnchanged(fullPath, file))
        {
            MarkDirty(root.RootId);
            return SyncFailureKind.FileRejected; // never commit content that changed under the upload
        }

        var done = await _transport.CompleteAsync(sessionId, new SyncCompleteRequest
        {
            RootId = root.RootId,
            Path = file.RelPath,
            ModifiedAt = file.ModifiedAt,
            Generation = generation
        }, ct).ConfigureAwait(false);
        if (done.Value is null)
        {
            return done.Failure;
        }

        // checkpoint only after the cloud confirmed the commit (§08)
        _store.UpsertFile(root.RootId, file.RelPath, file.Size, file.MtimeTicks, file.Sha256, SyncFileStates.Synced, null, done.Value.VersionId);
        return SyncFailureKind.None;
    }

    private static bool IsUnchanged(string fullPath, ScannedFile file)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc.Ticks == file.MtimeTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ failures / states

    private async Task FailAsync(SyncRootRecord root, SyncFailureKind kind, string detail, TimeSpan? retryAfter, CancellationToken ct)
    {
        var n = _failures.AddOrUpdate(root.RootId, 1, (_, v) => v + 1);
        var backoff = retryAfter ?? Backoff(n);
        var now = _time.GetUtcNow();
        (string State, string? Code, DateTimeOffset? Next) decision = kind switch
        {
            SyncFailureKind.Network => (SyncStates.WaitingNetwork, "NETWORK", now + backoff),
            SyncFailureKind.Quota => (SyncStates.QuotaBlocked, SyncReasonCodes.Quota, now.AddMinutes(30)),
            SyncFailureKind.Revoked => (SyncStates.Revoked, SyncReasonCodes.Revoked, null),
            SyncFailureKind.UnknownRoot => (SyncStates.InvalidRoot, SyncReasonCodes.NotConfigured, null),
            SyncFailureKind.Paused => (SyncStates.PausedPolicy, null, now.AddMinutes(10)),
            _ => (SyncStates.RetryBackoff, "TRANSIENT", now + backoff)
        };
        var (state, code, next) = decision;
        _logger.LogWarning("Sync root {RootId} → {State}: {Detail}", root.RootId, state, detail);
        Enter(root, state, code, next);
        if (kind is not (SyncFailureKind.Network or SyncFailureKind.Revoked))
        {
            await CheckpointAsync(root, root.Generation, state, code, new() { ["detail"] = detail.Length > 200 ? detail[..200] : detail }, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan Backoff(int failures)
    {
        var cap = Math.Min(_options.RetryMaxMinutes * 60.0, _options.RetryBaseSeconds * Math.Pow(2, Math.Max(0, failures - 1)));
        return TimeSpan.FromSeconds(Math.Max(15, Random.Shared.NextDouble() * cap)); // full jitter
    }

    private void Enter(SyncRootRecord root, string state, string? failureCode, DateTimeOffset? nextAttemptAt)
    {
        _live[root.RootId] = state;
        _store.SetRootState(root.RootId, state, failureCode, nextAttemptAt);
    }

    private async Task CheckpointAsync(SyncRootRecord root, int generation, string state, string? failureCode, Dictionary<string, object?> stats, CancellationToken ct)
    {
        var res = await _transport.CheckpointAsync(new SyncCheckpointRequest { RootId = root.RootId, Generation = generation, State = state, FailureCode = failureCode, Stats = stats }, ct).ConfigureAwait(false);
        if (res.Failure == SyncFailureKind.Revoked)
        {
            Enter(root, SyncStates.Revoked, SyncReasonCodes.Revoked, null);
        }
    }

    // ------------------------------------------------------------------ budgets

    private bool ProtectedAppRunning()
    {
        var now = _time.GetUtcNow();
        if (now - _protectedCheckedAt < TimeSpan.FromSeconds(30))
        {
            return _protectedRunning;
        }

        _protectedCheckedAt = now;
        try
        {
            _protectedRunning = _options.ProtectedProcessNames.Any(n => Process.GetProcessesByName(n).Length > 0);
        }
        catch (InvalidOperationException)
        {
            _protectedRunning = false;
        }

        return _protectedRunning;
    }

    private double ReadBudget() => _options.HashReadMbPerSecond * 1024 * 1024 * (ProtectedAppRunning() ? 0.25 : 1);

    private double UploadBudget(SyncPolicyDocument policy)
    {
        var window = SyncBandwidth.ActiveWindow(policy.BandwidthWindows, _time.GetLocalNow());
        if (window is not null)
        {
            // 0 in a window = no uploads during that window (a tiny trickle keeps sessions alive)
            return window.MaxUploadMbps <= 0 ? 16 * 1024 : window.MaxUploadMbps * 125_000 * (ProtectedAppRunning() ? 0.25 : 1);
        }

        var mbps = policy.MaxUploadMbps ?? _options.DefaultMaxUploadMbps;
        return mbps <= 0 ? 0 : mbps * 125_000 * (ProtectedAppRunning() ? 0.25 : 1);
    }

    // ------------------------------------------------------------------ change notifications (hints)

    private void MarkDirty(string rootId) => _dirtyAt[rootId] = _time.GetUtcNow();

    private void UpdateWatchers(Dictionary<string, SyncPolicyDocument> policies, IReadOnlyList<SyncRootRecord> roots)
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (policies.TryGetValue(r.ConnectionId, out var p) && p.State != "PAUSED" && r.State is not (SyncStates.Revoked or SyncStates.InvalidRoot)
                && SyncPathPolicy.Canonical(r.Path) is { } path && Directory.Exists(path))
            {
                wanted[r.RootId] = path;
            }
        }

        foreach (var (rootId, watcher) in _watchers)
        {
            if (!wanted.TryGetValue(rootId, out var path) || !string.Equals(watcher.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                if (_watchers.TryRemove(rootId, out var old))
                {
                    old.Dispose();
                }
            }
        }

        foreach (var (rootId, path) in wanted)
        {
            if (_watchers.ContainsKey(rootId))
            {
                continue;
            }

            try
            {
                var w = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };
                FileSystemEventHandler onChange = (_, _) => MarkDirty(rootId);
                w.Changed += onChange;
                w.Created += onChange;
                w.Deleted += onChange;
                w.Renamed += (_, _) => MarkDirty(rootId);
                w.Error += (_, _) => _store.MarkRootDirty(rootId); // buffer overflow → full rescan
                w.EnableRaisingEvents = true;
                _watchers[rootId] = w;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _logger.LogDebug(ex, "Could not watch {Path}; periodic rescans still apply.", path);
            }
        }
    }

    public override void Dispose()
    {
        foreach (var w in _watchers.Values)
        {
            w.Dispose();
        }

        _wake.Dispose();
        base.Dispose();
    }
}
