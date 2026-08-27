@echo off
REM Launcher for tools\DataSync.DevHarness. See `tools\dev-harness help`.
REM See the sh launcher for why this builds every time and runs the DLL directly rather than
REM using `dotnet run`.
REM
REM Windows note: a running `up` holds its own build output open, so if you edit harness or
REM DataSync.Core sources while `up` is running, this build fails with MSB3027 (file in use). Stop
REM `up` first, or run the verb from an unchanged working tree.
setlocal

set "REPO_ROOT=%~dp0.."

dotnet build "%REPO_ROOT%\tools\DataSync.DevHarness\DataSync.DevHarness.csproj" -v quiet --nologo 1>&2
if errorlevel 1 exit /b 1

dotnet exec "%REPO_ROOT%\tools\DataSync.DevHarness\bin\Debug\net10.0\DataSync.DevHarness.dll" %*
exit /b %errorlevel%
