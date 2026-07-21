@echo off
setlocal EnableExtensions
rem Deploy BotDsBridge to shell MyDocuments RIFT AddOns (OneDrive-backed on this machine).
rem Durable fact: docs\rift-local-paths.md and BotDs.Core.RiftLocalPaths
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
dotnet run --project "%ROOT%\src\BotDs.Tools\BotDs.Tools.csproj" -c Release --no-launch-profile -- deploy-addon %*
exit /b %ERRORLEVEL%
