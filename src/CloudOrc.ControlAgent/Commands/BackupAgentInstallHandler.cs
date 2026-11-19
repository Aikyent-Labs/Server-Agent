using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Commands;

/// <summary>Parameters of agent.backup-agent.install (sent by the portal for CloudOrc VPS servers).</summary>
public sealed class BackupAgentInstallParameters
{
    [JsonPropertyName("installId")] public string? InstallId { get; init; }

    /// <summary>https URL of CloudOrcBackupSetup.exe for this release.</summary>
    [JsonPropertyName("packageUrl")] public string? PackageUrl { get; init; }

    [JsonPropertyName("sha256")] public string? Sha256 { get; init; }

    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; init; }

    /// <summary>One-use, server-bound enrollment token for the NEW Backup Agent identity (zero-touch).</summary>
    [JsonPropertyName("enrollmentToken")] public string? EnrollmentToken { get; init; }

    /// <summary>Alternative to the token: a portal activation code.</summary>
    [JsonPropertyName("activationCode")] public string? ActivationCode { get; init; }

    [JsonPropertyName("apiBase")] public string? ApiBase { get; init; }

    [JsonPropertyName("portalUrl")] public string? PortalUrl { get; init; }
}

/// <summary>
/// agent.backup-agent.install - zero-touch installation of the separate CloudOrc Backup Agent on this
/// CloudOrc VPS. Downloads the signed setup over HTTPS, enforces size + SHA-256 (the hash arrives in
/// this backend-signed command) and the Authenticode signature, installs silently WITHOUT the status
/// icon, then restarts this Control Agent so it hands backup over to the Backup Agent
/// (<see cref="BackupOptions.DeferToBackupAgent"/>). The two agents stay separate products: separate
/// services, identities, updates and watchdogs.
/// </summary>
public sealed partial class BackupAgentInstallHandler(
    BackupPaths paths,
    IOptions<ControlAgentOptions> agentOptions,
    ILogger<BackupAgentInstallHandler> logger) : ITypedCommandHandler
{
    private const long MaxPackageBytes = 400L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string CommandType => BackupCommandTypes.BackupAgentInstall;

    public async Task<TypedCommandAcceptance> HandleAsync(TypedCommandContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<BackupAgentInstallParameters>();
        if (Validate(p) is { } error)
        {
            return TypedCommandDispatcher.Rejected("BACKUP_AGENT_INSTALL_INVALID", error);
        }

        if (!OperatingSystem.IsWindows())
        {
            return TypedCommandDispatcher.Rejected("UNSUPPORTED_OS", "Zero-touch Backup Agent install is supported on Windows servers.");
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return TypedCommandDispatcher.Rejected("BACKUP_AGENT_INSTALL_IN_PROGRESS", "A Backup Agent installation is already running.");
        }

        var workDir = Path.Combine(paths.UpdatesRoot, "backup-agent-" + BackupPaths.SafeName(p!.InstallId!));
        try
        {
            Directory.CreateDirectory(workDir);
            var setup = Path.Combine(workDir, "CloudOrcBackupSetup.exe");
            var downloadError = await DownloadAndVerifyAsync(new Uri(p.PackageUrl!), setup, p.Sha256!, p.SizeBytes, cancellationToken).ConfigureAwait(false);
            if (downloadError is not null)
            {
                return TypedCommandDispatcher.Rejected("BACKUP_AGENT_PACKAGE_REJECTED", downloadError);
            }

            var signature = new CloudOrc.Agent.Updater.AuthenticodeVerifier().Verify(setup);
            if (!signature.IsValid)
            {
                return TypedCommandDispatcher.Rejected("BACKUP_AGENT_SIGNATURE_INVALID", signature.Reason ?? "The setup is not validly signed.");
            }

            var start = new ProcessStartInfo(setup) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
            foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/COMPONENTS=agent", $"/LOG={Path.Combine(workDir, "setup.log")}" })
            {
                start.ArgumentList.Add(arg);
            }

            if (!string.IsNullOrWhiteSpace(p.ApiBase)) start.ArgumentList.Add($"/API-BASE={p.ApiBase}");
            if (!string.IsNullOrWhiteSpace(p.PortalUrl)) start.ArgumentList.Add($"/PORTAL-URL={p.PortalUrl}");
            if (!string.IsNullOrWhiteSpace(p.EnrollmentToken)) start.ArgumentList.Add($"/TOKEN={p.EnrollmentToken}");
            else if (!string.IsNullOrWhiteSpace(p.ActivationCode)) start.ArgumentList.Add($"/CODE={p.ActivationCode}");

            logger.LogInformation("Installing the CloudOrc Backup Agent ({InstallId}).", p.InstallId);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Setup could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return TypedCommandDispatcher.Rejected("BACKUP_AGENT_INSTALL_TIMEOUT", "The Backup Agent setup did not finish within 15 minutes.");
            }

            if (process.ExitCode != 0)
            {
                var reason = process.ExitCode switch
                {
                    20 => "activation/enrollment was refused (token or code invalid, expired or already used)",
                    10 or 11 => "a Backup Agent service did not start",
                    2 => "the API address was rejected",
                    _ => $"setup exit code {process.ExitCode}"
                };
                return TypedCommandDispatcher.Rejected("BACKUP_AGENT_INSTALL_FAILED", $"Backup Agent installation failed: {reason}.");
            }

            logger.LogInformation("CloudOrc Backup Agent installed; restarting the Control Agent to hand backup over to it.");
            ScheduleOwnRestart();
            return new TypedCommandAcceptance { Accepted = true, Message = "CloudOrc Backup Agent installed and started. Backup moves to the Backup Agent." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "Backup Agent installation {InstallId} failed.", p!.InstallId);
            return TypedCommandDispatcher.Rejected("BACKUP_AGENT_INSTALL_FAILED", "The Backup Agent could not be installed on this server.");
        }
        finally
        {
            Gate.Release();
            TryDelete(Path.Combine(workDir, "CloudOrcBackupSetup.exe"));
        }
    }

    public static string? Validate(BackupAgentInstallParameters? p)
    {
        if (p is null || !BackupPolicyValidator.IsSafeId(p.InstallId))
        {
            return "installId is required.";
        }

        if (!Uri.TryCreate(p.PackageUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return "packageUrl must be an absolute https URL.";
        }

        if (string.IsNullOrWhiteSpace(p.Sha256) || !Sha256Pattern().IsMatch(p.Sha256))
        {
            return "sha256 must be 64 hex characters.";
        }

        if (p.ApiBase is { Length: > 0 } api && (!Uri.TryCreate(api, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps))
        {
            return "apiBase must be an https URL.";
        }

        foreach (var secret in new[] { p.EnrollmentToken, p.ActivationCode })
        {
            if (secret is { Length: > 0 } && (secret.Length > 512 || secret.Any(c => char.IsWhiteSpace(c) || c is '"' or '/' or '\\')))
            {
                return "enrollmentToken / activationCode has an invalid format.";
            }
        }

        return p.SizeBytes is <= 0 or > MaxPackageBytes ? "sizeBytes is out of range." : null;
    }

    private static async Task<string?> DownloadAndVerifyAsync(Uri url, string target, string expectedSha256, long? expectedSize, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return $"Download failed (HTTP {(int)response.StatusCode}).";
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes)
                {
                    return "Package exceeds the maximum allowed size.";
                }

                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        if (expectedSize is { } size && size != total)
        {
            return "Downloaded size does not match the announced size.";
        }

        return string.Equals(Convert.ToHexString(sha.GetHashAndReset()), expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "SHA-256 mismatch - package rejected.";
    }

    private void ScheduleOwnRestart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("restart-service");
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not restart the Control Agent; backup moves to the Backup Agent at its next restart.");
        }

        _ = agentOptions;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
