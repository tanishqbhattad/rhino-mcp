@echo off
setlocal enabledelayedexpansion

:: ============================================================
::   Rhino MCP / RhinoAIBridge -- Installer
::   https://github.com/tanishqbhattad/rhino-mcp
::   Pre-built plugin -- .NET SDK NOT required.
::
::   This installer NEVER closes without telling you what happened.
::   Everything is also written to install-log.txt next to this file.
:: ============================================================

title Rhino MCP Installer

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "DIST_DIR=%ROOT%\dist\plugin"
set "SERVER_DIR=%ROOT%\server"
set "SCRIPTS_DIR=%ROOT%\scripts"
set "RHINO_PLUGIN_DIR=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge"
set "LOG=%ROOT%\install-log.txt"

:: Result flags used by the verification report at the end.
set "R_PLUGIN=FAIL"
set "R_UV=FAIL"
set "R_SERVER=FAIL"
set "R_CONFIG=none"
set "FATAL="

echo Rhino MCP install log  %DATE% %TIME% > "%LOG%"
echo root=%ROOT% >> "%LOG%"

call :say ""
call :say " ============================================================"
call :say "   Rhino MCP  -  RhinoAIBridge installer"
call :say "   https://github.com/tanishqbhattad/rhino-mcp"
call :say " ============================================================"
call :say ""
call :say "  Steps: [1] Rhino plugin   [2] uv + Python server   [3] AI clients"
call :say "  A full log is written to: install-log.txt"
call :say ""

:: -- Detect AI clients ---------------------------------------------------------
set "HAS_CLAUDE=0"
set "HAS_CODEX=0"
set "HAS_ANTIGRAVITY=0"
if exist "%APPDATA%\Claude" set "HAS_CLAUDE=1"
where codex >nul 2>&1
if not errorlevel 1 set "HAS_CODEX=1"
if exist "%USERPROFILE%\.codex" set "HAS_CODEX=1"
if exist "%USERPROFILE%\.gemini\antigravity" set "HAS_ANTIGRAVITY=1"

call :say "  Detected AI clients:"
if "!HAS_CLAUDE!"=="1"      (call :say "    [x] Claude Desktop")      else (call :say "    [ ] Claude Desktop     (not found)")
if "!HAS_CODEX!"=="1"       (call :say "    [x] OpenAI Codex")        else (call :say "    [ ] OpenAI Codex       (not found)")
if "!HAS_ANTIGRAVITY!"=="1" (call :say "    [x] Gemini Antigravity") else (call :say "    [ ] Gemini Antigravity (not found)")
call :say ""

:: ==============================================================
::  [1/3] Rhino plugin
:: ==============================================================
call :say "  [1/3] Installing the Rhino plugin ..."

if not exist "%DIST_DIR%\RhinoAIBridge.rhp" (
    call :say "  ERROR: Pre-built plugin not found:"
    call :say "         %DIST_DIR%\RhinoAIBridge.rhp"
    call :say "         Extract the whole ZIP (or git clone) before running this."
    set "FATAL=1"
    goto :report
)

:: Rhino must be closed - it holds the .rhp open while running.
set "RHINO_RUNNING=0"
for /f %%A in ('powershell -NoProfile -Command "@(Get-Process -Name Rhino,Rhinoceros -ErrorAction SilentlyContinue).Count" 2^>nul') do set "RHINO_RUNNING=%%A"
if not "!RHINO_RUNNING!"=="0" (
    call :say "  ERROR: Rhino is currently running, so its plugin file is locked."
    call :say "         Close Rhino completely, then run this installer again."
    set "FATAL=1"
    goto :report
)

