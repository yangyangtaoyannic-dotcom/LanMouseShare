@echo off
setlocal

echo Logs:
echo   %ProgramData%\LanMouseShare\Logs

if not exist "%ProgramData%\LanMouseShare\Logs" mkdir "%ProgramData%\LanMouseShare\Logs"
explorer "%ProgramData%\LanMouseShare\Logs"
