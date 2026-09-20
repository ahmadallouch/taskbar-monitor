@echo off
REM Publishes the ahead of time compiled executable to bin\ and restarts it.
REM The running instance holds the file open, so it is stopped first. Launch is
REM detached through Start-Process so this script does not hold the child's
REM stdout handle open, which would block whatever called it.
taskkill /IM TaskbarMonitor.exe /F >nul 2>&1
dotnet publish "%~dp0src\TaskbarMonitor.csproj" -c Release -o "%~dp0bin" --nologo
if errorlevel 1 exit /b 1
powershell -NoProfile -Command "Start-Process '%~dp0bin\TaskbarMonitor.exe'"
