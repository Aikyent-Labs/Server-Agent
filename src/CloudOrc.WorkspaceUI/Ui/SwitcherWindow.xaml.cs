using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// Native-like visual switcher (RW-08): every open window of this session, multiple Tally
/// instances shown separately. Opened from the Apps tab, Ctrl+Alt+Shift+A, or Alt+Tab (see AltTabHook).
/// </summary>
public partial class SwitcherWindow : Window
{
    private readonly WorkspaceController _c;

    public SwitcherWindow(WorkspaceController controller)
    {
        InitializeComponent();
        _c = controller;
        var items = controller.RunningApps.SelectMany(g => g.Windows).ToList();
        List.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Pre-select the window after the current one (like Alt+Tab).
        var activeIndex = items.FindIndex(w => w.State == "Active");
        List.SelectedIndex = items.Count == 0 ? -1 : (activeIndex + 1) % items.Count;

        SourceInitialized += (_, _) => WindowStyles.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            if (List.SelectedIndex >= 0 && List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) is UIElement el) el.Focus();
            else List.Focus();
        };
        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
    }

    /// <summary>Alt+Tab / Alt+Shift+Tab while the switcher is open: next / previous window (wraps).</summary>
    public void Step(bool forward)
    {
        var n = List.Items.Count;
        if (n == 0) return;
        var i = List.SelectedIndex < 0 ? 0 : List.SelectedIndex;
        List.SelectedIndex = ((i + (forward ? 1 : -1)) % n + n) % n;
        List.ScrollIntoView(List.SelectedItem);
    }

    /// <summary>Alt released: switch to the selected window.</summary>
    public void Commit() => SwitchToSelected();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SwitchToSelected();
    }

    private void OnListClick(object sender, MouseButtonEventArgs e) => SwitchToSelected();

    private void SwitchToSelected()
    {
        if (List.SelectedItem is not WindowVm w) return;
        // Focus first, while this (foreground) window may still hand the foreground on; closing first
        // would give the foreground back to whatever Windows picks and SetForegroundWindow is refused.
        _c.FocusWindow(w.Handle);
        Close();
    }
}
