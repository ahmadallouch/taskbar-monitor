@echo off
REM Publishes to bin\ and restarts. The running instance locks the exe, so it is
REM killed first. Start-Process detaches, otherwise this script blocks on the child.
taskkill /IM TaskbarMonitor.exe /F >nul 2>&1
dotnet publish "%~dp0src\TaskbarMonitor.csproj" -c Release -o "%~dp0bin" --nologo
if errorlevel 1 exit /b 1
powershell -NoProfile -Command "Start-Process '%~dp0bin\TaskbarMonitor.exe'"
