param (
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

Write-Host "Building and publishing SimplyTransfer ($Configuration)..." -ForegroundColor Cyan
dotnet publish SimplyTransfer.UI/SimplyTransfer.UI.csproj -c $Configuration -r win-x64 --self-contained true -o "$ScriptDir/dist/publish"

# Check if InnoSetup is installed
$ISCC = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $ISCC)) {
    $ISCC = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
}

if (-not (Test-Path $ISCC)) {
    Write-Warning "Inno Setup 6 (ISCC.exe) not found. Skipping installer compilation."
    Write-Host "Publish output is available at: $ScriptDir/dist/publish"
    exit 0
}

Write-Host "Compiling InnoSetup Installer..." -ForegroundColor Cyan
& $ISCC "$ScriptDir/installer.iss"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Installer built successfully: dist/SimplyTransfer-Setup.exe" -ForegroundColor Green
} else {
    Write-Error "Failed to build installer."
}
