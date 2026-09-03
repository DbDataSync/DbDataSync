@echo off
REM Launcher for tools\DbDataSync.Benchmarks. See `tools\benchmarks --help`.
REM Release build: measurements from a Debug build would be meaningless.
setlocal

set "REPO_ROOT=%~dp0.."

dotnet build "%REPO_ROOT%\tools\DbDataSync.Benchmarks\DbDataSync.Benchmarks.csproj" -c Release -v quiet --nologo 1>&2
if errorlevel 1 exit /b 1

dotnet exec "%REPO_ROOT%\tools\DbDataSync.Benchmarks\bin\Release\net10.0\DbDataSync.Benchmarks.dll" %*
exit /b %errorlevel%
