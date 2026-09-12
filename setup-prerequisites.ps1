<#
.SYNOPSIS
    Automated host setup and dependency resolution script for Simply Transfer.

.DESCRIPTION
    Configures host prerequisites and gathers dependencies for Simply Transfer:
    - Enables and configures OpenSSH Client & Server capabilities and Windows services (sshd, ssh-agent).
    - Configures Windows Defender Firewall inbound rules for SSH (Port 22).
    - Provisions and hardens user SSH key infrastructure (Ed25519) with strict NTFS permissions.
    - Verifies and gathers runtime dependencies (.NET 8 Desktop Runtime, Visual C++ 2015-2022 Redistributable).
    - Verifies system services required for live backups (Volume Shadow Copy / VSS, WireGuard).
    - Verifies deployment packages and optionally creates a desktop shortcut.

.PARAMETER SkipOpenSshServer
    Skips OpenSSH Server installation, service configuration, and firewall rule creation.

.PARAMETER SkipOpenSshClient
    Skips OpenSSH Client installation.

.PARAMETER SkipKeyGeneration
    Skips generating an Ed25519 SSH key pair if none exists.

.PARAMETER KeyComment
    Comment to embed in the generated SSH key. Defaults to "SimplyTransfer-<ComputerName>".

.PARAMETER InstallMissingDependencies
    Automatically attempts to install missing dependencies (.NET 8 Desktop Runtime, VC++ Redistributable, WireGuard)
    via Windows Package Manager (winget) or direct official installers. Default is $false.

.PARAMETER CreateDesktopShortcut
    Creates a desktop shortcut pointing to the published SimplyTransfer.UI.exe. Default is $false.

.PARAMETER BuildIfMissing
    Automatically invokes publish.ps1 if no published deployment package is found. Default is $false.

.PARAMETER Elevate
    Relaunches the script in an elevated Administrator session if currently unprivileged.

.EXAMPLE
    # Standard setup (inspects host, enables OpenSSH, configures keys, verifies dependencies)
    .\setup-prerequisites.ps1

.EXAMPLE
.PARAMETER NoPause
    Disables the interactive prompt to press Enter before exiting. Useful for headless CI/CD runs.

.PARAMETER SkipProfilePopulation
    Skips automatically updating %APPDATA%\SimplyTransfer\profiles.json with the detected SSH key path.

.PARAMETER Elevate
    Relaunches the script in an elevated Administrator session if currently unprivileged.

.EXAMPLE
    # Standard setup (inspects host, enables OpenSSH, configures keys, populates app, generates report, pauses)
    .\setup-prerequisites.ps1

.EXAMPLE
    # Automated setup without pause
    .\setup-prerequisites.ps1 -InstallMissingDependencies -CreateDesktopShortcut -NoPause

.EXAMPLE
    # Self-elevate and run with full permissions
    .\setup-prerequisites.ps1 -Elevate
#>

[CmdletBinding()]
param(
    [switch]$SkipOpenSshServer,
    [switch]$SkipOpenSshClient,
    [switch]$SkipKeyGeneration,
    [string]$KeyComment = "SimplyTransfer-$env:COMPUTERNAME",
    [switch]$InstallMissingDependencies,
    [switch]$CreateDesktopShortcut,
    [switch]$BuildIfMissing,
    [switch]$SkipProfilePopulation,
    [switch]$NoPause,
    [switch]$NonInteractive,
    [switch]$Elevate,
    [switch]$GenerateDestConfig
)

$EnableOpenSshServer = -not $SkipOpenSshServer
$EnableOpenSshClient = -not $SkipOpenSshClient
$GenerateSshKey = -not $SkipKeyGeneration

# -------------------------------------------------------------------------
# Helper Functions & UI Formatting
# -------------------------------------------------------------------------

function Write-Header {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host "   $Title" -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
}

function Write-Step {
    param([string]$StepNumber, [string]$Title)
    Write-Host ""
    Write-Host "[$StepNumber] $Title" -ForegroundColor Yellow
}

