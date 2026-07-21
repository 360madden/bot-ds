@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem ============================================================================
rem  BotDs dashboard launcher (thin convenience wrapper)
rem  Repo-root entry point. Application logic stays in C#/.NET.
rem
rem  Usage:
rem    run-botds.cmd [options]
rem
rem  Options:
rem    --help                 Show this help
rem    --url URL              Listen URL (default: http://localhost:5068)
rem    --process-name NAME    Target process basename (default: rift_x64)
rem    --process-id PID       Optional explicit PID (authoritative when set)
rem    --config Debug|Release Build/run configuration (default: Debug)
rem    --build                Build the App project before run
rem    --no-build             Skip build for dotnet run (pass --no-build)
rem    --publish              Prefer publish\BotDs.App.exe when present
rem    --dev                  Force Development + project run (not publish)
rem    --open                 Open the dashboard URL in the default browser
rem
rem  Precedence per setting (highest first):
rem    1) CLI flags
rem    2) Existing process environment and optional botds.local.cmd
rem    3) Built-in local-dev defaults
rem
rem  Examples:
rem    run-botds.cmd
rem    run-botds.cmd --open --process-name rift_x64
rem    run-botds.cmd --process-id 31584 --open
rem    run-botds.cmd --publish --config Release
rem
rem  Dashboard API is loopback-only with no token auth.
rem ============================================================================

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "ROOT=%SCRIPT_DIR%"

set "APP_PROJECT=%ROOT%\src\BotDs.App\BotDs.App.csproj"
set "APP_PUBLISH_EXE=%ROOT%\publish\BotDs.App.exe"
set "PROFILES_DIR=%ROOT%\profiles"
set "LOCAL_OVERRIDES=%ROOT%\botds.local.cmd"

set "BOTDS_URL=http://localhost:5068"
set "BOTDS_PROCESS_NAME=rift_x64"
set "BOTDS_PROCESS_ID="
set "BOTDS_CONFIG=Debug"
set "BOTDS_OPEN=0"
set "BOTDS_BUILD=0"
set "BOTDS_NO_BUILD=0"
set "BOTDS_PREFER_PUBLISH=0"
set "BOTDS_FORCE_DEV=0"
set "BOTDS_CLI_URL="
set "BOTDS_CLI_PROCESS_NAME="
set "BOTDS_CLI_PROCESS_ID="
set "BOTDS_OPTION="
set "BOTDS_HAD_ARGS=0"
if not "%~1"=="" set "BOTDS_HAD_ARGS=1"

rem Pause only for no-arg Explorer launches (window would otherwise vanish).
rem Do not treat every "cmd /c path\to\script.cmd args" as double-click.
set "BOTDS_PAUSE_ON_FAIL=0"
if "%BOTDS_HAD_ARGS%"=="0" (
  echo.%CMDCMDLINE% | find /I "/c" >nul 2>&1
  if not errorlevel 1 (
    echo.%CMDCMDLINE% | find /I "%~f0" >nul 2>&1
    if not errorlevel 1 set "BOTDS_PAUSE_ON_FAIL=1"
  )
)

rem Optional personal overrides (URL, process name). Never commit this file.
if exist "%LOCAL_OVERRIDES%" call "%LOCAL_OVERRIDES%"

:parse_args
if "%~1"=="" goto args_done

if /I "%~1"=="--help" goto show_help
if /I "%~1"=="-h" goto show_help
if /I "%~1"=="/?" goto show_help

if /I "%~1"=="--url" goto opt_url
if /I "%~1"=="--process-name" goto opt_process_name
if /I "%~1"=="--process-id" goto opt_process_id
if /I "%~1"=="--config" goto opt_config
if /I "%~1"=="--build" goto opt_build
if /I "%~1"=="--no-build" goto opt_no_build
if /I "%~1"=="--publish" goto opt_publish
if /I "%~1"=="--dev" goto opt_dev
if /I "%~1"=="--open" goto opt_open

