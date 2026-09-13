[CmdletBinding()]
param(
    [string]$DestinationHost = "",
    [string]$DestinationUser = "",
    [string]$DestinationDirectory = "",
    [switch]$NonInteractive
)

if (-not $DestinationHost -or -not $DestinationUser -or -not $DestinationDirectory) {
    Write-Host "Error: Missing required parameters." -ForegroundColor Red
    if (-not $NonInteractive) { pause }
    exit 1
}

$destUri = "${DestinationUser}@${DestinationHost}:${DestinationDirectory}/SimplyTransfer"

Write-Host "Deploying Simply Transfer to $DestinationHost..." -ForegroundColor Cyan

# Find the repository root by looking for publish.ps1
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

$publishDir = Join-Path $scriptRoot "dist\SimplyTransfer-Release"
if (-not (Test-Path $publishDir)) {
    Write-Host "Publish directory not found. Running publish.ps1..." -ForegroundColor Yellow
    & (Join-Path $scriptRoot "publish.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish failed due to errors. Cannot deploy." -ForegroundColor Red
        if (-not $NonInteractive) { pause }
        exit 1
    }
}

if (-not (Test-Path $publishDir)) {
    Write-Host "Publish failed. Cannot deploy." -ForegroundColor Red
    if (-not $NonInteractive) { pause }
    exit 1
}

# Use SSH to create the remote directory
Write-Host "Creating remote directory..."
ssh -o StrictHostKeyChecking=no "${DestinationUser}@${DestinationHost}" "mkdir -p `"$DestinationDirectory/SimplyTransfer`""

# Use SCP to copy the published files
Write-Host "Copying files via SCP..."
scp -r -o StrictHostKeyChecking=no "$publishDir\*" "$destUri"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Deployment completed successfully!" -ForegroundColor Green
    Write-Host "You can now run Simply Transfer on the destination and select 'Destination Host Mode'." -ForegroundColor Green
} else {
    Write-Host "Deployment failed with exit code $LASTEXITCODE." -ForegroundColor Red
}

if (-not $NonInteractive) { pause }
