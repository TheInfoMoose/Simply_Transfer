<#
.SYNOPSIS
    Generates a portable deployment package for the destination server.

.DESCRIPTION
    Builds the application, copies the exported profile.json and the user's public SSH key
    into the release folder, and zips the contents into a portable archive.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory=$false)][string]$ProfileJsonPath = "",
    [Parameter(Mandatory=$false)][string]$PublicKeyPath = "",
    [switch]$NonInteractive
)

Write-Host ">>> Generating Destination Setup Package <<<" -ForegroundColor Cyan

# Find repository root
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD.Path }
$currentDir = $scriptRoot
while ($currentDir -and (Test-Path $currentDir)) {
    if (Test-Path (Join-Path $currentDir "publish.ps1")) {
        $scriptRoot = $currentDir
        break
    }
    $parentDir = Split-Path $currentDir -Parent
    if ($parentDir -eq $currentDir) { break }
    $currentDir = $parentDir
}

# 1. Build release
Write-Host "`n[1/3] Building Release Package..." -ForegroundColor Cyan
& (Join-Path $scriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed. Cannot generate destination package." -ForegroundColor Red
    if (-not $NonInteractive) { pause }
    exit 1
}

$releaseDir = Join-Path $scriptRoot "dist\SimplyTransfer-Release"

# 2. Inject Configuration and Keys
Write-Host "`n[2/3] Injecting configuration and public key..." -ForegroundColor Cyan
if ($ProfileJsonPath -and (Test-Path $ProfileJsonPath)) {
    Copy-Item -Path $ProfileJsonPath -Destination (Join-Path $releaseDir "profile.json") -Force
    Write-Host "Injected profile.json" -ForegroundColor Green
} else {
    Write-Host "No profile.json provided or found. Package will be unconfigured." -ForegroundColor Yellow
}

if ($PublicKeyPath -and (Test-Path $PublicKeyPath)) {
    Copy-Item -Path $PublicKeyPath -Destination (Join-Path $releaseDir (Split-Path $PublicKeyPath -Leaf)) -Force
    Write-Host "Injected public key: $(Split-Path $PublicKeyPath -Leaf)" -ForegroundColor Green
} else {
    Write-Host "No public key provided or found. Administrator must manually configure keys." -ForegroundColor Yellow
}

# 3. Zip Package
Write-Host "`n[3/3] Archiving package..." -ForegroundColor Cyan
$zipPath = Join-Path $scriptRoot "dist\Destination-Setup.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path "$releaseDir\*" -DestinationPath $zipPath -Force
Write-Host "`n[SUCCESS] Package generated at: $zipPath" -ForegroundColor Green
Write-Host "Provide this zip file to the destination technician." -ForegroundColor Cyan

if (-not $NonInteractive) { pause }
