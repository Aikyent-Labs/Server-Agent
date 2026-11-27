using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// Backup tab, like the customer portal: take a backup, see the history, restore the whole backup or chosen
/// files. Data: /remote-workspace/backup-v2 (the portal's handlers, limited to this server and customer).
///
/// "Mine" = the backed-up folders this Windows user can open on the server. On a shared server every
/// subscription has its own workspace folder and NTFS lets a user open only their own, so the panel shows
/// and restores only those (the portal does the same per subscription). Folders and the schedule are
/// changed in the portal, not here.
/// </summary>
public partial class BackupView : UserControl
{
    private WorkspaceApi? _api;
    private Action<string>? _toast;
    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource _cts = new();
    private bool _loading;

    private BackupEnvironment? _env;
    private BackupPolicyResponse? _policy;
    private BackupJob? _running;
    private List<RecoveryPoint> _points = [];
    private List<string> _myRoots = [];

    public BackupView()
    {
        InitializeComponent();
        _live.Tick += async (_, _) => await LoadAsync(quiet: true);
    }

    public void Attach(WorkspaceApi api, Action<string> toast)
    {
        _api = api;
        _toast = toast;
    }

    public void Activate()
    {
        TabOverview.IsChecked = true;
        ShowPage(OverviewPage);
        _ = LoadAsync(quiet: false);
    }

    public void Deactivate() => _live.Stop();

    private static Brush Tone(string tone, bool soft) =>
        (Brush)(soft ? new ToneToSoftBrushConverter() : (System.Windows.Data.IValueConverter)new ToneToBrushConverter())
            .Convert(tone, typeof(Brush), null!, CultureInfo.CurrentCulture);

    // ================================================================== mine (this user's folders)

    private static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private bool IsMine(string root) => _myRoots.Any(m => string.Equals(m, Norm(root), StringComparison.OrdinalIgnoreCase));

