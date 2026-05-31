#Requires -Version 5.1
<#
.SYNOPSIS
    RhinoAIBridge v4.7.6 installer wrapper.

.DESCRIPTION
    Uses the maintained root INSTALL.bat so every installation path deploys
    the same pre-built plugin payload and configures the same AI clients.
    The target computer does not need the .NET SDK.
#>

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$installer = Join-Path $repoRoot "INSTALL.bat"

if (-not (Test-Path $installer)) {
    Write-Host "ERROR: INSTALL.bat not found at $installer" -ForegroundColor Red
    exit 1
}

Write-Host "Starting the RhinoAIBridge one-click installer..." -ForegroundColor Cyan
& cmd.exe /c "`"$installer`""
exit $LASTEXITCODE
