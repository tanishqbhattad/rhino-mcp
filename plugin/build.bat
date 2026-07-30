@echo off
echo.
echo   ====================================
echo    Building Rhino AI Bridge v4.11.0
echo   ====================================
echo.
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo   ERROR: .NET 8 SDK not found
    echo   Download: https://dotnet.microsoft.com/download/dotnet/8.0
    pause & exit /b 1
)
echo   [1/3] Restoring packages...
dotnet restore
if %errorlevel% neq 0 ( echo   FAILED & pause & exit /b 1 )
echo   [2/3] Building...
dotnet build --configuration Release
if %errorlevel% neq 0 ( echo   FAILED & pause & exit /b 1 )
echo   [3/3] Installing...
set PD=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\RhinoAIBridge
set BD=bin\Release\net8.0
if not exist "%PD%" mkdir "%PD%"
del /Q "%PD%\*.dll" "%PD%\*.deps.json" "%PD%\*.runtimeconfig.json" >nul 2>&1
if exist "%PD%\runtimes" rmdir /S /Q "%PD%\runtimes"
copy /Y "%BD%\*.*" "%PD%\" >nul
if exist "%BD%\runtimes" xcopy /E /I /Y "%BD%\runtimes" "%PD%\runtimes" >nul
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
pause