if not exist "%RHINO_PLUGIN_DIR%" mkdir "%RHINO_PLUGIN_DIR%" >>"%LOG%" 2>&1
del /Q "%RHINO_PLUGIN_DIR%\*.dll" "%RHINO_PLUGIN_DIR%\*.deps.json" "%RHINO_PLUGIN_DIR%\*.runtimeconfig.json" >nul 2>&1
if exist "%RHINO_PLUGIN_DIR%\runtimes" rmdir /S /Q "%RHINO_PLUGIN_DIR%\runtimes" >nul 2>&1
copy /Y "%DIST_DIR%\*.*" "%RHINO_PLUGIN_DIR%\" >>"%LOG%" 2>&1
if exist "%DIST_DIR%\runtimes" xcopy /E /I /Y "%DIST_DIR%\runtimes" "%RHINO_PLUGIN_DIR%\runtimes" >>"%LOG%" 2>&1

if exist "%RHINO_PLUGIN_DIR%\RhinoAIBridge.rhp" (
    set "R_PLUGIN=OK"
    call :say "  OK   plugin copied to %RHINO_PLUGIN_DIR%"
) else (
    call :say "  ERROR: the plugin did not land in %RHINO_PLUGIN_DIR%"
    call :say "         See install-log.txt for the copy errors."
    set "FATAL=1"
    goto :report
)

:: Make the bridge load with Rhino (no need to type AIBridge every session).
:: Only applies once Rhino has registered the plugin at least once.
powershell -NoProfile -Command ^
  "$k = Get-ChildItem 'HKCU:\Software\McNeel\Rhinoceros\8.0\Plug-Ins' -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).Name -like '*AIBridge*' }; if ($k) { foreach ($i in $k) { Set-ItemProperty $i.PSPath -Name LoadMode -Value 1 -ErrorAction SilentlyContinue }; 'set' } else { 'pending' }" >>"%LOG%" 2>&1

:: ==============================================================
::  [2/3] uv + Python MCP server
:: ==============================================================
call :say ""
call :say "  [2/3] Checking uv (Python package manager) ..."

where uv >nul 2>&1
if not errorlevel 1 goto :uv_ok

call :say "       uv not found - installing it now (this can take a minute) ..."
powershell -ExecutionPolicy Bypass -NoProfile -Command "irm https://astral.sh/uv/install.ps1 | iex" >>"%LOG%" 2>&1
set "PATH=%USERPROFILE%\.local\bin;%PATH%"
where uv >nul 2>&1
if errorlevel 1 (
    call :say "  ERROR: uv could not be installed automatically."
    call :say "         Install it from https://docs.astral.sh/uv/getting-started/installation/"
    call :say "         then run this installer again."
    set "FATAL=1"
    goto :report
)

:uv_ok
set "R_UV=OK"
call :say "  OK   uv is available"

:: A running AI client keeps the previous server executable open, which stops uv
:: from replacing it - the same class of lock as Rhino holding the .rhp.
set "MCP_RUNNING=0"
for /f %%A in ('powershell -NoProfile -Command "@(Get-Process -Name rhino-architect -ErrorAction SilentlyContinue).Count" 2^>nul') do set "MCP_RUNNING=%%A"
if not "!MCP_RUNNING!"=="0" (
    call :say ""
    call :say "  ERROR: a Rhino MCP server is still running, so its files are locked."
    call :say "         This happens when Claude Desktop / Codex / Antigravity is open."
    call :say "         Quit your AI client completely, then run this installer again."
    set "FATAL=1"
    goto :report
)

call :say ""
call :say "       Installing Python server dependencies (first run downloads a lot) ..."
cd /d "%SERVER_DIR%"
uv sync --frozen >>"%LOG%" 2>&1
if errorlevel 1 (
    call :say "       Global uv cache unavailable - retrying with a local cache ..."
    set "UV_CACHE_DIR=%SERVER_DIR%\.uv-cache"
    uv sync --frozen >>"%LOG%" 2>&1
)

