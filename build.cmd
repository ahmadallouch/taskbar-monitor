@echo off
REM Rebuild after editing anything in src\. The running instance locks the exe,
REM so stop it first. Launch is detached via Start-Process so this script does not
REM hold the child's stdout handle open (plain `start` does, and blocks callers).
taskkill /IM TaskbarMonitor.exe /F >nul 2>&1
dotnet build "%~dp0src\TaskbarMonitor.csproj" -c Release -o "%~dp0bin" --nologo
if errorlevel 1 exit /b 1
powershell -NoProfile -Command "Start-Process '%~dp0bin\TaskbarMonitor.exe'"
