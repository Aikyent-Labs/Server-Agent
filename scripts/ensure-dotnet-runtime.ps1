#Requires -Version 5.1
<#
.SYNOPSIS
    Makes sure the .NET 10 Windows Desktop Runtime is installed on this machine (installs it once).

.DESCRIPTION
    The CloudOrc agents are published framework-dependent: they share ONE machine-wide .NET runtime
    instead of each carrying its own copy (that made the package ~320 MB; now it is ~25 MB). This
    script is the single place that installs that runtime. It is used by:
        - scripts\install-agent.ps1 (runs it from the extracted package before touching anything)
        - installer\CloudOrcAgentSetup.iss (runs it in PrepareToInstall, also for silent self-updates)

    Microsoft's "Windows Desktop Runtime" installer contains both frameworks the agents need:
        Microsoft.NETCore.App          (services, backup worker, updater)
        Microsoft.WindowsDesktop.App   (Remote Workspace panel - WPF, status icon - WinForms)

    Already installed (any 10.x of both)  -> nothing is downloaded, exit 0.
    Missing -> download from Microsoft (or use -InstallerPath), check the file is signed by
    Microsoft (Authenticode), install quietly, check again. Any failure -> exit 1, nothing else on
    the machine is changed, so the calling installer can stop before it touches the running agent.

.PARAMETER RuntimeUrl
    Where to download the runtime installer. Defaults to Microsoft's "latest 10.0" link.

.PARAMETER InstallerPath
    A runtime installer already on this machine (offline servers). Used instead of downloading.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File ensure-dotnet-runtime.ps1
#>
[CmdletBinding()]
param(
    [string]$RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe",
    [string]$InstallerPath = "",
    [int]$MajorVersion = 10
)

$ErrorActionPreference = "Stop"
$Frameworks = @("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")

function Get-DotnetRoot {
    # The x64 runtime's location as registered by its installer; else the default folder.
    try {
        $reg = Get-ItemProperty -Path "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64" -Name InstallLocation -ErrorAction Stop
        if ($reg.InstallLocation -and (Test-Path $reg.InstallLocation)) { return $reg.InstallLocation }
    } catch { }
    $pf = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    return (Join-Path $pf "dotnet")
}

function Get-MissingFrameworks {
    $root = Get-DotnetRoot
    $missing = @()
    foreach ($fw in $Frameworks) {
        $dir = Join-Path $root "shared\$fw"
        $found = $false
        if (Test-Path $dir) {
            $found = [bool](Get-ChildItem -Path $dir -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match "^$MajorVersion\.\d+\.\d+$" } | Select-Object -First 1)
        }
        if (-not $found) { $missing += $fw }
    }
    return ,$missing
}

Write-Host "  Checking for the .NET $MajorVersion runtime ($($Frameworks -join ', '))..."
$missing = Get-MissingFrameworks
if ($missing.Count -eq 0) {
    Write-Host "  .NET $MajorVersion runtime already installed - nothing to download."
    exit 0
}
Write-Host "  Missing: $($missing -join ', ') - installing the .NET $MajorVersion Windows Desktop Runtime (one time)."

$workDir = Join-Path $env:TEMP ("CloudOrcDotnet-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
try {
    if ($InstallerPath) {
        if (-not (Test-Path $InstallerPath)) { Write-Host "  FAIL: runtime installer '$InstallerPath' not found." -ForegroundColor Red; exit 1 }
        $exe = $InstallerPath
    } else {
        $exe = Join-Path $workDir "windowsdesktop-runtime-win-x64.exe"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ProgressPreference = "SilentlyContinue"  # the progress bar makes Invoke-WebRequest many times slower
        try {
            Invoke-WebRequest -Uri $RuntimeUrl -OutFile $exe -UseBasicParsing
        } catch {
            Write-Host "  FAIL: could not download the .NET runtime from $RuntimeUrl : $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  On a server without internet, download it elsewhere and pass -InstallerPath." -ForegroundColor Yellow
            exit 1
        }
        Write-Host ("  Downloaded {0:N1} MB." -f ((Get-Item $exe).Length / 1MB))
    }

    # Only ever run Microsoft's own, untampered installer.
    $sig = Get-AuthenticodeSignature -FilePath $exe
    if ($sig.Status -ne "Valid" -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
        Write-Host "  FAIL: the runtime installer is not validly signed by Microsoft (status: $($sig.Status)). Not running it." -ForegroundColor Red
        exit 1
    }

    $proc = Start-Process -FilePath $exe -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru
    # 0 = ok, 3010/1641 = ok (restart suggested, not needed by the agents), 1638 = a newer version is already there.
    if (@(0, 3010, 1641, 1638) -notcontains $proc.ExitCode) {
        Write-Host "  FAIL: the .NET runtime installer exited with code $($proc.ExitCode)." -ForegroundColor Red
        exit 1
    }

    $missing = Get-MissingFrameworks
    if ($missing.Count -gt 0) {
        Write-Host "  FAIL: still missing after install: $($missing -join ', ')." -ForegroundColor Red
        exit 1
    }
    Write-Host "  .NET $MajorVersion runtime installed."
    exit 0
} finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
