@echo off
setlocal enabledelayedexpansion

title Rhino MCP Quick Repair

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "PLUGIN_DIR=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge"
set "DIST_DIR=%ROOT%\dist\plugin"

echo.
echo  ============================================================
echo    Rhino MCP Quick Repair
echo  ============================================================
echo.
echo  This fixes the common stuck state:
echo    - stale rhino-architect MCP helpers
echo    - Rhino still running invisibly after close
echo    - old RhinoAIBridge.rhp still loaded
echo.

echo  [1/4] Closing stale MCP helper processes...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process rhino-architect -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue"
echo  OK
echo.

echo  [2/4] Checking whether Rhino is still running...
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Get-Process Rhino -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"
if errorlevel 1 goto :rhino_closed

echo.
echo  Rhino is still running.
echo.
echo  If Rhino is visible: save your work and close Rhino normally, then press N here and rerun this BAT.
echo  If Rhino is NOT visible / already closed but stuck in the background, press Y to force-close it.
echo.
choice /C YN /N /M "Force-close background Rhino now? [Y/N]: "
if errorlevel 2 goto :cancel_rhino
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process Rhino -ErrorAction SilentlyContinue | Stop-Process -Force"
if errorlevel 1 goto :err_close_rhino
echo  Rhino closed.
goto :install_plugin

:cancel_rhino
echo.
echo  Stopped before plugin install. Close Rhino, then run this BAT again.
pause
exit /b 1

:err_close_rhino
echo.
echo  ERROR: Could not close Rhino automatically.
echo  Open Task Manager, end Rhino.exe, then run this BAT again.
pause
exit /b 1

:rhino_closed
echo  Rhino is closed.

:install_plugin
echo.
echo  [3/4] Installing fixed Rhino plugin...
if not exist "%DIST_DIR%\RhinoAIBridge.rhp" (
    echo  ERROR: Missing %DIST_DIR%\RhinoAIBridge.rhp
    pause
    exit /b 1
)
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%"
del /Q "%PLUGIN_DIR%\*.dll" "%PLUGIN_DIR%\*.deps.json" "%PLUGIN_DIR%\*.runtimeconfig.json" >nul 2>&1
if exist "%PLUGIN_DIR%\runtimes" rmdir /S /Q "%PLUGIN_DIR%\runtimes"
copy /Y "%DIST_DIR%\*.*" "%PLUGIN_DIR%\" >nul
if errorlevel 1 goto :err_install
if exist "%DIST_DIR%\runtimes" xcopy /E /I /Y "%DIST_DIR%\runtimes" "%PLUGIN_DIR%\runtimes" >nul
if errorlevel 1 goto :err_install
echo  OK
goto :check_server

:err_install
echo.
echo  ERROR: Could not copy the plugin files.
echo  Make sure Rhino is fully closed, then run this BAT again.
pause
exit /b 1

:check_server
echo.
echo  [4/4] Checking MCP server dependencies...
cd /d "%ROOT%\server"
uv sync --frozen
if not errorlevel 1 goto :done
echo  Retrying with local uv cache...
set "UV_CACHE_DIR=%ROOT%\server\.uv-cache"
uv sync --frozen
if errorlevel 1 goto :err_uv
goto :done

:err_uv
echo.
echo  WARNING: uv sync failed. The plugin was installed, but the Python MCP server may need repair.
echo  Send me this window text if ping still fails.
pause
exit /b 1

:done
echo.
echo  ============================================================
echo    REPAIR COMPLETE
echo  ============================================================
echo.
echo  Next:
echo    1. Open Rhino 8.
echo    2. Type: AIBridge
echo    3. Pick Developer mode.
echo    4. Restart Claude/Codex/Antigravity.
echo    5. Ping Rhino again.
echo.
pause
