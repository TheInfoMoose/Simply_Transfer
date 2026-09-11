# ==============================================================================
# Simply Transfer - Remote Destination Key Assignment Script
# Run this script on your DESTINATION SERVER in an Administrator PowerShell prompt.
# ==============================================================================

#Requires -RunAsAdministrator

$key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBFJdzCb1xIOrs2RfTLCYaxaAr9tpsg2AtUde0NK7SsG SimplyTransfer-TS_PRODESK_MINI"
$user = "Zak"
$destDir = "C:\Backups"

Write-Host ">>> Configuring Destination Server for Simply Transfer <<<" -ForegroundColor Cyan

# 1. Ensure OpenSSH ProgramData directory exists
$programDataSsh = "$env:ProgramData\ssh"
New-Item -ItemType Directory -Force -Path $programDataSsh | Out-Null
icacls.exe $programDataSsh /grant:r "*S-1-5-32-544:(OI)(CI)(F)" /grant:r "*S-1-5-18:(OI)(CI)(F)" | Out-Null

# 2. Configure administrators_authorized_keys (for Windows Administrator accounts)
$adminAuth = "$programDataSsh\administrators_authorized_keys"
Add-Content -Path $adminAuth -Value $key -Encoding Ascii -Force
try { takeown.exe /F $adminAuth /A | Out-Null } catch { }
icacls.exe $adminAuth /inheritance:r /grant:r "*S-1-5-32-544:F" /grant:r "*S-1-5-18:F" | Out-Null
icacls.exe $adminAuth /setowner "*S-1-5-32-544" | Out-Null
Write-Host "[OK] Configured $adminAuth" -ForegroundColor Green

# 3. Configure user authorized_keys (for Standard accounts)
$userSshDir = "$env:USERPROFILE\.ssh"
New-Item -ItemType Directory -Force -Path $userSshDir | Out-Null
$userAuth = "$userSshDir\authorized_keys"
Add-Content -Path $userAuth -Value $key -Encoding Ascii -Force
icacls.exe $userSshDir /inheritance:r /grant:r "$($user):(OI)(CI)(F)" /grant:r "*S-1-5-18:(OI)(CI)(F)" /grant:r "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null
icacls.exe $userAuth /inheritance:r /grant:r "$($user):F" /grant:r "*S-1-5-18:F" /grant:r "*S-1-5-32-544:F" | Out-Null
Write-Host "[OK] Configured $userAuth" -ForegroundColor Green

# 4. Pre-create destination backup directory
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
icacls.exe $destDir /grant:r "$($user):(OI)(CI)(M)" /grant:r "*S-1-5-32-544:(OI)(CI)(F)" | Out-Null
Write-Host "[OK] Pre-created backup folder: $destDir" -ForegroundColor Green

# 5. Restart sshd service
Set-Service -Name "sshd" -StartupType Automatic -ErrorAction SilentlyContinue
Restart-Service sshd -ErrorAction SilentlyContinue
Write-Host "[OK] Restarted sshd service." -ForegroundColor Green

Write-Host ""
Write-Host "Destination configuration complete! Simply Transfer can now authenticate." -ForegroundColor Cyan
