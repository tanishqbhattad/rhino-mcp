<#
.SYNOPSIS
    Fail if dist/plugin/RhinoAIBridge.rhp is stale relative to the source.

.DESCRIPTION
    dist/plugin is the binary INSTALL.bat copies onto a user's machine. It once
    drifted six weeks behind the source, so every install shipped a plugin that
    was missing most of the server's commands.

    We deliberately do NOT compare file hashes: .NET embeds a fresh MVID in every
    compile, so two builds of identical source never match. Instead we check the
    two things that actually matter and are deterministic:

      1. the shipped assembly version equals <Version> in the .csproj
      2. every command registered in CommandHandler.cs is present as a string in
         the shipped binary (this is what catches "dist has no assert_geometry")

.EXAMPLE
    pwsh scripts/check_dist_current.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not reliably bound inside a param() default under Windows
# PowerShell 5.1, so resolve the repo root here instead.
if (-not $RepoRoot) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path $MyInvocation.MyCommand.Path -Parent }
    $RepoRoot = Split-Path $here -Parent
}

$shipped = Join-Path $RepoRoot 'dist/plugin/RhinoAIBridge.rhp'
$csproj  = Join-Path $RepoRoot 'plugin/RhinoAIBridge.csproj'
$handler = Join-Path $RepoRoot 'plugin/CommandHandler.cs'

foreach ($p in @($shipped, $csproj, $handler)) {
    if (-not (Test-Path $p)) { throw "Required file missing: $p" }
}

# --- 1. version ---------------------------------------------------------
$expected = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$expected = "$expected".Trim()
$actual = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $shipped)).FileVersion
$actualShort = ($actual -split '\.')[0..2] -join '.'

Write-Host "csproj version : $expected"
Write-Host "shipped version: $actualShort  (raw $actual)"

if ($actualShort -ne $expected) {
    throw @"
dist/plugin is STALE.
  csproj declares $expected but the shipped binary reports $actualShort.
Rebuild and refresh it:
  dotnet build plugin/RhinoAIBridge.csproj -c Release
  copy plugin/bin/Release/net8.0/* into dist/plugin/
"@
}

# --- 2. command coverage -------------------------------------------------
# Only look inside the dispatch-table initializer, otherwise every JObject key
# in the file (["area"], ["hint"], ...) is mistaken for a command.
$source = Get-Content $handler -Raw
$start = $source.IndexOf('_commands = new Dictionary<string, Func<JObject, JObject>>')
if ($start -lt 0) { throw "Could not locate the _commands dispatch table in CommandHandler.cs" }
$end = $source.IndexOf('};', $start)
if ($end -lt 0) { throw "Could not find the end of the _commands dispatch table" }
$block = $source.Substring($start, $end - $start)

$commands = [regex]::Matches($block, '\["([a-z0-9_]+)"\]\s*=') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

if ($commands.Count -lt 50) {
    throw "Only $($commands.Count) commands parsed from the dispatch table - the parser is probably wrong."
}

# .NET keeps identifiers in a UTF-8 heap and user strings in a UTF-16 heap, so a
# literal can appear as either. UTF-16 strings are not guaranteed to start on an
# even byte offset, and an even-aligned decode mangles the odd-aligned ones, so
# decode from both offsets before declaring anything missing.
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $shipped))
$ascii  = [System.Text.Encoding]::ASCII.GetString($bytes)
$utf16a = [System.Text.Encoding]::Unicode.GetString($bytes)
$utf16b = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)

$missing = @($commands | Where-Object {
    -not ($ascii.Contains($_) -or $utf16a.Contains($_) -or $utf16b.Contains($_))
})

Write-Host "commands in source : $($commands.Count)"
Write-Host "missing from binary: $($missing.Count)"

if ($missing.Count -gt 0) {
    throw @"
dist/plugin is STALE - it does not contain $($missing.Count) command(s) that the
source registers: $($missing -join ', ')
Rebuild and refresh dist/plugin before committing.
"@
}

Write-Host "dist/plugin is current." -ForegroundColor Green
