using System.Net;
using System.Security.Cryptography;
using System.Text;
using CloudOrc.Agent.Contracts.Sync;
using CloudOrc.ControlAgent.Sync;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudOrc.ControlAgent.Tests.Sync;

public class SyncPathPolicyTests
{
    private static SyncPolicyDocument Policy(params string[] paths) => new()
    {
        ConnectionId = "syc_1",
        Roots = paths.Select((p, i) => new SyncRootSpec { RootId = $"syr_{i}", Path = p }).ToList()
    };

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files\Tally")]
    [InlineData(@"C:\Program Files (x86)\App")]
    [InlineData(@"C:\ProgramData\CloudOrc\ControlAgent")]
    [InlineData(@"C:\Users\rahul\AppData\Roaming")]
    [InlineData(@"D:\$Recycle.Bin\x")]
    [InlineData(@"D:\Tools\CloudOrc\data")]
    public void SystemProgramAndAgentFolders_AreRejected(string path) =>
        Assert.NotNull(SyncPathPolicy.RejectReason(path, [], null));

    [Theory]
    [InlineData(@"\\fileserver\share\x")]
    [InlineData(@"TallyData")]
    [InlineData(@"D:\a\..\Windows")]
    [InlineData(@"D:\")]
    [InlineData(@"D:\a:stream")]
    public void UnsafeOrWholeDrivePaths_AreRejected(string path) =>
        Assert.NotNull(SyncPathPolicy.RejectReason(path, [], null));

    [Fact]
    public void SharedServer_OnlyInsideAllowedRoots()
    {
        var allowed = new List<string> { @"D:\ORG1\CUST1" };
        Assert.NotNull(SyncPathPolicy.RejectReason(@"D:\ORG1\CUST2\Data", allowed, null));
        Assert.Null(SyncPathPolicy.RejectReason(@"D:\ORG1\CUST1\Data", allowed, null));
    }

    [Fact]
    public void Validate_RejectsOverlapsTwoWayAndBadIds()
    {
        Assert.Contains(SyncPathPolicy.Validate(Policy(@"D:\Data", @"D:\Data\Sub")), e => e.Contains("overlaps", StringComparison.Ordinal));
        Assert.Contains(SyncPathPolicy.Validate(new SyncPolicyDocument { ConnectionId = "syc_1", Direction = "TWO_WAY", Roots = [new() { RootId = "syr_1", Path = @"D:\Data" }] }), e => e.Contains("UPLOAD_ONLY", StringComparison.Ordinal));
        Assert.NotEmpty(SyncPathPolicy.Validate(new SyncPolicyDocument { ConnectionId = "bad id!", Roots = [new() { RootId = "syr_1", Path = @"D:\Data" }] }));
        Assert.NotEmpty(SyncPathPolicy.Validate(null));
        Assert.Empty(SyncPathPolicy.Validate(Policy(@"D:\Data", @"E:\Tally")));
    }

    [Fact]
    public void Canonical_NormalisesSlashesAndTrailingSeparator() =>
        Assert.Equal(@"D:\Tally\Data", SyncPathPolicy.Canonical("D:/Tally/Data/"));
}

public class SyncGlobTests
{
    [Theory]
    [InlineData("*.tmp", "a/b/c.tmp", true)]
    [InlineData("Backup", "Company/Backup/x.900", true)]
    [InlineData("Company/*.log", "Company/x.log", true)]
    [InlineData("Company/*.log", "Company/sub/x.log", false)]
    [InlineData("**/cache/**", "a/cache/b/c", true)]
    [InlineData("*.TMP", "x.tmp", true)]
    [InlineData("*.900", "Data.901", false)]
    public void Matches(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new SyncGlob([pattern]).IsMatch(path));

    [Fact]
    public void BuiltInSkipsOfficeLockAndTempFiles()
    {
        var g = new SyncGlob(SyncGlob.BuiltIn);
        Assert.True(g.IsMatch("Docs/~$report.docx"));
        Assert.True(g.IsMatch("Thumbs.db"));
        Assert.False(g.IsMatch("Docs/report.docx"));
    }
}

public sealed class SyncStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cloudorc-sync-" + Guid.NewGuid().ToString("N"));
    private readonly SyncDatabase _db;
    private readonly SyncStore _store;

    public SyncStoreTests()
    {
        _db = new SyncDatabase(Path.Combine(_dir, "sync.db"), NullLogger<SyncDatabase>.Instance);
        _store = new SyncStore(_db);
    }

    [Fact]
    public void PolicyRoundTrip_RootsFollowPolicy_AndRemovalForgetsLocalState()
    {
        var policy = new SyncPolicyDocument { ConnectionId = "syc_1", EffectiveHash = "h1", Roots = [new() { RootId = "syr_1", Path = @"D:\A" }, new() { RootId = "syr_2", Path = @"D:\B" }] };
        _store.UpsertPolicy(policy, DateTimeOffset.UtcNow);
        Assert.Equal(2, _store.Roots().Count);
        _store.UpsertFile("syr_2", "x.txt", 1, 1, new string('a', 64), SyncFileStates.Synced);

        // syr_2 removed from the policy → its cache is dropped
        _store.UpsertPolicy(new SyncPolicyDocument { ConnectionId = "syc_1", EffectiveHash = "h2", Roots = [new() { RootId = "syr_1", Path = @"D:\A" }] }, DateTimeOffset.UtcNow);
        Assert.Single(_store.Roots());
        Assert.Empty(_store.Files("syr_2"));

        Assert.True(_store.SetPaused("syc_1", true));
        Assert.True(_store.IsPaused("syc_1"));
        Assert.True(_store.RemoveConnection("syc_1"));
        Assert.Empty(_store.Roots());
        Assert.Empty(_store.Policies());
    }

    [Fact]
    public void FileKeys_AreCaseInsensitive_AndFailuresCount()
    {
        _store.UpsertPolicy(new SyncPolicyDocument { ConnectionId = "c", EffectiveHash = "h", Roots = [new() { RootId = "r", Path = @"D:\A" }] }, DateTimeOffset.UtcNow);
        _store.UpsertFile("r", "Company/Data.900", 10, 5, new string('b', 64), SyncFileStates.Failed, "x");
        _store.UpsertFile("r", "company/DATA.900", 10, 5, new string('b', 64), SyncFileStates.Failed, "y");
        var files = _store.Files("r");
        Assert.Single(files);
        Assert.Equal(2, files.Values.Single().Attempts);
        _store.CompleteScan("r", 3, DateTimeOffset.UtcNow);
        var root = _store.Roots().Single();
        Assert.Equal(3, root.Generation);
        Assert.False(root.RescanRequested);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class SyncScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloudorc-scan-" + Guid.NewGuid().ToString("N"));

    public SyncScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Company"));
        File.WriteAllText(Path.Combine(_root, "Company", "Data.900"), "tally data");
        File.WriteAllText(Path.Combine(_root, "notes.tmp"), "temp");
        File.WriteAllText(Path.Combine(_root, "Company", "skip.log"), "log");
    }

    [Fact]
    public async Task Scan_HashesFiles_AppliesExcludes_AndReusesCache()
    {
        var limiter = new ByteRateLimiter(() => 0);
        var first = await SyncScanner.ScanAsync(_root, new SyncGlob(["*.log"]), new Dictionary<string, SyncFileRecord>(), limiter, CancellationToken.None);
        var data = Assert.Single(first.Files);
        Assert.Equal("Company/Data.900", data.RelPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("tally data"))).ToLowerInvariant(), data.Sha256);
        Assert.True(data.Rehashed);

