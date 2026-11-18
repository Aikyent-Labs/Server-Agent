using System.Globalization;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Protocol;
using CloudOrc.Agent.Contracts.Sync;

namespace CloudOrc.ControlAgent.Sync;

public sealed record SyncRootRecord(
    string RootId,
    string ConnectionId,
    string Path,
    int Generation,
    string State,
    string? FailureCode,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastFullScanAt,
    bool RescanRequested);

/// <summary>Cached fingerprint of a local file (lets a rescan skip re-hashing unchanged files).</summary>
public sealed record SyncFileRecord(string RelPath, long Size, long MtimeTicks, string Sha256, string State, int Attempts);

public static class SyncFileStates
{
    public const string Synced = "SYNCED";
    public const string Pending = "PENDING";
    public const string Failed = "FAILED";
}

public sealed class SyncStore(SyncDatabase db)
{
    private static string Ts(DateTimeOffset v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ParseTs(object? v) => v is string s && s.Length > 0 ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null;
    public static string KeyOf(string relPath) => relPath.Normalize().ToLowerInvariant();

    // ------------------------------------------------------------ policy

    public void UpsertPolicy(SyncPolicyDocument policy, DateTimeOffset now) =>
        db.InTransaction((c, tx) =>
        {
            using (var cmd = SyncDatabase.Command(c,
                """
                INSERT INTO sync_policy(connection_id, document, effective_hash, paused, applied_at)
                VALUES ($id, $doc, $hash, $paused, $at)
                ON CONFLICT(connection_id) DO UPDATE SET document = excluded.document, effective_hash = excluded.effective_hash,
                    paused = excluded.paused, applied_at = excluded.applied_at;
                """, tx,
                ("$id", policy.ConnectionId), ("$doc", JsonSerializer.Serialize(policy, ProtocolJson.Options)),
                ("$hash", policy.EffectiveHash), ("$paused", policy.State == "PAUSED" ? 1 : 0), ("$at", Ts(now))))
            {
                cmd.ExecuteNonQuery();
            }

            var keep = policy.Roots.Select(r => r.RootId).ToHashSet(StringComparer.Ordinal);
            foreach (var existing in RootIds(c, tx, policy.ConnectionId).Where(id => !keep.Contains(id)))
            {
                DeleteRoot(c, tx, existing); // removed from the policy: forget its local cache (cloud copies stay)
            }

            foreach (var root in policy.Roots)
            {
                using var cmd = SyncDatabase.Command(c,
                    """
                    INSERT INTO sync_roots(root_id, connection_id, path, rescan_requested) VALUES ($r, $c, $p, 1)
                    ON CONFLICT(root_id) DO UPDATE SET path = excluded.path, connection_id = excluded.connection_id,
                        state = CASE WHEN sync_roots.state IN ('INVALID_ROOT','REVOKED') THEN 'IDLE' ELSE sync_roots.state END,
                        rescan_requested = 1;
                    """, tx, ("$r", root.RootId), ("$c", policy.ConnectionId), ("$p", root.Path));
                cmd.ExecuteNonQuery();
            }
        });

    public IReadOnlyList<SyncPolicyDocument> Policies() =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c, "SELECT document FROM sync_policy;");
            using var r = cmd.ExecuteReader();
            var list = new List<SyncPolicyDocument>();
            while (r.Read())
            {
                if (JsonSerializer.Deserialize<SyncPolicyDocument>(r.GetString(0), ProtocolJson.Options) is { } p)
                {
                    list.Add(p);
                }
            }

