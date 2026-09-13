param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RootDir = (Resolve-Path "$ScriptDir\..\..").Path
Set-Location $RootDir

Write-Host "Building and publishing SimplyTransfer ($Configuration)..." -ForegroundColor Cyan
dotnet publish SimplyTransfer.UI/SimplyTransfer.UI.csproj -c $Configuration -r win-x64 --self-contained true -o "$RootDir/dist/publish"

# Read version from Directory.Build.props
[xml]$props = Get-Content "$RootDir/Directory.Build.props"
$appVersion = $props.Project.PropertyGroup.Version
if (-not $appVersion) { $appVersion = "1.0.0" }
Write-Host "Detected Version: $appVersion" -ForegroundColor Cyan

# Check if InnoSetup is installed
$ISCC = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $ISCC)) {
    $ISCC = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
}

if (-not (Test-Path $ISCC)) {
    Write-Warning "Inno Setup 6 (ISCC.exe) not found. Skipping installer compilation."
    Write-Host "Publish output is available at: $RootDir/dist/publish"
    exit 0
}

Write-Host "Compiling InnoSetup Installer..." -ForegroundColor Cyan
& $ISCC "/DAppVersion=$appVersion" "$ScriptDir/installer.iss"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Installer built successfully: dist/SimplyTransfer-Setup-v$appVersion.exe" -ForegroundColor Green
} else {
    Write-Error "Failed to build installer."
}
