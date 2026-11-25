using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CloudOrc.Agent.Contracts.Workspace;
using CloudOrc.WorkspaceUI.Services;
using CloudOrc.WorkspaceUI.Ui;

namespace CloudOrc.WorkspaceUI;

/// <summary>
/// Entry point. One panel per Windows session (Local\ mutex). Any unhandled error logs and exits
/// with code 1 so CloudOrc Watch restarts the panel with backoff; the user's apps are never
/// affected (handoff §31, §48).
///
/// Arguments: --started-by=watch (restart; skips DIRECT_APP auto-launch), --force (show even if the
/// workspace is not enabled for this server — support/testing), --pipe=NAME (test pipe).
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private WorkspaceController? _controller;
    private AssistanceManager? _assistance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Prune();

        // One-shot from CloudOrc Watch after a crash loop: only make the TSPlus panel visible again, no UI.
        if (e.Args.Any(a => a.Equals("--recover-tsplus", StringComparison.OrdinalIgnoreCase)))
        {
            try { TsplusPanelHider.Instance.RecoverOrphans(); } catch (Exception ex) { Log.Error("tsplus recover (watch)", ex); }
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crash("domain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("unobserved task", args.Exception); args.SetObserved(); };

        var startedByWatch = e.Args.Any(a => a.Equals("--started-by=watch", StringComparison.OrdinalIgnoreCase));
        var force = e.Args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        var pipeName = e.Args.FirstOrDefault(a => a.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
                       ?? WorkspaceProtocol.PipeName;

        // Self-restart: wait for the previous instance to exit before taking the session mutex.
        var waitPidArg = e.Args.FirstOrDefault(a => a.StartsWith("--wait-pid=", StringComparison.OrdinalIgnoreCase));
        if (waitPidArg is not null && int.TryParse(waitPidArg.Split('=', 2)[1], out var waitPid))
        {
            try { System.Diagnostics.Process.GetProcessById(waitPid).WaitForExit(15000); }
            catch (ArgumentException) { /* already gone */ }
        }

        startedByWatch |= e.Args.Any(a => a.Equals("--started-by=restart", StringComparison.OrdinalIgnoreCase));
        var openArg = e.Args.FirstOrDefault(a => a.StartsWith("--open", StringComparison.OrdinalIgnoreCase));
        _mutex = new Mutex(true, @"Local\CloudOrc.WorkspaceUI", out var createdNew);
        if (!createdNew)
        {
            // A Start-menu / desktop shortcut (or "--open=apps") asks the running panel to open.
            var snapArg = e.Args.FirstOrDefault(a => a.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase));
            if (snapArg is not null)
            {
                PanelCommand.SendToRunningPanel("snapshot:" + snapArg.Split('=', 2)[1]);
            }
            else if (openArg is not null)
            {
                var tab = openArg.Contains('=') ? openArg.Split('=', 2)[1] : "apps";
                Log.Write($"Forwarding open request '{tab}' to the running panel.");
                PanelCommand.SendToRunningPanel("open:" + tab);
            }
            else
            {
                Log.Write("Another panel is already running in this session; exiting.");
            }

            Shutdown(0);
            return;
        }

        // The panel is for the customer's Windows users only. Server administrators (local Administrators
        // group: Administrator, domain/support admins) never get it; --force still opens it for support/testing.
        if (!force && SessionActions.IsServerAdministrator())
        {
            Log.Write($"WorkspaceUI not shown: '{Environment.UserName}' is a server administrator (panel is for customer users only).");
            new SessionStateFile(SessionActions.CurrentSessionId, Environment.UserName).Write(WorkspaceSessionState.StateDisabled);
            Shutdown(0);
            return;
        }

        if (SystemParameters.HighContrast) ApplyHighContrast();

        // A previous panel that was killed may have left the TSPlus panel transparent: put it back
        // first; the current policy re-applies within seconds if hiding is still wanted.
        try { TsplusPanelHider.Instance.RecoverOrphans(); } catch (Exception ex) { Log.Error("tsplus recover", ex); }

        var sessionId = SessionActions.CurrentSessionId;
        Log.Write($"WorkspaceUI {AppInfo.Version} starting in session {sessionId} (startedByWatch={startedByWatch}, force={force}).");

        var pipe = new AgentPipeClient(pipeName);
        var api = new WorkspaceApi(pipe);
        var controller = new WorkspaceController(pipe, api, new WindowTracker(), new SessionStateFile(sessionId, Environment.UserName), Dispatcher);
        _controller = controller;

        var shell = new ShellWindow(controller);
        controller.WorkspaceUnavailable += reason =>
        {
            Log.Write($"Workspace panel not enabled for this user/server ({reason}); exiting quietly.");
            controller.MarkDisabled();
            shell.CloseForApp();
            Shutdown(0);
        };
        controller.RestartRequested += () => Dispatcher.BeginInvoke(() => RestartSelf(shell));
        shell.Show();
        _assistance = new AssistanceManager(api, Dispatcher);
        shell.AttachAssistance(_assistance);
        _ = controller.StartAsync(startedByWatch, force);
    }

    /// <summary>Clean self-restart (workspace.ui.restart): start the new instance, then exit; the apps are untouched.</summary>
    private void RestartSelf(ShellWindow shell)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--started-by=restart --wait-pid={Environment.ProcessId}") { UseShellExecute = false });
            Log.Write("Restarting panel on CloudOrc request.");
        }
        catch (Exception ex)
        {
            // Could not start the new instance: exit non-zero so the Watch restarts us instead.
            Log.Error("self-restart", ex);
            Environment.Exit(1);
        }

        shell.CloseForApp();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _assistance?.Dispose();
            _controller?.Dispose();
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // ignore during shutdown
        }

        Log.Write($"WorkspaceUI exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Crash("dispatcher", e.Exception);
    }

    private static void Crash(string where, Exception? ex)
    {
        Log.Write($"FATAL ({where}): {ex}");
        // Never leave the user without the TSPlus panel because ours crashed.
        try { TsplusPanelHider.Instance.RestoreAll(); } catch { /* best effort */ }
        // Exit non-zero without a dialog: Watch restarts us with bounded backoff (§48).
        Environment.Exit(1);
    }

    /// <summary>High Contrast: use the system palette instead of the brand palette (accessibility).</summary>
    private void ApplyHighContrast()
    {
        Resources["RailBg"] = SystemColors.WindowBrush;
        Resources["RailFg"] = SystemColors.WindowTextBrush;
        Resources["RailHover"] = SystemColors.HighlightBrush;
        Resources["RailSelected"] = SystemColors.HighlightBrush;
        Resources["PanelBg"] = SystemColors.WindowBrush;
        Resources["SurfaceBg"] = SystemColors.WindowBrush;
        Resources["Fg"] = SystemColors.WindowTextBrush;
        Resources["Muted"] = SystemColors.GrayTextBrush;
        Resources["Border"] = SystemColors.WindowTextBrush;
        Resources["Accent"] = SystemColors.HighlightBrush;
        Resources["AccentSoft"] = SystemColors.WindowBrush;
        Resources["OkSoft"] = SystemColors.WindowBrush;
        Resources["WarnSoft"] = SystemColors.WindowBrush;
        Resources["BadSoft"] = SystemColors.WindowBrush;
    }
}
