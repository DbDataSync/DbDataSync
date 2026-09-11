@echo off
REM Packs this repo's own DbDataSync.Cli and installs/updates it as a local dotnet tool — the fast-
REM iteration loop for working on the CLI itself. See docs\install.md for a real machine-wide install;
REM this is the dev-loop shortcut, not that. See the sh script for the full option reference.
REM
REM Usage:
REM   tools\install-local-tool [--global|--machine-wide|--tool-path <dir>] [--no-spa] [--uninstall]
REM
REM --machine-wide needs an elevated prompt (it writes to %ProgramFiles%\DbDataSync and the Machine
REM PATH) — this script does not elevate itself; run it from one already.
setlocal enabledelayedexpansion

set "REPO_ROOT=%~dp0.."
set "PROJECT=%REPO_ROOT%\src\DbDataSync.Cli\DbDataSync.Cli.csproj"
set "FEED=%REPO_ROOT%\bin\local-tool-feed"
REM Mirrors CliOptions.DefaultToolDir's own Windows answer.
set "DEFAULT_TOOL_DIR=%ProgramFiles%\DbDataSync"

set "TARGET=global"
set "TOOL_PATH="
set "PACK_ARGS="
set "DO_UNINSTALL="

:parse
if "%~1"=="" goto afterparse
if "%~1"=="--global" (set "TARGET=global") & shift & goto parse
if "%~1"=="--machine-wide" (set "TARGET=machine-wide") & (set "TOOL_PATH=%DEFAULT_TOOL_DIR%") & shift & goto parse
if "%~1"=="--tool-path" (set "TARGET=tool-path") & (set "TOOL_PATH=%~2") & shift & shift & goto parse
if "%~1"=="--no-spa" (set "PACK_ARGS=-p:SkipWebBuild=true") & shift & goto parse
if "%~1"=="--uninstall" (set "DO_UNINSTALL=1") & shift & goto parse
echo Unknown argument: %~1 1>&2
exit /b 1
:afterparse

if defined DO_UNINSTALL (
    if "%TARGET%"=="global" (
        dotnet tool uninstall --global DbDataSync
        exit /b %errorlevel%
    )
    if "%TARGET%"=="machine-wide" (
        "%DEFAULT_TOOL_DIR%\dbdatasync.exe" tool uninstall
        dotnet tool uninstall --tool-path "%DEFAULT_TOOL_DIR%" DbDataSync
        exit /b %errorlevel%
    )
    dotnet tool uninstall --tool-path "%TOOL_PATH%" DbDataSync
    exit /b %errorlevel%
)

REM No -p:Version passed here: DbDataSync.Cli.csproj computes a dated, alpha-labelled default of its
REM own (see that file's <Version> comment) for exactly this dev loop, on the same UTC-clock convention
REM release.yml uses for a real release.
echo Packing DbDataSync.Cli... 1>&2
dotnet pack "%PROJECT%" -c Release -o "%FEED%" %PACK_ARGS% 1>&2
if errorlevel 1 exit /b 1

REM Read back off whatever pack just wrote, not decided here — the same reason release.yml reads its
REM own shipped version back off the nupkg filename rather than trusting what it asked for. Newest by
REM LastWriteTime, not by name (NuGet's leading-zero stripping can sort a filename out of date order
REM across a month boundary); the "[0-9]" wildcard right after "DbDataSync." excludes a project
REM reference's own incidentally packed sibling (DbDataSync.Drivers.Abstractions.*.nupkg) from matching.
for /f "usebackq" %%T in (`powershell -NoProfile -Command ^
    "(Get-ChildItem '%FEED%\DbDataSync.*.nupkg' | Where-Object { $_.BaseName -like 'DbDataSync.[0-9]*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1).BaseName.Substring(11)"`) do set "VERSION=%%T"
echo Packed DbDataSync.Cli %VERSION% 1>&2

if "%TARGET%"=="global" (
    dotnet tool update --global --add-source "%FEED%" --version %VERSION% DbDataSync
    exit /b %errorlevel%
)
if "%TARGET%"=="machine-wide" (
    dotnet tool update --tool-path "%DEFAULT_TOOL_DIR%" --add-source "%FEED%" --version %VERSION% DbDataSync
    if errorlevel 1 exit /b 1
    "%DEFAULT_TOOL_DIR%\dbdatasync.exe" tool install
    exit /b %errorlevel%
)
dotnet tool update --tool-path "%TOOL_PATH%" --add-source "%FEED%" --version %VERSION% DbDataSync
exit /b %errorlevel%
