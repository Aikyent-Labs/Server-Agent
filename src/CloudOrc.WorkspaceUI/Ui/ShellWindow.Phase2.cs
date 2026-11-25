using System.Windows;
using CloudOrc.WorkspaceUI.Services;

namespace CloudOrc.WorkspaceUI.Ui;

/// <summary>
/// Phase 2 surfaces of the panel: Security card (RW-03), Users &amp; Access for customer admins (§28),
/// "Send a file to my PC" (§32) and consent-based Remote Assistance (RW-04/05).
/// </summary>
public partial class ShellWindow
{
    private AssistanceManager? _assist;
    private AssistanceBanner? _banner;
    private ConsentWindow? _consent;
    private Func<string, Task>? _otpAction;

    /// <summary>Wires Remote Assistance (created by App with the same API client).</summary>
    public void AttachAssistance(AssistanceManager manager)
    {
        _assist = manager;
        _banner = new AssistanceBanner(manager);
        manager.Message += ShowToast;
        manager.ConsentRequested += r => Dispatcher.BeginInvoke(() =>
        {
            _consent?.Close();
            _consent = new ConsentWindow(r, manager, _c.BrandName);
            _consent.Show();
            _consent.Activate(); // a consent prompt is a deliberate, user-facing security question
        });
        manager.BannerChanged += r => Dispatcher.BeginInvoke(() =>
        {
            if (r is null) _banner.HideBanner();
            else _banner.Show(r);
        });
        manager.Start();
    }

    private bool AssistanceActive => _assist?.IsAccessGranted == true;

    // ------------------------------------------------------------------ security (RW-03)

    private async Task LoadSecurityAsync()
    {
        try
        {
            var s = await _c.Api.GetSecurityAsync(CancellationToken.None);
            if (!s.Managed)
            {
                SecurityTitle.Text = "This Windows account is not managed by CloudOrc";
                SecurityMfa.Text = SecurityIp.Text = SecuritySignedIn.Text = string.Empty;
                return;
            }

            var mfaOk = !s.Mfa.Required || s.Mfa.Verified;
            SecurityTitle.Text = mfaOk ? "Secure" : "Two-step verification pending";
            SecurityTitle.Foreground = (System.Windows.Media.Brush)FindResource(mfaOk ? "Ok" : "Warn");
            SecurityMfa.Text = !s.Mfa.Required
                ? "Two-step verification: off for this account"
                : s.Mfa.Verified
                    ? $"Two-step verification: verified {TimeText.Ago(s.Mfa.VerifiedAt)}{(s.Mfa.RememberUntil is { } until ? $" · remembered until {until.ToLocalTime():dd MMM HH:mm}" : "")}"
                    : "Two-step verification: required — you will be asked for a code";
            SecurityIp.Text = s.IpPolicy is { Enabled: true } ip
                ? $"Network rule: on ({ip.AllowedNetworks} allowed network{(ip.AllowedNetworks == 1 ? "" : "s")})"
                : "Network rule: off";
            SecuritySignedIn.Text = s.SignedInAt is { } at ? $"Signed in: {at.ToLocalTime():dd MMM, HH:mm}" : string.Empty;
        }
        catch (WorkspaceApiException ex)
        {
            SecurityTitle.Text = "Security status is not available right now";
            SecurityMfa.Text = ex.Message;
        }
    }

    // ------------------------------------------------------------------ Users & Access (§28)

