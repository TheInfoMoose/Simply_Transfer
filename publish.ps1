<#
.SYNOPSIS
    Automated build and deployment publishing script for Simply Transfer.

.DESCRIPTION
    Compiles the solution in Release configuration, verifies or runs automated test suites,
    and publishes a self-contained or framework-dependent Windows x64 distribution package.
    Ensures native AlphaVSS and Ijwhost assemblies are properly colocated.
    Supports execution on both Windows and Linux / macOS developer environments.

.PARAMETER SelfContained
    Switch to publish a fully self-contained distribution (does not require .NET 8 desktop runtime pre-installed).

.PARAMETER OutputDir
    Target directory for the published deployment output. Defaults to "./publish/SimplyTransfer-x64".

.PARAMETER DotNetPath
    Optional explicit path to the dotnet executable.

.PARAMETER Clean
    Purges any previous build output directory before compiling.

.PARAMETER SkipTests
    Skips test execution.

.EXAMPLE
    .\publish.ps1 -SelfContained

.EXAMPLE
    pwsh ./publish.ps1 -Clean
#>

[CmdletBinding()]
param(
    [switch]$SelfContained = $true,
    [string]$OutputDir = "",
    [string]$DotNetPath = "",
    [switch]$Clean = $false,
    [switch]$SkipTests = $false,
    [switch]$PublishSingleFile = $true
)

$ErrorActionPreference = "Stop"

$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) {
    $scriptDir = (Get-Location).Path
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $scriptDir "dist/SimplyTransfer-Release"
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "   Simply Transfer Deployment Publisher (.NET 8)  " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

if ($Clean -and (Test-Path $OutputDir)) {
    Write-Host "`nCleaning existing output directory '$OutputDir'..." -ForegroundColor Yellow
    Remove-Item -Path $OutputDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] Cleaned previous build output." -ForegroundColor Green
}

# 1. Locate dotnet CLI
$dotnetExe = $null

if (-not [string]::IsNullOrWhiteSpace($DotNetPath) -and (Test-Path $DotNetPath)) {
    $dotnetExe = (Resolve-Path $DotNetPath).Path
} elseif (Get-Command "dotnet" -ErrorAction SilentlyContinue) {
    $dotnetExe = (Get-Command "dotnet").Source
} else {
    $candidatePaths = @(
        "$HOME/.dotnet/dotnet",
        "$env:DOTNET_ROOT/dotnet",
        "/usr/bin/dotnet",
        "/usr/lib64/dotnet/dotnet",
        "/usr/share/dotnet/dotnet",
        "$env:DOTNET_ROOT\dotnet.exe",
        "C:\Program Files\dotnet\dotnet.exe",
        "C:\Program Files (x86)\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    )

    foreach ($candidate in $candidatePaths) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            $dotnetExe = (Resolve-Path $candidate).Path
            break
        }
    }
}

if (-not $dotnetExe) {
    Write-Host "`n[!] The .NET 8 SDK was not found in PATH or standard installation locations." -ForegroundColor Red
    Write-Host "`nTo install the .NET 8 SDK, you can choose one of the following options:" -ForegroundColor Yellow
    Write-Host "  1. Windows Package Manager (winget):" -ForegroundColor White
    Write-Host "     winget install Microsoft.DotNet.SDK.8" -ForegroundColor Cyan
    Write-Host "  2. Linux (Fedora):" -ForegroundColor White
    Write-Host "     curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0" -ForegroundColor Cyan
    Write-Host "  3. Official Microsoft Download:" -ForegroundColor White
    Write-Host "     https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
    Write-Host "`nIf you have .NET installed in a custom directory, specify it directly:" -ForegroundColor Yellow
    Write-Host "  .\publish.ps1 -DotNetPath '/path/to/dotnet'" -ForegroundColor Cyan
    exit 1
}

$dotnetVersion = & $dotnetExe --version
Write-Host "[OK] .NET SDK Located: $dotnetExe ($dotnetVersion)" -ForegroundColor Green

$solutionPath = Join-Path $scriptDir "SimplyTransfer.sln"
$projectPath = Join-Path $scriptDir "SimplyTransfer.UI/SimplyTransfer.UI.csproj"

