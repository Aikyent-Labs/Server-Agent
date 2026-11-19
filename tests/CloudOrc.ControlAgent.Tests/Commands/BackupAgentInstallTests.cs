using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.ControlAgent.Backup;
using CloudOrc.ControlAgent.Commands;
using CloudOrc.ControlAgent.Configuration;
using Microsoft.Extensions.Options;

namespace CloudOrc.ControlAgent.Tests.Commands;

public class BackupAgentInstallTests
{
    private static BackupAgentInstallParameters Valid() => new()
    {
        InstallId = "inst-1",
        PackageUrl = "https://downloads.cloudorc.example/backup-agent/1.0.0/CloudOrcBackupSetup.exe",
        Sha256 = new string('a', 64),
        SizeBytes = 60_000_000,
        EnrollmentToken = "ENR-abc123",
        ApiBase = "https://api.cloudorc.example/"
    };

    [Fact]
    public void ValidParameters_Pass() => Assert.Null(BackupAgentInstallHandler.Validate(Valid()));

    [Fact]
    public void PlainHttpPackage_IsRejected()
    {
        var p = Valid();
        Assert.NotNull(BackupAgentInstallHandler.Validate(new BackupAgentInstallParameters
        {
            InstallId = p.InstallId, PackageUrl = "http://evil.example/setup.exe", Sha256 = p.Sha256, SizeBytes = p.SizeBytes
        }));
    }

    [Fact]
    public void BadHash_Token_OrApi_AreRejected()
    {
        Assert.NotNull(BackupAgentInstallHandler.Validate(new BackupAgentInstallParameters { InstallId = "i", PackageUrl = Valid().PackageUrl, Sha256 = "xyz" }));
        Assert.NotNull(BackupAgentInstallHandler.Validate(new BackupAgentInstallParameters
        {
            InstallId = "i", PackageUrl = Valid().PackageUrl, Sha256 = Valid().Sha256, EnrollmentToken = "ENR x /VERYSILENT"
        }));
        Assert.NotNull(BackupAgentInstallHandler.Validate(new BackupAgentInstallParameters
        {
            InstallId = "i", PackageUrl = Valid().PackageUrl, Sha256 = Valid().Sha256, ApiBase = "http://api.example/"
        }));
        Assert.NotNull(BackupAgentInstallHandler.Validate(null));
    }

    [Fact]
    public void CommandType_IsAllowListed() =>
        Assert.True(BackupCommandTypes.IsTyped(BackupCommandTypes.BackupAgentInstall));

    [Fact]
    public void WhenTheBackupAgentIsInstalled_TheControlAgentAdvertisesDelegation()
    {
        BackupAgentPresence.Probe = () => true;
        try
        {
            var paths = new BackupPaths(Options.Create(new ControlAgentOptions { DataDirectory = Path.GetTempPath() }), Options.Create(new BackupOptions { Enabled = false }));
            var caps = new AgentCapabilitiesProvider(Options.Create(new BackupOptions { Enabled = false }), paths);
            Assert.Contains(AgentCapabilities.BackupDelegatedV1, caps.Capabilities);
            Assert.DoesNotContain(AgentCapabilities.BackupV1, caps.Capabilities);
        }
        finally
        {
            BackupAgentPresence.Probe = null;
        }
    }
}
