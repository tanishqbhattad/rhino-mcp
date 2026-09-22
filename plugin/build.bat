@echo off
setlocal
rem Run from this script's own folder. dotnet restore/build and the bin\ paths below
rem are all relative, so launching build.bat from anywhere else used to restore and
rem build in the wrong directory while still reporting success.
pushd "%~dp0"
for /f "usebackq delims=" %%v in ("%~dp0..\VERSION") do set "VER=%%v"
if not defined VER set "VER=unknown"
echo.
echo   ====================================
echo    Building Rhino AI Bridge v%VER%
echo   ====================================
echo.
where dotnet >nul 2>&1
if errorlevel 1 goto :no_dotnet
echo   [1/3] Restoring packages...
dotnet restore
if errorlevel 1 goto :build_failed
echo   [2/3] Building...
dotnet build --configuration Release
if errorlevel 1 goto :build_failed
echo   [3/3] Installing...
set "PD=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge"
set "BD=bin\Release\net8.0"
if not exist "%PD%" mkdir "%PD%"
rem Check Rhino is not holding the plugin BEFORE deleting anything. The old script
rem deleted the dependency DLLs first and then let the copy fail silently, which
rem could leave a half-installed plugin behind a "BUILD SUCCESSFUL" banner.
rem Opening the .rhp for append writes nothing, but fails while Rhino has it loaded.
if not exist "%PD%\RhinoAIBridge.rhp" goto :install
type nul >> "%PD%\RhinoAIBridge.rhp" 2>nul || goto :rhino_running
:install
del /Q "%PD%\*.dll" "%PD%\*.deps.json" "%PD%\*.runtimeconfig.json" >nul 2>&1
if exist "%PD%\runtimes" rmdir /S /Q "%PD%\runtimes"
copy /Y "%BD%\*.*" "%PD%\" >nul
if errorlevel 1 goto :copy_failed
if not exist "%BD%\runtimes" goto :installed
xcopy /E /I /Y "%BD%\runtimes" "%PD%\runtimes" >nul
if errorlevel 1 goto :copy_failed
:installed
echo.
echo   ====================================
echo    BUILD SUCCESSFUL
echo   ====================================
echo.
echo   Plugin: %PD%\RhinoAIBridge.rhp
echo.
echo   FIRST TIME: Rhino 8 ^> PlugInManager ^> Install ^> browse to .rhp
echo   AFTER THAT: Auto-loads. Type "AIBridge" to restart server.
echo   LOGS: %APPDATA%\AIBridge\logs\
echo.
popd
pause
exit /b 0

:no_dotnet
echo   ERROR: .NET 8 SDK not found
echo   Download: https://dotnet.microsoft.com/download/dotnet/8.0
goto :fail

:build_failed
echo   ERROR: the build failed - see the dotnet output above.
goto :fail

:rhino_running
echo.
echo   ERROR: Rhino is running and has the plugin loaded, so it cannot be replaced.
echo   Nothing was installed or deleted. Close Rhino and run build.bat again.
echo   The new build itself is ready in %CD%\%BD%
goto :fail

:copy_failed
echo.
echo   ERROR: could not copy the plugin into %PD%
echo   The install may be incomplete. Close Rhino and run build.bat again.
goto :fail

:fail
echo.
echo   ====================================
echo    BUILD FAILED
echo   ====================================
echo.
popd
pause
exit /b 1