# 2. Restore NuGet Packages
Write-Host "`n[1/4] Restoring NuGet dependencies..." -ForegroundColor Yellow
& $dotnetExe restore $solutionPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "NuGet package restore failed. Aborting deployment publish."
    exit $LASTEXITCODE
}
Write-Host "[OK] Dependencies restored successfully." -ForegroundColor Green

# 3. Run or Verify Test Suite
Write-Host "`n[2/4] Testing / Verifying solution..." -ForegroundColor Yellow
$runningOnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

if ($SkipTests) {
    Write-Host "[INFO] Automated unit tests skipped by -SkipTests switch." -ForegroundColor Cyan
} elseif (-not $runningOnWindows) {
    Write-Host "[INFO] Non-Windows host detected: Verifying compilation of all solution projects and tests..." -ForegroundColor Cyan
    & $dotnetExe build $solutionPath --configuration Release --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Compilation of solution and test suite failed. Aborting deployment publish."
        exit $LASTEXITCODE
    }
    Write-Host "[OK] All solution projects and test suites compiled cleanly without errors." -ForegroundColor Green
    Write-Host "     (Note: Full WPF VSTest execution requires the Windows Desktop runtime on Windows)." -ForegroundColor DarkGray
} else {
    & $dotnetExe test $solutionPath --configuration Release --no-restore --verbosity normal
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Automated tests failed. Aborting deployment publish."
        exit $LASTEXITCODE
    }
    Write-Host "[OK] All unit tests passed successfully." -ForegroundColor Green
}

# 4. Publish SimplyTransfer.UI
Write-Host "`n[3/4] Publishing SimplyTransfer.UI (win-x64)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $projectPath,
    "-c", "Release",
    "-r", "win-x64",
    "-o", $OutputDir
)

if ($SelfContained) {
    $publishArgs += "--self-contained"
    if ($PublishSingleFile) {
        $publishArgs += "-p:PublishSingleFile=true"
        $publishArgs += "-p:IncludeNativeLibrariesForSelfContained=true"
        Write-Host "Publish mode: Self-Contained Single-File Executable" -ForegroundColor Magenta
    } else {
        $publishArgs += "-p:PublishSingleFile=false"
        Write-Host "Publish mode: Self-Contained Directory" -ForegroundColor Magenta
    }
} else {
    $publishArgs += "--no-self-contained"
    Write-Host "Publish mode: Framework-Dependent (.NET 8 Windows Desktop Runtime required)" -ForegroundColor Magenta
}

& $dotnetExe @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish command failed."
    exit $LASTEXITCODE
}

# 5. Verify & Copy AlphaVSS native binaries if needed
Write-Host "`n[4/4] Verifying AlphaVSS native dependencies..." -ForegroundColor Yellow
$nativeSource = Join-Path $OutputDir "runtimes/win-x64/native"

if (Test-Path $nativeSource) {
    Get-ChildItem -Path $nativeSource -Filter "*.dll" | ForEach-Object {
        $dest = Join-Path $OutputDir $_.Name
        if (-not (Test-Path $dest)) {
            Copy-Item -Path $_.FullName -Destination $dest -Force
            Write-Host "Copied native dependency: $($_.Name)" -ForegroundColor Gray
        }
    }
}

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host "   Deployment Build Succeeded!                    " -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
$resolvedOut = (Resolve-Path $OutputDir).Path
$exePath = Join-Path $resolvedOut "SimplyTransfer.UI.exe"

# 6. Package to ZIP
Write-Host "`n[5/5] Packaging deployment to ZIP archive..." -ForegroundColor Yellow
$zipPath = Join-Path $scriptDir "SimplyTransfer-Windows-x64.zip"
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}
Compress-Archive -Path "$resolvedOut\*" -DestinationPath $zipPath
Write-Host "[OK] Deployment packaged to $zipPath" -ForegroundColor Green

Write-Host "`nOutput Location: $resolvedOut" -ForegroundColor White
Write-Host "Executable:      $exePath" -ForegroundColor White
Write-Host "Deployment ZIP:  $zipPath" -ForegroundColor White
Write-Host ""
Write-Host "Note: To use Volume Shadow Copy (VSS), run the application as Administrator." -ForegroundColor Yellow