echo ERROR: Unknown argument: %~1
echo Run "%~nx0 --help" for usage.
goto fail

:opt_url
set "BOTDS_OPTION=--url"
if "%~2"=="" goto missing_value
set "BOTDS_CLI_URL=%~2"
shift
shift
goto parse_args

:opt_process_name
set "BOTDS_OPTION=--process-name"
if "%~2"=="" goto missing_value
set "BOTDS_CLI_PROCESS_NAME=%~2"
shift
shift
goto parse_args

:opt_process_id
set "BOTDS_OPTION=--process-id"
if "%~2"=="" goto missing_value
set "BOTDS_CLI_PROCESS_ID=%~2"
shift
shift
goto parse_args

:opt_config
set "BOTDS_OPTION=--config"
if "%~2"=="" goto missing_value
set "BOTDS_CONFIG=%~2"
shift
shift
goto parse_args

:opt_build
set "BOTDS_BUILD=1"
shift
goto parse_args

:opt_no_build
set "BOTDS_NO_BUILD=1"
shift
goto parse_args

:opt_publish
set "BOTDS_PREFER_PUBLISH=1"
shift
goto parse_args

:opt_dev
set "BOTDS_FORCE_DEV=1"
set "BOTDS_PREFER_PUBLISH=0"
shift
goto parse_args

:opt_open
set "BOTDS_OPEN=1"
shift
goto parse_args

:args_done

rem ---- Resolve configuration ----
rem CLI overrides env/local; env/local override built-in defaults.

if defined BOTDS_CLI_URL (
  set "BOTDS_URL=%BOTDS_CLI_URL%"
) else if defined ASPNETCORE_URLS (
  set "BOTDS_URL=%ASPNETCORE_URLS%"
)

if defined BOTDS_CLI_PROCESS_NAME (
  set "BOTDS_PROCESS_NAME=%BOTDS_CLI_PROCESS_NAME%"
) else if defined BotDs__Scanner__ProcessName (
  set "BOTDS_PROCESS_NAME=%BotDs__Scanner__ProcessName%"
)

if defined BOTDS_CLI_PROCESS_ID (
  set "BOTDS_PROCESS_ID=%BOTDS_CLI_PROCESS_ID%"
) else if defined BotDs__Scanner__ProcessId (
  set "BOTDS_PROCESS_ID=%BotDs__Scanner__ProcessId%"
)

rem Light PID sanity check: digits only when provided.
if defined BOTDS_PROCESS_ID (
  echo.%BOTDS_PROCESS_ID%| findstr /R /C:"^[1-9][0-9]*$" >nul
  if errorlevel 1 (
    echo ERROR: --process-id must be a positive integer. Got: %BOTDS_PROCESS_ID%
    goto fail
  )
)

rem Always pin absolute profiles path so publish and project runs resolve the same tree.
set "BotDs__Profiles__Directory=%PROFILES_DIR%"
set "BotDs__Scanner__ProcessName=%BOTDS_PROCESS_NAME%"
if defined BOTDS_PROCESS_ID (
  set "BotDs__Scanner__ProcessId=%BOTDS_PROCESS_ID%"
) else (
  rem Clear stale inherited PID so name-only selection remains clean.
  set "BotDs__Scanner__ProcessId="
)

set "ASPNETCORE_URLS=%BOTDS_URL%"
if not defined ASPNETCORE_ENVIRONMENT set "ASPNETCORE_ENVIRONMENT=Development"
if "%BOTDS_FORCE_DEV%"=="1" set "ASPNETCORE_ENVIRONMENT=Development"

if not exist "%APP_PROJECT%" (
  echo ERROR: App project not found:
  echo   %APP_PROJECT%
  goto fail
)

if not exist "%PROFILES_DIR%\" (
  echo WARNING: Profiles directory missing: %PROFILES_DIR%
)

