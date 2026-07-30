# RhinoAIBridge -- Claude Desktop config patcher
# Called by INSTALL.bat:  powershell -File patch_config.ps1 "C:\path\to\server"
param([Parameter(Mandatory)][string]$ServerDir)

$ServerDir = $ServerDir.TrimEnd('\').TrimEnd('/')
$ConfigPath = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
$ConfigDir  = Split-Path $ConfigPath

if (-not (Test-Path $ConfigDir)) {
    Write-Host "  Claude Desktop not found -- skipping."
    Write-Host "  (Run INSTALL.bat again after installing Claude Desktop)"
    exit 0
}

# Read or create config
if (Test-Path $ConfigPath) {
    $bak = $ConfigPath + ".backup_" + (Get-Date -Format "yyyyMMdd_HHmmss")
    Copy-Item $ConfigPath $bak
    Write-Host "  Backed up existing config to: $bak"
    try {
        $cfg = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-Host "  WARNING: Existing config was invalid JSON -- starting fresh."
        $cfg = [PSCustomObject]@{}
    }
} else {
    $cfg = [PSCustomObject]@{}
}

# Ensure mcpServers key exists
if (-not ($cfg.PSObject.Properties.Name -contains "mcpServers")) {
    $cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([PSCustomObject]@{})
}

# Add/replace rhino-architect entry
$entry = [PSCustomObject]@{
    command = "uv"
    args    = @("--directory", $ServerDir, "run", "rhino-architect")
}
$cfg.mcpServers | Add-Member -NotePropertyName "rhino-architect" -NotePropertyValue $entry -Force

# Write back as UTF-8 without BOM
$json = $cfg | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($ConfigPath, $json, [System.Text.Encoding]::UTF8)

Write-Host "  Claude Desktop configured successfully."
Write-Host "  MCP server path: $ServerDir"
Write-Host "  --> Restart Claude Desktop to activate."