            return (IReadOnlyList<SyncPolicyDocument>)list;
        });

    public bool IsPaused(string connectionId) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c, "SELECT paused FROM sync_policy WHERE connection_id = $id;", null, ("$id", connectionId));
            return cmd.ExecuteScalar() is long v && v == 1;
        });

    public bool SetPaused(string connectionId, bool paused) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c, "UPDATE sync_policy SET paused = $p WHERE connection_id = $id;", null, ("$p", paused ? 1 : 0), ("$id", connectionId));
            return cmd.ExecuteNonQuery() > 0;
        });

    public bool RemoveConnection(string connectionId)
    {
        var removed = false;
        db.InTransaction((c, tx) =>
        {
            foreach (var id in RootIds(c, tx, connectionId))
            {
                DeleteRoot(c, tx, id);
            }

            using var cmd = SyncDatabase.Command(c, "DELETE FROM sync_policy WHERE connection_id = $id;", tx, ("$id", connectionId));
            removed = cmd.ExecuteNonQuery() > 0;
        });
        return removed;
    }

    private static List<string> RootIds(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction? tx, string connectionId)
    {
        using var cmd = SyncDatabase.Command(c, "SELECT root_id FROM sync_roots WHERE connection_id = $c;", tx, ("$c", connectionId));
        using var r = cmd.ExecuteReader();
        var ids = new List<string>();
        while (r.Read())
        {
            ids.Add(r.GetString(0));
        }

        return ids;
    }

    private static void DeleteRoot(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction? tx, string rootId)
    {
        using var a = SyncDatabase.Command(c, "DELETE FROM sync_files WHERE root_id = $r;", tx, ("$r", rootId));
        a.ExecuteNonQuery();
        using var b = SyncDatabase.Command(c, "DELETE FROM sync_roots WHERE root_id = $r;", tx, ("$r", rootId));
        b.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ roots

    public IReadOnlyList<SyncRootRecord> Roots() =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c,
                "SELECT root_id, connection_id, path, generation, state, failure_code, next_attempt_at, last_full_scan_at, rescan_requested FROM sync_roots;");
            using var r = cmd.ExecuteReader();
            var list = new List<SyncRootRecord>();
            while (r.Read())
            {
                list.Add(new SyncRootRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), ParseTs(r.IsDBNull(6) ? null : r.GetString(6)),
                    ParseTs(r.IsDBNull(7) ? null : r.GetString(7)), r.GetInt64(8) == 1));
            }

            return (IReadOnlyList<SyncRootRecord>)list;
        });

    public void SetRootState(string rootId, string state, string? failureCode = null, DateTimeOffset? nextAttemptAt = null) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c,
                "UPDATE sync_roots SET state = $s, failure_code = $f, next_attempt_at = $n WHERE root_id = $r;", null,
                ("$s", state), ("$f", failureCode), ("$n", nextAttemptAt is { } n ? Ts(n) : null), ("$r", rootId));
            cmd.ExecuteNonQuery();
        });

    public void RequestRescan(string? connectionId = null) =>
        db.Run(c =>
        {
            using var cmd = connectionId is null
                ? SyncDatabase.Command(c, "UPDATE sync_roots SET rescan_requested = 1;")
                : SyncDatabase.Command(c, "UPDATE sync_roots SET rescan_requested = 1 WHERE connection_id = $c;", null, ("$c", connectionId));
            cmd.ExecuteNonQuery();
        });

    public void MarkRootDirty(string rootId) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c, "UPDATE sync_roots SET rescan_requested = 1 WHERE root_id = $r;", null, ("$r", rootId));
            cmd.ExecuteNonQuery();
        });

    /// <summary>A completed full scan: generation advanced, rescan flag cleared.</summary>
    public void CompleteScan(string rootId, int generation, DateTimeOffset now) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c,
                "UPDATE sync_roots SET generation = $g, last_full_scan_at = $t, last_checkpoint_at = $t, rescan_requested = 0 WHERE root_id = $r;", null,
                ("$g", generation), ("$t", Ts(now)), ("$r", rootId));
            cmd.ExecuteNonQuery();
        });

    // ------------------------------------------------------------ files

    public Dictionary<string, SyncFileRecord> Files(string rootId) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c, "SELECT rel_key, rel_path, size, mtime_ticks, sha256, state, attempts FROM sync_files WHERE root_id = $r;", null, ("$r", rootId));
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, SyncFileRecord>(StringComparer.Ordinal);
            while (r.Read())
            {
                map[r.GetString(0)] = new SyncFileRecord(r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetInt32(6));
            }

            return map;
        });

    public void UpsertFile(string rootId, string relPath, long size, long mtimeTicks, string sha256, string state, string? error = null, string? cloudVersion = null) =>
        db.Run(c =>
        {
            using var cmd = SyncDatabase.Command(c,
                """
                INSERT INTO sync_files(root_id, rel_key, rel_path, size, mtime_ticks, sha256, state, attempts, last_error, cloud_version)
                VALUES ($r, $k, $p, $s, $m, $h, $st, CASE WHEN $st = 'FAILED' THEN 1 ELSE 0 END, $e, $v)
                ON CONFLICT(root_id, rel_key) DO UPDATE SET rel_path = excluded.rel_path, size = excluded.size, mtime_ticks = excluded.mtime_ticks,
                    sha256 = excluded.sha256, state = excluded.state,
                    attempts = CASE WHEN excluded.state = 'FAILED' THEN sync_files.attempts + 1 ELSE 0 END,
                    last_error = excluded.last_error, cloud_version = COALESCE(excluded.cloud_version, sync_files.cloud_version);
                """, null,
                ("$r", rootId), ("$k", KeyOf(relPath)), ("$p", relPath), ("$s", size), ("$m", mtimeTicks), ("$h", sha256),
                ("$st", state), ("$e", error), ("$v", cloudVersion));
            cmd.ExecuteNonQuery();
        });

    public void RemoveFiles(string rootId, IEnumerable<string> relKeys) =>
        db.InTransaction((c, tx) =>
        {
            foreach (var key in relKeys)
            {
                using var cmd = SyncDatabase.Command(c, "DELETE FROM sync_files WHERE root_id = $r AND rel_key = $k;", tx, ("$r", rootId), ("$k", key));
                cmd.ExecuteNonQuery();
            }
        });
}
