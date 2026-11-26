using System.Windows.Threading;
using CloudOrc.Agent.Contracts.Workspace;

namespace CloudOrc.WorkspaceUI.Services;

/// <summary>
/// Applies the fallback switches (%ProgramData%\CloudOrc\Workspace\fallback-*.json) to this panel within
/// a few seconds, without needing the Control Agent or CloudOrc: the files are local and sticky.
///  - panel off      -> the panel restores the TSPlus panel, gives Alt+Tab back to Windows and exits;
///  - Alt+Tab off    -> Windows Alt+Tab (panel keeps running);
///  - TSPlus hide off -> the TSPlus floating panel is never hidden (panel keeps running).
/// The CloudOrc Watch enforces "panel off" as well (ends a panel that does not exit, never restarts it).
/// </summary>
public sealed class FallbackGuard : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly string _username;
    private readonly DispatcherTimer _timer;
    private WorkspaceFallbackDecision _current = WorkspaceFallbackDecision.None;

    public FallbackGuard(string username, Dispatcher ui)
    {
        _username = username;
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = Interval };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>Panel must stop (TSPlus panel already restored by the caller's shutdown path).</summary>
    public event Action<WorkspaceFallbackDecision>? PanelOff;

    public event Action<bool, string>? AltTabChanged;

    public event Action<bool>? KeepTsplusChanged;

    public WorkspaceFallbackDecision Current => _current;

    public void Start()
    {
        Check();
        _timer.Start();
    }

    public void Check()
    {
        WorkspaceFallbackDecision d;
        try
        {
            d = WorkspaceFallback.ForUser(_username);
        }
        catch (Exception ex)
        {
            Log.Error("fallback check", ex);
            return;
        }

        if (d == _current) return;
        var before = _current;
        _current = d;
        Log.Write($"Fallback switch: panelOff={d.PanelOff} altTabOff={d.AltTabOff} keepTsplus={d.HideTsplusOff} source={d.Source ?? "-"} reason={d.Reason ?? "-"}");

        if (d.PanelOff)
        {
            _timer.Stop();
            PanelOff?.Invoke(d);
            return;
        }

        if (d.AltTabOff != before.AltTabOff) AltTabChanged?.Invoke(!d.AltTabOff, d.AltTabOff ? $"fallback switch ({d.Source})" : "fallback switch cleared");
        if (d.HideTsplusOff != before.HideTsplusOff) KeepTsplusChanged?.Invoke(d.HideTsplusOff);
    }

    public void Dispose() => _timer.Stop();
}
