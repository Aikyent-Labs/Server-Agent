using System.Diagnostics;
using System.Security.Cryptography;

namespace CloudOrc.ControlAgent.Sync;

public sealed record ScannedFile(string RelPath, long Size, long MtimeTicks, DateTimeOffset ModifiedAt, string Sha256, bool Rehashed, string? FileId = null);

public sealed record ScanOutcome(List<ScannedFile> Files, int Locked, int ChangedDuringRead, int Errors, int Skipped, bool RootMissing, long Bytes, long DurationMs);

/// <summary>Simple byte-rate limiter (token bucket over wall-clock time).</summary>
public sealed class ByteRateLimiter(Func<double> bytesPerSecond)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _sent;

    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        var rate = bytesPerSecond();
        if (rate <= 0)
        {
            return;
        }

        _sent += bytes;
        var due = _sent / rate;
        var elapsed = _clock.Elapsed.TotalSeconds;
        if (due > elapsed)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(due - elapsed, 5)), ct).ConfigureAwait(false);
        }

        if (_clock.Elapsed.TotalSeconds > 60)
        {
            // re-anchor so a long idle period does not allow a huge burst
            _clock.Restart();
            _sent = 0;
        }
    }
}

/// <summary>
/// Full inventory scan of one root (§08 "Initial inventory scan plus ... periodic reconciliation";
/// "Manifest: path-ref, size, mtime, checksum"; "Consistency: changed-during-read file is retried;
/// locked files marked SKIPPED_RETRYABLE").
///
/// - never follows reparse points (junctions/symlinks) - no escape out of the root, no loops
/// - re-hashes only files whose size or mtime changed since the last scan (fingerprint cache)
/// - files are opened with FileShare.ReadWrite|Delete so applications keep working; a file that changes
///   while it is read is retried on the next pass instead of being uploaded half-written
/// </summary>
public static class SyncScanner
{
    public const int MaxFilesPerRoot = 500_000;

    public static async Task<ScanOutcome> ScanAsync(
        string root,
        SyncGlob excludes,
        IReadOnlyDictionary<string, SyncFileRecord> cache,
        ByteRateLimiter readLimiter,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var files = new List<ScannedFile>();
        int locked = 0, changed = 0, errors = 0, skipped = 0;
        long bytes = 0;
        if (!Directory.Exists(root))
        {
            return new ScanOutcome(files, 0, 0, 0, 0, true, 0, sw.ElapsedMilliseconds);
        }

        var builtIn = new SyncGlob(SyncGlob.BuiltIn);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0 && files.Count < MaxFilesPerRoot)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 }).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                errors++;
                continue;
            }

            foreach (var child in children)
            {
                var rel = Path.GetRelativePath(root, child.FullName).Replace('\\', '/');
                if (excludes.IsMatch(rel) || builtIn.IsMatch(rel))
                {
                    skipped++;
                    continue;
                }

                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++; // never follow links out of the root
                    continue;
                }

                if (child is DirectoryInfo d)
                {
                    stack.Push(d);
                    continue;
                }

                if (child is not FileInfo f || f.Attributes.HasFlag(FileAttributes.System) || f.Attributes.HasFlag(FileAttributes.Temporary))
                {
                    skipped++;
                    continue;
                }

                long size;
                DateTime mtime;
                try
                {
                    f.Refresh();
                    size = f.Length;
                    mtime = f.LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    continue;
                }

                var key = SyncStore.KeyOf(rel);
                if (cache.TryGetValue(key, out var known) && known.Size == size && known.MtimeTicks == mtime.Ticks && known.Sha256.Length == 64)
                {
                    files.Add(new ScannedFile(rel, size, mtime.Ticks, new DateTimeOffset(mtime, TimeSpan.Zero), known.Sha256, false));
                    bytes += size;
                    continue;
                }

                var hashed = await HashStableAsync(f.FullName, size, mtime, readLimiter, ct).ConfigureAwait(false);
                switch (hashed.Result)
                {
                    case HashResult.Ok:
                        files.Add(new ScannedFile(rel, size, mtime.Ticks, new DateTimeOffset(mtime, TimeSpan.Zero), hashed.Sha256!, true, hashed.FileId));
                        bytes += size;
                        break;
                    case HashResult.Locked:
                        locked++;
                        break;
                    case HashResult.Changed:
                        changed++;
                        break;
                    default:
                        errors++;
                        break;
                }
            }
        }

        return new ScanOutcome(files, locked, changed, errors, skipped, false, bytes, sw.ElapsedMilliseconds);
    }

    public enum HashResult
    {
        Ok,
        Locked,
        Changed,
        Error
    }

    /// <summary>SHA-256 of a file that did not change while it was read (size + mtime re-checked after).</summary>
    public static async Task<(HashResult Result, string? Sha256, string? FileId)> HashStableAsync(string path, long expectedSize, DateTime expectedMtimeUtc, ByteRateLimiter limiter, CancellationToken ct)
    {
        try
        {
            using var sha = SHA256.Create();
            string? fileId;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                fileId = StableFileId.TryGet(stream.SafeFileHandle);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await limiter.WaitAsync(read, ct).ConfigureAwait(false);
                }

                sha.TransformFinalBlock([], 0, 0);
            }

            var after = new FileInfo(path);
            after.Refresh();
            if (!after.Exists || after.Length != expectedSize || after.LastWriteTimeUtc != expectedMtimeUtc)
            {
                return (HashResult.Changed, null, null);
            }

            return (HashResult.Ok, Convert.ToHexString(sha.Hash!).ToLowerInvariant(), fileId);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return (HashResult.Locked, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (HashResult.Error, null, null);
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33).</summary>
    public static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
