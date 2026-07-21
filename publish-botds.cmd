@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem ============================================================================
rem  BotDs publish — one-step win-x64 self-contained deployment
rem
rem  Usage:
rem    publish-botds.cmd [--config Release|Debug]
rem
rem  Default: Release, outputs to .\publish\
rem  Output:  publish\BotDs.App.exe  (self-contained, no .NET runtime required)
rem ============================================================================

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"

set "CONFIG=Release"

:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--config" (
    if "%~2"=="" (
        echo ERROR: --config requires a value ^(Release or Debug^)
        exit /b 1
    )
    set "CONFIG=%~2"
    shift
    shift
    goto parse_args
)
if /I "%~1"=="--help" (
    echo Usage: publish-botds.cmd [--config Release^|Debug]
    echo Default: Release, outputs to .\publish\
    exit /b 0
)
echo ERROR: Unknown argument: %~1
echo Usage: publish-botds.cmd [--config Release^|Debug]
exit /b 1

:args_done

rem Validate config
if /I not "%CONFIG%"=="Release" if /I not "%CONFIG%"=="Debug" (
    echo ERROR: --config must be Release or Debug. Got: %CONFIG%
    exit /b 1
)

if not exist "%ROOT%\src\BotDs.App\BotDs.App.csproj" (
    echo ERROR: BotDs.App.csproj not found at expected path.
    echo   %ROOT%\src\BotDs.App\BotDs.App.csproj
    exit /b 1
)

echo.
echo ============================================================
echo  BotDs publish — win-x64 self-contained ^(%CONFIG%^)
echo ============================================================
echo.
echo Output directory: %ROOT%\publish\
echo.

dotnet publish "%ROOT%\src\BotDs.App\BotDs.App.csproj" -c "%CONFIG%" -r win-x64 -p:SelfContained=true -p:PublishSingleFile=false -p:DebugType=embedded -o "%ROOT%\publish" --nologo

if errorlevel 1 (
    echo.
    echo Publish FAILED.
    exit /b 1
)

echo.
echo ============================================================
echo  Publish succeeded.
echo.
echo  Launch with:
echo    run-botds.cmd --publish [--open] [--process-id PID]
echo ============================================================
exit /b 0
