@echo off
REM Thin launcher for tools\DataSync.DevHarness. See `scripts\dev-harness help`.
setlocal

set "SCRIPT_DIR=%~dp0"
set "REPO_ROOT=%SCRIPT_DIR%.."
set "PROJECT=%REPO_ROOT%\tools\DataSync.DevHarness\DataSync.DevHarness.csproj"
set "ASSEMBLY=%REPO_ROOT%\tools\DataSync.DevHarness\bin\Debug\net10.0\DataSync.DevHarness.dll"

REM Build only when the tool is missing. Unlike the shell launcher this doesn't compare timestamps —
REM batch has no clean way to do it — so run `dotnet build` yourself after editing the harness.
if not exist "%ASSEMBLY%" (
  echo Building the dev harness...
  dotnet build "%PROJECT%" -v quiet --nologo
  if errorlevel 1 exit /b 1
)

dotnet exec "%ASSEMBLY%" %*
exit /b %errorlevel%
