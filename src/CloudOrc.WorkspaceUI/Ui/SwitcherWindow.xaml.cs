using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// Native-like visual switcher (RW-08): every open window of this session, multiple Tally
/// instances shown separately. A companion to Windows Alt+Tab, never a replacement.
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

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SwitchToSelected();
    }

    private void OnListClick(object sender, MouseButtonEventArgs e) => SwitchToSelected();

    private void SwitchToSelected()
    {
        if (List.SelectedItem is not WindowVm w) return;
        Close();
        _c.FocusWindow(w.Handle);
    }
}
