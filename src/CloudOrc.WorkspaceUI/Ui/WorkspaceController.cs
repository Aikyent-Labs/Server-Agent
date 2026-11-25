using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>Row in "Running now": one app with its windows (RW-02).</summary>
public sealed class RunningAppVm : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? PublishedAppId { get; init; }
    public ImageSource? Icon { get; init; }
    public required ObservableCollection<WindowVm> Windows { get; init; }
    public int Count => Windows.Count;
    public bool HasMany => Windows.Count > 1;
    public string Badge => Windows.Count > 1 ? Windows.Count.ToString() : string.Empty;
    public IntPtr FirstHandle => Windows.Count > 0 ? Windows[0].Handle : IntPtr.Zero;
    public string StateSummary => Windows.Count > 1 ? $"{Windows.Count} windows" : Windows.FirstOrDefault()?.State ?? string.Empty;

    private bool _expanded;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    /// <summary>Screen-reader name of the list item.</summary>
    public override string ToString() => $"{Name}, {StateSummary}";
}

public sealed class WindowVm
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public ImageSource? Icon { get; init; }
    public string AppName { get; init; } = string.Empty;

    public override string ToString() => $"{AppName}: {Title}, {State}";
}

public sealed class PublishedAppVm
{
    public required PublishedApp App { get; init; }
    public ImageSource? Icon { get; init; }
    public bool Running { get; init; }
    public string Name => App.Name;
    public string StateText => Running ? "Running" : "Open";

    public override string ToString() => $"{Name}, {StateText}";
}

public sealed class ChatLine
{
    public required string Text { get; init; }
    public required bool FromUser { get; init; }

    public override string ToString() => (FromUser ? "You: " : "Assistant: ") + Text;
}

/// <summary>
/// All panel state and behaviour. Data is fetched in parallel; a failing source degrades only its
/// own tab (§36 "Fetch independent modules in parallel"). Windows apps keep working whatever
/// happens here.
/// </summary>
public sealed class WorkspaceController : ObservableObject, IDisposable
{
    private readonly IAgentPipe _pipe;
    private readonly WorkspaceApi _api;
    private readonly WindowTracker _windows;
    private readonly SessionStateFile _stateFile;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _heartbeat;
    private readonly DispatcherTimer _slowPoll;
    private readonly DispatcherTimer _windowPoll;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _aiSession = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _seenCriticalAlerts = new(StringComparer.Ordinal);
    private List<PublishedApp> _apps = [];
    private bool _loading;