    private async Task LoadUsersAsync()
    {
        var admin = _c.Context?.Can("users.manage") == true;
        UsersSection.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        if (!admin) return;
        try
        {
            var users = (await _c.Api.GetUsersAsync(CancellationToken.None)).Users;
            UsersList.ItemsSource = users;
            UsersEmpty.Text = users.Count == 0 ? "No users on this server." : string.Empty;
            UsersEmpty.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (WorkspaceApiException ex)
        {
            UsersEmpty.Text = ex.Message;
            UsersEmpty.Visibility = Visibility.Visible;
        }
    }

    private static WorkspaceUserRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as WorkspaceUserRow;

    private void OnAddUserClick(object sender, RoutedEventArgs e)
    {
        var url = _c.Context?.PortalUsersUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowToast("Add users from the CloudOrc portal → Users.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"open portal: {ex.Message}");
            ShowToast("Could not open the browser. Go to the CloudOrc portal → Users.");
        }
    }

    private void OnUserResetClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } u) return;
        Confirm($"Send a password reset link to {u.DisplayName ?? u.Username}?", "The user gets an email with a link to set a new password. No password is shown or stored here.", "Send link", async () =>
        {
            try { ShowToast((await _c.Api.SendResetLinkAsync(u.Id, CancellationToken.None)).Message ?? "Link sent."); }
            catch (WorkspaceApiException ex) { ShowToast(ex.Message); }
        });
    }

    private void OnUserSignOutClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } u) return;
        if (u.IsYou)
        {
            ShowToast("Use Sign out below for your own session.");
            return;
        }

        Confirm($"Sign out {u.DisplayName ?? u.Username}?", "Their apps close and unsaved work (for example in Tally) may be lost. Ask them to save first.", "Sign out", async () =>
        {
            try { ShowToast((await _c.Api.SignOutUserAsync(u.Id, CancellationToken.None)).Message ?? "Sign-out requested."); }
            catch (WorkspaceApiException ex) { ShowToast(ex.Message); }
            await LoadUsersAsync();
        });
    }

    private async void OnUserTwoFactorClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } u) return;
        await StartOtpActionAsync(u, u.TwoFactorEnabled ? "two-factor-off" : "two-factor-on",
            u.TwoFactorEnabled ? $"Turn off two-step verification for {u.Username}" : $"Turn on two-step verification for {u.Username}");
    }

    private async void OnUserSuspendClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } u) return;
        if (u.IsYou && !u.IsSuspended)
        {
            ShowToast("You cannot suspend the account you are signed in with.");
            return;
        }

        await StartOtpActionAsync(u, u.IsSuspended ? "reinstate" : "suspend",
            u.IsSuspended ? $"Reinstate {u.Username}" : $"Suspend {u.Username}");
    }

    /// <summary>Step-up: request the emailed OTP, then apply the action with it (existing windowsUser flow).</summary>
    private async Task StartOtpActionAsync(WorkspaceUserRow u, string action, string title)
    {
        try
        {
            var r = await _c.Api.RequestUserOtpAsync(u.Id, action, CancellationToken.None);
            ShowToast(r.Message ?? "Code sent.");
        }
        catch (WorkspaceApiException ex)
        {
            ShowToast(ex.Message);
            return;
        }

        OtpTitle.Text = title;
        OtpInput.Text = string.Empty;
        _otpAction = async otp =>
        {
            try
            {
                var r = await _c.Api.ApplyUserActionAsync(u.Id, action, otp, CancellationToken.None);
                ShowToast(r.Message ?? "Request submitted.");
            }
            catch (WorkspaceApiException ex)
            {
                ShowToast(ex.Message);
            }

            await LoadUsersAsync();
        };
        OtpOverlay.Visibility = Visibility.Visible;
        OtpInput.Focus();
    }

    private async void OnOtpConfirm(object sender, RoutedEventArgs e)
    {
        var otp = OtpInput.Text.Trim();
        if (otp.Length < 4)
        {
            ShowToast("Enter the code from the email.");
            return;
        }

        OtpOverlay.Visibility = Visibility.Collapsed;
        var action = _otpAction;
        _otpAction = null;
        if (action is not null) await action(otp);
    }

    private void OnOtpCancel(object sender, RoutedEventArgs e)
    {
        OtpOverlay.Visibility = Visibility.Collapsed;
        _otpAction = null;
    }

    // ------------------------------------------------------------------ send a file to my PC (§32)

    private async void OnSendFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a file to send to your PC",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;

        SendFileButton.IsEnabled = false;
        try
        {
            var r = await _c.Api.SendFileToPcAsync(dlg.FileName, CancellationToken.None);
            SendFileDoneText.Text = $"\"{r.FileName}\" link sent to {r.SentTo}. Open the email on your PC within 15 minutes.";
            SendFileDone.Visibility = Visibility.Visible;
        }
        catch (WorkspaceApiException ex)
        {
            ShowToast(ex.Message);
        }
        finally
        {
            SendFileButton.IsEnabled = true;
        }
    }
}
