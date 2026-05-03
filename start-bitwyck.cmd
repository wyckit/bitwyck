@echo off
REM Bitwyck launcher (cmd) — runs the daemon command from the repo root.
pushd "%~dp0"
dotnet run --project src\Bitwyck.CLI -- %*
set EXITCODE=%ERRORLEVEL%
popd
exit /b %EXITCODE%