        var cache = new Dictionary<string, SyncFileRecord> { [SyncStore.KeyOf(data.RelPath)] = new(data.RelPath, data.Size, data.MtimeTicks, data.Sha256, SyncFileStates.Synced, 0) };
        var second = await SyncScanner.ScanAsync(_root, new SyncGlob(["*.log"]), cache, limiter, CancellationToken.None);
        Assert.False(Assert.Single(second.Files).Rehashed); // unchanged size+mtime → no re-read
    }

    [Fact]
    public async Task LockedFile_IsReportedAsLocked_NotUploadedHalfWritten()
    {
        var path = Path.Combine(_root, "Company", "Data.900");
        await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var scan = await SyncScanner.ScanAsync(_root, new SyncGlob([]), new Dictionary<string, SyncFileRecord>(), new ByteRateLimiter(() => 0), CancellationToken.None);
        Assert.Equal(1, scan.Locked);
        Assert.DoesNotContain(scan.Files, f => f.RelPath == "Company/Data.900");
    }

    [Fact]
    public async Task MissingRoot_IsReported() =>
        Assert.True((await SyncScanner.ScanAsync(Path.Combine(_root, "nope"), new SyncGlob([]), new Dictionary<string, SyncFileRecord>(), new ByteRateLimiter(() => 0), CancellationToken.None)).RootMissing);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}

