@echo off
setlocal DisableDelayedExpansion
if not "%~2"=="" goto usage
if "%~1"=="" goto launch
if not exist "%~f1\" goto missing
start "" "%~dp0VibeHarder.exe" --workspace "%~f1\."
exit /b 0
:launch
start "" "%~dp0VibeHarder.exe"
exit /b 0
:missing
echo Directory does not exist: "%~f1" >&2
exit /b 2
:usage
echo Usage: vibe-harder [directory] >&2
exit /b 2