echo.
echo BotDs launcher
echo   Root:      %ROOT%
echo   URL:       %BOTDS_URL%
echo   Process:   %BOTDS_PROCESS_NAME%
if defined BOTDS_PROCESS_ID echo   PID:       %BOTDS_PROCESS_ID%
echo   Profiles:  %PROFILES_DIR%
echo   Config:    %BOTDS_CONFIG%
echo   Auth:      loopback only ^(no API token^)
if exist "%LOCAL_OVERRIDES%" echo   Overrides: botds.local.cmd loaded
echo.

if "%BOTDS_OPEN%"=="1" start "" "%BOTDS_URL%"

rem Prefer published binary only when requested and present.
if "%BOTDS_PREFER_PUBLISH%"=="1" if exist "%APP_PUBLISH_EXE%" goto run_publish
if "%BOTDS_PREFER_PUBLISH%"=="1" (
  echo WARNING: --publish requested but not found:
  echo   %APP_PUBLISH_EXE%
  echo Falling back to project run.
  echo.
)

goto run_project

:run_publish
echo Starting published app:
echo   %APP_PUBLISH_EXE%
echo.
pushd "%ROOT%\publish"
if errorlevel 1 goto fail
"%APP_PUBLISH_EXE%"
set "EXIT_CODE=%ERRORLEVEL%"
popd
goto after_run

:run_project
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: dotnet was not found on PATH.
  goto fail
)

if "%BOTDS_BUILD%"=="1" (
  echo Building %APP_PROJECT% ^(%BOTDS_CONFIG%^)...
  dotnet build "%APP_PROJECT%" -c "%BOTDS_CONFIG%" --nologo
  if errorlevel 1 goto fail
  echo.
)

rem --no-launch-profile keeps ASPNETCORE_URLS / env config authoritative.
if "%BOTDS_NO_BUILD%"=="1" (
  echo Starting project:
  echo   dotnet run --project "%APP_PROJECT%" -c %BOTDS_CONFIG% --no-launch-profile --no-build
  echo.
  dotnet run --project "%APP_PROJECT%" -c "%BOTDS_CONFIG%" --no-launch-profile --no-build
) else (
  echo Starting project:
  echo   dotnet run --project "%APP_PROJECT%" -c %BOTDS_CONFIG% --no-launch-profile
  echo.
  dotnet run --project "%APP_PROJECT%" -c "%BOTDS_CONFIG%" --no-launch-profile
)
set "EXIT_CODE=%ERRORLEVEL%"
goto after_run

:after_run
if not defined EXIT_CODE set "EXIT_CODE=1"
if not "%EXIT_CODE%"=="0" (
  echo.
  echo BotDs exited with code %EXIT_CODE%.
  if "%BOTDS_PAUSE_ON_FAIL%"=="1" pause
  exit /b %EXIT_CODE%
)
exit /b 0

:missing_value
echo ERROR: Option %BOTDS_OPTION% requires a value.
goto fail

:show_help
echo.
echo BotDs dashboard launcher
echo.
echo Usage:
echo   %~nx0 [options]
echo.
echo Options:
echo   --help                 Show help
echo   --url URL              Listen URL ^(default: http://localhost:5068^)
echo   --process-name NAME    RIFT process basename ^(default: rift_x64^)
echo   --process-id PID       Optional explicit process id
echo   --config Debug^|Release Configuration ^(default: Debug^)
echo   --build                Build before run
echo   --no-build             Pass --no-build to dotnet run
echo   --publish              Use publish\BotDs.App.exe when available
echo   --dev                  Force Development + project run
echo   --open                 Open dashboard URL in the browser
echo.
echo Dashboard API is loopback-only with no token.
echo Local overrides file ^(optional, gitignored^):
echo   %LOCAL_OVERRIDES%
echo.
exit /b 0

:fail
if "%BOTDS_PAUSE_ON_FAIL%"=="1" pause
exit /b 1
