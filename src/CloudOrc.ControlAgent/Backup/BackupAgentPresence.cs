namespace CloudOrc.ControlAgent.Backup;

/// <summary>Detects the separate CloudOrc Backup Agent product on this server.</summary>
public static class BackupAgentPresence
{
    public const string ServiceName = "CloudOrcBackupAgent";

    /// <summary>Overridable for tests.</summary>
    public static Func<bool>? Probe { get; set; }

    public static bool IsInstalled()
    {
        if (Probe is { } probe)
        {
            return probe();
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(ServiceName);
            _ = controller.Status; // throws InvalidOperationException when the service does not exist
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
