using System.Diagnostics;
using System.Windows.Threading;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>Pure lifecycle decisions for one assistance request (unit-tested).</summary>
public static class AssistancePolicy
{
    public enum Step { None, AskConsent, ShowBanner, MarkActive, EndLeaseExpired, EndShadowGone }

    /// <param name="shadowRunning">The Windows shadow agent (RdpSa.exe) runs in this session right now.</param>
    /// <param name="shadowSeenSince">When the shadow was first seen for this request (null = never).</param>
    public static Step Next(AssistanceRequest? r, bool shadowRunning, DateTimeOffset? shadowSeenSince, DateTimeOffset? shadowGoneSince, DateTimeOffset now)
    {
        if (r is null) return Step.None;
        if (r.IsPending) return Step.AskConsent;
        if (!r.IsGranted) return Step.None;
        if (r.LeaseExpiresAt is { } end && now >= end) return Step.EndLeaseExpired;
        if (shadowRunning && r.Status == "GRANTED") return Step.MarkActive;
        // The engineer closed the shadow: end once it has been gone for a few seconds (reconnect blips).
        if (!shadowRunning && shadowSeenSince is not null && shadowGoneSince is { } gone && now - gone >= TimeSpan.FromSeconds(8)) return Step.EndShadowGone;
        return Step.ShowBanner;
    }
}

/// <summary>
/// Consent-based Remote Assistance in the panel (RW-04, RW-05). Polls CloudOrc for a request for
/// THIS session, asks the user, shows the always-visible banner while access is granted, and ends the
/// Windows shadow (RdpSa.exe in this session) on Stop or when the consent time runs out.
/// The panel never starts or widens access by itself.
/// </summary>
public sealed class AssistanceManager : IDisposable
{
    private readonly WorkspaceApi _api;
    private readonly DispatcherTimer _timer;
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private string? _askedFor;
    private string? _trackedId;
    private DateTimeOffset? _shadowSeen;
    private DateTimeOffset? _shadowGone;
    private bool _busy;

    public AssistanceManager(WorkspaceApi api, Dispatcher ui)
    {
        _api = api;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>A new request needs the user's decision.</summary>
    public event Action<AssistanceRequest>? ConsentRequested;

    /// <summary>Access is granted/active (show the banner) or null (hide it).</summary>
    public event Action<AssistanceRequest?>? BannerChanged;

    public event Action<string>? Message;

    public AssistanceRequest? Current { get; private set; }

    public bool IsAccessGranted => Current?.IsGranted == true;

    public void Start()
    {
        _timer.Start();
        _ = TickAsync();
    }

    public async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            AssistanceRequest? r;
            try
            {
                r = (await _api.GetAssistanceAsync(CancellationToken.None)).Request;
            }
            catch (WorkspaceApiException)
            {
                return; // offline: keep the current banner state; Stop still works locally
            }

            if (r?.Id != _trackedId)
            {
                _trackedId = r?.Id;
                _shadowSeen = null;
                _shadowGone = null;
            }

            var now = DateTimeOffset.UtcNow;
            var running = ShadowRunning();
            if (running) { _shadowSeen ??= now; _shadowGone = null; }
            else if (_shadowSeen is not null) _shadowGone ??= now;

            Current = r;
            switch (AssistancePolicy.Next(r, running, _shadowSeen, _shadowGone, now))
            {
                case AssistancePolicy.Step.AskConsent:
                    if (_askedFor != r!.Id)
                    {
                        _askedFor = r.Id;
                        ConsentRequested?.Invoke(r);
                    }

                    BannerChanged?.Invoke(null);
                    break;
                case AssistancePolicy.Step.MarkActive:
                    Current = (await _api.ReportAssistanceAsync(r!.Id, "ACTIVE", null, CancellationToken.None)).Request ?? r;
                    Log.Write($"Remote assistance {r.Id} started (Windows shadow seen).");
                    BannerChanged?.Invoke(Current);
                    break;
                case AssistancePolicy.Step.EndLeaseExpired:
                    await EndAsync("consent time ended");
                    Message?.Invoke("Remote assistance ended: the time you allowed is over.");
                    break;
                case AssistancePolicy.Step.EndShadowGone:
                    await EndAsync("engineer disconnected", killShadow: false);
                    Message?.Invoke("The support engineer has disconnected.");
                    break;
                case AssistancePolicy.Step.ShowBanner:
                    BannerChanged?.Invoke(r);
                    break;
                default:
                    BannerChanged?.Invoke(null);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("assistance tick", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task<bool> DecideAsync(AssistanceRequest r, bool allow, IEnumerable<string> capabilities, int durationMinutes)
    {
        try
        {
            var result = (await _api.DecideAssistanceAsync(r.Id, allow, capabilities, durationMinutes, CancellationToken.None)).Request;
            Current = result;
            Log.Write($"Remote assistance {r.Id}: user {(allow ? "allowed " + string.Join(",", result?.GrantedCapabilities ?? []) : "declined")}.");
            BannerChanged?.Invoke(allow ? result : null);
            return true;
        }
        catch (WorkspaceApiException ex)
        {
            Message?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>User pressed Stop: end the Windows shadow at once, then tell CloudOrc (never waits for the engineer).</summary>
    public Task StopAsync() => EndAsync("stopped by user");

    private async Task EndAsync(string reason, bool killShadow = true)
    {
        var r = Current;
        if (killShadow) KillShadow();
        BannerChanged?.Invoke(null);
        Current = null;
        if (r is null) return;
        try
        {
            await _api.ReportAssistanceAsync(r.Id, "ENDED", reason, CancellationToken.None);
        }
        catch (WorkspaceApiException ex)
        {
            Log.Write($"assistance end report failed: {ex.Code}");
        }

        Log.Write($"Remote assistance {r.Id} ended ({reason}).");
    }

    private bool ShadowRunning()
    {
        foreach (var p in Process.GetProcessesByName("RdpSa"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId == _session) return true;
                }
                catch (InvalidOperationException)
                {
                    // exited
                }
            }
        }

        return false;
    }

    private void KillShadow()
    {
        foreach (var p in Process.GetProcessesByName("RdpSa"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != _session) continue;
                    p.Kill();
                    Log.Write("Windows shadow (RdpSa) ended in this session.");
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not end the Windows shadow: {ex.Message}");
                }
            }
        }
    }

    public void Dispose() => _timer.Stop();
}
