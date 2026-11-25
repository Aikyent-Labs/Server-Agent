using System.Windows;
using System.Windows.Threading;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// RW-04 consent. View is the only pre-selected capability; anything wider must be ticked by the user
/// and only what was requested AND what the provider can deliver can be ticked. Elevation is never
/// offered here. One decision is committed (Allow/Decline); closing or timing out declines nothing —
/// the request simply expires on the server.
/// </summary>
public partial class ConsentWindow : Window
{
    private readonly AssistanceRequest _r;
    private readonly AssistanceManager _manager;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _decided;

    public ConsentWindow(AssistanceRequest r, AssistanceManager manager, string brand)
    {
        InitializeComponent();
        _r = r;
        _manager = manager;
        BrandText.Text = $"{brand} · Remote Assistance";
        Headline.Text = $"{r.RequesterOrg} wants to help in your session";
        EngineerText.Text = r.RequesterName;
        OrgText.Text = r.RequesterOrg;
        ReasonText.Text = string.IsNullOrWhiteSpace(r.TicketRef) ? r.Reason : $"{r.Reason} — {r.TicketRef}";

        bool Offered(string cap) => r.RequestedCapabilities.Contains(cap) && r.ProviderCapabilities.Contains(cap);
        CapControl.IsEnabled = Offered("control");
        CapControl.IsChecked = false;
        if (!CapControl.IsEnabled) CapControl.Content = "Control mouse and keyboard (not requested)";
        CapClipboard.IsEnabled = Offered("clipboard");
        CapFiles.IsEnabled = Offered("file_transfer");
        CapNote.Text = $"Using {r.ProviderLabel ?? "Windows shadowing"}: clipboard and file transfer are not available. Administrator rights are never given from here.";

        _countdown.Tick += (_, _) => UpdateExpiry();
        Loaded += (_, _) => { UpdateExpiry(); _countdown.Start(); };
        Closed += (_, _) => _countdown.Stop();
    }

    private void UpdateExpiry()
    {
        if (_r.RequestExpiresAt is not { } end) return;
        var left = end - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero && !_decided)
        {
            Close();
            return;
        }

        ExpiryText.Text = $"Request expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private int Duration() => Dur30.IsChecked == true ? 30 : Dur60.IsChecked == true ? 60 : DurSession.IsChecked == true ? 0 : 15;

    private async void OnAllow(object sender, RoutedEventArgs e)
    {
        AllowButton.IsEnabled = false;
        var caps = new List<string> { "view" };
        if (CapControl.IsChecked == true) caps.Add("control");
        if (CapClipboard.IsChecked == true) caps.Add("clipboard");
        if (CapFiles.IsChecked == true) caps.Add("file_transfer");
        _decided = await _manager.DecideAsync(_r, true, caps, Duration());
        if (_decided) Close(); else AllowButton.IsEnabled = true;
    }

    private async void OnDecline(object sender, RoutedEventArgs e)
    {
        _decided = true;
        await _manager.DecideAsync(_r, false, [], 0);
        Close();
    }
}
