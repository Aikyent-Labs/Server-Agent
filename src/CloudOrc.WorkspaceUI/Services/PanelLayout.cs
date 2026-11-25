using System.Windows;

namespace CloudOrc.WorkspaceUI.Services;

public enum PanelMode
{
    /// <summary>Idle: only a slim edge handle is visible.</summary>
    Handle,

    /// <summary>Icon rail (hover / keyboard).</summary>
    Rail,

    /// <summary>Rail + one contextual panel.</summary>
    Expanded
}

/// <summary>Pure placement maths (unit-tested): always inside the monitor work area, never over the taskbar (RW-01).</summary>
public static class PanelLayout
{
    public const double HandleWidth = 10;
    public const double HandleHeight = 96;
    public const double RailWidth = 64;
    public const double PanelWidth = 380;
    public const double Margin = 8;
    public const double MaxExpandedHeight = 760;

    public static double RailHeight(int itemCount) => 64 + itemCount * 54 + 12;

    public static Rect Compute(Rect workArea, PanelMode mode, bool leftSide, int railItemCount)
    {
        double width, height;
        switch (mode)
        {
            case PanelMode.Handle:
                width = HandleWidth;
                height = HandleHeight;
                break;
            case PanelMode.Rail:
                width = RailWidth;
                height = RailHeight(railItemCount);
                break;
            default:
                width = RailWidth + PanelWidth;
                height = Math.Max(RailHeight(railItemCount), Math.Min(MaxExpandedHeight, workArea.Height - 2 * Margin));
                break;
        }

        height = Math.Min(height, Math.Max(HandleHeight, workArea.Height - 2 * Margin));
        width = Math.Min(width, workArea.Width);
        var left = leftSide ? workArea.Left : workArea.Right - width;
        var top = workArea.Top + Math.Max(Margin, (workArea.Height - height) / 2);
        if (top + height > workArea.Bottom - Margin) top = Math.Max(workArea.Top, workArea.Bottom - Margin - height);
        return new Rect(left, top, width, height);
    }
}

/// <summary>Pure auto-hide rule (§41, RW-01): collapse only when nothing important is open.</summary>
public static class AutoHidePolicy
{
    public static bool ShouldCollapse(bool pinned, bool mouseOver, bool keyboardFocusInside, bool modalOpen, bool criticalAlertShowing, bool remoteAssistanceActive, TimeSpan idleFor, TimeSpan delay) =>
        !pinned && !mouseOver && !keyboardFocusInside && !modalOpen && !criticalAlertShowing && !remoteAssistanceActive && idleFor >= delay;
}

/// <summary>Human-friendly relative time ("2 h ago").</summary>
public static class TimeText
{
    public static string Ago(DateTimeOffset? at, DateTimeOffset? now = null)
    {
        if (at is null) return "never";
        var d = (now ?? DateTimeOffset.UtcNow) - at.Value;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }
}
