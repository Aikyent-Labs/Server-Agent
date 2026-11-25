using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudOrc.ControlAgent.Transport;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Workspace;

/// <summary>One interactive Windows session, in the same shape the backend's quser snapshot uses.</summary>
public sealed record SessionRow(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("sessionName")] string SessionName,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("logonAt")] string? LogonAt);

/// <summary>
/// Pushes the server's interactive sessions to CloudOrc the moment they change (sign-in, reconnect,
/// disconnect, sign-out) plus a heartbeat (handoff §29: the second gate must apply before the session
/// is used). The backend's sign-in 2FA watcher uses this feed instead of polling the server with remote
/// PowerShell every few seconds — faster gate, far less load — and falls back to the old poll when the
/// feed is stale. Observation only: no session is touched here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionFeedReporter(BackendApiClient api, ILogger<SessionFeedReporter> logger) : BackgroundService
{
    public const string Route = "api/v1/remote-workspace/agent/sessions";
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastSignature = null;
        var lastSent = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (api.IsConfigured)
                {
                    var rows = Enumerate();
                    var signature = Signature(rows);
                    var changed = signature != lastSignature;
                    if (changed || DateTimeOffset.UtcNow - lastSent >= Heartbeat)
                    {
                        var body = JsonSerializer.Serialize(new { sessions = rows, changed }, BackendApiClient.SnakeCaseJson);
                        var r = await api.PostJsonStringAsync(Route, body, stoppingToken).ConfigureAwait(false);
                        if (r.IsSuccess)
                        {
                            lastSignature = signature;
                            lastSent = DateTimeOffset.UtcNow;
                        }
                        else
                        {
                            logger.LogDebug("Session feed not accepted: {Outcome}.", r.Describe());
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Session feed cycle failed.");
            }

            try { await Task.Delay(Poll, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Stable signature of what matters for the gate (state, connection name, logon).</summary>
    internal static string Signature(IEnumerable<SessionRow> rows) =>
        string.Join("|", rows.OrderBy(r => r.SessionId, StringComparer.Ordinal).Select(r => $"{r.SessionId}:{r.Username}:{r.State}:{r.SessionName}:{r.LogonAt}"));

    /// <summary>
    /// quser-compatible: Active sessions carry their WinStation connection name, disconnected ones an
    /// empty name; logonAt is UTC ISO-8601 truncated to the minute (quser's precision) so a row reads the
    /// same whether it came from this feed or from the fallback poll.
    /// </summary>
    internal static string? FormatLogon(long fileTime)
    {
        if (fileTime <= 0) return null;
        var utc = DateTime.FromFileTimeUtc(fileTime);
        var minute = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        return minute.ToString("o");
    }

    internal static List<SessionRow> Enumerate()
    {
        var rows = new List<SessionRow>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) return rows;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                if (info.SessionId == 0) continue;
                var state = info.State switch { 0 => "Active", 4 => "Disc", _ => null };
                if (state is null) continue;
                var user = QueryString(info.SessionId, 5 /* WTSUserName */);
                if (string.IsNullOrWhiteSpace(user)) continue;
                var station = state == "Active" ? QueryString(info.SessionId, 6 /* WTSWinStationName */) ?? string.Empty : string.Empty;
                // Username exactly as Windows reports it (same as quser): the 2FA challenge lookups compare it case-sensitively.
                rows.Add(new SessionRow(user, station, info.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), state, FormatLogon(QueryLogonTime(info.SessionId))));
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return rows;
    }

    private static string? QueryString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var b, out _)) return null;
        try { return Marshal.PtrToStringUni(b); }
        finally { WTSFreeMemory(b); }
    }

    private static long QueryLogonTime(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 24 /* WTSSessionInfo */, out var b, out _)) return 0;
        try { return Marshal.PtrToStructure<WTSINFOW>(b).LogonTime; }
        catch { return 0; }
        finally { WTSFreeMemory(b); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFOW
    {
        public int State;
        public int SessionId;
        public int IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
