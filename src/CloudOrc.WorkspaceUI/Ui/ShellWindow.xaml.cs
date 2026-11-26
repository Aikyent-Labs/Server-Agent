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
    private readonly DispatcherTimer _handleDwell = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, Button> _railButtons = [];
    private HotKeys? _hotKeys;
    private IntPtr _hwnd;
    private PanelMode _mode = PanelMode.Handle;

    /// <summary>
    /// The icon rail stays visible at the screen edge by default (like the TSPlus floating panel); the
    /// user can hide it to a slim edge handle with the arrow and bring it back by clicking the handle.
    /// Remembered per Windows user.
    /// </summary>
    private bool _railDocked = RailPreference.LoadDocked();

    /// <summary>Where the panel rests when not expanded: the docked rail, or only the edge handle.</summary>
    private PanelMode RestMode => _railDocked ? PanelMode.Rail : PanelMode.Handle;
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
        // Hidden to the edge: pointing at the edge handle (a short dwell, so brushing past does nothing) or
        // one click brings the rail back, and it stays. Hiding again is one click on the rail arrow.
        _handleDwell.Tick += (_, _) => { _handleDwell.Stop(); if (_mode == PanelMode.Handle && Handle.IsMouseOver) DockRail(true); };
        Handle.MouseEnter += (_, _) => { if (_mode == PanelMode.Handle) _handleDwell.Start(); };
        Handle.MouseLeave += (_, _) => _handleDwell.Stop();
        Handle.MouseLeftButtonDown += (_, e) => { _handleDwell.Stop(); DockRail(true); e.Handled = true; };
        SizeChanged += (_, _) => QueueShape();
        Rail.SizeChanged += (_, _) => QueueShape();
        Handle.SizeChanged += (_, _) => QueueShape();
        MouseEnter += (_, _) => _leftAt = DateTime.MaxValue;
        MouseLeave += (_, _) => _leftAt = DateTime.UtcNow;
        PreviewKeyDown += OnPreviewKeyDown;
        _hideTimer.Tick += (_, _) =>
        {
            KeepPlacedAndOnTop();
            CheckAutoHide();
        };
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
        UpdateRailToggle();
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
                if (_mode == PanelMode.Expanded) SetMode(RestMode);
                else Open(_tab);
            }
            else if (id == HotKeys.SwitcherId)
            {
                OpenSwitcher();
            }
        };
        HwndSource.FromHwnd(_hwnd).AddHook(OnPanelCommand);
        InstallAltTab();
        _hideTimer.Start();
        SetMode(RestMode);
        ShowAppsOnceForLauncher();
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
            else if (cmd == "close") SetMode(RestMode);
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
        // Rounded look: the rail and the open panel are two rounded cards (all corners).
        Rail.CornerRadius = new CornerRadius(18);
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
        QueueShape();
    }

    private Rect _placedFor = Rect.Empty;
    private DateTime _lastOnTop = DateTime.MinValue;

    /// <summary>
    /// Every timer tick: follow the screen when its size changes (a RemoteApp / web session gets the
    /// user's real screen size only after sign-in, which left the rail in the middle), and every 2 s put
    /// the panel back on top of windows TSPlus creates later.
    /// </summary>
    /// <summary>
    /// The live work area straight from Windows (in DIPs). WPF's SystemParameters.WorkArea is cached and is
    /// not always refreshed when a RemoteApp / web session takes the client's screen size after sign-in.
    /// </summary>
    private Rect CurrentWorkArea()
    {
        try
        {
            if (SystemParametersInfo(0x0030 /* SPI_GETWORKAREA */, 0, out var r, 0))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
            }
        }
        catch
        {
            // fall back to WPF's value
        }

        return SystemParameters.WorkArea;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(int action, int param, out NativeRect rect, int winIni);

    private void KeepPlacedAndOnTop()
    {
        var area = CurrentWorkArea();
        if (area != _placedFor) Reposition();
        if (DateTime.UtcNow - _lastOnTop >= TimeSpan.FromSeconds(2))
        {
            _lastOnTop = DateTime.UtcNow;
            WindowStyles.KeepOnTop(_hwnd);
            // Our own pop-ups (switcher, consent, assistance banner) must stay above the panel.
            foreach (Window w in Application.Current.Windows)
            {
                if (ReferenceEquals(w, this) || !w.IsVisible) continue;
                WindowStyles.KeepOnTop(new WindowInteropHelper(w).Handle);
            }
        }
    }

    private void Reposition()
    {
        _placedFor = CurrentWorkArea();
        var rect = PanelLayout.Compute(_placedFor, _mode, _leftSide, _railButtons.Count);
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
        if (_mode == RestMode || _mode == PanelMode.Handle) return;
        // The open panel stays open until the user closes it (X, Esc, the rail arrow or the hotkey).
        if (_mode == PanelMode.Expanded) return;
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
            SetMode(RestMode);
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
            SetMode(RestMode);
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
            // Users & Access is not shown in the panel (owner decision 26 Nov 2026): the panel shows only the
            // signed-in user's own details; other users are managed in the CloudOrc portal.
        }
        if (tab == "support") _ = LoadTicketCategoriesAsync();
        UpdateRailSelection();
    }

    // ------------------------------------------------------------------ controller -> view

    private bool _launcherShown;

    /// <summary>
    /// Launcher users (.connect / web / Floating Panel) used to see their apps straight away in the TSPlus
    /// panel: show the Apps list once after sign-in, without taking focus; it folds back to the rail on
    /// its own when the user does not use it.
    /// </summary>
    private void ShowAppsOnceForLauncher()
    {
        // Open by default at sign-in (every user) and stay open until the user closes it. A launcher
        // user (.connect / web / Floating Panel) waits until their apps are known, so the list is not empty.
        if (_launcherShown || _hwnd == IntPtr.Zero || (_c.IsSeamless && !_c.HasPublishedApps)) return;
        _launcherShown = true;
        SelectTab("apps");
        SetMode(PanelMode.Expanded, activate: false);
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceController.HasPublishedApps) or nameof(WorkspaceController.ShowLauncher)) ShowAppsOnceForLauncher();
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

    private void OnCollapseClick(object sender, RoutedEventArgs e) => SetMode(RestMode);

    /// <summary>Clip the open panel's content to its rounded corners (the header/list backgrounds are square).</summary>
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Panel.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 18, 18);
        QueueShape();
    }

    private bool _shapeQueued;

    private void QueueShape()
    {
        if (_shapeQueued || _hwnd == IntPtr.Zero) return;
        _shapeQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _shapeQueued = false; ApplyWindowShape(); });
    }

    /// <summary>
    /// The window's own shape = the visible rounded cards (handle / rail / open panel). A RemoteApp (.connect)
    /// or web session does not draw a transparent window's corners: without a window region they show black
    /// (or only turn round much later). The region makes the corners round from the first frame everywhere.
    /// </summary>
    private void ApplyWindowShape()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            IntPtr shape = IntPtr.Zero;
            void Add(FrameworkElement el, double radius)
            {
                if (el.Visibility != Visibility.Visible || el.ActualWidth < 1 || el.ActualHeight < 1) return;
                var r = el.TransformToAncestor(this).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                var part = CreateRoundRectRgn(
                    (int)Math.Floor(r.Left * dpi.DpiScaleX), (int)Math.Floor(r.Top * dpi.DpiScaleY),
                    (int)Math.Ceiling(r.Right * dpi.DpiScaleX) + 1, (int)Math.Ceiling(r.Bottom * dpi.DpiScaleY) + 1,
                    (int)Math.Round(radius * 2 * dpi.DpiScaleX), (int)Math.Round(radius * 2 * dpi.DpiScaleY));
                if (shape == IntPtr.Zero) { shape = part; return; }
                CombineRgn(shape, shape, part, 2 /* RGN_OR */);
                DeleteObject(part);
            }

            Add(Handle, 6);
            Add(Rail, 18);
            Add(Panel, 18);
            if (shape == IntPtr.Zero) return;
            // The system owns the region after SetWindowRgn (do not delete it).
            if (SetWindowRgn(_hwnd, shape, true) == 0) DeleteObject(shape);
        }
        catch (Exception ex)
        {
            Log.Error("window shape", ex);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    /// <summary>
    /// Rail arrow: docked → "›" hides the rail to the edge; hidden (rail only shown while hovering) →
    /// "‹" keeps it visible at the edge again.
    /// </summary>
    private void OnHideRailClick(object sender, RoutedEventArgs e) => DockRail(false);

    /// <summary>The arrow always hides to the edge in one click (points to the edge); the edge handle brings it back.</summary>
    private void UpdateRailToggle()
    {
        HideRailGlyph.Text = _leftSide ? "" : ""; // chevron towards the screen edge
        HideRailButton.ToolTip = "Hide to the edge (click the edge to show it again)";
        System.Windows.Automation.AutomationProperties.SetName(HideRailButton, "Hide to edge");
    }

    /// <summary>Show the rail permanently at the edge (true) or hide it to the edge handle (false).</summary>
    private void DockRail(bool docked)
    {
        _railDocked = docked;
        RailPreference.SaveDocked(docked);
        UpdateRailToggle();
        SetMode(RestMode);
    }

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

    /// <summary>Seamless launcher card: focus the app when exactly one window is open, else open it.</summary>
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

        SetMode(RestMode);
    }

    private void OnSwitcherClick(object sender, RoutedEventArgs e) => OpenSwitcher();

    private AltTabHook? _altTab;
    private bool _altTabWanted = true;
    private bool _altTabBroken;

    /// <summary>Alt+Tab drives the CloudOrc switcher (hold Alt, Tab / Shift+Tab to move, release Alt to switch).</summary>
    private void InstallAltTab()
    {
        if (_altTab is not null || !_altTabWanted || _altTabBroken) return;
        var hook = new AltTabHook(Dispatcher);
        if (!hook.Installed) { hook.Dispose(); return; }
        _altTab = hook;
        Log.Write("Alt+Tab opens the CloudOrc switcher in this session.");
        hook.Step += forward => Guard("step", () =>
        {
            if (_switcher is not { IsVisible: true })
            {
                OpenSwitcher(); // pre-selects the window after the current one
                if (_switcher is not { IsVisible: true }) throw new InvalidOperationException("switcher did not open");
                if (!forward) { _switcher.Step(false); _switcher.Step(false); }
                return;
            }

            _switcher.Step(forward);
        });
        hook.Commit += () => Guard("commit", () => { if (_switcher is { IsVisible: true }) _switcher.Commit(); });
        hook.Cancel += () => Guard("cancel", () => _switcher?.Close());
        hook.Failed += () => DisableAltTab("the panel did not respond in time", broken: true);
    }

    /// <summary>Any error in our Alt+Tab handling gives Alt+Tab back to Windows for the rest of the session.</summary>
    private void Guard(string what, Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            Log.Error($"Alt+Tab {what}", ex);
            DisableAltTab($"error in {what}", broken: true);
        }
    }

    private void DisableAltTab(string why, bool broken = false)
    {
        _altTabBroken |= broken;
        if (_altTab is null) return;
        _altTab.Dispose();
        _altTab = null;
        Log.Write($"Alt+Tab is the Windows switcher again ({why}).");
    }

    /// <summary>Fallback switch: Alt+Tab off = Windows switcher; back on = CloudOrc switcher (unless it failed this session).</summary>
    public void SetAltTabEnabled(bool enabled, string why)
    {
        _altTabWanted = enabled;
        if (enabled) InstallAltTab();
        else DisableAltTab(why);
    }

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
        WindowStyles.KeepOnTop(new WindowInteropHelper(_switcher).Handle); // above the panel
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
        UpdateRailToggle();
        QueueShape();
        // Rail sits against the screen edge; the panel opens towards the screen centre.
        ColA.Width = _leftSide ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ColB.Width = _leftSide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumn(Rail, _leftSide ? 0 : 1);
        Grid.SetColumn(Handle, _leftSide ? 0 : 1);
        Grid.SetColumn(Panel, _leftSide ? 1 : 0);
        Handle.HorizontalAlignment = _leftSide ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Handle.CornerRadius = _leftSide ? new CornerRadius(0, 6, 6, 0) : new CornerRadius(6, 0, 0, 6);
        // Two separate rounded cards (all corners), with a small gap between the panel and the rail.
        Rail.CornerRadius = new CornerRadius(18);
        Panel.CornerRadius = new CornerRadius(18);
        Panel.BorderThickness = new Thickness(1);
        Panel.Margin = _leftSide ? new Thickness(6, 0, 0, 0) : new Thickness(0, 0, 6, 0);
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
            _closeAllowed = true;
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

    private bool _closeAllowed;

    /// <summary>The app itself closes the panel (workspace disabled, self-restart, user's own "hide for session").</summary>
    public void CloseForApp()
    {
        _closeAllowed = true;
        Close();
    }

    /// <summary>
    /// Alt+F4 / a stray close only collapses the panel to its edge handle. In a seamless (RemoteApp) session
    /// the panel is the user's launcher, and closing the last RemoteApp window would end the session.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeAllowed && _c.IsSeamless)
        {
            e.Cancel = true;
            SetMode(RestMode);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        _altTab?.Dispose();
        _hotKeys?.Dispose();
        _switcher?.Close();
        _consent?.Close();
        _banner?.Close();
        base.OnClosed(e);
    }
}
