<#
.SYNOPSIS
    Convenience wrapper for dest_prerequisites.ps1
#>
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$targetScript = Join-Path $scriptDir "dest_prerequisites.ps1"
& $targetScript @args