:: Prove the server actually imports rather than trusting the exit code.
set "RHINO_HEARTBEAT_STARTUP_MAX_WAIT=1"
uv run --frozen python -c "import rhino_architect.server as s; print('SERVER_OK', s._VERSION)" >>"%LOG%" 2>&1
if errorlevel 1 (
    call :say "  ERROR: the MCP server did not import cleanly."
    call :say "         Look at the end of install-log.txt for the Python error."
    set "FATAL=1"
    goto :report
)
set "R_SERVER=OK"
call :say "  OK   MCP server installed and imports cleanly"

:: ==============================================================
::  [3/3] AI client configuration
:: ==============================================================
call :say ""
call :say "  [3/3] Configuring AI clients ..."
set "CONFIGURED="

if "!HAS_CLAUDE!"=="1" call :patch_client "Claude Desktop" patch_claude_config.py
if "!HAS_CODEX!"=="1" call :patch_client "OpenAI Codex" patch_codex_config.py
if "!HAS_ANTIGRAVITY!"=="1" call :patch_client "Gemini Antigravity" patch_antigravity_config.py

if not defined CONFIGURED (
    set "R_CONFIG=none"
    call :say "       No AI client was configured (none detected, or all failed)."
) else (
    set "R_CONFIG=!CONFIGURED!"
)

:: ==============================================================
::  Verification report - ALWAYS shown
:: ==============================================================
:report
call :say ""
call :say " ============================================================"
if defined FATAL (
    call :say "   INSTALLATION FAILED"
) else (
    call :say "   INSTALLATION COMPLETE"
)
call :say " ============================================================"
call :say ""
call :say "   Rhino plugin ........ !R_PLUGIN!"
call :say "   uv .................. !R_UV!"
call :say "   Python MCP server ... !R_SERVER!"
call :say "   Clients configured .. !R_CONFIG!"
call :say ""

if defined FATAL (
    call :say "   Something above failed. The full log is at:"
    call :say "     %LOG%"
    call :say "   Open an issue with that file attached and it can be diagnosed:"
    call :say "     https://github.com/tanishqbhattad/rhino-mcp/issues"
    call :say ""
    goto :hold
)

call :say "   NEXT STEPS"
call :say ""
call :say "   1. Open Rhino 8."
call :say "      First time only: run the PlugInManager command, click Install,"
call :say "      and pick:"
call :say "        %RHINO_PLUGIN_DIR%\RhinoAIBridge.rhp"
call :say "      After that the bridge starts automatically with Rhino."
call :say ""
call :say "   2. Restart your AI client so it picks up the new server."
call :say ""
call :say "   3. Ask it:  ping Rhino"
call :say "      A healthy reply shows status ok, your Rhino version and units."
call :say ""
call :say "   Troubleshooting:  install\rhino-mcp-healthcheck.ps1"
call :say "   Log of this run:  install-log.txt"
call :say ""

:hold
call :say " ============================================================"
echo.
echo  This window will stay open. Press ENTER to close it.
:: set /p waits for a real ENTER, so a stray buffered keypress (from mashing a
:: key at an earlier prompt) cannot dismiss the report before it is read.
set /p "_dummy="
endlocal
exit /b 0

:: ---------------------------------------------------------------
:: :say <text>   echo to console AND append to the log
:: ---------------------------------------------------------------
:say
set "_line=%~1"
echo(!_line!
echo(!_line!>>"%LOG%"
exit /b 0

:: ---------------------------------------------------------------
:: :patch_client <display name> <script>
:: ---------------------------------------------------------------
:patch_client
call :say "       Configuring %~1 ..."
cd /d "%SERVER_DIR%"
uv run python "%SCRIPTS_DIR%\%~2" "%SERVER_DIR%" >>"%LOG%" 2>&1
if errorlevel 1 (
    call :say "       WARNING: %~1 could not be configured automatically."
    call :say "                Details are in install-log.txt."
) else (
    call :say "       OK   %~1"
    if defined CONFIGURED (set "CONFIGURED=!CONFIGURED!, %~1") else (set "CONFIGURED=%~1")
)
exit /b 0

