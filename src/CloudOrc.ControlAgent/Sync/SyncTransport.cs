using System.Net;
using System.Net.Http.Headers;
using CloudOrc.Agent.Contracts.Sync;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Sync;

/// <summary>What the coordinator should do after a failed call.</summary>
public enum SyncFailureKind
{
    None,
    Network,       // WAITING_NETWORK
    Transient,     // RETRY_BACKOFF
    Quota,         // QUOTA_BLOCKED
    Revoked,       // REVOKED
    UnknownRoot,   // INVALID_ROOT (the backend no longer knows this root)
    Paused,        // PAUSED_POLICY (backend says paused)
    FileRejected,  // this file only (too large, blocked type, checksum mismatch)
    GrantExpired   // fetch fresh part grants
}

public sealed record SyncCall<T>(T? Value, SyncFailureKind Failure, string Detail, TimeSpan? RetryAfter);

/// <summary>
/// Control-plane calls (CloudOrc relays them to AikySpace) + direct part uploads to object storage
/// through short-lived signed URLs (§08 "Chunked/resumable uploads ... checkpoint after confirmed
/// cloud commit"). The part URLs carry their own authorization: the agent's CloudOrc credential is
/// never sent to storage.
/// </summary>
public sealed class SyncTransport : IDisposable
{
    private readonly BackendApiClient _api;
    private readonly HttpClient _storage;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncTransport> _logger;

    public SyncTransport(BackendApiClient api, IOptions<SyncOptions> options, ILogger<SyncTransport> logger)
    {
        _api = api;
        _options = options.Value;
        _logger = logger;
        _storage = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(30, _options.PartTimeoutSeconds))
        };
        _storage.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CloudOrcAgent", Agent.Contracts.Versioning.AgentVersionInfo.GetVersion()));
    }

    public bool IsConfigured => _api.IsConfigured;

    public async Task<SyncCall<SyncManifestResponse>> ManifestAsync(SyncManifestRequest request, CancellationToken ct) =>
        Map<SyncManifestResponse>(await _api.PostJsonAsync(SyncRoutes.Manifests, request, ct).ConfigureAwait(false));

    public async Task<SyncCall<SyncUploadResponse>> StartUploadAsync(SyncUploadRequest request, CancellationToken ct) =>
        Map<SyncUploadResponse>(await _api.PostJsonAsync(SyncRoutes.Uploads, request, ct).ConfigureAwait(false));

    public async Task<SyncCall<SyncUploadResponse>> RefreshUploadAsync(string sessionId, string rootId, CancellationToken ct) =>
        Map<SyncUploadResponse>(await _api.GetAsync(SyncRoutes.Upload(sessionId, rootId), ct).ConfigureAwait(false));

    public async Task<SyncCall<SyncCompleteResponse>> CompleteAsync(string sessionId, SyncCompleteRequest request, CancellationToken ct) =>
        Map<SyncCompleteResponse>(await _api.PostJsonAsync(SyncRoutes.Complete(sessionId), request, ct).ConfigureAwait(false));

    public async Task<SyncCall<object>> CheckpointAsync(SyncCheckpointRequest request, CancellationToken ct) =>
        Map<object>(await _api.PostJsonAsync(SyncRoutes.Checkpoints, request, ct).ConfigureAwait(false));

    /// <summary>Uploads one part (exact byte range of the file) to its signed URL, rate limited.</summary>
    public async Task<SyncFailureKind> PutPartAsync(string url, string filePath, long offset, long size, ByteRateLimiter limiter, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(_options.DevelopmentAllowInsecureStorage && uri.Scheme == Uri.UriSchemeHttp)))
        {
            _logger.LogWarning("Refusing a non-https part URL.");
            return SyncFailureKind.FileRejected;
        }

        try
        {
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
            file.Seek(offset, SeekOrigin.Begin);
            using var content = new StreamContent(new ThrottledSliceStream(file, size, limiter, ct), 1 << 16);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = size;
            using var response = await _storage.PutAsync(uri, content, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return SyncFailureKind.None;
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Forbidden => SyncFailureKind.GrantExpired, // expired / invalid grant → refresh
                HttpStatusCode.Gone => SyncFailureKind.GrantExpired,
                HttpStatusCode.Conflict => SyncFailureKind.Transient,
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => SyncFailureKind.FileRejected,
                HttpStatusCode.TooManyRequests => SyncFailureKind.Transient,
                _ when (int)response.StatusCode >= 500 => SyncFailureKind.Transient,
                _ => SyncFailureKind.Transient
            };
        }
        catch (IOException ex) when (SyncScanner.IsSharingViolation(ex))
        {
            return SyncFailureKind.FileRejected; // became locked; the file is retried on the next pass
        }
        catch (HttpRequestException)
        {
            return SyncFailureKind.Network;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return SyncFailureKind.Network;
        }
    }

    public static SyncCall<T> Map<T>(ApiResponse response) where T : class
    {
        if (response.IsSuccess)
        {
            try
            {
                var value = response.Read<T>(BackendApiClient.SnakeCaseJson);
                return new SyncCall<T>(value, SyncFailureKind.None, "OK", null);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new SyncCall<T>(null, SyncFailureKind.Transient, $"Unreadable response: {ex.Message}", null);
            }
        }

        if (response.IsNetworkFailure)
        {
            return new SyncCall<T>(null, SyncFailureKind.Network, response.Describe(), response.RetryAfter);
        }

        SyncProblem? problem = null;
        try
        {
            problem = response.Read<SyncProblem>(BackendApiClient.SnakeCaseJson);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var code = problem?.EffectiveCode ?? "";
        var detail = $"{response.Describe()} {code} {problem?.EffectiveMessage}".Trim();
        if (response.IsTransient)
        {
            return new SyncCall<T>(null, SyncFailureKind.Transient, detail, response.RetryAfter);
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.RequestEntityTooLarge when code.Contains("TOO_LARGE", StringComparison.OrdinalIgnoreCase) => SyncFailureKind.FileRejected,
            HttpStatusCode.RequestEntityTooLarge => SyncFailureKind.Quota,
            HttpStatusCode.Gone => SyncFailureKind.Revoked,
            HttpStatusCode.Forbidden when code.Contains("REVOK", StringComparison.OrdinalIgnoreCase) => SyncFailureKind.Revoked,
            HttpStatusCode.NotFound => SyncFailureKind.UnknownRoot,
            HttpStatusCode.Conflict when code.Contains("PAUSED", StringComparison.OrdinalIgnoreCase) || detail.Contains("paused", StringComparison.OrdinalIgnoreCase) => SyncFailureKind.Paused,
            HttpStatusCode.Conflict => SyncFailureKind.Transient,
            HttpStatusCode.PreconditionFailed => SyncFailureKind.Transient, // document head moved: re-run the manifest
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType => SyncFailureKind.FileRejected,
            HttpStatusCode.Forbidden => SyncFailureKind.Revoked,
            _ => SyncFailureKind.Transient
        };
        return new SyncCall<T>(null, kind, detail, response.RetryAfter);
    }

    public void Dispose() => _storage.Dispose();
}

/// <summary>Read-only view of the next <c>length</c> bytes of a stream, paced by a rate limiter.</summary>
internal sealed class ThrottledSliceStream(Stream inner, long length, ByteRateLimiter limiter, CancellationToken ct) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        if (n == 0)
        {
            throw new IOException("The file became shorter while it was uploaded.");
        }

        _remaining -= n;
        await limiter.WaitAsync(n, ct).ConfigureAwait(false);
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