public class SyncTransportMapTests
{
    private static ApiResponse Json(HttpStatusCode code, string body) => new(code, body, null, null);

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "{\"success\":false,\"error\":{\"code\":\"AKYS_QUOTA\"}}", SyncFailureKind.Quota)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "{\"success\":false,\"error\":{\"code\":\"AKYS_TOO_LARGE\"}}", SyncFailureKind.FileRejected)]
    [InlineData(HttpStatusCode.Gone, "{\"success\":false,\"error\":{\"code\":\"AKYS_SYNC_REVOKED\"}}", SyncFailureKind.Revoked)]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"SYNC_ROOT_NOT_FOUND\"}}", SyncFailureKind.UnknownRoot)]
    [InlineData(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"AKYS_CONFLICT\",\"message\":\"This sync is paused\"}}", SyncFailureKind.Paused)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"error\":{\"code\":\"AKYS_CHECKSUM_MISMATCH\"}}", SyncFailureKind.FileRejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", SyncFailureKind.Transient)]
    public void Map_UsesStatusFirst(HttpStatusCode code, string body, SyncFailureKind expected) =>
        Assert.Equal(expected, SyncTransport.Map<SyncManifestResponse>(Json(code, body)).Failure);

    [Fact]
    public void Map_NetworkFailure_IsWaitingNetwork() =>
        Assert.Equal(SyncFailureKind.Network, SyncTransport.Map<SyncManifestResponse>(new ApiResponse(null, null, null, "Network error: refused")).Failure);

    [Fact]
    public void Map_Success_ReadsSnakeCase()
    {
        var res = SyncTransport.Map<SyncManifestResponse>(Json(HttpStatusCode.OK, "{\"root_id\":\"syr_1\",\"generation\":2,\"upload\":[{\"path\":\"a.txt\",\"reason\":\"new\",\"size\":3,\"sha256\":\"x\"}],\"unchanged\":5,\"tombstoned\":1}"));
        Assert.Equal(SyncFailureKind.None, res.Failure);
        Assert.Equal("a.txt", Assert.Single(res.Value!.Upload).Path);
        Assert.Equal(5, res.Value.Unchanged);
    }

    [Fact]
    public void CommandTypes_AreAllowListed()
    {
        Assert.True(SyncCommandTypes.IsSync("sync.policy.apply"));
        Assert.False(SyncCommandTypes.IsSync("sync.exec"));
        Assert.False(SyncCommandTypes.IsSync("powershell-exec"));
    }
}

public class SyncBandwidthTests
{
    private static readonly List<SyncBandwidthWindow> Windows =
    [
        new() { Days = ["Mon", "Tue", "Wed", "Thu", "Fri"], Start = "09:00", End = "18:00", MaxUploadMbps = 5 },
        new() { Days = [], Start = "22:00", End = "06:00", MaxUploadMbps = 100 }
    ];

    [Fact]
    public void OfficeHours_UseTheLowLimit() =>
        Assert.Equal(5, SyncBandwidth.ActiveWindow(Windows, new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero))!.MaxUploadMbps); // Monday

    [Fact]
    public void OverMidnightWindow_Matches() =>
        Assert.Equal(100, SyncBandwidth.ActiveWindow(Windows, new DateTimeOffset(2026, 11, 21, 2, 30, 0, TimeSpan.Zero))!.MaxUploadMbps);

    [Fact]
    public void OutsideWindows_IsNull() =>
        Assert.Null(SyncBandwidth.ActiveWindow(Windows, new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero))); // Saturday noon

    [Fact]
    public void StableFileId_IsReturnedOnWindows()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var fs = File.OpenRead(path);
            var id = StableFileId.TryGet(fs.SafeFileHandle);
            if (OperatingSystem.IsWindows())
            {
                Assert.Matches("^[0-9a-f]{8}:[0-9a-f]{16}$", id);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SyncDatabaseResilienceTests
{
    [Fact]
    public void CorruptSyncDb_IsMovedAside_AndAgentKeepsStarting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "syncdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sync.db");
        File.WriteAllText(path, "this is not a sqlite database - garbage garbage garbage garbage garbage garbage");
        try
        {
            using (var db = new SyncDatabase(path, NullLogger<SyncDatabase>.Instance))
            {
                Assert.False(db.InMemory);
                Assert.Equal(0L, db.Run(c =>
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT count(*) FROM sync_policy";
                    return (long)cmd.ExecuteScalar()!;
                }));
            }

            Assert.Contains(Directory.GetFiles(dir), f => f.Contains(".broken-", StringComparison.Ordinal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class SyncPolicyContractTests
{
    [Fact]
    public void CloudOrcPolicyJson_Deserializes()
    {
        // exactly what CloudOrc buildPolicy() sends inside parametersJson
        const string json = """
            {"policy":{"connectionId":"syc_1","state":"ACTIVE","direction":"UPLOAD_ONLY",
              "roots":[{"rootId":"syr_1","path":"D:\\TallyData","excludes":["*.tmp"]}],
              "allowedRoots":[],"rescanMinutes":60,"resourceProfile":"eco","maxUploadMbps":20,
              "bandwidthWindows":[{"days":["Mon","Tue"],"start":"09:00","end":"18:00","maxUploadMbps":5}],
              "policyVersion":"abc","effectiveHash":"abcdef"}}
            """;
        var p = System.Text.Json.JsonSerializer.Deserialize<SyncPolicyApplyParameters>(json, CloudOrc.Agent.Contracts.Protocol.ProtocolJson.Options)!.Policy!;
        Assert.Equal("syc_1", p.ConnectionId);
        Assert.Equal(@"D:\TallyData", p.Roots[0].Path);
        Assert.Equal(20, p.MaxUploadMbps);
        Assert.Equal(5, p.BandwidthWindows[0].MaxUploadMbps);
        Assert.Equal(["Mon", "Tue"], p.BandwidthWindows[0].Days);
    }
}
