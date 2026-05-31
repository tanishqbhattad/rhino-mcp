#Requires -Version 5.1
<#
.SYNOPSIS
    RhinoAIBridge healthcheck - verifies every layer of the stack is alive.

.DESCRIPTION
    Checks:
      1. Rhino 8 process is running
      2. Port 9544 is open (TCP)
      3. TCP ping (protocol-level handshake) returns ok
      4. Protocol version matches server expectation (4.x)
      5. Python MCP server process is running
      6. Python venv is intact (uv run --frozen can resolve entry point)

    Run any time after starting Rhino + AIBridge:
        powershell -ExecutionPolicy Bypass -File install\rhino-mcp-healthcheck.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "SilentlyContinue"

$HOST_ADDR = "127.0.0.1"
$PORT      = 9544

function Banner($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function OK($msg)     { Write-Host "  [OK] $msg"    -ForegroundColor Green }
function FAIL($msg)   { Write-Host "  [X]  $msg"    -ForegroundColor Red; $script:failures++ }
function WARN($msg)   { Write-Host "  [!]  $msg"    -ForegroundColor Yellow }
function INFO($msg)   { Write-Host "  ... $msg"     -ForegroundColor Gray }

$script:failures = 0

# ── 1. Rhino process ──────────────────────────────────────────────────────────
Banner "1. Rhino 8 process"
$rhinoProc = Get-Process -Name "Rhino" -ErrorAction SilentlyContinue
if ($rhinoProc) {
    OK "Rhino running (PID $($rhinoProc.Id))"
} else {
    FAIL "Rhino is NOT running. Open Rhino 8 and type 'AIBridge'."
}

# ── 2. Port 9544 open ─────────────────────────────────────────────────────────
Banner "2. Port $PORT (TCP listener)"
$portTest = Test-NetConnection -ComputerName $HOST_ADDR -Port $PORT -WarningAction SilentlyContinue
if ($portTest.TcpTestSucceeded) {
    OK "Port $PORT is open"
} else {
    FAIL "Port $PORT is NOT open. In Rhino, type 'AIBridge' to start the server."
}

# ── 3. Protocol ping ──────────────────────────────────────────────────────────
Banner "3. Protocol ping (send/receive JSON)"
function Send-RhinoPing {
    param([string]$host_, [int]$port_)
    try {
        $tcp    = New-Object System.Net.Sockets.TcpClient($host_, $port_)
        $stream = $tcp.GetStream()
        function Exchange-Frame([string]$json) {
            $cmd = [System.Text.Encoding]::UTF8.GetBytes($json)
            $len = [System.BitConverter]::GetBytes([uint32]$cmd.Length)
            if ([System.BitConverter]::IsLittleEndian) { [Array]::Reverse($len) }
            $stream.Write($len, 0, 4)
            $stream.Write($cmd, 0, $cmd.Length)
            $stream.Flush()

            $header = New-Object byte[] 5
            $read = 0
            while ($read -lt 5) {
                $n = $stream.Read($header, $read, 5 - $read)
                if ($n -eq 0) { break }
                $read += $n
            }
            $lenBytes = $header[1..4]
            if ([System.BitConverter]::IsLittleEndian) { [Array]::Reverse($lenBytes) }
            $payLen = [System.BitConverter]::ToUInt32($lenBytes, 0)
            $payload = New-Object byte[] $payLen
            $read2 = 0
            while ($read2 -lt $payLen) {
                $n = $stream.Read($payload, $read2, $payLen - $read2)
                if ($n -eq 0) { break }
                $read2 += $n
            }
            return [System.Text.Encoding]::UTF8.GetString($payload)
        }

        $tokenPath = Join-Path $env:LOCALAPPDATA "AIBridge\token"
        if (Test-Path $tokenPath) {
            $token = (Get-Content $tokenPath -Raw -Encoding UTF8).Trim()
            if ($token) {
                $auth = Exchange-Frame ('{"type":"auth","token":"' + $token + '"}')
                $authJson = $auth | ConvertFrom-Json
                if ($authJson.status -ne "ok" -and $authJson.error_code -eq "AUTH_REQUIRED") {
                    throw "Local authentication failed. Restart AIBridge in Rhino."
                }
            }
        }
        $payload = Exchange-Frame '{"type":"ping","params":{}}'
        $stream.Close(); $tcp.Close()
        return $payload
    } catch {
        return $null
    }
}

if ($portTest.TcpTestSucceeded) {
    $resp = Send-RhinoPing $HOST_ADDR $PORT
    if ($resp) {
        try {
            $json = $resp | ConvertFrom-Json
            if ($json.status -eq "ok") {
                OK "Ping OK - build: $($json.build_hash)  protocol: $($json.protocol_version)"
            } else {
                FAIL "Ping returned non-ok status: $resp"
            }
        } catch {
            FAIL "Ping response not valid JSON: $resp"
        }
    } else {
        FAIL "No response to ping (server may be starting - retry in a few seconds)"
    }
} else {
    WARN "Skipping ping (port not open)"
}

# ── 4. Protocol version ───────────────────────────────────────────────────────
Banner "4. Protocol version check"
if ($resp) {
    try {
        $json = $resp | ConvertFrom-Json
        $ver  = $json.protocol_version
        if ($ver -match "^4\.") {
            OK "Protocol v$ver (compatible)"
        } elseif ($ver) {
            WARN "Protocol v$ver - expected 4.x. You may need to rebuild the plugin."
        } else {
            WARN "Protocol version not reported"
        }
    } catch { WARN "Could not parse version" }
} else {
    WARN "Skipping (no ping response)"
}

# ── 5. Python MCP server process ──────────────────────────────────────────────
Banner "5. Python MCP server process"
$pyProcs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "python|uv" -and $_.CommandLine -match "rhino.architect|rhino-architect" }
if ($pyProcs) {
    OK "MCP server process found (PID $($pyProcs[0].Id))"
} else {
    # Not a fatal failure - server may be running as a child of Claude
    WARN "No standalone MCP server process detected (normal if Claude Desktop is managing it)"
}

# ── 6. Python venv / entry point ─────────────────────────────────────────────
Banner "6. Python venv + entry point"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = if ((Split-Path -Leaf $scriptDir) -eq "install") { Split-Path -Parent $scriptDir } else { $scriptDir }
$serverDir = Join-Path $repoRoot "server"

if (Test-Path $serverDir) {
    $uvCmd = Get-Command uv -ErrorAction SilentlyContinue
    if ($uvCmd) {
        Push-Location $serverDir
        $check = & uv run --frozen python -c "from rhino_architect.server import main; print('entry_point_ok')" 2>&1
        Pop-Location
        if ($check -match "entry_point_ok") {
            OK "Python entry point imports cleanly"
        } else {
            FAIL "Python entry point check failed: $check"
        }
    } else {
        WARN "'uv' not in PATH - cannot check venv"
    }
} else {
    WARN "Server directory not found: $serverDir"
}

# ── Summary ───────────────────────────────────────────────────────────────────
Banner "Summary"
if ($script:failures -eq 0) {
    Write-Host "`n  All checks passed. RhinoAIBridge is healthy." -ForegroundColor Green
} else {
    Write-Host "`n  $($script:failures) check(s) failed - see [X] items above." -ForegroundColor Red
    Write-Host "  Quick fixes:" -ForegroundColor Yellow
    Write-Host "    - Open Rhino 8, type 'AIBridge' to start the server" -ForegroundColor Yellow
    Write-Host "    - Re-run: install\install-rhino-mcp.ps1 to repair config" -ForegroundColor Yellow
}
Write-Host ""