    public WorkspaceController(IAgentPipe pipe, WorkspaceApi api, WindowTracker windows, SessionStateFile stateFile, Dispatcher ui)
    {
        _pipe = pipe;
        _api = api;
        _windows = windows;
        _stateFile = stateFile;
        _ui = ui;
        _windows.WindowsChanged += () => _ui.BeginInvoke(RefreshWindows);

        _heartbeat = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(15) };
        _heartbeat.Tick += async (_, _) => await HeartbeatAsync();
        _slowPoll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(60) };
        _slowPoll.Tick += async (_, _) => await PollAsync();
        _windowPoll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(2) };
        _windowPoll.Tick += (_, _) =>
        {
            RefreshWindows();
            UpdateSeamless();
            TsplusPanelHider.Instance.Apply(); // TSPlus may (re)start its panel after us
        };
    }

    /// <summary>Backend client for the Phase 2 surfaces (security, users, files, assistance).</summary>
    public WorkspaceApi Api => _api;

    // ------------------------------------------------------------------ bindable state

    public ObservableCollection<PublishedAppVm> PublishedApps { get; } = [];
    public ObservableCollection<RunningAppVm> RunningApps { get; } = [];
    public ObservableCollection<AlertItem> Alerts { get; } = [];
    public ObservableCollection<BackupPoint> BackupHistory { get; } = [];
    public ObservableCollection<ChatLine> Chat { get; } = [];

    private WorkspaceContext? _context;
    public WorkspaceContext? Context
    {
        get => _context;
        private set
        {
            if (Set(ref _context, value))
            {
                Raise(nameof(BrandName)); Raise(nameof(UserName)); Raise(nameof(IsAdmin)); Raise(nameof(SupportOrganization));
                Raise(nameof(SupportContact)); Raise(nameof(LaunchModeText)); Raise(nameof(ServerName)); Raise(nameof(RailItems));
            }
        }
    }

    public string BrandName => Context?.Brand.Name ?? "CloudOrc";
    public string UserName => Context?.User.DisplayName ?? Environment.UserName;
    public bool IsAdmin => Context?.User.IsCustomerAdmin == true;
    public string SupportOrganization => Context?.Brand.SupportOrganization ?? "CloudOrc Support";
    public string LaunchModeText => (Context?.LaunchMode ?? WorkspaceLaunchModes.MinimalPanel).Replace('_', ' ');
    public IReadOnlyList<string> RailItems => Context?.RailItems is { Count: > 0 } r ? r : ["apps", "backup", "files", "print", "ai", "support", "alerts", "more"];

    public string SupportContact
    {
        get
        {
            var b = Context?.Brand;
            if (b is null) return string.Empty;
            return string.Join("   ", new[] { b.SupportPhone, b.SupportEmail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    private ImageSource? _logo;
    public ImageSource? Logo { get => _logo; private set => Set(ref _logo, value); }

    private string _connection = "Connecting…";
    public string ConnectionText { get => _connection; private set => Set(ref _connection, value); }

    /// <summary>connected | degraded | offline | connecting</summary>
    private string _connectionState = "connecting";
    public string ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }

    private string? _banner;
    public string? Banner { get => _banner; private set { if (Set(ref _banner, value)) Raise(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrWhiteSpace(Banner);

    private string _agentVersion = "-";
    public string AgentVersion { get => _agentVersion; private set => Set(ref _agentVersion, value); }

    private DateTimeOffset? _lastContact;
    public string LastContactText => TimeText.Ago(_lastContact);

    public int SessionId => SessionActions.CurrentSessionId;
    public string PanelVersion => AppInfo.Version;
    public string ServerName => Context?.Server?.Name ?? Environment.MachineName;

    private Capabilities? _caps;
    public Capabilities? Caps { get => _caps; private set => Set(ref _caps, value); }

    private BackupStatus? _backup;
    public BackupStatus? Backup
    {
        get => _backup;
        private set { if (Set(ref _backup, value)) { Raise(nameof(BackupHeadline)); Raise(nameof(BackupDetail)); Raise(nameof(BackupTone)); Raise(nameof(CanRunBackup)); } }
    }

    public bool CanRunBackup => Backup?.CanRun == true && !Backup.Running;

    public string BackupHeadline => Backup?.State switch
    {
        "PROTECTED" => "Protected",
        "STALE" => "Backup is old",
        "UNVERIFIED" => "Not verified yet",
        "NO_BACKUP_YET" => "No backup yet",
        "ATTENTION" => "Needs attention",
        "PAUSED" => "Backups paused",
        "NOT_CONFIGURED" => "Backup not set up",
        null => "Checking…",
        var s => s
    };

    public string BackupDetail
    {
        get
        {
            if (Backup is null) return string.Empty;
            if (Backup.Running) return "A backup is running now. You can keep working.";
            if (Backup.State == "NOT_CONFIGURED") return "Backup is not configured for this workspace. Contact your administrator.";
            return Backup.LastVerifiedAt is { } at
                ? $"Last verified backup: {TimeText.Ago(at)} ({at.ToLocalTime():dd MMM, HH:mm})"
                : "No verified backup point yet.";
        }
    }

    /// <summary>ok | warn | bad | neutral</summary>
    public string BackupTone => Backup?.State switch
    {
        "PROTECTED" => "ok",
        "STALE" or "UNVERIFIED" or "NO_BACKUP_YET" or "PAUSED" => "warn",
        "ATTENTION" => "bad",
        _ => "neutral"
    };

    private int _unread;
    public int UnreadAlerts { get => _unread; private set { if (Set(ref _unread, value)) Raise(nameof(HasUnread)); } }
    public bool HasUnread => UnreadAlerts > 0;

    private bool _busy;
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public bool HasPublishedApps => PublishedApps.Count > 0;

    private bool _seamless = SessionActions.IsSeamlessSession();
    /// <summary>
    /// RemoteApp / seamless session (TSPlus .connect "Remote App", .rdp RemoteApp, HTML5 RemoteApp): the
    /// user has no remote desktop, taskbar or Start menu, so this panel is their launcher — published apps
    /// and folders are shown with Open, the TSPlus floating panel is replaced, and the panel cannot be
    /// closed for the session. In a full desktop session the Apps tab shows only what is running.
    /// </summary>
    public bool IsSeamless { get => _seamless; private set { if (Set(ref _seamless, value)) { Raise(nameof(ShowLauncher)); Raise(nameof(AppsHeader)); Raise(nameof(AppsTip)); Raise(nameof(CanHideForSession)); UpdateTsplusHiding(); } } }
    public bool ShowLauncher => IsSeamless;
    public string AppsHeader => IsSeamless ? "YOUR APPS" : "RUNNING NOW";
    public string AppsTip => IsSeamless
        ? "Tip: these apps open as windows on your own PC — Alt+Tab shows them together with your PC's apps."
        : "Tip: Alt+Tab works as usual. In a windowed RDP client use Alt+Page Up / Alt+Page Down.";
    public bool CanHideForSession => !IsSeamless;

    private void UpdateSeamless() => IsSeamless = SessionActions.IsSeamlessSession();

    /// <summary>
    /// Hide the TSPlus floating panel when the admin asked for it, or automatically in a seamless session —
    /// but only once this panel actually has apps to offer, so a user is never left with no way to open one.
    /// </summary>
    private void UpdateTsplusHiding()
    {
        var ctx = Context;
        if (ctx is null) return;
        var hide = ctx.WorkspaceEnabled && (ctx.HideTsplusPanel || (IsSeamless && HasPublishedApps));
        TsplusPanelHider.Instance.SetEnabled(hide);
    }
    public bool HasRunningApps => RunningApps.Count > 0;
    public bool HasAlerts => Alerts.Count > 0;

    /// <summary>A new critical alert the panel should surface once (without stealing focus).</summary>
    public event Action<AlertItem>? CriticalAlert;

    /// <summary>The workspace is not enabled for this user/server: the panel should not show.</summary>
    public event Action<string>? WorkspaceUnavailable;

    /// <summary>Short non-modal message ("Ticket SUP-123 created").</summary>
    public event Action<string>? Toast;

    /// <summary>CloudOrc asked this panel to restart (workspace.ui.restart).</summary>
    public event Action? RestartRequested;

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync(bool startedByWatch, bool force)
    {
        // 1) Cached apps first: instant and works offline.
        var cached = LocalCache.Load();
        if (cached is not null)
        {
            if (cached.Context is not null) Context = cached.Context;
            SetApps(cached.Apps);
        }

        RefreshWindows();
        _windowPoll.Start();
        _heartbeat.Start();
        _slowPoll.Start();
        _stateFile.Write(WorkspaceSessionState.StateRunning);
        await HeartbeatAsync();

        // 2) Live data.
        var ok = await LoadAllAsync();
        if (ok && Context is { } ctx && !ctx.WorkspaceEnabled && !force)
        {
            WorkspaceUnavailable?.Invoke("disabled");
            return;
        }

        // DIRECT_APP: open the single published app at logon (never on a Watch restart: it is already open).
        if (ok && !startedByWatch && Context?.LaunchMode == WorkspaceLaunchModes.DirectApp)
        {
            var apps = _apps.Where(a => !a.IsFolder).ToList();
            if (apps.Count == 1 && !RunningApps.Any(r => r.PublishedAppId == apps[0].Id))
            {
                Launch(apps[0]);
            }
        }
    }

    public async Task<bool> LoadAllAsync()
    {
        if (_loading) return false;
        _loading = true;
        var ct = _cts.Token;
        try
        {
            try
            {
                var ctx = await _api.GetContextAsync(ct);
                Context = ctx;
                UpdateTsplusHiding();
                SetConnection("connected", "Connected");
                Banner = ctx.ReasonCode == "WORKSPACE_SEAT_NOT_FOUND"
                    ? "This Windows user is not linked to a CloudOrc workspace yet. Apps and support are limited."
                    : null;
                _ = LoadLogoAsync(ctx.Brand.LogoUrl);
            }
            catch (WorkspaceApiException ex)
            {
                HandleOffline(ex);
                return false;
            }

            await Task.WhenAll(LoadAppsAsync(), LoadCapsAsync(), LoadBackupAsync(), LoadAlertsAsync());
            LocalCache.Save(Context, _apps);
            return true;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task PollAsync()
    {
        if (ConnectionState != "connected")
        {
            await LoadAllAsync();
            return;
        }

        await Task.WhenAll(LoadAlertsAsync(), LoadBackupAsync());
    }

    private async Task HeartbeatAsync()
    {
        _stateFile.Write(WorkspaceSessionState.StateRunning);
        var hello = await _pipe.SendAsync(new WorkspacePipeRequest { Action = WorkspaceProtocol.ActionHeartbeat, UiVersion = AppInfo.Version, State = WorkspaceSessionState.StateRunning }, _cts.Token);
        if (hello is null)
        {
            if (ConnectionState == "connected") SetConnection("degraded", "Agent offline");
            AgentVersion = "-";
            return;
        }

        AgentVersion = hello.AgentVersion ?? "-";
        switch (hello.Command)
        {
            case WorkspacePanelCommands.Refresh:
                Log.Write("Policy refresh requested by CloudOrc.");
                _ = LoadAllAsync();
                break;
            case WorkspacePanelCommands.Restart:
                Log.Write("Restart requested by CloudOrc.");
                _stateFile.Write(WorkspaceSessionState.StateRestarting);
                RestartRequested?.Invoke();
                return;
        }

        _lastContact = hello.LastContactAt;
        Raise(nameof(LastContactText));
        if (ConnectionState is "offline" or "degraded") _ = LoadAllAsync();
    }

    private void HandleOffline(WorkspaceApiException ex)
    {
        Log.Write($"Backend unavailable: {ex.Code} {ex.Message}");
        var text = ex.Code switch
        {
            WorkspaceReasonCodes.AgentOffline => "CloudOrc agent is not running. Your apps keep working.",
            WorkspaceReasonCodes.AgentNotEnrolled => "This server is not connected to CloudOrc yet.",
            _ => "CloudOrc is not reachable right now. Your apps keep working."
        };
        SetConnection("offline", ex.Code == WorkspaceReasonCodes.AgentOffline ? "Agent offline" : "Offline");
        Banner = text;
    }

    private void SetConnection(string state, string text)
    {
        ConnectionState = state;
        ConnectionText = text;
    }

    private async Task LoadLogoAsync(string? url)
    {
        var bytes = await _api.TryDownloadAsync(url, _cts.Token);
        if (bytes is null) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.DecodePixelHeight = 64;
            img.EndInit();
            img.Freeze();
            Logo = img;
        }
        catch (Exception ex)
        {
            Log.Write($"logo decode failed: {ex.Message}");
        }
    }

    private async Task LoadAppsAsync()
    {
        try
        {
            var r = await _api.GetAppsAsync(_cts.Token);
            SetApps(r.Apps);
        }
        catch (WorkspaceApiException ex) { Log.Write($"apps: {ex.Code}"); }
    }

    private async Task LoadCapsAsync()
    {
        try { Caps = await _api.GetCapabilitiesAsync(_cts.Token); }
        catch (WorkspaceApiException ex) { Log.Write($"capabilities: {ex.Code}"); }
    }

    public async Task LoadBackupAsync()
    {
        try
        {
            var b = await _api.GetBackupAsync(_cts.Token);
            Backup = b;
            BackupHistory.Clear();
            foreach (var p in b.History) BackupHistory.Add(p);
        }
        catch (WorkspaceApiException ex) { Log.Write($"backup: {ex.Code}"); }
    }

    public async Task LoadAlertsAsync()
    {
        try
        {
            var r = await _api.GetAlertsAsync(_cts.Token);
            Alerts.Clear();
            foreach (var a in r.Items) Alerts.Add(a);
            UnreadAlerts = r.Unread;
            Raise(nameof(HasAlerts));
            // Surface each NEW unread critical alert once; never repeat popups (§36, §45).
            foreach (var a in r.Items.Where(a => a.IsCritical && !a.Read))
            {
                if (_seenCriticalAlerts.Add(a.Id)) CriticalAlert?.Invoke(a);
            }
        }
        catch (WorkspaceApiException ex) { Log.Write($"alerts: {ex.Code}"); }
    }

    private void SetApps(List<PublishedApp>? apps)
    {
        _apps = apps ?? [];
        RebuildPublished();
    }

    private void RebuildPublished()
    {
        var running = new HashSet<string>(RunningApps.Where(r => r.PublishedAppId is not null).Select(r => r.PublishedAppId!), StringComparer.Ordinal);
        PublishedApps.Clear();
        foreach (var a in _apps)
        {
            PublishedApps.Add(new PublishedAppVm { App = a, Icon = IconCache.For(a.ExePath, a.IsFolder), Running = running.Contains(a.Id) });
        }

        Raise(nameof(HasPublishedApps));
        UpdateTsplusHiding();
    }

    /// <summary>Rebuilds "Running now" from the real windows (never optimistic state, §44).</summary>
    public void RefreshWindows()
    {
        try
        {
            var groups = WindowGrouping.Group(_windows.Enumerate(), _apps);
            var before = string.Join("|", RunningApps.Select(r => $"{r.Key}:{string.Join(",", r.Windows.Select(w => $"{w.Handle}{w.State}{w.Title}"))}"));
            var after = string.Join("|", groups.Select(g => $"{g.Key}:{string.Join(",", g.Windows.Select(w => $"{w.Handle}{StateText(w.State)}{WindowGrouping.InstanceTitle(w.Title, g.Name)}"))}"));
            if (before == after) return;

            var expanded = new HashSet<string>(RunningApps.Where(r => r.Expanded).Select(r => r.Key));
            RunningApps.Clear();
            foreach (var g in groups)
            {
                var icon = IconCache.For(g.ExePath);
                RunningApps.Add(new RunningAppVm
                {
                    Key = g.Key,
                    Name = g.Name,
                    PublishedAppId = g.PublishedAppId,
                    Icon = icon,
                    Expanded = expanded.Contains(g.Key),
                    Windows = new ObservableCollection<WindowVm>(g.Windows.Select(w => new WindowVm
                    {
                        Handle = w.Handle,
                        Title = WindowGrouping.InstanceTitle(w.Title, g.Name),
                        State = StateText(w.State),
                        Icon = icon,
                        AppName = g.Name
                    }))
                });
            }

            Raise(nameof(HasRunningApps));
            RebuildPublished();
        }
        catch (Exception ex)
        {
            Log.Error("refresh windows", ex);
        }
    }

    private static string StateText(WindowState2 s) => s switch
    {
        WindowState2.Active => "Active",
        WindowState2.Minimized => "Minimized",
        _ => "Running"
    };

    // ------------------------------------------------------------------ actions

    public void Launch(PublishedApp app)
    {
        if (!AppLauncher.TryLaunch(app, out var error))
        {
            Toast?.Invoke(error ?? "Could not start the application.");
            return;
        }

        Toast?.Invoke($"Opening {app.Name}…");
        _ = Task.Run(async () =>
        {
            try { await _api.AppLaunchedAsync(app.Id, CancellationToken.None); }
            catch (WorkspaceApiException ex) { Log.Write($"launch audit: {ex.Code}"); }
        });
    }

    public void FocusWindow(IntPtr h)
    {
        if (!WindowTracker.Focus(h)) Toast?.Invoke("That window is no longer open.");
        RefreshWindows();
    }

    public void MinimizeWindow(IntPtr h)
    {
        WindowTracker.Minimize(h);
        RefreshWindows();
    }

    public void CloseWindow(IntPtr h)
    {
        WindowTracker.RequestClose(h);
        _ui.BeginInvoke(DispatcherPriority.Background, new Action(RefreshWindows));
    }

    public async Task RunBackupAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            await _api.RunBackupAsync(_cts.Token);
            Toast?.Invoke("Backup started. You can keep working.");
            await LoadBackupAsync();
        }
        catch (WorkspaceApiException ex)
        {
            Toast?.Invoke(ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task MarkAllAlertsReadAsync()
    {
        try
        {
            await _api.MarkAlertsReadAsync([], _cts.Token);
            await LoadAlertsAsync();
        }
        catch (WorkspaceApiException ex) { Toast?.Invoke(ex.Message); }
    }

    private SupportCategories? _categories;

    /// <summary>Central ticket category catalog (same as the customer portal); cached per panel run.</summary>
    public async Task<SupportCategories?> GetSupportCategoriesAsync()
    {
        if (_categories is not null) return _categories;
        try { _categories = await _api.GetSupportCategoriesAsync(_cts.Token); }
        catch (WorkspaceApiException ex) { Log.Write($"support categories: {ex.Code}"); }
        return _categories;
    }

    public async Task<string?> CreateTicketAsync(string subject, string description, string priority, string? category = null, string? subcategory = null)
    {
        if (Busy) return null;
        Busy = true;
        try
        {
            var active = RunningApps.FirstOrDefault(r => r.Windows.Any(w => w.State == "Active"))?.Name;
            var result = await _api.CreateTicketAsync(new
            {
                subject,
                description,
                priority,
                category,
                subcategory,
                context = new { activeApp = active, agentState = ConnectionText, panelVersion = AppInfo.Version }
            }, _cts.Token);
            return result.TicketId ?? "created";
        }
        catch (WorkspaceApiException ex)
        {
            Toast?.Invoke(ex.Message);
            return null;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task AskAiAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || Busy) return;
        Chat.Add(new ChatLine { Text = question.Trim(), FromUser = true });
        Busy = true;
        try
        {
            var answer = await _api.AskAiAsync(question.Trim(), _aiSession, _cts.Token);
            Chat.Add(new ChatLine { Text = answer.Answer, FromUser = false });
        }
        catch (WorkspaceApiException ex)
        {
            Chat.Add(new ChatLine { Text = ex.Message, FromUser = false });
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task SavePreferencesAsync(object patch)
    {
        try
        {
            var prefs = await _api.SavePreferencesAsync(patch, _cts.Token);
            if (Context is not null) Context.Preferences = prefs;
        }
        catch (WorkspaceApiException ex)
        {
            Log.Write($"preferences: {ex.Code}");
        }
    }

    public void MarkUserExit() => _stateFile.Write(WorkspaceSessionState.StateUserExit);

    public void MarkDisabled() => _stateFile.Write(WorkspaceSessionState.StateDisabled);

    public void Dispose()
    {
        TsplusPanelHider.Instance.RestoreAll();
        _cts.Cancel();
        _heartbeat.Stop();
        _slowPoll.Stop();
        _windowPoll.Stop();
        _windows.Dispose();
        _api.Dispose();
    }
}
