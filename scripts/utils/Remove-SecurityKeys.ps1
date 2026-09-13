<#
.SYNOPSIS
    Removes Simply Transfer SSH keys and clears known hosts.

.DESCRIPTION
    This script is used to clean up the SSH security footprint of Simply Transfer.
    It deletes the generated Ed25519 private and public keys, clears the known_hosts file,
    and removes authorized keys from both standard user and Administrator profiles.
    This is useful for creating a fresh test bed or enforcing post-transfer security.
#>

[CmdletBinding()]
param()

Write-Host ">>> Starting Simply Transfer Security Key Cleanup <<<" -ForegroundColor Cyan

$userSshDir = Join-Path $env:USERPROFILE ".ssh"

# 1. Delete private and public keys
$keysToRemove = @(
    "id_ed25519",
    "id_ed25519.pub",
    "id_rsa",
    "id_rsa.pub"
)

foreach ($keyName in $keysToRemove) {
    $keyPath = Join-Path $userSshDir $keyName
    if (Test-Path $keyPath) {
        Remove-Item -Path $keyPath -Force -ErrorAction SilentlyContinue
        Write-Host "[OK] Removed local key: $keyName" -ForegroundColor Green
    }
}

# 2. Clear known_hosts
$knownHostsPath = Join-Path $userSshDir "known_hosts"
if (Test-Path $knownHostsPath) {
    Remove-Item -Path $knownHostsPath -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] Cleared known_hosts file." -ForegroundColor Green
}

# 3. Remove from Destination authorized_keys (Standard User)
$userAuthorizedKeys = Join-Path $userSshDir "authorized_keys"
if (Test-Path $userAuthorizedKeys) {
    # Remove any lines containing "SimplyTransfer"
    $filteredLines = Get-Content $userAuthorizedKeys | Where-Object { $_ -notmatch "SimplyTransfer" }
    Set-Content -Path $userAuthorizedKeys -Value $filteredLines -Force -Encoding Ascii
    Write-Host "[OK] Cleaned user authorized_keys." -ForegroundColor Green
}

# 4. Remove from Destination administrators_authorized_keys (Admin)
$programDataSsh = "$env:ProgramData\ssh"
$adminAuth = "$programDataSsh\administrators_authorized_keys"

if (Test-Path $adminAuth) {
    try {
        # This requires Administrator privileges
        $filteredAdminLines = Get-Content $adminAuth -ErrorAction Stop | Where-Object { $_ -notmatch "SimplyTransfer" }
        Set-Content -Path $adminAuth -Value $filteredAdminLines -Force -Encoding Ascii -ErrorAction Stop
        Write-Host "[OK] Cleaned administrators_authorized_keys." -ForegroundColor Green
    } catch {
        Write-Host "[WARNING] Could not clean administrators_authorized_keys. This usually requires running as Administrator." -ForegroundColor Yellow
    }
}

Write-Host "Security Key Cleanup complete!" -ForegroundColor Cyan
