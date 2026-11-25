using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// RW-05: always-visible bar at the top centre while support has access — who, what, how long, Stop.
/// It stays visible even when the panel is collapsed, never takes focus, and Stop ends access at once.
/// </summary>
public partial class AssistanceBanner : Window
{
    private readonly AssistanceManager _manager;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private AssistanceRequest? _r;

    public AssistanceBanner(AssistanceManager manager)
    {
        InitializeComponent();
        _manager = manager;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            WindowStyles.MakeToolWindow(h);
            WindowStyles.SetNoActivate(h, true);
        };
        SizeChanged += (_, _) => Place();
        _tick.Tick += (_, _) => Refresh();
    }

    public void Show(AssistanceRequest r)
    {
        _r = r;
        var control = r.GrantedCapabilities?.Contains("control") == true;
        Text.Text = r.Status == "ACTIVE"
            ? $"{r.RequesterName} ({r.RequesterOrg}) {(control ? "can control" : "can see")} your screen"
            : $"{r.RequesterName} ({r.RequesterOrg}) is allowed to {(control ? "control" : "view")} your screen";
        Refresh();
        if (!IsVisible) Show();
        _tick.Start();
        Place();
    }

    public void HideBanner()
    {
        _tick.Stop();
        _r = null;
        Hide();
    }

    private void Refresh()
    {
        if (_r is null) return;
        var since = _r.StartedAt ?? DateTimeOffset.UtcNow;
        var on = DateTimeOffset.UtcNow - since;
        var left = _r.LeaseExpiresAt is { } end ? end - DateTimeOffset.UtcNow : TimeSpan.Zero;
        Timer.Text = _r.Status == "ACTIVE"
            ? $"{(int)on.TotalMinutes:00}:{on.Seconds:00} · ends in {Math.Max(0, (int)left.TotalMinutes)} min"
            : $"waiting for the engineer · ends in {Math.Max(0, (int)left.TotalMinutes)} min";
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top;
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        HideBanner();
        await _manager.StopAsync();
    }
}