    /// <summary>A backed-up folder this Windows user can open on this server (their own workspace).</summary>
    private static bool CanOpen(string path)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            e.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>D:\ORG\CUST-1\CloudDrive -> D:\ORG\CUST-1 (the subscription's workspace); else the folder's parent.</summary>
    private static string WorkspaceOf(string root)
    {
        var p = Norm(root);
        var i = p.LastIndexOf('\\');
        return i > 2 ? p[..i] : p;
    }

    /// <summary>What the customer sees instead of the server path (no drive letter): "CloudDrive\Tally".</summary>
    private static string Shown(string path)
    {
        var p = Norm(path);
        var i = p.IndexOf("\\CloudDrive", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return p[(i + 1)..];
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? parts.LastOrDefault() ?? p : string.Join("\\", parts[^2..]);
    }

    // ================================================================== tabs

    private void ShowPage(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { OverviewPage, HistoryPage, RestorePage })
        {
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Content.BringIntoView(new Rect(0, 0, 1, 1)));
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (OverviewPage is null) return; // during InitializeComponent
        if (sender == TabOverview) ShowPage(OverviewPage);
        else if (sender == TabHistory)
        {
            ShowPage(HistoryPage);
            ShowBackupList();
        }
        else if (sender == TabRestore)
        {
            ShowPage(RestorePage);
            UseWizardIn(RestoreHost, detail: false);
            StartRestore();
        }
    }

    private void OnSeeAllClick(object sender, RoutedEventArgs e) => TabHistory.IsChecked = true;

    private string? _preselectPoint;

    /// <summary>"Restore" next to a backup in the Backups tab: the Restore tab with that backup chosen.</summary>
    private void OnRestoreFromJobClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not JobRow row || row.PointId is null) return;
        _preselectPoint = row.PointId;
        if (TabRestore.IsChecked == true) StartRestore(); else TabRestore.IsChecked = true;
    }

    private void OnOpenRestoreClick(object sender, RoutedEventArgs e) => TabRestore.IsChecked = true;

    // ================================================================== load + overview

    private async Task LoadAsync(bool quiet)
    {
        if (_api is null || _loading) return;
        _loading = true;
        if (!quiet) LoadingText.Visibility = Visibility.Visible;
        var ct = _cts.Token;
        try
        {
            // Like the portal: no Backup in the customer's plan = nothing to back up or restore here.
            var me = await _api.BackupMeAsync(ct);
            if (!me.IsAdmin)
            {
                ShowNotSetUp("Backup for this workspace is managed by your account admin. Ask them if you need files back.",
                    "Backup is managed by your admin");
                return;
            }

            if (!me.HasBackupPlan)
            {
                ShowNotSetUp("Your plan does not include Backup, so your files on this server are not backed up. Add Backup to your plan in the CloudOrc portal (Plans) to protect them.",
                    "Backup is not in your plan");
                return;
            }

            var envs = await _api.BackupEnvironmentsAsync(ct);
            _env = envs.Environments.FirstOrDefault(e => e.EngineMode == "v2") ?? envs.Environments.FirstOrDefault();
            if (_env is null)
            {
                ShowNotSetUp("Your data on this server is not being backed up yet. Turn on backup in the CloudOrc portal, or contact support.");
                return;
            }

            var policyTask = _api.BackupPolicyAsync(_env.Id, ct);
            var jobsTask = _api.BackupJobsAsync(12, ct);
            var restoresTask = _api.RestoresAsync(ct);
            var pointsTask = _api.RecoveryPointsAsync(30, ct);
            var quotaTask = _api.BackupQuotaAsync(_env.SubscriptionId, ct);
            try { _policy = await policyTask; } catch (WorkspaceApiException ex) { Log.Write($"backup policy: {ex.Code}"); }
            var jobs = (await jobsTask).Jobs;
            var restores = (await restoresTask).Restores;
            _points = (await pointsTask).RecoveryPoints;
            BackupQuota? quota = null;
            try { quota = await quotaTask; } catch (WorkspaceApiException ex) { Log.Write($"backup quota: {ex.Code}"); }

            // This subscription's own backed-up folders: inside its workspace folder (from the server, the same
            // seat -> subscription rule as the drive mapping). Without one, the folders this user can open.
            var sources = (_policy?.Effective.Sources ?? []).Select(s => Norm(s.Path))
                .Concat(_points.SelectMany(p => p.Roots).Select(Norm))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var workspace = string.IsNullOrWhiteSpace(me.WorkspaceRoot) ? null : Norm(me.WorkspaceRoot);
            _myRoots = workspace is not null
                ? sources.Where(s => BackupText.IsInside(s, workspace)).ToList()
                : await Task.Run(() => sources.Where(CanOpen).ToList());
            if (sources.Count > 0 && _myRoots.Count == 0)
            {
                ShowNotSetUp("Your own folders on this server are not part of the backup yet. Turn on backup for them in the CloudOrc portal, or contact support.");
                return;
            }

            NotSetUpCard.Visibility = Visibility.Collapsed;
            RenderStatus(jobs);
            RenderOverview(quota);
            RenderJobs(jobs);
            RenderRestores(restores);
            Content.Visibility = Visibility.Visible;

            var busy = _running is not null || restores.Any(r => !BackupText.Terminal.Contains((r.State ?? "").ToUpperInvariant()) && r.ApprovalState != "pending");
            if (busy && IsVisible) _live.Start(); else _live.Stop();
        }
        catch (WorkspaceApiException ex)
        {
            Log.Write($"backup: {ex.Code}");
            if (!quiet)
            {
                ShowNotSetUp(ex.Code is "BACKEND_UNAVAILABLE" or "AGENT_OFFLINE"
                    ? "CloudOrc is not reachable right now, so your backup cannot be shown. Your backups keep running on schedule."
                    : ex.Message);
            }
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            _loading = false;
        }
    }

    private void ShowNotSetUp(string text, string title = "Backup is not set up for you yet")
    {
        NotSetUpTitle.Text = title;
        Content.Visibility = Visibility.Collapsed;
        NotSetUpCard.Visibility = Visibility.Visible;
        NotSetUpText.Text = text;
        _live.Stop();
    }

    private void RenderStatus(List<BackupJob> jobs)
    {
        var env = _env!;
        _running = jobs.FirstOrDefault(j => j.Kind == "backup" && !BackupText.Terminal.Contains((j.State ?? "").ToUpperInvariant()));
        var last = jobs.FirstOrDefault(j => j.Kind == "backup" && BackupText.Terminal.Contains((j.State ?? "").ToUpperInvariant()));
        var age = env.LastRecoveryPointAt is { } at ? DateTimeOffset.UtcNow - at : (TimeSpan?)null;
        // The backup service of this server (Server Agent or the separate CloudOrc Backup Agent) is not in touch.
        var agentDown = !env.AgentOnline && env.PausedAt is null
                        && (env.AgentLastSeenAt is null || DateTimeOffset.UtcNow - env.AgentLastSeenAt > TimeSpan.FromMinutes(30));

        string tone, glyph, headline, detail;
        if (agentDown)
        {
            (tone, glyph, headline) = ("bad", "\ue783", "Backups are not running on this server");
            detail = (env.AgentLastSeenAt is { } seen
                         ? $"The backup service on this server has not been in touch since {BackupText.When(seen)} ({TimeText.Ago(seen)}). "
                         : "The backup service on this server is not connected. ")
                     + "No new backups are being taken. Please contact support - your existing backups are safe.";
        }
        else if (_running is not null)
        {
            (tone, glyph, headline, detail) = ("run", "\ue898", "Backing up now", "You can keep working - the backup runs in the background.");
        }
        else if (env.PausedAt is not null)
        {
            (tone, glyph, headline, detail) = ("warn", "\ue769", "Backups are paused", "No new backups are taken until they are resumed in the CloudOrc portal.");
        }
        else if (env.LastRecoveryPointAt is null)
        {
            (tone, glyph, headline, detail) = ("warn", "\ue7ba", "No backup yet", "The first backup runs at the next scheduled time - or start it now.");
        }
        else if (last?.State is "FAILED")
        {
            (tone, glyph, headline) = ("bad", "\ue783", "The last backup did not complete");
            detail = BackupText.Reason(last.ReasonCode, last.Message) ?? "Please try again or contact support.";
        }
        else if (age > TimeSpan.FromHours(36))
        {
            (tone, glyph, headline, detail) = ("warn", "\ue7ba", "Your backup is out of date", "The last good backup is more than a day and a half old.");
        }
        else
        {
            (tone, glyph, headline, detail) = ("ok", "\ue73e", "Your data is safe", "Your files are backed up and checked.");
        }

        var strong = ((SolidColorBrush)Tone(tone, false)).Color;
        StatusCard.Background = Tone(tone, soft: true);
        StatusCard.BorderBrush = new SolidColorBrush(Color.FromArgb(70, strong.R, strong.G, strong.B));
        StatusCircle.Fill = Tone(tone, soft: false);
        StatusIcon.Text = glyph;
        StatusHeadline.Text = headline;
        StatusHeadline.Foreground = Tone(tone, soft: false);
        StatusDetail.Text = detail;

        LastBackupValue.Text = env.LastRecoveryPointAt is { } lastAt ? BackupText.Friendly(lastAt.LocalDateTime, DateTime.Now) : "None yet";
        LastBackupValue.ToolTip = env.LastRecoveryPointAt is { } l2 ? $"{BackupText.When(l2)} ({TimeText.Ago(l2)})" : null;
        var next = env.PausedAt is null && _policy is not null ? BackupText.NextRun(_policy.Effective.Schedule, DateTime.Now, env.LastRecoveryPointAt) : null;
        NextBackupValue.Text = env.PausedAt is not null ? "Paused" : agentDown ? "Not running" : next is { } n ? BackupText.Friendly(n, DateTime.Now) : _policy is null ? "-" : "When you start it";

        if (_running is { } run)
        {
            RunningCard.Visibility = Visibility.Visible;
            RunStage.Text = BackupText.JobState(run.State);
            var p = run.Progress;
            RunProgress.IsIndeterminate = p?.Percent is null;
            RunProgress.Value = p?.Percent ?? 0;
            var parts = new List<string>();
            if (p?.Percent is { } pc) parts.Add($"{pc:0}%");
            if (p?.FilesTotal is > 0) parts.Add($"{p.FilesDone ?? 0:N0} of {p.FilesTotal:N0} files");
            if (p?.BytesTotal is > 0) parts.Add($"{BackupText.Bytes(p.BytesRead)} of {BackupText.Bytes(p.BytesTotal)}");
            parts.Add($"started {TimeText.Ago(run.StartedAt ?? run.CreatedAt)}");
            RunDetail.Text = string.Join(" · ", parts);
        }
        else
        {
            RunningCard.Visibility = Visibility.Collapsed;
        }

        BackupNowButton.IsEnabled = _running is null && env.PausedAt is null && !agentDown;
        BackupNowText.Text = _running is null ? "Back up now" : "Backing up…";
    }

    private void RenderOverview(BackupQuota? q)
    {
        // Size of MY folders in the latest backup (a shared server's backup holds other plans' folders too).
        var latest = _points.FirstOrDefault(p => p.Status is "SUCCEEDED" or "PARTIAL");
        var mine = latest?.RootSizes.Where(r => IsMine(r.Root)).ToList() ?? [];
        StorageValue.Text = mine.Count > 0 ? BackupText.Bytes(mine.Sum(r => r.Bytes)) : "-";

        FoldersList.ItemsSource = _myRoots.Select(r => new FolderRow(r)).ToList();
        FoldersValue.Text = _myRoots.Count.ToString(CultureInfo.InvariantCulture);
        NoFoldersText.Visibility = _myRoots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var sch = _policy?.Effective.Schedule ?? default;
        var time = sch.ValueKind == System.Text.Json.JsonValueKind.Object && sch.TryGetProperty("kind", out var k) && k.GetString() == "daily"
                   && sch.TryGetProperty("timesOfDay", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Array && t.GetArrayLength() > 0
            ? t[0].GetString() : null;
        ScheduleValue.Text = _env?.PausedAt is not null ? "Paused" : time ?? (_policy is null ? "-" : "Custom");
        ScheduleValue.ToolTip = _policy is null ? null : BackupText.Schedule(sch);

        if (q?.LimitBytes is > 0)
        {
            StorageRow.Visibility = Visibility.Visible;
            StoragePlan.Text = q.PlanName ?? string.Empty;
            StorageBar.Value = Math.Min(100, q.PercentUsed ?? q.UsedBytes * 100 / q.LimitBytes.Value);
            StorageBar.Foreground = Tone(q.Status == "exceeded" ? "bad" : q.Status == "warning" ? "warn" : "run", soft: false);
            StorageText.Text = $"{BackupText.Bytes(q.UsedBytes)} of {BackupText.Bytes(q.LimitBytes)} used"
                + (q.Status == "exceeded" ? " - full. New backups need more backup storage in your plan." : q.Status == "warning" ? " - almost full." : ".");
        }
        else
        {
            StorageRow.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderJobs(List<BackupJob> jobs)
    {
        // A backup can be restored from when it left a usable recovery point that holds one of MY folders.
        var pointByJob = _points.Where(p => p.JobId is not null && p.Status is "SUCCEEDED" or "PARTIAL" or null && p.Roots.Any(IsMine))
            .GroupBy(p => p.JobId!).ToDictionary(g => g.Key, g => g.First().Id);
        var rows = jobs.Where(j => j.Kind == "backup").Take(15)
            .Select(j => new JobRow(j) { PointId = pointByJob.GetValueOrDefault(j.Id) }).ToList();
        JobsList.ItemsSource = rows;
        JobsPreview.ItemsSource = rows.Take(3).ToList();
        NoJobsText.Visibility = NoJobsPreview.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderRestores(List<RestoreRow> restores)
    {
        var rows = restores.Where(r => string.IsNullOrEmpty(r.SourceRoot) || IsMine(r.SourceRoot)).Take(8).Select(r => new RestoreItemRow(r)).ToList();
        RestoresList.ItemsSource = rows;
        NoRestoresText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync(quiet: false);

    private async void OnBackupNowClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || _env is null) return;
        BackupNowButton.IsEnabled = false;
        try
        {
            await _api.RunDeviceBackupAsync(_env.Id, _cts.Token);
            _toast?.Invoke("Backup started. You can keep working.");
        }
        catch (WorkspaceApiException ex)
        {
            _toast?.Invoke(BackupText.Reason(ex.Code, ex.Message) ?? ex.Message);
        }

        await LoadAsync(quiet: true);
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || _running is null) return;
        StopButton.IsEnabled = false;
        try
        {
            await _api.CancelBackupJobAsync(_running.Id, _cts.Token);
            _toast?.Invoke("Stopping the backup…");
        }
        catch (WorkspaceApiException ex) { _toast?.Invoke(ex.Message); }
        finally { StopButton.IsEnabled = true; }
        await LoadAsync(quiet: true);
    }

    // ================================================================== one backup (Backups tab)

    private bool _detailMode;

    /// <summary>The restore wizard is one control, shown in the Restore tab or inside an opened backup.</summary>
    private void UseWizardIn(ContentControl host, bool detail)
    {
        _detailMode = detail;
        if (RestoreWizard.Parent is ContentControl current && !ReferenceEquals(current, host)) current.Content = null;
        if (!ReferenceEquals(host.Content, RestoreWizard)) host.Content = RestoreWizard;
        PointStep.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        WhatHeading.Text = detail ? "Restore" : "2. What to restore";
        WhatHeading.Margin = detail ? new Thickness(0, 0, 0, 2) : new Thickness(0, 12, 0, 2);
        WhereHeading.Text = detail ? "Where to put them" : "3. Where to put them";
    }

    private void ShowBackupList()
    {
        BackupDetail.Visibility = Visibility.Collapsed;
        BackupList.Visibility = Visibility.Visible;
    }

    private void OnBackToListClick(object sender, RoutedEventArgs e) => ShowBackupList();

    private void OnJobRowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobRow row) _ = OpenBackupAsync(row);
    }

    /// <summary>Open one backup: what happened, how big, what is in it - and restore from it.</summary>
    private async Task OpenBackupAsync(JobRow row)
    {
        BackupList.Visibility = Visibility.Collapsed;
        BackupDetail.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Content.BringIntoView(new Rect(0, 0, 1, 1)));
        var job = row.Job;
        DetailWhen.Text = row.When;
        DetailState.Text = row.StateText;
        DetailState.Foreground = Tone(row.Tone, soft: false);
        DetailPill.Background = Tone(row.Tone, soft: true);
        var point = row.PointId is null ? null : _points.FirstOrDefault(p => p.Id == row.PointId);
        var mine = point?.RootSizes.Where(r => IsMine(r.Root)).ToList() ?? [];
        DetailSize.Text = mine.Count > 0 ? BackupText.Bytes(mine.Sum(r => r.Bytes)) : "-";
        DetailFiles.Text = mine.Count > 0 ? mine.Sum(r => r.Files).ToString("N0", CultureInfo.CurrentCulture) : "-";
        DetailTook.Text = job.EndedAt is { } end && job.StartedAt is { } start && end > start
            ? (end - start).TotalMinutes < 1 ? "< 1 min" : $"{(int)Math.Round((end - start).TotalMinutes)} min"
            : "-";
        var reason = job.State is "SUCCEEDED" ? null : BackupText.Reason(job.ReasonCode, job.Message);
        DetailReason.Text = reason ?? string.Empty;
        DetailReason.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        DetailReason.Foreground = Tone(job.State == "FAILED" ? "bad" : "muted", soft: false);

        if (point is not null)
        {
            DetailEvents.Visibility = Visibility.Collapsed;
            DetailHeading.Visibility = DetailHost.Visibility = Visibility.Visible;
            UseWizardIn(DetailHost, detail: true);
            _preselectPoint = point.Id;
            StartRestore();
            PickFiles.IsChecked = true; // show what is in the backup straight away
            return;
        }

        // Nothing to restore from this one (cancelled / failed): show what happened.
        DetailHeading.Visibility = DetailHost.Visibility = Visibility.Collapsed;
        DetailEvents.Text = string.Empty;
        DetailEvents.Visibility = Visibility.Collapsed;
        if (_api is null) return;
        try
        {
            var d = await _api.BackupJobAsync(job.Id, _cts.Token);
            var lines = d.Events.TakeLast(6).Select(ev => $"{ev.ReceivedAt?.ToLocalTime():HH:mm:ss}  {Pretty(ev)}").ToList();
            DetailEvents.Text = lines.Count == 0 ? "This backup did not run, so it holds no files." : "What happened:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
            DetailEvents.Visibility = Visibility.Visible;
        }
        catch (WorkspaceApiException ex)
        {
            DetailEvents.Text = ex.Message;
            DetailEvents.Visibility = Visibility.Visible;
        }
    }

    private static string Pretty(BackupJobEvent ev)
    {
        var what = (ev.Stage ?? ev.EventType ?? string.Empty).Replace('_', ' ').Replace('.', ' ').Trim();
        var msg = BackupText.Reason(ev.ReasonCode, ev.Message);
        return msg is null ? what : $"{what}: {msg}";
    }

    // ================================================================== restore

    private RecoveryPoint? _point;
    private List<RecoveryRoot> _pointRoots = [];
    private string? _root;
    private string? _folder;
    private readonly Dictionary<string, (string Root, string Path, bool Folder)> _picked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Restore tab opened: backups that hold at least one of MY folders.</summary>
    private void StartRestore()
    {
        _picked.Clear();
        _point = null;
        WhatStep.Visibility = WhereStep.Visibility = FilesStep.Visibility = Visibility.Collapsed;
        ConfirmCard.Visibility = RestoreError.Visibility = Visibility.Collapsed;
        if (!_detailMode) PickEverything.IsChecked = true;
        ToNewFolder.IsChecked = true;
        ModeCombo.SelectedIndex = 0;
        var usable = _points.Where(p => p.Status is "SUCCEEDED" or "PARTIAL" or null && p.Roots.Any(IsMine)).ToList();
        PointsList.ItemsSource = usable.Select(p => new PointRow(p, p.RootSizes.Where(r => IsMine(r.Root)).Sum(r => r.Bytes))).ToList();
        NoPointsText.Visibility = usable.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pre = _preselectPoint is null ? -1 : usable.FindIndex(p => p.Id == _preselectPoint);
        _preselectPoint = null;
        PointsList.SelectedIndex = usable.Count == 0 ? -1 : Math.Max(0, pre);
    }

    private async void OnPointChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PointsList.SelectedItem is not PointRow row || _api is null) return;
        _point = row.Point;
        _picked.Clear();
        _pointRoots = [];
        RestoreError.Visibility = ConfirmCard.Visibility = Visibility.Collapsed;
        WhatStep.Visibility = Visibility.Visible;
        if (!_point.Browsable)
        {
            EverythingRun.Text = " - the file list of this backup is not available";
            WhereStep.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _pointRoots = (await _api.RecoveryRootsAsync(_point.Id, _cts.Token)).Roots.Where(r => IsMine(r.Root)).ToList();
            EverythingRun.Text = _pointRoots.Count == 0
                ? " - your folders are not in this backup"
                : " (" + string.Join(", ", _pointRoots.Select(r => $"{Shown(r.Root)}, {r.Entries:N0} items")) + ")";
        }
        catch (WorkspaceApiException ex)
        {
            EverythingRun.Text = ex.Code == "MANIFEST_NOT_AVAILABLE" ? " - the file list is still being prepared, try again in a few minutes" : " - " + ex.Message;
        }

        UpdateWhat();
        if (PickFiles.IsChecked == true) ShowRoots();
    }

    private void OnWhatChanged(object sender, RoutedEventArgs e)
    {
        if (FilesStep is null) return;
        UpdateWhat();
        if (PickFiles.IsChecked == true) ShowRoots();
    }

    private void UpdateWhat()
    {
        var everything = PickEverything.IsChecked == true;
        FilesStep.Visibility = everything ? Visibility.Collapsed : Visibility.Visible;
        var ready = _pointRoots.Count > 0 && (everything || _picked.Count > 0);
        WhereStep.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        ConfirmCard.Visibility = Visibility.Collapsed;
        RestoreGoButton.Content = _picked.Count == 0 || everything ? "Restore" : $"Restore {Plural(_picked.Count, "item")}";
        NewFolderRun.Text = Shown(TargetFolder(_pointRoots.FirstOrDefault()?.Root ?? _myRoots.FirstOrDefault() ?? "C:\\Restored"));
    }

    private void ShowRoots()
    {
        _root = null;
        _folder = null;
        SetCrumbs();
        ShowFiles(_pointRoots.Select(r => new FileRow(new RecoveryItem { Name = Shown(r.Root), Path = string.Empty, Root = r.Root, Type = "root" }, r.Entries)).ToList(),
            "Tick what you want back, or open a folder to pick single files.");
    }

    private void ShowFiles(List<FileRow> rows, string note)
    {
        foreach (var r in rows) r.IsChecked = _picked.ContainsKey(r.Key) || IsCoveredByFolder(r);
        FilesList.ItemsSource = rows;
        FilesNote.Text = note;
    }

    private bool IsCoveredByFolder(FileRow r) =>
        _picked.Values.Any(p => p.Folder && string.Equals(p.Root, r.Item.Root, StringComparison.OrdinalIgnoreCase)
            && (p.Path.Length == 0 || r.Item.Path.StartsWith(p.Path.TrimEnd('\\', '/') + (r.Item.Path.Contains('/') ? "/" : "\\"), StringComparison.OrdinalIgnoreCase)));

    private async Task BrowseAsync(string root, string? path)
    {
        if (_api is null || _point is null || !IsMine(root)) return;
        try
        {
            var res = await _api.BrowseRecoveryAsync(_point.Id, root, path, _cts.Token);
            _root = root;
            _folder = path;
            SetCrumbs();
            ShowFiles(res.Items.Select(i => new FileRow(i, null)).ToList(),
                res.Items.Count == 0 ? "This folder is empty." : res.Total > res.Items.Count ? $"Showing {res.Items.Count} of {res.Total}. Use search to find a file." : string.Empty);
        }
        catch (WorkspaceApiException ex) { ShowFiles([], ex.Message); }
    }

    private void SetCrumbs()
    {
        var crumbs = new List<Crumb> { new("Your folders", null, null) };
        if (_root is not null)
        {
            crumbs.Add(new Crumb(Shown(_root), _root, null));
            if (!string.IsNullOrEmpty(_folder))
            {
                var sep = _folder.Contains('/') ? '/' : '\\';
                var acc = string.Empty;
                foreach (var part in _folder.Split(sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    acc = acc.Length == 0 ? part : acc + sep + part;
                    crumbs.Add(new Crumb(part, _root, acc));
                }
            }
        }

        for (var i = 0; i < crumbs.Count; i++) crumbs[i].HasNext = i < crumbs.Count - 1;
        Crumbs.ItemsSource = crumbs;
    }

    private async void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Crumb c) return;
        if (c.Root is null) { ShowRoots(); return; }
        await BrowseAsync(c.Root, c.Path);
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FileRow row) await OpenAsync(row);
    }

    private async void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedItem is FileRow row && row.IsFolder) await OpenAsync(row);
    }

    private Task OpenAsync(FileRow row) => BrowseAsync(row.Item.Root, row.Item.Type == "root" ? null : row.Item.Path);

    private async void OnSearchClick(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_api is null || _point is null) return;
        var q = SearchBox.Text.Trim();
        if (q.Length < 2) return;
        try
        {
            var res = await _api.SearchRecoveryAsync(_point.Id, q, _cts.Token);
            var items = res.Items.Where(i => IsMine(i.Root)).ToList();
            Crumbs.ItemsSource = new List<Crumb> { new("Your folders", null, null) { HasNext = true }, new($"Search: {q}", null, null) };
            ShowFiles(items.Select(i => new FileRow(i, null)).ToList(), items.Count == 0 ? "Nothing found." : $"{items.Count} found.");
        }
        catch (WorkspaceApiException ex) { ShowFiles([], ex.Message); }
    }

    private void OnPickChanged(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileRow row) return;
        if (row.IsChecked) _picked[row.Key] = (row.Item.Root, row.Item.Type == "root" ? string.Empty : row.Item.Path, row.IsFolder);
        else _picked.Remove(row.Key);
        var folders = _picked.Values.Count(p => p.Folder);
        var files = _picked.Count - folders;
        PickedText.Text = _picked.Count == 0 ? string.Empty : "Selected: " + string.Join(", ", new[] { Plural(folders, "folder"), Plural(files, "file") }.Where(x => x.Length > 0));
        UpdateWhat();
    }

    private static string Plural(int n, string word) => n == 0 ? string.Empty : $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>The safe target inside the user's own workspace: "...\CUST-1\Restored files\2026-11-20 1057".</summary>
    private string TargetFolder(string root) =>
        $"{WorkspaceOf(root)}\\Restored files\\{(_point?.SourceTime ?? DateTimeOffset.Now).ToLocalTime():yyyy-MM-dd HHmm}";

    private void OnWhereChanged(object sender, RoutedEventArgs e)
    {
        if (ExistingRow is null) return;
        ExistingRow.Visibility = ToOriginal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ConfirmCard.Visibility = Visibility.Collapsed;
        OnModeChanged(sender, null!);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverwriteWarning is null) return;
        OverwriteWarning.Visibility = ToOriginal.IsChecked == true && Mode == "overwrite" ? Visibility.Visible : Visibility.Collapsed;
        ConfirmCard.Visibility = Visibility.Collapsed;
    }

    private string Mode => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "rename";

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_point is null) return;
        var everything = PickEverything.IsChecked == true;
        if (!everything && _picked.Count == 0) return;
        RestoreError.Visibility = Visibility.Collapsed;
        var original = ToOriginal.IsChecked == true;
        var what = everything ? "everything" : Plural(_picked.Count, "item");
        var where = original ? "back where it was" : $"into {Shown(TargetFolder(_pointRoots.FirstOrDefault()?.Root ?? _myRoots[0]))}";
        var how = !original ? "Nothing on the server is replaced." : Mode switch
        {
            "overwrite" => "Files that are already there will be REPLACED by the backup copy.",
            "skip-existing" => "Files that are already there are kept as they are.",
            _ => "Where a file is already there, the restored copy gets a new name."
        };
        ConfirmText.Text = $"Restore {what} from the backup of {BackupText.When(_point.SourceTime)} {where}? {how}";
        ConfirmCard.Visibility = Visibility.Visible;
        ConfirmCard.BringIntoView();
    }

    private void OnCancelConfirmClick(object sender, RoutedEventArgs e) => ConfirmCard.Visibility = Visibility.Collapsed;

    private async void OnConfirmRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || _point is null) return;
        ConfirmCard.Visibility = Visibility.Collapsed;
        var original = ToOriginal.IsChecked == true;
        var everything = PickEverything.IsChecked == true;
        // One request per backed-up folder (the portal does the same).
        var groups = everything
            ? _pointRoots.Select(r => (Root: r.Root, Paths: new List<string>(), Whole: true)).ToList()
            : _picked.Values.GroupBy(p => p.Root, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Root: g.Key, Paths: g.Where(p => p.Path.Length > 0).Select(p => p.Path).ToList(), Whole: g.Any(p => p.Path.Length == 0))).ToList();
        var failed = new List<string>();
        var started = 0;
        foreach (var g in groups)
        {
            try
            {
                var selection = g.Whole
                    ? (await _api.BrowseRecoveryAsync(_point.Id, g.Root, null, _cts.Token)).Items.Select(i => i.Path).ToList()
                    : g.Paths;
                if (selection.Count == 0) throw new WorkspaceApiException(null, "EMPTY", "nothing to restore in this folder");
                var target = groups.Count > 1 ? $"{TargetFolder(g.Root)}\\{BackupText.FolderLabel(g.Root)}" : TargetFolder(g.Root);
                await _api.RequestRestoreAsync(_point.Id, new
                {
                    root = g.Root,
                    selection,
                    targetKind = original ? "original" : "alternate",
                    targetPath = original ? null : target,
                    mode = original ? Mode : "rename",
                }, _cts.Token);
                started++;
            }
            catch (WorkspaceApiException ex)
            {
                failed.Add($"{Shown(g.Root)}: {ex.Message}");
            }
        }

        if (failed.Count > 0)
        {
            RestoreError.Text = (started > 0 ? "Some of the restore was started. " : string.Empty) + string.Join(" ", failed);
            RestoreError.Visibility = Visibility.Visible;
            if (started == 0) return;
        }

        _toast?.Invoke("Restore started. You can follow it under Restore.");
        await LoadAsync(quiet: true);
        if (TabRestore.IsChecked == true) StartRestore(); else TabRestore.IsChecked = true;
    }

    // ================================================================== rows

    public sealed class FolderRow(string path)
    {
        public string Path { get; } = path;
        public string Name { get; } = BackupText.FolderLabel(path);
        public string Shown { get; } = BackupView.Shown(System.IO.Path.GetDirectoryName(path) ?? path) is var ws && ws.Length > 0 ? $"in {ws}" : string.Empty;
    }

    public sealed class JobRow(BackupJob job) : ObservableObject
    {
        public BackupJob Job { get; } = job;
        public string When { get; } = BackupText.When(job.StartedAt ?? job.CreatedAt);
        public string StateText { get; } = BackupText.JobState(job.State);
        public string Tone { get; } = BackupText.JobTone(job.State);

        public string Sub { get; } = job.State switch
        {
            "SUCCEEDED" or "PARTIAL" when job.EndedAt is { } end && job.StartedAt is { } start && end > start =>
                (end - start).TotalMinutes < 1 ? "Took under a minute" : $"Took {(int)Math.Round((end - start).TotalMinutes)} min",
            "CANCELLED" when job.StartedAt is null => "Cancelled before it started",
            "CANCELLED" => "Stopped",
            "FAILED" => "Tap to see why",
            "SUCCEEDED" or "PARTIAL" => "Completed",
            _ => "In progress"
        };

        private string? _details;
        public string? Details { get => _details; set => Set(ref _details, value); }

        /// <summary>Recovery point of this backup that holds the user's folders (null = nothing to restore).</summary>
        public string? PointId { get; init; }
        public bool CanRestore => PointId is not null;
    }

    public sealed class RestoreItemRow
    {
        public RestoreItemRow(RestoreRow r)
        {
            (Status, Tone) = BackupText.RestoreStatus(r);
            When = BackupText.When(r.CreatedAt);
            var parts = new List<string>();
            if (r.RecoveryPointTime is { } rp) parts.Add($"From the backup of {BackupText.When(rp)}");
            parts.Add(r.TargetKind == "original" ? "back where they were" : $"into {BackupView.Shown(r.TargetPath ?? r.Result?.RestoreTarget ?? "a new folder")}");
            if (r.Result?.RestoredFiles is > 0) parts.Add($"{r.Result.RestoredFiles:N0} files ({BackupText.Bytes(r.Result.RestoredBytes)})");
            var reason = r.State is "FAILED" or "PARTIAL" ? BackupText.Reason(r.ReasonCode, r.Message) : null;
            Detail = string.Join(" · ", parts) + (reason is null ? string.Empty : $"{Environment.NewLine}{reason}");
        }

        public string Status { get; }
        public string Tone { get; }
        public string When { get; }
        public string Detail { get; }
    }

    public sealed class PointRow(RecoveryPoint p, double myBytes)
    {
        public RecoveryPoint Point { get; } = p;
        public string When { get; } = BackupText.When(p.SourceTime);

        public string Detail { get; } = string.Join(" · ", new[]
        {
            myBytes > 0 ? BackupText.Bytes(myBytes) : null,
            p.Status == "PARTIAL" ? "some files were skipped" : null,
            p.VerificationState is "VERIFIED" or "verified" ? "checked" : null,
            p.Browsable ? null : "file list not available",
        }.Where(x => x is not null));
    }

    public sealed class Crumb(string label, string? root, string? path)
    {
        public string Label { get; } = label;
        public string? Root { get; } = root;
        public string? Path { get; } = path;
        public bool HasNext { get; set; }
    }

    public sealed class FileRow(RecoveryItem item, long? entries) : ObservableObject
    {
        public RecoveryItem Item { get; } = item;
        public string Key => $"{Item.Root}|{Item.Path}";
        public string Name => Item.Name;
        public string Path => Item.Type == "root" ? Item.Root : Item.Path;
        public bool IsFolder => Item.Type is "dir" or "root";
        public string Glyph => IsFolder ? "\ue8b7" : "\ue8a5";
        public Brush GlyphBrush => (Brush)(Application.Current.TryFindResource(IsFolder ? "Warn" : "Muted") ?? Brushes.Gray);

        public string Detail => Item.Type == "root"
            ? $"Your backed-up folder{(entries is > 0 ? $" · {entries:N0} items" : string.Empty)}"
            : IsFolder ? "Folder" : $"{BackupText.Bytes(Item.Size)}{(Item.Mtime is { } m ? $" · {m.ToLocalTime():dd MMM yyyy HH:mm}" : string.Empty)}";

        private bool _checked;
        public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    }
}
