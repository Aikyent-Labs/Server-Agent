using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// The CloudOrc edge layer (RW-01): hidden edge handle -&gt; icon rail (hover) -&gt; expanded panel
/// (click) -&gt; pinned. Topmost tool window: no taskbar button, no Alt+Tab entry, and it never takes
/// focus from Tally unless the user opens it. Placement stays inside the work area (never over
/// the taskbar).
/// </summary>
public partial class ShellWindow : Window
{
    private static readonly Dictionary<string, (string Glyph, string Label)> RailDefs = new()
    {
        ["apps"] = ("", "Apps"),
        ["backup"] = ("", "Backup"),
        ["files"] = ("", "Files"),
        ["clipboard"] = ("", "Clipboard"),
        ["print"] = ("", "Print"),
        ["ai"] = ("", "AI"),
        ["support"] = ("", "Support"),
        ["alerts"] = ("", "Alerts"),
        ["more"] = ("", "More"),
    };

    private readonly WorkspaceController _c;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, Button> _railButtons = [];
    private HotKeys? _hotKeys;
    private IntPtr _hwnd;
    private PanelMode _mode = PanelMode.Handle;
    private string _tab = "apps";
    private DateTime _leftAt = DateTime.UtcNow;
    private DateTime? _criticalUntil;
    private bool _pinned;
    private bool _leftSide;
    private bool _loadingPrefs;
    private Action? _confirmAction;
    private SwitcherWindow? _switcher;

    public ShellWindow(WorkspaceController controller)
    {
        InitializeComponent();
        _c = controller;
        DataContext = controller;

        SourceInitialized += OnSourceInitialized;
        Handle.MouseEnter += (_, _) => { if (_mode == PanelMode.Handle) SetMode(PanelMode.Rail); };
        Handle.MouseLeftButtonUp += (_, _) => Open(_tab);
        MouseEnter += (_, _) => _leftAt = DateTime.MaxValue;
        MouseLeave += (_, _) => _leftAt = DateTime.UtcNow;
        PreviewKeyDown += OnPreviewKeyDown;
        _hideTimer.Tick += (_, _) => CheckAutoHide();
        _toastTimer.Tick += (_, _) => { ToastBar.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        _c.PropertyChanged += OnControllerChanged;
        _c.Toast += ShowToast;
        _c.CriticalAlert += alert =>
        {
            // Material notification: reveal the panel WITHOUT taking focus from the user's app.
            _criticalUntil = DateTime.UtcNow.AddSeconds(20);
            SelectTab("alerts");
            SetMode(PanelMode.Expanded, activate: false);
            ShowToast(alert.Title ?? "New alert");
        };

        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(Reposition);
        };

        BuildRail();
        UpdateCapabilities();
        UpdateConnectionDot();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowStyles.MakeToolWindow(_hwnd);
        WindowStyles.SetNoActivate(_hwnd, true);
        _hotKeys = new HotKeys(_hwnd);
        _hotKeys.Pressed += id =>
        {
            if (id == HotKeys.TogglePanelId)
            {
                if (_mode == PanelMode.Expanded) SetMode(PanelMode.Handle);
                else Open(_tab);
            }
            else if (id == HotKeys.SwitcherId)
            {
                OpenSwitcher();
            }
        };
        HwndSource.FromHwnd(_hwnd).AddHook(OnPanelCommand);
        _hideTimer.Start();
        SetMode(PanelMode.Handle);
    }

