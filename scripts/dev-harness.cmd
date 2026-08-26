@echo off
REM Launcher for tools\DataSync.DevHarness. See `scripts\dev-harness help`.
REM
REM `dotnet run` handles the incremental build itself, including changes in referenced projects such
REM as DataSync.Core, and propagates the app's exit code — which `verify` relies on to report
REM pass/fail. See the sh launcher for the fuller note.
setlocal

dotnet run --project "%~dp0..\tools\DataSync.DevHarness" -- %*
exit /b %errorlevel%