function Write-Success {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Info {
    param([string]$Message)
    Write-Host "  [INFO] $Message" -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-AsAdmin {
    param([string[]]$ScriptArgs)
    Write-Info "Relaunching setup script with elevated Administrator privileges..."
    $argList = @("-NoExit", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    if ($ScriptArgs) {
        $argList += $ScriptArgs
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit 0
}

# -------------------------------------------------------------------------
# 0. Privilege Check & Auto-Elevation
# -------------------------------------------------------------------------

Write-Header "Simply Transfer - Host Setup & Prerequisites Utility"

$isAdmin = Test-IsAdmin

if (-not $isAdmin) {
    if ($Elevate) {
        Restart-AsAdmin -ScriptArgs $PSBoundParameters.GetEnumerator() | ForEach-Object { "-$($_.Key)" }
    }
    else {
        Write-Warn "Script is running without Administrator elevation."
        Write-Warn "OpenSSH capability installation, service configuration, and firewall rules require Administrator rights."
        Write-Info "To automatically elevate, rerun with: .\setup-prerequisites.ps1 -Elevate"
        Write-Host ""
    }
}
else {
    Write-Success "Running with elevated Administrator privileges."
}

# Summary status tracker hashtable
$statusReport = [ordered]@{}

# -------------------------------------------------------------------------
# 1. OpenSSH Client Configuration
# -------------------------------------------------------------------------

Write-Step "1/8" "Checking OpenSSH Client..."

$sshCmd = Get-Command "ssh.exe" -ErrorAction SilentlyContinue
$sftpCmd = Get-Command "sftp.exe" -ErrorAction SilentlyContinue
$keygenCmd = Get-Command "ssh-keygen.exe" -ErrorAction SilentlyContinue

if ($sshCmd -and $sftpCmd -and $keygenCmd) {
    Write-Success "OpenSSH Client tools found: $($sshCmd.Source)"
    $statusReport["OpenSSH Client"] = "Installed ($($sshCmd.Source))"
}
else {
    if ($EnableOpenSshClient) {
        if ($isAdmin) {
            Write-Info "Installing OpenSSH Client capability via DISM / Windows Capability..."
            try {
                Add-WindowsCapability -Online -Name "OpenSSH.Client~~~~0.0.1.0" -ErrorAction Stop | Out-Null
                Write-Success "OpenSSH Client successfully installed."
                $statusReport["OpenSSH Client"] = "Installed via Windows Capability"
            }
            catch {
                Write-Fail "Failed to install OpenSSH Client: $($_.Exception.Message)"
                $statusReport["OpenSSH Client"] = "Installation Failed"
            }
        }
        else {
            Write-Warn "OpenSSH Client is missing and cannot be installed without Administrator elevation."
            $statusReport["OpenSSH Client"] = "Missing (Elevation Required)"
        }
    }
    else {
        $statusReport["OpenSSH Client"] = "Skipped by parameter"
    }
}

# -------------------------------------------------------------------------
# 2. OpenSSH Server Configuration
# -------------------------------------------------------------------------

Write-Step "2/8" "Checking OpenSSH Server (sshd)..."

$sshdService = Get-Service -Name "sshd" -ErrorAction SilentlyContinue

if ($sshdService) {
    Write-Success "OpenSSH Server service (sshd) is installed."
    
    if ($isAdmin) {
        # Configure automatic startup
        if ($sshdService.StartType -ne "Automatic") {
            Write-Info "Setting sshd startup type to 'Automatic'..."
            Set-Service -Name "sshd" -StartupType "Automatic"
        }
        
        # Start service if not running
        if ($sshdService.Status -ne "Running") {
            Write-Info "Starting sshd service..."
            Start-Service -Name "sshd"
            $sshdService.Refresh()
        }
        
        Write-Success "sshd service status: $($sshdService.Status)"
        $statusReport["OpenSSH Server Service"] = "Running (Automatic)"
    }
    else {
        $statusReport["OpenSSH Server Service"] = "Installed (Status: $($sshdService.Status))"
    }
}
else {
    if ($EnableOpenSshServer) {
        if ($isAdmin) {
            Write-Info "Installing OpenSSH Server capability..."
            try {
                Add-WindowsCapability -Online -Name "OpenSSH.Server~~~~0.0.1.0" -ErrorAction Stop | Out-Null
                Write-Success "OpenSSH Server capability installed."
                
                # Start and set startup to Automatic
                Set-Service -Name "sshd" -StartupType "Automatic"
                Start-Service -Name "sshd"
                Write-Success "sshd service started and set to Automatic startup."
                $statusReport["OpenSSH Server Service"] = "Installed & Running"
            }
            catch {
                Write-Fail "Failed to install OpenSSH Server: $($_.Exception.Message)"
                $statusReport["OpenSSH Server Service"] = "Installation Failed"
            }
        }
        else {
            Write-Warn "OpenSSH Server is not installed. Run with -Elevate to install and configure it."
            $statusReport["OpenSSH Server Service"] = "Not Installed (Elevation Required)"
        }
    }
    else {
        Write-Info "OpenSSH Server setup skipped (-EnableOpenSshServer: `$false)."
        $statusReport["OpenSSH Server Service"] = "Disabled"
    }
}

# Configure ssh-agent service
Write-Info "Checking OpenSSH Authentication Agent (ssh-agent)..."
$sshAgentService = Get-Service -Name "ssh-agent" -ErrorAction SilentlyContinue

if ($sshAgentService) {
    if ($isAdmin) {
        if ($sshAgentService.StartType -ne "Automatic") {
            Write-Info "Setting ssh-agent startup type to 'Automatic'..."
            Set-Service -Name "ssh-agent" -StartupType "Automatic"
        }
        if ($sshAgentService.Status -ne "Running") {
            Write-Info "Starting ssh-agent service..."
            Start-Service -Name "ssh-agent"
            $sshAgentService.Refresh()
        }
        Write-Success "ssh-agent service status: $($sshAgentService.Status)"
        $statusReport["SSH Agent Service"] = "Running (Automatic)"
    }
    else {
        $statusReport["SSH Agent Service"] = "Installed (Status: $($sshAgentService.Status))"
    }
}
else {
    $statusReport["SSH Agent Service"] = "Not Found"
}

# Configure Windows Firewall rule for SSH (port 22)
if ($isAdmin -and $EnableOpenSshServer) {
    Write-Info "Verifying Windows Defender Firewall rule for inbound SSH (Port 22)..."
    $firewallRule = Get-NetFirewallRule -Name "OpenSSH-Server-In-TCP" -ErrorAction SilentlyContinue
    
    if (-not $firewallRule) {
        # Check by display name
        $firewallRule = Get-NetFirewallRule -DisplayName "OpenSSH Server (sshd)" -ErrorAction SilentlyContinue
    }
    
    if ($firewallRule) {
        if (-not $firewallRule.Enabled) {
            Enable-NetFirewallRule -Name $firewallRule.Name
            Write-Success "Enabled existing firewall rule: $($firewallRule.DisplayName)"
        }
        else {
            Write-Success "Inbound Port 22 firewall rule is active: $($firewallRule.DisplayName)"
        }
        $statusReport["Firewall Port 22"] = "Allowed"
    }
    else {
        try {
            New-NetFirewallRule -Name "OpenSSH-Server-In-TCP" `
                -DisplayName "OpenSSH Server (sshd)" `
                -Enabled True `
                -Direction Inbound `
                -Protocol TCP `
                -Action Allow `
                -LocalPort 22 | Out-Null
            Write-Success "Created inbound firewall rule for Port 22 (OpenSSH Server)."
            $statusReport["Firewall Port 22"] = "Created & Allowed"
        }
        catch {
            Write-Warn "Could not automatically create firewall rule: $($_.Exception.Message)"
            $statusReport["Firewall Port 22"] = "Manual Configuration Needed"
        }
    }
}

# -------------------------------------------------------------------------
# 3. SSH Key Infrastructure & NTFS Hardening
# -------------------------------------------------------------------------

Write-Step "3/8" "Checking SSH Key Pair & Directory Permissions..."

$sshDir = Join-Path $env:USERPROFILE ".ssh"
if (-not (Test-Path $sshDir)) {
    Write-Info "Creating user SSH directory: $sshDir"
    New-Item -Path $sshDir -ItemType Directory -Force | Out-Null
}

# Apply strict NTFS ACL permissions to .ssh directory
try {
    icacls.exe "$sshDir" /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)(F)" /grant:r "SYSTEM:(OI)(CI)(F)" /grant:r "Administrators:(OI)(CI)(F)" | Out-Null
    Write-Success "Applied secure NTFS permissions to $sshDir"
}
catch {
    Write-Warn "Unable to verify ACLs on .ssh directory: $($_.Exception.Message)"
}

$candidateKeys = @(
    (Join-Path $sshDir "id_ed25519"),
    (Join-Path $sshDir "id_rsa")
)

$activeKey = $null
foreach ($keyPath in $candidateKeys) {
    if (Test-Path $keyPath) {
        $activeKey = $keyPath
        break
    }
}

if ($activeKey) {
    Write-Success "Found existing SSH private key: $activeKey"
    $statusReport["SSH Key Pair"] = "Existing ($activeKey)"
}
else {
    if ($GenerateSshKey -and $keygenCmd) {
        $newKeyPath = Join-Path $sshDir "id_ed25519"
        Write-Info "Generating new modern Ed25519 key pair ($KeyComment)..."
        
        & $keygenCmd.Source -t ed25519 -C "$KeyComment" -f "$newKeyPath" -N '""'
        
        if (Test-Path $newKeyPath) {
            # Apply strict NTFS ACL permissions to the new private key
            icacls.exe "$newKeyPath" /inheritance:r /grant:r "$($env:USERNAME):(F)" /grant:r "SYSTEM:(F)" | Out-Null
            Write-Success "Generated Ed25519 key pair with strict ACLs: $newKeyPath"
            $activeKey = $newKeyPath
            $statusReport["SSH Key Pair"] = "Generated ($newKeyPath)"
        }
        else {
            Write-Fail "Failed to generate SSH key pair."
            $statusReport["SSH Key Pair"] = "Generation Failed"
        }
    }
    else {
        Write-Warn "No SSH key pair found. Simply Transfer requires a private key for pure key authentication."
        $statusReport["SSH Key Pair"] = "Missing"
    }
}

# Display public key for remote server authorization
if ($activeKey) {
    $pubKeyPath = "$activeKey.pub"
    if (Test-Path $pubKeyPath) {
        $pubKeyContent = (Get-Content $pubKeyPath -Raw).Trim()
        Write-Host ""
        Write-Host "  >>> Remote Server Authorized Key <<<" -ForegroundColor Green
        Write-Host "  Add the following line to your destination server's ~/.ssh/authorized_keys:" -ForegroundColor Gray
        Write-Host "  $pubKeyContent" -ForegroundColor White
        Write-Host ""
    }
}

if ($GenerateDestConfig) {
    Write-Step "8b" "Generating Destination Configuration"
    $destConfigPath = Join-Path $PSScriptRoot "deploy-to-remote-dest.ps1"
    
    $pubKey = ""
    $defaultKey = $activeKey
    if ($defaultKey -and (Test-Path "$defaultKey.pub")) {
        $pubKey = Get-Content "$defaultKey.pub" -Raw
    }
    
    $configContent = @"
# ==============================================================================
# Simply Transfer - Remote Destination Key Assignment Script
# Run this script on your DESTINATION SERVER in an Administrator PowerShell prompt.
# ==============================================================================

#Requires -RunAsAdministrator

`$key = `"$($pubKey.Trim())`"
`$user = `"$env:USERNAME`"
`$destDir = `"C:\Backups`"

Write-Host ">>> Configuring Destination Server for Simply Transfer <<<" -ForegroundColor Cyan

# 1. Ensure OpenSSH ProgramData directory exists
`$programDataSsh = "`$env:ProgramData\ssh"
New-Item -ItemType Directory -Force -Path `$programDataSsh | Out-Null
icacls.exe `$programDataSsh /grant:r "*S-1-5-32-544:(OI)(CI)(F)" /grant:r "*S-1-5-18:(OI)(CI)(F)" | Out-Null

# 2. Configure administrators_authorized_keys (for Windows Administrator accounts)
`$adminAuth = "`$programDataSsh\administrators_authorized_keys"
Add-Content -Path `$adminAuth -Value `$key -Encoding Ascii -Force
try { takeown.exe /F `$adminAuth /A | Out-Null } catch { }
icacls.exe `$adminAuth /inheritance:r /grant:r "*S-1-5-32-544:F" /grant:r "*S-1-5-18:F" | Out-Null
icacls.exe `$adminAuth /setowner "*S-1-5-32-544" | Out-Null
Write-Host "[OK] Configured `$adminAuth" -ForegroundColor Green

# 3. Configure user authorized_keys (for Standard accounts)
`$userSshDir = "`$env:USERPROFILE\.ssh"
New-Item -ItemType Directory -Force -Path `$userSshDir | Out-Null
`$userAuth = "`$userSshDir\authorized_keys"
Add-Content -Path `$userAuth -Value `$key -Encoding Ascii -Force
icacls.exe `$userSshDir /inheritance:r /grant:r "`$(`$user):(OI)(CI)(F)" /grant:r "*S-1-5-18:(OI)(CI)(F)" /grant:r "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null
icacls.exe `$userAuth /inheritance:r /grant:r "`$(`$user):F" /grant:r "*S-1-5-18:F" /grant:r "*S-1-5-32-544:F" | Out-Null
Write-Host "[OK] Configured `$userAuth" -ForegroundColor Green

# 4. Pre-create destination backup directory
New-Item -ItemType Directory -Force -Path `$destDir | Out-Null
icacls.exe `$destDir /grant:r "`$(`$user):(OI)(CI)(M)" /grant:r "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null
Write-Host "[OK] Pre-created backup folder: `$destDir" -ForegroundColor Green

# 5. Restart sshd service
Set-Service -Name "sshd" -StartupType Automatic -ErrorAction SilentlyContinue
Restart-Service sshd -ErrorAction SilentlyContinue
Write-Host "[OK] Restarted sshd service." -ForegroundColor Green

Write-Host ""
Write-Host "Destination configuration complete! Simply Transfer can now authenticate." -ForegroundColor Cyan
"@

    Set-Content -Path $destConfigPath -Value $configContent -Encoding UTF8
    Write-Host "Generated destination configuration at: $destConfigPath" -ForegroundColor Green
}

# -------------------------------------------------------------------------
# 4. Gather & Verify Simply Transfer Runtime Dependencies
# -------------------------------------------------------------------------

Write-Step "4/8" "Checking Runtime Dependencies..."

# 4A. .NET 8 Windows Desktop Runtime
$desktopRuntimeFound = $false

# Method 1: Check dotnet CLI
$dotnetCmd = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
if (-not $dotnetCmd -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    $dotnetCmd = Get-Item "C:\Program Files\dotnet\dotnet.exe"
}

if ($dotnetCmd) {
    $runtimes = & $dotnetCmd.FullName --list-runtimes 2>&1
    if ($runtimes -match "Microsoft\.WindowsDesktop\.App\s+8\.") {
        $desktopRuntimeFound = $true
        Write-Success ".NET 8 Windows Desktop Runtime detected via dotnet CLI."
        $statusReport[".NET 8 Desktop Runtime"] = "Installed"
    }
}

# Method 2: Check registry / filesystem if CLI check didn't pass
if (-not $desktopRuntimeFound) {
    $desktopFxDir = "C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App"
    if (Test-Path $desktopFxDir) {
        $v8dirs = Get-ChildItem -Path $desktopFxDir -Directory | Where-Object { $_.Name -like "8.*" }
        if ($v8dirs) {
            $desktopRuntimeFound = $true
            Write-Success ".NET 8 Windows Desktop Runtime detected in $desktopFxDir"
            $statusReport[".NET 8 Desktop Runtime"] = "Installed ($($v8dirs[0].Name))"
        }
    }
}

if (-not $desktopRuntimeFound) {
    Write-Warn ".NET 8 Windows Desktop Runtime (x64) is not installed."
    
    if ($InstallMissingDependencies) {
        Write-Info "Attempting installation via winget..."
        $wingetCmd = Get-Command "winget.exe" -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            & $wingetCmd.Source install Microsoft.DotNet.DesktopRuntime.8 --accept-source-agreements --accept-package-agreements --silent
            if ($LASTEXITCODE -eq 0) {
                Write-Success ".NET 8 Desktop Runtime successfully installed via winget."
                $statusReport[".NET 8 Desktop Runtime"] = "Installed via winget"
                $desktopRuntimeFound = $true
            }
        }
        
        if (-not $desktopRuntimeFound) {
            Write-Info "Downloading official .NET 8 Desktop Runtime installer..."
            $installerUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
            $tempInstaller = Join-Path $env:TEMP "windowsdesktop-runtime-win-x64.exe"
            try {
                Invoke-WebRequest -Uri $installerUrl -OutFile $tempInstaller -UseBasicParsing
                Write-Info "Running silent installer..."
                Start-Process -FilePath $tempInstaller -ArgumentList "/install", "/quiet", "/norestart" -Wait
                Write-Success ".NET 8 Desktop Runtime installed successfully."
                $statusReport[".NET 8 Desktop Runtime"] = "Installed via direct download"
            }
            catch {
                Write-Fail "Failed to download/install .NET 8 Desktop Runtime: $($_.Exception.Message)"
                $statusReport[".NET 8 Desktop Runtime"] = "Missing (Install required)"
            }
        }
    }
    else {
        Write-Info "To install manually: winget install Microsoft.DotNet.DesktopRuntime.8"
        Write-Info "Or download from: https://dotnet.microsoft.com/download/dotnet/8.0"
        $statusReport[".NET 8 Desktop Runtime"] = "Missing"
    }
}

# 4B. Visual C++ 2015-2022 Redistributable (Required for AlphaVSS / Ijwhost native interop)
Write-Info "Checking Microsoft Visual C++ 2015-2022 Redistributable (x64)..."
$vcRedistFound = $false

$vcKey = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"
if (Test-Path $vcKey) {
    $val = Get-ItemProperty -Path $vcKey -ErrorAction SilentlyContinue
    if ($val -and $val.Installed -eq 1) {
        $vcRedistFound = $true
        Write-Success "Visual C++ 2015-2022 x64 Redistributable detected (v$($val.Version))."
        $statusReport["Visual C++ Redist (x64)"] = "Installed ($($val.Version))"
    }
}

if (-not $vcRedistFound -and (Test-Path "C:\Windows\System32\vcruntime140.dll")) {
    $vcRedistFound = $true
    Write-Success "Visual C++ runtime binaries (vcruntime140.dll) detected in System32."
    $statusReport["Visual C++ Redist (x64)"] = "Installed (System32)"
}

if (-not $vcRedistFound) {
    Write-Warn "Visual C++ 2015-2022 Redistributable (x64) was not detected. Required for Volume Shadow Copy (AlphaVSS) native binaries."
    
    if ($InstallMissingDependencies) {
        Write-Info "Attempting installation of VC++ Redistributable..."
        $wingetCmd = Get-Command "winget.exe" -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            & $wingetCmd.Source install Microsoft.VCRedist.2015+.x64 --accept-source-agreements --accept-package-agreements --silent
            if ($LASTEXITCODE -eq 0) {
                Write-Success "Visual C++ Redistributable installed via winget."
                $statusReport["Visual C++ Redist (x64)"] = "Installed via winget"
                $vcRedistFound = $true
            }
        }
        
        if (-not $vcRedistFound) {
            $vcUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
            $tempVc = Join-Path $env:TEMP "vc_redist.x64.exe"
            try {
                Invoke-WebRequest -Uri $vcUrl -OutFile $tempVc -UseBasicParsing
                Start-Process -FilePath $tempVc -ArgumentList "/install", "/quiet", "/norestart" -Wait
                Write-Success "Visual C++ Redistributable installed successfully."
                $statusReport["Visual C++ Redist (x64)"] = "Installed via direct download"
            }
            catch {
                Write-Fail "Failed to install VC++ Redistributable: $($_.Exception.Message)"
                $statusReport["Visual C++ Redist (x64)"] = "Missing"
            }
        }
    }
    else {
        Write-Info "To install manually: winget install Microsoft.VCRedist.2015+.x64"
        Write-Info "Or download from: https://aka.ms/vs/17/release/vc_redist.x64.exe"
        $statusReport["Visual C++ Redist (x64)"] = "Missing"
    }
}

# -------------------------------------------------------------------------
# 5. Verify System Services (VSS & WireGuard)
# -------------------------------------------------------------------------

Write-Step "5/8" "Checking System Services (Volume Shadow Copy & WireGuard)..."

# 5A. Volume Shadow Copy (VSS)
$vssService = Get-Service -Name "vss" -ErrorAction SilentlyContinue
if ($vssService) {
    if ($vssService.StartType -eq "Disabled") {
        if ($isAdmin) {
            Write-Info "VSS service is currently Disabled. Enabling service (StartupType: Manual)..."
            Set-Service -Name "vss" -StartupType "Manual"
            Write-Success "VSS service set to Manual startup."
            $statusReport["Volume Shadow Copy (VSS)"] = "Enabled (Manual)"
        }
        else {
            Write-Warn "VSS service is Disabled. Administrator rights required to enable."
            $statusReport["Volume Shadow Copy (VSS)"] = "Disabled (Elevation Required)"
        }
    }
    else {
        Write-Success "VSS service is available (StartupType: $($vssService.StartType))."
        $statusReport["Volume Shadow Copy (VSS)"] = "Ready ($($vssService.StartType))"
    }
}
else {
    Write-Warn "Volume Shadow Copy service not found on this system."
    $statusReport["Volume Shadow Copy (VSS)"] = "Not Found"
}

# 5B. WireGuard (Optional VPN Provider)
$wgCmd = Get-Command "wireguard.exe" -ErrorAction SilentlyContinue
if (-not $wgCmd -and (Test-Path "C:\Program Files\WireGuard\wireguard.exe")) {
    $wgCmd = Get-Item "C:\Program Files\WireGuard\wireguard.exe"
}

if ($wgCmd) {
    $wgPath = if ($wgCmd.Source) { $wgCmd.Source } else { $wgCmd.FullName }
    Write-Success "WireGuard client found: $wgPath"
    $statusReport["WireGuard (Optional VPN)"] = "Ready ($wgPath)"
}
else {
    Write-Info "WireGuard is not installed. (Optional: only needed if using WireGuard pre-flight VPN)."
    if ($InstallMissingDependencies) {
        $wingetCmd = Get-Command "winget.exe" -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            Write-Info "Installing WireGuard via winget..."
            & $wingetCmd.Source install WireGuard.WireGuard --accept-source-agreements --accept-package-agreements --silent
            if ($LASTEXITCODE -eq 0) {
                Write-Success "WireGuard installed successfully."
                $statusReport["WireGuard (Optional VPN)"] = "Installed via winget"
            }
        }
    }
    else {
        Write-Info "To install WireGuard: winget install WireGuard.WireGuard"
        $statusReport["WireGuard (Optional VPN)"] = "Not Installed (Optional)"
    }
}

# -------------------------------------------------------------------------
# 6. Deployment Packaging & Desktop Shortcut
# -------------------------------------------------------------------------

Write-Step "6/8" "Checking Simply Transfer Application Deployment..."

$scriptDir = Split-Path -Parent $PSCommandPath
$publishedDir = Join-Path $scriptDir "publish\SimplyTransfer-x64"
$standaloneDir = Join-Path $scriptDir "publish\SimplyTransfer-x64-Standalone"
$chosenExe = $null

if (Test-Path (Join-Path $standaloneDir "SimplyTransfer.UI.exe")) {
    $chosenExe = Join-Path $standaloneDir "SimplyTransfer.UI.exe"
    Write-Success "Self-contained deployment found: $chosenExe"
    $statusReport["Application Package"] = "Ready (Self-Contained in $standaloneDir)"
}
elseif (Test-Path (Join-Path $publishedDir "SimplyTransfer.UI.exe")) {
    $chosenExe = Join-Path $publishedDir "SimplyTransfer.UI.exe"
    Write-Success "Framework-dependent deployment found: $chosenExe"
    $statusReport["Application Package"] = "Ready (Framework-Dependent in $publishedDir)"
}
else {
    Write-Warn "Simply Transfer deployment package not found in .\publish\"
    if ($BuildIfMissing) {
        $publishScript = Join-Path $scriptDir "publish.ps1"
        if (Test-Path $publishScript) {
            Write-Info "Executing publish.ps1 to build deployment package..."
            & powershell.exe -ExecutionPolicy Bypass -File $publishScript
            if (Test-Path (Join-Path $publishedDir "SimplyTransfer.UI.exe")) {
                $chosenExe = Join-Path $publishedDir "SimplyTransfer.UI.exe"
                Write-Success "Application successfully compiled and published: $chosenExe"
                $statusReport["Application Package"] = "Built & Ready"
            }
        }
    }
    else {
        Write-Info "Run .\publish.ps1 to build and package the application."
        $statusReport["Application Package"] = "Not Built (Run .\publish.ps1)"
    }
}

# Verify AlphaVSS native DLLs if package exists
if ($chosenExe) {
    $appDir = Split-Path -Parent $chosenExe
    $alphaVssDll = Join-Path $appDir "AlphaVSS.x64.dll"
    $ijwHostDll = Join-Path $appDir "Ijwhost.dll"
    
    if ((Test-Path $alphaVssDll) -and (Test-Path $ijwHostDll)) {
        Write-Success "Native AlphaVSS x64 libraries colocated with executable."
        $statusReport["AlphaVSS Native Assemblies"] = "Verified"
    }
    else {
        Write-Warn "AlphaVSS native binaries missing in output directory! Run .\publish.ps1 to fix."
        $statusReport["AlphaVSS Native Assemblies"] = "Missing in output"
    }
}

# Create Desktop Shortcut if requested
if ($CreateDesktopShortcut -and $chosenExe) {
    Write-Info "Creating Desktop shortcut for Simply Transfer..."
    try {
        $wshShell = New-Object -ComObject WScript.Shell
        $desktopDir = [Environment]::GetFolderPath("Desktop")
        $shortcutPath = Join-Path $desktopDir "Simply Transfer.lnk"
        $shortcut = $wshShell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $chosenExe
        $shortcut.WorkingDirectory = Split-Path -Parent $chosenExe
        $shortcut.Description = "Simply Transfer - Enterprise VSS & SFTP Sync Utility"
        $shortcut.Save()
        Write-Success "Desktop shortcut created: $shortcutPath"
        $statusReport["Desktop Shortcut"] = "Created ($shortcutPath)"
    }
    catch {
        Write-Warn "Could not create desktop shortcut: $($_.Exception.Message)"
        $statusReport["Desktop Shortcut"] = "Failed"
    }
}

# -------------------------------------------------------------------------
# 7. Populate Application Configuration Data
# -------------------------------------------------------------------------

Write-Step "7/8" "Configuring Application Profile Data..."

if (-not $SkipProfilePopulation -and $activeKey) {
    try {
        $appDataFolder = Join-Path $env:APPDATA "SimplyTransfer"
        if (-not (Test-Path $appDataFolder)) {
            New-Item -Path $appDataFolder -ItemType Directory -Force | Out-Null
        }
        $profilesJsonPath = Join-Path $appDataFolder "profiles.json"
        
        $profilesList = @()
        $profileUpdated = $false
        
        if (Test-Path $profilesJsonPath) {
            $existingRaw = Get-Content $profilesJsonPath -Raw -ErrorAction Stop
            $existingData = ConvertFrom-Json $existingRaw
            $profilesList = @($existingData)
            
            foreach ($prof in $profilesList) {
                if ([string]::IsNullOrWhiteSpace($prof.PrivateKeyPath) -or ($prof.Name -eq "Default Profile")) {
                    $prof.PrivateKeyPath = $activeKey
                    $prof.KeyType = "Auto"
                    if (-not $prof.DestinationDirectory) {
                        $prof.DestinationDirectory = "/backups/"
                    }
                    $prof.UseVssForLockedFiles = $true
                    $prof.AutoDetectQuickBooks = $true
                    $profileUpdated = $true
                }
            }
        }
        
        if (-not $profileUpdated -and ($profilesList.Count -eq 0)) {
            $newProfile = [ordered]@{
                Id = [Guid]::NewGuid().ToString()
                Name = "Default Profile"
                Host = ""
                Port = 22
                Username = ""
                PrivateKeyPath = $activeKey
                KeyType = "Auto"
                UseVpn = $false
                VpnType = "WireGuard"
                VpnConnectionName = ""
                VpnConfigPath = ""
                PreFlightTimeoutSeconds = 20
                DisconnectVpnAfterSync = $false
                UseVssForLockedFiles = $true
                AutoDetectQuickBooks = $true
                SourcePaths = @()
                DestinationDirectory = "/backups/"
            }
            $profilesList = @($newProfile)
            $profileUpdated = $true
        }
        
        if ($profileUpdated) {
            $serializedJson = ConvertTo-Json -InputObject @($profilesList) -Depth 10
            Set-Content -Path $profilesJsonPath -Value $serializedJson -Encoding UTF8
            Write-Success "Populated $profilesJsonPath with PrivateKeyPath: $activeKey"
            $statusReport["App Profile Configuration"] = "Populated ($profilesJsonPath)"
        }
        else {
            Write-Info "Existing application profiles retained."
            $statusReport["App Profile Configuration"] = "Existing profiles verified"
        }
    }
    catch {
        Write-Warn "Could not populate application profiles: $($_.Exception.Message)"
        $statusReport["App Profile Configuration"] = "Failed ($($_.Exception.Message))"
    }
}
else {
    $statusReport["App Profile Configuration"] = "Skipped"
}

# -------------------------------------------------------------------------
# 8. Generate Prerequisites Audit Report File
# -------------------------------------------------------------------------

Write-Step "8/8" "Generating Prerequisites Audit Report..."

try {
    $reportTimestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    $reportOs = [System.Environment]::OSVersion.VersionString
    $reportUser = "$env:USERDOMAIN\$env:USERNAME"
    $reportElevation = if ($isAdmin) { "Administrator (Elevated)" } else { "Standard User (Non-Elevated)" }
    
    $pubKeyContent = ""
    if ($activeKey -and (Test-Path "$activeKey.pub")) {
        $pubKeyContent = (Get-Content "$activeKey.pub" -Raw).Trim()
    }

    $reportBuilder = New-Object System.Text.StringBuilder
    [void]$reportBuilder.AppendLine("================================================================================")
    [void]$reportBuilder.AppendLine("            SIMPLY TRANSFER - PREREQUISITES & HOST AUDIT REPORT                 ")
    [void]$reportBuilder.AppendLine("================================================================================")
    [void]$reportBuilder.AppendLine("Generated:  $reportTimestamp")
    [void]$reportBuilder.AppendLine("Computer:   $env:COMPUTERNAME")
    [void]$reportBuilder.AppendLine("User:       $reportUser")
    [void]$reportBuilder.AppendLine("OS Version: $reportOs")
    [void]$reportBuilder.AppendLine("Privilege:  $reportElevation")
    [void]$reportBuilder.AppendLine("")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    [void]$reportBuilder.AppendLine("1. HOST READINESS & PREREQUISITES STATUS")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    foreach ($entry in $statusReport.GetEnumerator()) {
        $line = ("- " + $entry.Key.PadRight(32) + ": " + $entry.Value)
        [void]$reportBuilder.AppendLine($line)
    }
    [void]$reportBuilder.AppendLine("")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    [void]$reportBuilder.AppendLine("2. SSH KEY AUTHENTICATION INFRASTRUCTURE")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    [void]$reportBuilder.AppendLine("Active Private Key: $activeKey")
    [void]$reportBuilder.AppendLine("Active Public Key:  $activeKey.pub")
    [void]$reportBuilder.AppendLine("")
    if ($pubKeyContent) {
        [void]$reportBuilder.AppendLine("AUTHORIZED KEY ENTRY (Add this line to remote destination server's ~/.ssh/authorized_keys):")
        [void]$reportBuilder.AppendLine($pubKeyContent)
    }
    else {
        [void]$reportBuilder.AppendLine("No SSH public key found.")
    }
    [void]$reportBuilder.AppendLine("")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    [void]$reportBuilder.AppendLine("3. APPLICATION PROFILE DATA")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    $appDataFile = Join-Path $env:APPDATA "SimplyTransfer\profiles.json"
    [void]$reportBuilder.AppendLine("Profiles Path: $appDataFile")
    [void]$reportBuilder.AppendLine("Default Key:   $activeKey")
    [void]$reportBuilder.AppendLine("")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    [void]$reportBuilder.AppendLine("4. EXECUTION INSTRUCTIONS")
    [void]$reportBuilder.AppendLine("--------------------------------------------------------------------------------")
    [void]$reportBuilder.AppendLine("For full live database support (Volume Shadow Copy / VSS for locked files),")
    [void]$reportBuilder.AppendLine("always run Simply Transfer with Administrator elevation:")
    if ($chosenExe) {
        [void]$reportBuilder.AppendLine("  Start-Process -FilePath `"$chosenExe`" -Verb RunAs")
    }
    else {
        [void]$reportBuilder.AppendLine("  Start-Process -FilePath `".\publish\SimplyTransfer-x64\SimplyTransfer.UI.exe`" -Verb RunAs")
    }
    [void]$reportBuilder.AppendLine("================================================================================")
    
    $reportText = $reportBuilder.ToString()
    
    # Save copy to project root
    $localReportPath = Join-Path $scriptDir "prerequisites-report.txt"
    Set-Content -Path $localReportPath -Value $reportText -Encoding UTF8
    
    # Save copy to %APPDATA%\SimplyTransfer\
    $appDataDir = Join-Path $env:APPDATA "SimplyTransfer"
    if (Test-Path $appDataDir) {
        $appReportPath = Join-Path $appDataDir "prerequisites-report.txt"
        Set-Content -Path $appReportPath -Value $reportText -Encoding UTF8
    }
    
    Write-Success "Prerequisites report generated at: $localReportPath"
    $statusReport["Prerequisites Report"] = "Generated ($localReportPath)"
}
catch {
    Write-Warn "Could not write report file: $($_.Exception.Message)"
}

# -------------------------------------------------------------------------
# Final Summary Report
# -------------------------------------------------------------------------

Write-Header "Host Readiness Summary"

$statusReport.GetEnumerator() | ForEach-Object {
    $key = $_.Key.PadRight(32)
    $val = $_.Value
    if ($val -match "Installed|Running|Ready|Allowed|Verified|Generated|Created|Populated") {
        Write-Host "  $key : " -NoNewline
        Write-Host "$val" -ForegroundColor Green
    }
    elseif ($val -match "Optional|Disabled|Skipped") {
        Write-Host "  $key : " -NoNewline
        Write-Host "$val" -ForegroundColor Gray
    }
    else {
        Write-Host "  $key : " -NoNewline
        Write-Host "$val" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
if ($activeKey) {
    Write-Host "  1. Copy your public key to your destination SFTP server's ~/.ssh/authorized_keys." -ForegroundColor White
}
Write-Host "  2. Run Simply Transfer as Administrator to enable live VSS snapshots of locked files:" -ForegroundColor White
if ($chosenExe) {
    Write-Host "     Start-Process -FilePath '$chosenExe' -Verb RunAs" -ForegroundColor Yellow
}
else {
    Write-Host "     .\publish.ps1  (then launch the published SimplyTransfer.UI.exe as Admin)" -ForegroundColor Yellow
}
Write-Host ""

# -------------------------------------------------------------------------
# Pause Before Exiting
# -------------------------------------------------------------------------

if (-not $NoPause -and -not $NonInteractive) {
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host "Setup complete. Press [Enter] to exit..." -ForegroundColor Cyan -NoNewline
    try {
        [void][System.Console]::ReadLine()
    }
    catch {
        Read-Host | Out-Null
    }
}