    /// <summary>"open:&lt;tab&gt;" | "close" | "switcher" from a same-session shortcut (see PanelCommand).</summary>
    private IntPtr OnPanelCommand(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != PanelCommand.WM_COPYDATA) return IntPtr.Zero;
        var cmd = PanelCommand.Decode(lParam);
        if (cmd is null) return IntPtr.Zero;
        handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (cmd.StartsWith("open:", StringComparison.Ordinal))
            {
                var tab = cmd[5..];
                Open(_railButtons.ContainsKey(tab) ? tab : "apps");
                // A shortcut click is a user gesture: give the user time to reach the panel before auto-hide.
                _leftAt = DateTime.UtcNow.AddSeconds(10);
            }
            else if (cmd.StartsWith("snapshot:", StringComparison.Ordinal)) SaveSnapshot(cmd[9..]);
            else if (cmd == "close") SetMode(PanelMode.Handle);
            else if (cmd == "switcher") OpenSwitcher();
        });
        return new IntPtr(1);
    }

    /// <summary>
    /// Renders the panel itself to %LocalAppData%\CloudOrc\WorkspaceUI\snapshots\&lt;name&gt;.png — works even
    /// when the RDP window is minimized (no screen capture). For support and QA; name is sanitized.
    /// </summary>
    private void SaveSnapshot(string name)
    {
        try
        {
            var safe = new string((name ?? "panel").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (safe.Length == 0) safe = "panel";
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudOrc", "WorkspaceUI", "snapshots");
            Directory.CreateDirectory(dir);
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY);
            if (w <= 0 || h <= 0) return;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(Root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, safe + ".png"));
            enc.Save(fs);
            Log.Write($"Panel snapshot saved: {safe}.png");
        }
        catch (Exception ex)
        {
            Log.Error("snapshot", ex);
        }
    }

    // ------------------------------------------------------------------ states

    private void SetMode(PanelMode mode, bool activate = false)
    {
        _mode = mode;
        Handle.Visibility = mode == PanelMode.Handle ? Visibility.Visible : Visibility.Collapsed;
        Rail.Visibility = mode == PanelMode.Handle ? Visibility.Collapsed : Visibility.Visible;
        Panel.Visibility = mode == PanelMode.Expanded ? Visibility.Visible : Visibility.Collapsed;
        if (mode != PanelMode.Expanded) ConfirmOverlay.Visibility = Visibility.Collapsed;

        if (_hwnd != IntPtr.Zero)
        {
            // Only an explicit user gesture on the expanded panel may take focus.
            WindowStyles.SetNoActivate(_hwnd, !(mode == PanelMode.Expanded && activate));
        }

        Reposition();
        UpdateRailSelection();
        if (mode == PanelMode.Expanded && activate)
        {
            Activate();
            _railButtons.GetValueOrDefault(_tab)?.Focus();
        }

        _leftAt = IsMouseOver ? DateTime.MaxValue : DateTime.UtcNow;
    }

    private void Reposition()
    {
        var rect = PanelLayout.Compute(SystemParameters.WorkArea, _mode, _leftSide, _railButtons.Count);
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
        Rail.Height = _mode == PanelMode.Handle ? double.NaN : rect.Height;
    }

    private void Open(string tab)
    {
        SelectTab(tab);
        SetMode(PanelMode.Expanded, activate: true);
    }

    private void CheckAutoHide()
    {
        if (_mode == PanelMode.Handle) return;
        var critical = _criticalUntil is { } until && DateTime.UtcNow < until;
        var idle = _leftAt == DateTime.MaxValue ? TimeSpan.Zero : DateTime.UtcNow - _leftAt;
        var delay = TimeSpan.FromSeconds(_mode == PanelMode.Rail ? Math.Min(2, AutoHideSeconds) : AutoHideSeconds);
        if (AutoHidePolicy.ShouldCollapse(
                _pinned && _mode == PanelMode.Expanded,
                IsMouseOver,
                IsTyping(),
                ConfirmOverlay.Visibility == Visibility.Visible || OtpOverlay.Visibility == Visibility.Visible,
                critical,
                remoteAssistanceActive: false, // the red Stop bar is a separate always-visible window
                idle,
                delay))
        {
            SetMode(PanelMode.Handle);
        }
    }

    /// <summary>
    /// Keyboard focus blocks auto-hide only while the user is actually typing or choosing (text box,
    /// open drop-down) — not merely because a rail button kept focus after a click (RW-01).
    /// </summary>
    private bool IsTyping() =>
        IsActive && IsKeyboardFocusWithin && Keyboard.FocusedElement switch
        {
            TextBox tb => tb.Text.Length > 0 || tb.IsKeyboardFocused,
            ComboBox { IsDropDownOpen: true } => true,
            ComboBoxItem => true,
            _ => false
        };

    private int AutoHideSeconds => Math.Max(1, _c.Context?.Preferences.AutoHideSeconds ?? 4);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ConfirmOverlay.Visibility != Visibility.Visible && OtpOverlay.Visibility != Visibility.Visible)
        {
            SetMode(PanelMode.Handle);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ rail

    private void BuildRail()
    {
        RailItemsHost.Children.Clear();
        _railButtons.Clear();
        foreach (var item in _c.RailItems)
        {
            if (!RailDefs.TryGetValue(item, out var def)) continue;
            var glyph = new TextBlock { Text = def.Glyph, Style = (Style)FindResource("Icon"), FontSize = 17 };
            glyph.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            var label = new TextBlock { Text = def.Label, FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap };
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            var stack = new StackPanel();
            stack.Children.Add(glyph);
            stack.Children.Add(label);
            var content = new Grid();
            content.Children.Add(stack);
            if (item == "alerts")
            {
                var badge = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = (Brush)FindResource("Bad"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -4, 0) };
                badge.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(WorkspaceController.HasUnread)) { Converter = (System.Windows.Data.IValueConverter)FindResource("BoolToVis") });
                content.Children.Add(badge);
            }

            var button = new Button { Style = (Style)FindResource("RailButton"), Content = content, ToolTip = def.Label, Tag = item };
            System.Windows.Automation.AutomationProperties.SetName(button, def.Label);
            var key = item;
            button.Click += (_, _) =>
            {
                if (_mode == PanelMode.Expanded && _tab == key) SetMode(PanelMode.Rail);
                else Open(key);
            };
            _railButtons[item] = button;
            RailItemsHost.Children.Add(button);
        }

        if (!_railButtons.ContainsKey(_tab)) _tab = _railButtons.Keys.FirstOrDefault() ?? "more";
        Reposition();
    }

    private void UpdateRailSelection()
    {
        foreach (var (key, button) in _railButtons)
        {
            // The RailButton template uses Tag == "selected" for the highlighted state.
            button.Tag = _mode == PanelMode.Expanded && key == _tab ? "selected" : key;
        }
    }

    private void SelectTab(string tab)
    {
        _tab = tab;
        foreach (var (name, element) in new (string, FrameworkElement)[]
                 {
                     ("apps", TabApps), ("backup", TabBackup), ("files", TabFiles), ("clipboard", TabClipboard), ("print", TabPrint),
                     ("ai", TabAi), ("support", TabSupport), ("alerts", TabAlerts), ("more", TabMore)
                 })
        {
            element.Visibility = name == tab ? Visibility.Visible : Visibility.Collapsed;
        }

        if (tab == "backup") _ = _c.LoadBackupAsync();
        if (tab == "alerts") _ = _c.LoadAlertsAsync();
        if (tab == "apps") _c.RefreshWindows();
        if (tab == "more")
        {
            LoadPreferencesIntoUi();
            _ = LoadSecurityAsync();
            _ = LoadUsersAsync();
        }
        if (tab == "support") _ = LoadTicketCategoriesAsync();
        UpdateRailSelection();
    }

    // ------------------------------------------------------------------ controller -> view

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceController.RailItems):
                BuildRail();
                break;
            case nameof(WorkspaceController.Caps):
                UpdateCapabilities();
                break;
            case nameof(WorkspaceController.ConnectionState):
                UpdateConnectionDot();
                break;
            case nameof(WorkspaceController.Context):
                var p = _c.Context?.Preferences;
                if (p is not null)
                {
                    _pinned = p.PanelPinned;
                    _leftSide = p.PanelSide == "left";
                    UpdatePinGlyph();
                    ApplySide();
                }

                break;
        }
    }

    private void UpdateConnectionDot()
    {
        var brush = (Brush)FindResource(_c.ConnectionState switch
        {
            "connected" => "Ok",
            "degraded" or "connecting" => "Warn",
            _ => "Bad"
        });
        ConnDot.Fill = brush;
        HeaderDot.Fill = brush;
    }

    private void UpdateCapabilities()
    {
        var caps = _c.Caps;
        if (caps is null)
        {
            FilesTitle.Text = ClipTitle.Text = PrintTitle.Text = "Checking…";
            return;
        }

        var filesOk = caps.Files.State == "ALLOWED";
        FilesTitle.Text = filesOk ? "File transfer is allowed" : "File transfer is off";
        FilesDetail.Text = filesOk
            ? (caps.Files.DriveRedirection
                ? "Your own PC's drives appear under 'This PC' in this session. Copy files between them as usual."
                : "Copy and paste files between your PC and this workspace.")
            : caps.Files.Reason ?? "File transfer is turned off by your administrator.";
        FilesExtensions.Text = filesOk && caps.Files.FileCopyMode == "WHITELIST" && caps.Files.AllowedExtensions.Count > 0
            ? "Allowed file types: " + string.Join(", ", caps.Files.AllowedExtensions.Select(x => "." + x.TrimStart('.')))
            : string.Empty;
        Tone(FilesCard, filesOk ? "ok" : "bad");
        // "Send a file to my PC" only when the administrator allows file transfer (server re-checks anyway).
        SendFileButton.IsEnabled = filesOk;
        SendFileButton.ToolTip = filesOk ? null : "File transfer is turned off by your administrator.";

        (ClipTitle.Text, ClipDetail.Text) = caps.Clipboard.State switch
        {
            "ON" => ("Clipboard is on", "Copy and paste text and files between your PC and this workspace."),
            "BLOCKED" => ("Clipboard is off", caps.Clipboard.Reason ?? "Copy and paste is turned off by your administrator."),
            _ => ("Text only", caps.Clipboard.Reason ?? "You can copy and paste text. Copying files is controlled by your administrator.")
        };
        Tone(ClipCard, caps.Clipboard.State switch { "ON" => "ok", "BLOCKED" => "bad", _ => "warn" });

        var printOk = caps.Print.State == "READY";
        PrintTitle.Text = printOk ? "Printing is ready" : "Printing is off";
        PrintDetail.Text = printOk
            ? "Print from your app as usual. Your local printers appear with '(redirected)' in their name."
            : caps.Print.Reason ?? "Printing is turned off for this workspace.";
        Tone(PrintCard, printOk ? "ok" : "bad");
    }

    private void Tone(Border card, string tone)
    {
        card.Background = (Brush)FindResource(tone switch { "ok" => "OkSoft", "warn" => "WarnSoft", "bad" => "BadSoft", _ => "PanelBg" });
    }

    private void ShowToast(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ToastText.Text = message;
            ToastBar.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    // ------------------------------------------------------------------ header

    private void OnLogoClick(object sender, RoutedEventArgs e) => Open("apps");

    private void OnCollapseClick(object sender, RoutedEventArgs e) => SetMode(PanelMode.Handle);

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        UpdatePinGlyph();
        _ = _c.SavePreferencesAsync(new { panelPinned = _pinned });
    }

    private void UpdatePinGlyph()
    {
        PinButton.Content = _pinned ? "" : "";
        PinButton.ToolTip = _pinned ? "Unpin (auto-hide)" : "Pin panel";
        System.Windows.Automation.AutomationProperties.SetName(PinButton, _pinned ? "Unpin panel" : "Pin panel");
    }

    // ------------------------------------------------------------------ apps

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PublishedAppVm vm) return;
        var running = _c.RunningApps.FirstOrDefault(r => r.PublishedAppId == vm.App.Id);
        if (running is not null && running.Count == 1)
        {
            _c.FocusWindow(running.FirstHandle);
            return;
        }

        if (running is not null) running.Expanded = true;
        _c.Launch(vm.App);
    }

    private void OnFocusGroupClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RunningAppVm vm) return;
        if (vm.HasMany && !vm.Expanded)
        {
            vm.Expanded = true;
            return;
        }

        _c.FocusWindow(vm.FirstHandle);
    }

    private void OnExpandGroupClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RunningAppVm vm) vm.Expanded = !vm.Expanded;
    }

    private void OnFocusWindowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WindowVm w) _c.FocusWindow(w.Handle);
    }

    private void OnMinimizeWindowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WindowVm w) _c.MinimizeWindow(w.Handle);
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is WindowVm w) _c.CloseWindow(w.Handle);
    }

    private void OnShowDesktopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is not null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                shell.ToggleDesktop();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"show desktop: {ex.Message}");
        }

        SetMode(PanelMode.Handle);
    }

    private void OnSwitcherClick(object sender, RoutedEventArgs e) => OpenSwitcher();

    private void OpenSwitcher()
    {
        _c.RefreshWindows();
        if (_switcher is { IsVisible: true })
        {
            _switcher.Activate();
            return;
        }

        _switcher = new SwitcherWindow(_c);
        _switcher.Show();
        _switcher.Activate();
    }

    // ------------------------------------------------------------------ backup

    private async void OnRunBackupClick(object sender, RoutedEventArgs e) => await _c.RunBackupAsync();

    private async void OnRefreshBackupClick(object sender, RoutedEventArgs e) => await _c.LoadBackupAsync();

    // ------------------------------------------------------------------ AI

    private async void OnAskClick(object sender, RoutedEventArgs e) => await AskAsync();

    private async void OnAiKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AskAsync();
        }
    }

    private async void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string q })
        {
            AiInput.Text = q;
            await AskAsync();
        }
    }

    private async Task AskAsync()
    {
        var q = AiInput.Text;
        if (string.IsNullOrWhiteSpace(q)) return;
        AiInput.Text = string.Empty;
        await _c.AskAiAsync(q);
    }

    // ------------------------------------------------------------------ support

    private async void OnSendTicketClick(object sender, RoutedEventArgs e)
    {
        var subject = TicketSubject.Text.Trim();
        var body = TicketBody.Text.Trim();
        if (subject.Length == 0 || body.Length == 0)
        {
            ShowToast("Please enter a subject and describe the problem.");
            return;
        }

        var priority = (TicketPriority.SelectedItem as ComboBoxItem)?.Tag as string ?? "medium";
        var category = TicketCategory.SelectedItem as SupportCategory;
        var sub = TicketSubcategoryRow.Visibility == Visibility.Visible ? TicketSubcategory.SelectedItem as SupportCategory : null;
        if (category is not null && category.Subcategories.Count > 0 && sub is null)
        {
            ShowToast("Please choose the details for this topic.");
            return;
        }

        var id = await _c.CreateTicketAsync(subject, body, priority, category?.Code, sub?.Code);
        if (id is null) return;
        TicketSubject.Text = TicketBody.Text = string.Empty;
        TicketDoneText.Text = $"Ticket {id} sent to {_c.SupportOrganization}. You'll get updates by email.";
        TicketDone.Visibility = Visibility.Visible;
    }

    private async Task LoadTicketCategoriesAsync()
    {
        if (TicketCategory.Items.Count > 0) return;
        var cats = await _c.GetSupportCategoriesAsync();
        if (cats is null || cats.Categories.Count == 0) return;
        TicketCategory.ItemsSource = cats.Categories;
        TicketCategory.SelectedItem = cats.Categories.FirstOrDefault(c => c.Code == cats.DefaultCategory) ?? cats.Categories[0];
    }

    private void OnTicketCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        var subs = (TicketCategory.SelectedItem as SupportCategory)?.Subcategories ?? [];
        TicketSubcategory.ItemsSource = subs;
        TicketSubcategory.SelectedItem = null;
        TicketSubcategoryRow.Visibility = subs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ alerts

    private async void OnMarkReadClick(object sender, RoutedEventArgs e) => await _c.MarkAllAlertsReadAsync();

    // ------------------------------------------------------------------ more

    private void LoadPreferencesIntoUi()
    {
        _loadingPrefs = true;
        SideLeft.IsChecked = _leftSide;
        SideRight.IsChecked = !_leftSide;
        var secs = AutoHideSeconds.ToString();
        AutoHideBox.SelectedItem = AutoHideBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == secs) ?? AutoHideBox.Items[1];
        _loadingPrefs = false;
    }

    private void OnSideChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPrefs) return;
        _leftSide = SideLeft.IsChecked == true;
        ApplySide();
        _ = _c.SavePreferencesAsync(new { panelSide = _leftSide ? "left" : "right" });
    }

    private void ApplySide()
    {
        // Rail sits against the screen edge; the panel opens towards the screen centre.
        ColA.Width = _leftSide ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ColB.Width = _leftSide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumn(Rail, _leftSide ? 0 : 1);
        Grid.SetColumn(Handle, _leftSide ? 0 : 1);
        Grid.SetColumn(Panel, _leftSide ? 1 : 0);
        Handle.HorizontalAlignment = _leftSide ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Handle.CornerRadius = _leftSide ? new CornerRadius(0, 6, 6, 0) : new CornerRadius(6, 0, 0, 6);
        Rail.CornerRadius = _leftSide ? new CornerRadius(0, 10, 10, 0) : new CornerRadius(10, 0, 0, 10);
        Panel.CornerRadius = _leftSide ? new CornerRadius(0, 10, 10, 0) : new CornerRadius(10, 0, 0, 10);
        Panel.BorderThickness = _leftSide ? new Thickness(0, 1, 1, 1) : new Thickness(1, 1, 0, 1);
        Panel.Margin = _leftSide ? new Thickness(-1, 0, 0, 0) : new Thickness(0, 0, -1, 0);
        Reposition();
    }

    private void OnAutoHideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPrefs || AutoHideBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !int.TryParse(tag, out var secs)) return;
        if (_c.Context is not null) _c.Context.Preferences.AutoHideSeconds = secs;
        _ = _c.SavePreferencesAsync(new { autoHideSeconds = secs });
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        Confirm("Disconnect?", "Your apps keep running on the server. Reconnect later to continue where you left off.", "Disconnect", () =>
        {
            if (!SessionActions.Disconnect()) ShowToast("Could not disconnect. Close the Remote Desktop window instead.");
        });
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var open = _c.RunningApps.Sum(r => r.Count);
        var warning = open > 0
            ? $"{open} app window(s) are open. Signing out closes them — save your work first (for example in Tally)."
            : "This closes your session on the server.";
        Confirm("Sign out of the remote session?", warning, "Sign out", () =>
        {
            _c.MarkUserExit();
            if (!SessionActions.SignOut()) ShowToast("Could not sign out. Use Start → Sign out.");
        });
    }

    private void OnHideForSessionClick(object sender, RoutedEventArgs e)
    {
        Confirm("Hide the panel?", "The CloudOrc panel stays hidden until you sign in again. Your apps are not affected.", "Hide panel", () =>
        {
            _c.MarkUserExit();
            Application.Current.Shutdown(0);
        });
    }

    private void Confirm(string title, string text, string ok, Action action)
    {
        ConfirmTitle.Text = title;
        ConfirmText.Text = text;
        ConfirmOk.Content = ok;
        _confirmAction = action;
        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmOk.Focus();
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _confirmAction = null;
    }

    private void OnConfirmOk(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        var action = _confirmAction;
        _confirmAction = null;
        action?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        _hotKeys?.Dispose();
        _switcher?.Close();
        _consent?.Close();
        _banner?.Close();
        base.OnClosed(e);
    }
}
