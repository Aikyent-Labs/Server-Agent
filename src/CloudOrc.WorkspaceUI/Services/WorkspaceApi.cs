using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.WorkspaceUI.Services;

public sealed class WorkspaceApiException(HttpStatusCode? status, string code, string message) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Backend client for /api/v1/remote-workspace/*. The bearer token comes from the agent pipe and
/// is refreshed before expiry (and once on a 401). The token is kept in memory only.
/// </summary>
public sealed class WorkspaceApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IAgentPipe _pipe;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public WorkspaceApi(IAgentPipe pipe, HttpMessageHandler? handler = null)
    {
        _pipe = pipe;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CloudOrcWorkspaceUI", AppInfo.Version));
    }

    public Uri? BaseAddress { get; private set; }

    /// <summary>True once the backend accepted us for this user (seat found).</summary>
    public bool? SeatFound { get; private set; }

    public string? LastReasonCode { get; private set; }

    public async Task<bool> EnsureTokenAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (!force && _token is not null && _expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
        {
            return true;
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _token is not null && _expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
            {
                return true;
            }

            var response = await _pipe.SendAsync(new WorkspacePipeRequest { Action = WorkspaceProtocol.ActionToken, ForceRefresh = force, UiVersion = AppInfo.Version }, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                LastReasonCode = WorkspaceReasonCodes.AgentOffline;
                return false;
            }

            if (!response.Ok || string.IsNullOrWhiteSpace(response.Token) || string.IsNullOrWhiteSpace(response.ApiBaseUrl))
            {
                LastReasonCode = response.ReasonCode ?? WorkspaceReasonCodes.BackendUnavailable;
                Log.Write($"Token not available: {response.ReasonCode} {response.Error}");
                return false;
            }

            _token = response.Token;
            _expiresAt = response.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10);
            BaseAddress = new Uri(response.ApiBaseUrl.TrimEnd('/') + "/");
            SeatFound = response.SeatFound;
            LastReasonCode = null;
            return true;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public Task<WorkspaceContext> GetContextAsync(CancellationToken ct) => GetAsync<WorkspaceContext>("context", ct);
    public Task<AppsResponse> GetAppsAsync(CancellationToken ct) => GetAsync<AppsResponse>("apps", ct);
    public Task<Capabilities> GetCapabilitiesAsync(CancellationToken ct) => GetAsync<Capabilities>("capabilities", ct);
    public Task<BackupStatus> GetBackupAsync(CancellationToken ct) => GetAsync<BackupStatus>("backup", ct);
    public Task<AlertsResponse> GetAlertsAsync(CancellationToken ct) => GetAsync<AlertsResponse>("notifications?limit=30", ct);

    public Task RunBackupAsync(CancellationToken ct) => SendAsync<JsonElement>(HttpMethod.Post, "backup/run", new { }, ct);
    public Task MarkAlertsReadAsync(IEnumerable<string> ids, CancellationToken ct) => SendAsync<JsonElement>(HttpMethod.Post, "notifications/read", new { ids = ids.ToArray() }, ct);
    public Task AppLaunchedAsync(string appId, CancellationToken ct) => SendAsync<JsonElement>(HttpMethod.Post, $"apps/{Uri.EscapeDataString(appId)}/launched", new { }, ct);
    public Task<SupportCategories> GetSupportCategoriesAsync(CancellationToken ct) => GetAsync<SupportCategories>("support/categories", ct);
    public Task<TicketResult> CreateTicketAsync(object body, CancellationToken ct) => SendAsync<TicketResult>(HttpMethod.Post, "support/ticket", body, ct);
    public Task<AiAnswer> AskAiAsync(string message, string sessionId, CancellationToken ct) => SendAsync<AiAnswer>(HttpMethod.Post, "ai/ask", new { message, sessionId }, ct);
    public Task<Preferences> SavePreferencesAsync(object patch, CancellationToken ct) => SendAsync<Preferences>(HttpMethod.Patch, "preferences", patch, ct);

    /// <summary>Downloads a brand logo (absolute URL or path relative to the API host). Null on failure.</summary>
    public async Task<byte[]?> TryDownloadAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || BaseAddress is null)
        {
            return null;
        }

        try
        {
            var uri = Uri.TryCreate(url, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp)
                ? abs
                : new Uri(new Uri(BaseAddress.GetLeftPart(UriPartial.Authority)), url);
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > 2_000_000)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await EnsureTokenAsync(ct, force: attempt > 0).ConfigureAwait(false))
            {
                throw new WorkspaceApiException(null, LastReasonCode ?? WorkspaceReasonCodes.AgentOffline, "CloudOrc is not reachable right now.");
            }

            using var request = new HttpRequestMessage(method, new Uri(BaseAddress!, "api/v1/remote-workspace/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new WorkspaceApiException(null, WorkspaceReasonCodes.BackendUnavailable, $"CloudOrc is not reachable ({ex.Message}).");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new WorkspaceApiException(null, WorkspaceReasonCodes.BackendUnavailable, "CloudOrc did not answer in time.");
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    continue; // token expired/rotated: refresh once
                }

                if (!response.IsSuccessStatusCode)
                {
                    var (code, message) = ParseError(text);
                    throw new WorkspaceApiException(response.StatusCode, code ?? $"HTTP_{(int)response.StatusCode}", message ?? "The request could not be completed.");
                }

                if (typeof(T) == typeof(JsonElement))
                {
                    return (T)(object)(string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<JsonElement>(text));
                }

                return JsonSerializer.Deserialize<T>(text, Json)
                    ?? throw new WorkspaceApiException(response.StatusCode, "EMPTY_RESPONSE", "Empty response.");
            }
        }

        throw new WorkspaceApiException(HttpStatusCode.Unauthorized, "UNAUTHORIZED", "Not authorized.");
    }

    internal static (string? Code, string? Message) ParseError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                return (err.TryGetProperty("code", out var c) ? c.GetString() : null, err.TryGetProperty("message", out var m) ? m.GetString() : null);
            }

            return (root.TryGetProperty("code", out var c2) ? c2.GetString() : null, root.TryGetProperty("message", out var m2) ? m2.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public void Dispose() => _http.Dispose();
}

public static class AppInfo
{
    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v && v.Major > 0
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                System.Reflection.Assembly.GetExecutingAssembly())?.InformationalVersion ?? "0.0.0-dev");
}
